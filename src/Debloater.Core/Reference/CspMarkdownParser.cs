using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Debloater.Core.Reference;

/// <summary>
/// Lê as páginas policy-csp-*.md da documentação da Microsoft (o Markdown que learn.microsoft.com
/// devolve com "Accept: text/markdown"). Cada seção "## Nome" com um bloco ```Device/```User é uma
/// política: escopo, edições, SO aplicável, formato, valores permitidos e mapeamento ADMX.
/// </summary>
public static partial class CspMarkdownParser
{
    private const int MaxDescription = 240;

    [GeneratedRegex(@"(✅|❌)\s*([^✅❌|]+)")]
    private static partial Regex CheckItem();

    [GeneratedRegex(@"\[10\.0\.(\d+)(?:\.(\d+))?\]")]
    private static partial Regex BuildRef();

    [GeneratedRegex(@"^\./(Device|User)/Vendor/MSFT/Policy/Config/([^/]+)/(.+)$")]
    private static partial Regex CspUri();

    [GeneratedRegex(@"[^.]*\bonly\s+(?:be\s+)?(?:supported|applicable|available|honored|respected|effective|works?)\b[^.]*\b(?:Enterprise|Education|Server|Pro)\b[^.]*\.?", RegexOptions.IgnoreCase)]
    private static partial Regex EditionSentence();

    [GeneratedRegex(@"\bthis\s+policy\s+(?:setting\s+)?(?:is|has\s+been)\s+deprecated\b", RegexOptions.IgnoreCase)]
    private static partial Regex DeprecatedSentence();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex MdLink();

