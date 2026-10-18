using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Debloater.Core.Reference;

namespace Debloater.RefGen;

/// <summary>
/// Generates reference/msdocs-policy-csp.json from the Policy CSP documentation.
///
/// Where the docs live: the public MicrosoftDocs/windows-itpro-docs repository no longer exists
/// (404); the pages come from a private repository (MicrosoftDocs/windows-docs-pr). So we
/// read straight from learn.microsoft.com, which returns the page's Markdown when the request
/// sends "Accept: text/markdown". The page list comes from the index (toc.json) of the MDM section.
/// </summary>
internal static partial class Program
{
    private const string BaseUrl = "https://learn.microsoft.com/en-us/windows/client-management/mdm/";

    private const string Usage = """
        Debloater.RefGen - generates the Policy CSP reference (Microsoft Learn)

        Usage:
          Debloater.RefGen [--out reference/msdocs-policy-csp.json] [--cache DIR] [--from-cache DIR] [--parallel 6]
        Debloater.RefGen compare <old.json> <new.json> [--markdown output.md]

        --out DIR/file     JSON destination (default: reference/msdocs-policy-csp.json at the repository root)
        --cache DIR        also writes the downloaded Markdown to DIR (useful for debugging the parser)
        --from-cache DIR   does not access the network: reads the *.md files from DIR
        """;

    [GeneratedRegex(@"""href""\s*:\s*""(policy-csp-[a-z0-9-]+)""")]
    private static partial Regex TocHref();

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length >= 1 && args[0] == "compare")
            return Compare(args);

        string? outFile = null, cacheDir = null, fromCache = null;
        int parallel = 6;
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"'{args[i]}' requires a value.");
            switch (args[i])
            {
                case "--out": outFile = Path.GetFullPath(Next()); break;
                case "--cache": cacheDir = Path.GetFullPath(Next()); break;
                case "--from-cache": fromCache = Path.GetFullPath(Next()); break;
                case "--parallel": parallel = int.Parse(Next()); break;
                case "-h" or "--help": Console.WriteLine(Usage); return 0;
                default: Console.Error.WriteLine($"Unknown option '{args[i]}'.\n\n{Usage}"); return 2;
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
            Console.Error.WriteLine($"Failed to download the documentation: {ex.Message}");
            return 1;
        }

        var parsed = pages.Select(p => CspMarkdownParser.ParsePage(p.Page, p.Markdown)).ToList();
        var reference = CspMarkdownParser.Build(parsed);

        // Safeguard: do not overwrite the reference with something clearly broken (site down, format changed...).
        if (reference.Pages.Count < 100 || reference.Policies.Count < 1000)
        {
            Console.Error.WriteLine($"Suspicious result ({reference.Pages.Count} pages, {reference.Policies.Count} policies); file NOT written.");
            return 1;
        }
        var empty = reference.Pages.Where(p => p.PolicyCount == 0).Select(p => p.Page).ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
        File.WriteAllText(outFile, CspMarkdownParser.Serialize(reference), new UTF8Encoding(false));

        Console.WriteLine($"{reference.Pages.Count} pages, {reference.Policies.Count} policies → {outFile}");
        Console.WriteLine($"  with GP/ADMX mapping: {reference.Policies.Count(p => p.GpMappings is not null)}");
        Console.WriteLine($"  ADMX-backed: {reference.Policies.Count(p => p.AdmxBacked)}");
        Console.WriteLine($"  Insider only: {reference.Policies.Count(p => p.InsiderOnly)}; deprecated: {reference.Policies.Count(p => p.Deprecated)}");
        Console.WriteLine($"  with edition notes: {reference.Policies.Count(p => p.EditionNotes is not null)}");
        if (empty.Count > 0) Console.WriteLine($"  pages without recognized policies ({empty.Count}): {string.Join(", ", empty)}");
        return 0;
    }

    /// <summary>
    /// compare: per-policy differences between two references (added, removed, changed and
    /// which fields changed). Writes Markdown (for the CI issue body) and returns 0 if equal, 1 if changed.
    /// </summary>
    private static int Compare(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        string? markdownOut = null;
        for (int i = 3; i < args.Length; i++)
            if (args[i] == "--markdown" && i + 1 < args.Length) markdownOut = Path.GetFullPath(args[++i]);

        var oldRef = CspMarkdownParser.Load(args[1]);
        var newRef = CspMarkdownParser.Load(args[2]);
        static string Key(CspPolicy p) => $"{p.Area}/{p.Name}";
        var oldMap = oldRef.Policies.GroupBy(Key).ToDictionary(g => g.Key, g => g.First());
        var newMap = newRef.Policies.GroupBy(Key).ToDictionary(g => g.Key, g => g.First());

        var added = newMap.Keys.Except(oldMap.Keys).Order(StringComparer.Ordinal).ToList();
        var removed = oldMap.Keys.Except(newMap.Keys).Order(StringComparer.Ordinal).ToList();
        var changed = new List<(string Key, List<string> Fields)>();
        foreach (var key in newMap.Keys.Intersect(oldMap.Keys).Order(StringComparer.Ordinal))
        {
            var a = JsonSerializer.SerializeToNode(oldMap[key], CspMarkdownParser.JsonOptions)!.AsObject();
            var b = JsonSerializer.SerializeToNode(newMap[key], CspMarkdownParser.JsonOptions)!.AsObject();
            var fields = a.Select(p => p.Key).Union(b.Select(p => p.Key))
                .Where(f => a[f]?.ToJsonString() != b[f]?.ToJsonString())
                .Order(StringComparer.Ordinal).ToList();
            if (fields.Count > 0) changed.Add((key, fields));
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Policies: {oldRef.Policies.Count} → {newRef.Policies.Count} | added {added.Count} | removed {removed.Count} | changed {changed.Count}");
        void Section(string title, IEnumerable<string> items)
        {
            var list = items.ToList();
            if (list.Count == 0) return;
            sb.AppendLine().AppendLine($"### {title} ({list.Count})").AppendLine();
            foreach (var item in list.Take(300)) sb.AppendLine($"- {item}");
            if (list.Count > 300) sb.AppendLine($"- … and {list.Count - 300} more");
        }
        Section("Added", added.Select(k => $"`{k}`"));
        Section("Removed", removed.Select(k => $"`{k}`"));
        Section("Changed", changed.Select(c => $"`{c.Key}`: {string.Join(", ", c.Fields)}"));

        var text = sb.ToString();
        Console.WriteLine(text);
        if (markdownOut is not null) File.WriteAllText(markdownOut, text, new UTF8Encoding(false));
        return added.Count + removed.Count + changed.Count == 0 ? 0 : 1;
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
        Console.WriteLine($"{names.Count} policy-csp-* pages in the index.");
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
                    throw new HttpRequestException($"{url}: response is not Markdown (did the server ignore Accept: text/markdown?).");
                return body;
            }
            catch (Exception ex) when (attempt < 4 && ex is HttpRequestException or TaskCanceledException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
            }
        }
    }
}
