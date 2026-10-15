using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Debloater.Core.Reference;

namespace Debloater.RefGen;

/// <summary>
/// Gera reference/msdocs-policy-csp.json a partir da documentação da Policy CSP.
///
/// Onde estão os docs: o repositório público MicrosoftDocs/windows-itpro-docs não existe mais
/// (404); as páginas vêm de um repositório privado (MicrosoftDocs/windows-docs-pr). Por isso
/// lemos direto do learn.microsoft.com, que devolve o Markdown da página quando a requisição
/// envia "Accept: text/markdown". A lista de páginas vem do índice (toc.json) da seção MDM.
/// </summary>
internal static partial class Program
{
    private const string BaseUrl = "https://learn.microsoft.com/en-us/windows/client-management/mdm/";

    private const string Usage = """
        Debloater.RefGen - gera a referência da Policy CSP (Microsoft Learn)

        Uso:
          Debloater.RefGen [--out reference/msdocs-policy-csp.json] [--cache DIR] [--from-cache DIR] [--parallel 6]

        --out DIR/arquivo  destino do JSON (padrão: reference/msdocs-policy-csp.json na raiz do repositório)
        --cache DIR        também grava o Markdown baixado em DIR (útil para depurar o parser)
        --from-cache DIR   não acessa a rede: lê os *.md de DIR
        """;

    [GeneratedRegex(@"""href""\s*:\s*""(policy-csp-[a-z0-9-]+)""")]
    private static partial Regex TocHref();

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string? outFile = null, cacheDir = null, fromCache = null;
        int parallel = 6;
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"'{args[i]}' exige um valor.");
            switch (args[i])
            {
                case "--out": outFile = Path.GetFullPath(Next()); break;
                case "--cache": cacheDir = Path.GetFullPath(Next()); break;
                case "--from-cache": fromCache = Path.GetFullPath(Next()); break;
                case "--parallel": parallel = int.Parse(Next()); break;
                case "-h" or "--help": Console.WriteLine(Usage); return 0;
                default: Console.Error.WriteLine($"Opção desconhecida '{args[i]}'.\n\n{Usage}"); return 2;
            }
        }
        outFile ??= Path.Combine(FindRepoRoot() ?? Directory.GetCurrentDirectory(), "reference", "msdocs-policy-csp.json");

        List<(string Page, string Markdown)> pages;
        try
        {
            pages = fromCache is not null ? ReadCache(fromCache) : await DownloadAsync(cacheDir, parallel);
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"Falha ao baixar a documentação: {ex.Message}");
            return 1;
        }

        var parsed = pages.Select(p => CspMarkdownParser.ParsePage(p.Page, p.Markdown)).ToList();
        var reference = CspMarkdownParser.Build(parsed);

        // Proteção: não sobrescrever a referência com algo claramente quebrado (site fora do ar, formato mudou...).
        if (reference.Pages.Count < 100 || reference.Policies.Count < 1000)
        {
            Console.Error.WriteLine($"Resultado suspeito ({reference.Pages.Count} páginas, {reference.Policies.Count} políticas); arquivo NÃO gravado.");
            return 1;
        }
        var empty = reference.Pages.Where(p => p.PolicyCount == 0).Select(p => p.Page).ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
        File.WriteAllText(outFile, CspMarkdownParser.Serialize(reference), new UTF8Encoding(false));

        Console.WriteLine($"{reference.Pages.Count} páginas, {reference.Policies.Count} políticas → {outFile}");
        Console.WriteLine($"  com mapeamento GP/ADMX: {reference.Policies.Count(p => p.GpMappings is not null)}");
        Console.WriteLine($"  ADMX-backed: {reference.Policies.Count(p => p.AdmxBacked)}");
        Console.WriteLine($"  só Insider: {reference.Policies.Count(p => p.InsiderOnly)}; obsoletas: {reference.Policies.Count(p => p.Deprecated)}");
        Console.WriteLine($"  com notas de edição: {reference.Policies.Count(p => p.EditionNotes is not null)}");
        if (empty.Count > 0) Console.WriteLine($"  páginas sem políticas reconhecidas ({empty.Count}): {string.Join(", ", empty)}");
        return 0;
    }

    private static string? FindRepoRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "Debloater.slnx"))) return dir.FullName;
        }
        return null;
    }

    private static List<(string, string)> ReadCache(string dir) =>
        Directory.EnumerateFiles(dir, "policy-csp-*.md")
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (Path.GetFileNameWithoutExtension(f), File.ReadAllText(f)))
            .ToList();

    private static async Task<List<(string, string)>> DownloadAsync(string? cacheDir, int parallel)
    {
        using var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Debloater.RefGen/1.0 (+https://github.com/0-programming-skill/windows-debloater)");

        var toc = await GetWithRetryAsync(http, BaseUrl + "toc.json", "application/json");
        var names = TocHref().Matches(toc).Select(m => m.Groups[1].Value).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
        Console.WriteLine($"{names.Count} páginas policy-csp-* no índice.");
        if (cacheDir is not null) Directory.CreateDirectory(cacheDir);

        var results = new (string, string)[names.Count];
        using var gate = new SemaphoreSlim(parallel);
        int done = 0;
        await Task.WhenAll(names.Select(async (name, idx) =>
        {
            await gate.WaitAsync();
            try
            {
                var md = await GetWithRetryAsync(http, BaseUrl + name, "text/markdown");
                results[idx] = (name, md);
                if (cacheDir is not null) await File.WriteAllTextAsync(Path.Combine(cacheDir, name + ".md"), md);
                var n = Interlocked.Increment(ref done);
                if (n % 50 == 0) Console.WriteLine($"  {n}/{names.Count}");
            }
            finally
            {
                gate.Release();
            }
        }));
        return [.. results];
    }

    private static async Task<string> GetWithRetryAsync(HttpClient http, string url, string accept)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.ParseAdd(accept);
                using var resp = await http.SendAsync(req);
                if ((int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500)
                    throw new HttpRequestException($"HTTP {(int)resp.StatusCode}", null, resp.StatusCode);
                resp.EnsureSuccessStatusCode();
                var body = await resp.Content.ReadAsStringAsync();
                if (accept == "text/markdown" && !body.TrimStart().StartsWith("---", StringComparison.Ordinal))
                    throw new HttpRequestException($"{url}: resposta não é Markdown (o servidor ignorou Accept: text/markdown?).");
                return body;
            }
            catch (Exception ex) when (attempt < 4 && ex is HttpRequestException or TaskCanceledException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
            }
        }
    }
}
