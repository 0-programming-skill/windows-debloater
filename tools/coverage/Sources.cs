using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Debloater.Coverage;

/// <summary>Uma ferramenta comparada (entrada de sources.json).</summary>
public sealed class SourceSpec
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Repo { get; set; } = "";
    /// <summary>Tag da versão (informativo); o que vale para baixar é <see cref="Sha"/>.</summary>
    public string? Tag { get; set; }
    public string Sha { get; set; } = "";
    public string License { get; set; } = "";
    /// <summary>"reference": licença que não permite copiar (AGPL, GPL, CC-BY-SA, própria). Só nomes e caminhos entram no relatório.</summary>
    public string Usage { get; set; } = "reference";
    /// <summary>Parser: winutil | privacysexy | win11debloat | sophia | csharp | playbook.</summary>
    public string Parser { get; set; } = "";
    /// <summary>Padrões (glob com ** e *) dos arquivos a baixar e ler.</summary>
    public List<string> Include { get; set; } = [];
    public List<string> Exclude { get; set; } = [];
    public string? Note { get; set; }
}

public sealed class SourcesFile
{
    public List<SourceSpec> Sources { get; set; } = [];
}

/// <summary>Baixa os arquivos de cada fonte do GitHub, fixados no commit (sha), com cache local.</summary>
public sealed class SourceFetcher(string cacheDir) : IDisposable
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpClient _http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Debloater.Coverage", "1.0"));
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? Environment.GetEnvironmentVariable("GH_TOKEN");
        if (!string.IsNullOrWhiteSpace(token)) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    public string SourceDir(SourceSpec s) => Path.Combine(cacheDir, s.Id, s.Sha);

    /// <summary>Glob simples: ** = qualquer coisa (inclusive /), * = qualquer coisa sem /.</summary>
    public static Regex Glob(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace(@"\*\*/", "(?:.*/)?").Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*") + "$",
            RegexOptions.IgnoreCase);

    /// <summary>Lista os arquivos (pela árvore do commit) que casam com Include e não com Exclude.</summary>
    public async Task<List<string>> ListAsync(SourceSpec s, CancellationToken ct)
    {
        var listFile = Path.Combine(SourceDir(s), ".files.json");
        if (File.Exists(listFile)) return JsonSerializer.Deserialize<List<string>>(await File.ReadAllTextAsync(listFile, ct))!;

        var url = $"https://api.github.com/repos/{s.Repo}/git/trees/{s.Sha}?recursive=1";
        using var doc = JsonDocument.Parse(await _http.GetStringAsync(url, ct));
        var inc = s.Include.Select(Glob).ToList();
        var exc = s.Exclude.Select(Glob).ToList();
        var files = doc.RootElement.GetProperty("tree").EnumerateArray()
            .Where(e => e.GetProperty("type").GetString() == "blob")
            .Select(e => e.GetProperty("path").GetString()!)
            .Where(p => inc.Any(g => g.IsMatch(p)) && !exc.Any(g => g.IsMatch(p)))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        Directory.CreateDirectory(SourceDir(s));
        await File.WriteAllTextAsync(listFile, JsonSerializer.Serialize(files), ct);
        return files;
    }

    /// <summary>Baixa (se ainda não estão no cache) e devolve (caminho no repositório, bytes).</summary>
    public async Task<List<(string Path, byte[] Bytes)>> FetchAsync(SourceSpec s, CancellationToken ct)
    {
        var files = await ListAsync(s, ct);
        var result = new (string, byte[])[files.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, files.Count), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (i, token) =>
        {
            var rel = files[i];
            var local = Path.Combine(SourceDir(s), rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(local))
            {
                var url = $"https://raw.githubusercontent.com/{s.Repo}/{s.Sha}/{string.Join('/', rel.Split('/').Select(Uri.EscapeDataString))}";
                var bytes = await _http.GetByteArrayAsync(url, token);
                Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                await File.WriteAllBytesAsync(local, bytes, token);
            }
            result[i] = (rel, await File.ReadAllBytesAsync(local, token));
        });
        return [.. result];
    }

    /// <summary>Última versão publicada (release) e o commit dela; sem release, o HEAD do ramo padrão.</summary>
    public async Task<(string? Tag, string Sha)> LatestAsync(SourceSpec s, CancellationToken ct)
    {
        string? tag = null;
        string reference;
        using (var resp = await _http.GetAsync($"https://api.github.com/repos/{s.Repo}/releases/latest", ct))
        {
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                tag = doc.RootElement.GetProperty("tag_name").GetString();
            }
        }
        if (tag is null)
        {
            using var repo = JsonDocument.Parse(await _http.GetStringAsync($"https://api.github.com/repos/{s.Repo}", ct));
            reference = repo.RootElement.GetProperty("default_branch").GetString()!;
        }
        else reference = tag;
        using var commit = JsonDocument.Parse(await _http.GetStringAsync($"https://api.github.com/repos/{s.Repo}/commits/{Uri.EscapeDataString(reference)}", ct));
        return (tag, commit.RootElement.GetProperty("sha").GetString()!);
    }

    public void Dispose() => _http.Dispose();
}