    /// <summary>Lê uma página. <paramref name="page"/> é o nome sem extensão (ex.: policy-csp-system).</summary>
    public static (CspPage Page, List<CspPolicy> Policies) ParsePage(string page, string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var info = new CspPage { Page = page };
        int start = 0;

        // Frontmatter YAML entre "---".
        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Trim() == "---") { start = i + 1; break; }
                if (lines[i].StartsWith("ms.date:", StringComparison.Ordinal))
                    info.MsDate = lines[i]["ms.date:".Length..].Trim().Split('T')[0];
            }
        }

        var policies = new List<CspPolicy>();
        int? sectionStart = null;
        string? sectionName = null;
        for (int i = start; i <= lines.Length; i++)
        {
            bool atEnd = i == lines.Length;
            // Políticas ficam em "## Nome" ou, em páginas agrupadas (ex.: Update), em "### Nome".
            bool isHeading = !atEnd && (lines[i].StartsWith("## ", StringComparison.Ordinal) || lines[i].StartsWith("### ", StringComparison.Ordinal));
            if (atEnd || isHeading)
            {
                if (sectionStart is { } s && sectionName is not null)
                {
                    var policy = ParseSection(page, sectionName, lines[s..i]);
                    if (policy is not null) policies.Add(policy);
                }
                if (isHeading)
                {
                    sectionName = lines[i].TrimStart('#').Trim();
                    sectionStart = i + 1;
                }
            }
        }
        info.PolicyCount = policies.Count;
        return (info, policies);
    }

    private static CspPolicy? ParseSection(string page, string heading, string[] lines)
    {
        var policy = new CspPolicy { Page = page, Name = heading };

        // Blocos de código ```Device / ```User com o URI da política.
        for (int i = 0; i < lines.Length - 1; i++)
        {
            var fence = lines[i].Trim();
            if (fence is not ("```Device" or "```User")) continue;
            var uri = lines[i + 1].Trim();
            var m = CspUri().Match(uri);
            if (!m.Success) continue;
            if (m.Groups[1].Value == "Device") policy.DeviceUri = uri; else policy.UserUri = uri;
            policy.Area = m.Groups[2].Value;
            policy.Name = m.Groups[3].Value;
        }
        if (policy.DeviceUri is null && policy.UserUri is null) return null;

        // Tabela Scope | Editions | Applicable OS.
        for (int i = 0; i < lines.Length - 2; i++)
        {
            if (!lines[i].StartsWith("| Scope", StringComparison.Ordinal)) continue;
            var cells = SplitRow(lines[i + 2]);
            if (cells.Count < 3) break;
            foreach (var (ok, item) in CheckItems(cells[0]))
            {
                if (item.StartsWith("Device", StringComparison.OrdinalIgnoreCase)) policy.ScopeDevice = ok;
                if (item.StartsWith("User", StringComparison.OrdinalIgnoreCase)) policy.ScopeUser = ok;
            }
            foreach (var (ok, item) in CheckItems(cells[1]))
            {
                if (ok) policy.Editions.Add(item);
                else (policy.NotEditions ??= []).Add(item);
            }
            foreach (var (ok, item) in CheckItems(cells[2]))
                if (ok) policy.Applicability.Add(StripLinks(item));
            break;
        }
        (policy.Win11MinBuild, policy.Win11MinUbr, policy.InsiderOnly) = ComputeWin11MinBuild(policy.Applicability);

        // Tabelas de propriedades, valores permitidos e mapeamento GP/ADMX.
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("**Description framework properties**", StringComparison.Ordinal))
            {
                foreach (var row in ReadTable(lines, i + 1))
                {
                    if (row.Count < 2) continue;
                    var value = Unquote(row[1]);
                    switch (row[0])
                    {
                        case "Format": policy.Format = value; break;
                        case "Default Value": policy.DefaultValue = value; break;
                        case "Allowed Values": policy.AllowedRange = value; break;
                    }
                }
            }
            else if (line.StartsWith("**Allowed values**", StringComparison.Ordinal))
            {
                foreach (var row in ReadTable(lines, i + 1))
                {
                    if (row.Count < 2) continue;
                    var raw = Unquote(row[0]);
                    bool isDefault = raw.Contains("(Default)", StringComparison.OrdinalIgnoreCase);
                    var value = raw.Replace("(Default)", "", StringComparison.OrdinalIgnoreCase).Trim();
                    (policy.AllowedValues ??= []).Add(new CspAllowedValue
                    {
                        Value = value,
                        IsDefault = isDefault,
                        Description = Truncate(StripLinks(row[1])),
                    });
                    foreach (Match m in EditionSentence().Matches(StripLinks(row[1])))
                        AddNote(policy, $"[{value}] {m.Value.Trim()}");
                }
            }
            else if (line.StartsWith("**Group policy mapping**", StringComparison.Ordinal) || line.StartsWith("**ADMX mapping**", StringComparison.Ordinal))
            {
                var mapping = new CspGpMapping();
                foreach (var row in ReadTable(lines, i + 1))
                {
                    if (row.Count < 2) continue;
                    var value = Unquote(row[1]);
                    switch (row[0])
                    {
                        case "Name": mapping.Name = value.Replace("\\_", "_"); break;
                        case "Friendly Name": mapping.FriendlyName = value; break;
                        case "Location": mapping.Location = value; break;
                        case "Path": mapping.Path = value; break;
                        case "Registry Key Name": mapping.RegistryKey = value; break;
                        case "Registry Value Name": mapping.RegistryValueName = value.Replace("\\_", "_"); break;
                        case "ADMX File Name": mapping.AdmxFile = value; break;
                    }
                }
                if (mapping.Name is not null || mapping.RegistryKey is not null) (policy.GpMappings ??= []).Add(mapping);
            }
            else if (!line.StartsWith('|'))
            {
                var text = StripLinks(line);
                foreach (Match m in EditionSentence().Matches(text)) AddNote(policy, m.Value.Trim());
                if (DeprecatedSentence().IsMatch(text)) policy.Deprecated = true;
            }
        }
        policy.AdmxBacked = policy.Area.StartsWith("ADMX_", StringComparison.OrdinalIgnoreCase)
            || string.Equals(policy.Format, "chr (string)", StringComparison.OrdinalIgnoreCase) && policy.GpMappings is not null
               && lines.Any(l => l.Contains("ADMX-backed policy", StringComparison.OrdinalIgnoreCase));
        return policy;
    }

    private static void AddNote(CspPolicy policy, string note)
    {
        note = Truncate(note);
        policy.EditionNotes ??= [];
        if (!policy.EditionNotes.Contains(note)) policy.EditionNotes.Add(note);
    }

    /// <summary>
    /// Build mínimo no Windows 11 a partir das linhas "Applicable OS":
    /// - qualquer linha com build &lt; 22000 ("Windows 10 ... and later") vale para todo o Windows 11 → null;
    /// - senão, o menor build ≥ 22000 (e o UBR, quando exige uma KB);
    /// - só "Windows Insider Preview" → InsiderOnly.
    /// </summary>
    public static (int? Build, int? Ubr, bool InsiderOnly) ComputeWin11MinBuild(IEnumerable<string> applicability)
    {
        var list = applicability.ToList();
        (int Build, int Ubr)? best = null;
        bool coversAllWin11 = false, insider = false;
        foreach (var entry in list)
        {
            var m = BuildRef().Match(entry);
            if (!m.Success)
            {
                if (entry.Contains("Insider", StringComparison.OrdinalIgnoreCase)) insider = true;
                continue;
            }
            int build = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            int ubr = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            if (build < 22000) { coversAllWin11 = true; continue; }
            if (best is null || build < best.Value.Build || (build == best.Value.Build && ubr < best.Value.Ubr))
                best = (build, ubr);
        }
        if (coversAllWin11) return (null, null, false);
        if (best is { } b) return (b.Build, b.Ubr == 0 ? null : b.Ubr, false);
        return (null, null, insider);
    }

    private static IEnumerable<(bool Ok, string Item)> CheckItems(string cell)
    {
        foreach (Match m in CheckItem().Matches(cell))
            yield return (m.Groups[1].Value == "✅", WebUtility.HtmlDecode(m.Groups[2].Value).Trim());
    }

    /// <summary>Lê as linhas de uma tabela Markdown que começa após <paramref name="from"/> (pula o cabeçalho e o separador).</summary>
    private static IEnumerable<List<string>> ReadTable(string[] lines, int from)
    {
        int i = from;
        while (i < lines.Length && string.IsNullOrWhiteSpace(lines[i])) i++;
        if (i >= lines.Length || !lines[i].TrimStart().StartsWith('|')) yield break;
        i += 2; // cabeçalho + separador
        for (; i < lines.Length && lines[i].TrimStart().StartsWith('|'); i++)
            yield return SplitRow(lines[i]);
    }

    private static List<string> SplitRow(string row)
    {
        var t = row.Trim();
        if (t.StartsWith('|')) t = t[1..];
        if (t.EndsWith('|')) t = t[..^1];
        return t.Split(" | ").Select(c => WebUtility.HtmlDecode(c.Trim())).ToList();
    }

    private static string Unquote(string s) => s.Replace("`", "").Trim();

    private static string StripLinks(string s) => MdLink().Replace(s, "$1");

    private static string Truncate(string s)
    {
        s = s.Trim();
        return s.Length <= MaxDescription ? s : s[..(MaxDescription - 1)].TrimEnd() + "…";
    }

    // ---------------- persistência ----------------

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Monta a referência ordenada de forma determinística (para diffs limpos no git).</summary>
    public static CspReference Build(IEnumerable<(CspPage Page, List<CspPolicy> Policies)> pages)
    {
        var list = pages.ToList();
        return new CspReference
        {
            Pages = [.. list.Select(p => p.Page).OrderBy(p => p.Page, StringComparer.Ordinal)],
            Policies = [.. list.SelectMany(p => p.Policies)
                .OrderBy(p => p.Area, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Page, StringComparer.Ordinal)],
        };
    }

    public static string Serialize(CspReference reference) =>
        JsonSerializer.Serialize(reference, JsonOptions).Replace("\r\n", "\n") + "\n";

    public static CspReference Load(string file) =>
        JsonSerializer.Deserialize<CspReference>(File.ReadAllText(file), JsonOptions)
        ?? throw new InvalidDataException($"referência vazia: {file}");
}
