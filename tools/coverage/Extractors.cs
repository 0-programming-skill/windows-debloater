using System.Text;
using System.Text.RegularExpressions;

namespace Debloater.Coverage;

/// <summary>
/// Extratores heurísticos de alvos a partir de texto: arquivos .reg, PowerShell, cmd/batch e C#.
/// Não entendem a linguagem; procuram as formas usuais de mexer em registro, serviços, tarefas,
/// pacotes appx, recursos opcionais e capacidades. O resultado é aproximado (ver docs/coverage).
/// </summary>
public static partial class Extractors
{
    /// <summary>Lê texto respeitando a BOM (os .reg costumam ser UTF-16 LE).</summary>
    public static string ReadText(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        return Encoding.UTF8.GetString(bytes);
    }

    // ---------------------------------------------------------------- .reg

    [GeneratedRegex(@"^\s*\[(-?)(HKEY_[^\]]+)\]\s*$")]
    private static partial Regex RegSection();

    [GeneratedRegex(@"^\s*(@|""(?:[^""\\]|\\.)*"")\s*=\s*(.*)$")]
    private static partial Regex RegValue();

    public static IEnumerable<Target> FromRegFile(string text)
    {
        string? key = null;
        bool deleteKey = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var s = RegSection().Match(line);
            if (s.Success)
            {
                key = s.Groups[2].Value;
                deleteKey = s.Groups[1].Value == "-";
                if (deleteKey && Targets.Registry(key, null) is { } k) yield return k;
                continue;
            }
            if (key is null || deleteKey) continue;
            var v = RegValue().Match(line);
            if (!v.Success) continue;
            var name = v.Groups[1].Value == "@" ? "" : Regex.Unescape(v.Groups[1].Value[1..^1]);
            if (Targets.Registry(key, name) is { } t) yield return t;
        }
    }

    // ---------------------------------------------------------------- PowerShell / cmd

    /// <summary>Valor de um parâmetro nomeado numa linha de comando ("-Path 'x'", "-Name x").</summary>
    private static string? Param(string line, params string[] names)
    {
        foreach (var n in names)
        {
            var m = Regex.Match(line, @"(?<![\w-])-" + n + @"(?::|\s+)(?:""([^""]*)""|'([^']*)'|([^\s;|)`]+))", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var val = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
                return val;
            }
        }
        return null;
    }

    /// <summary>Primeiros argumentos posicionais depois do comando (sem os que começam com "-").</summary>
    private static List<string> Positional(string afterCommand)
    {
        var list = new List<string>();
        foreach (Match m in Regex.Matches(afterCommand, @"""([^""]*)""|'([^']*)'|([^\s;|)]+)"))
        {
            var v = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            if (v.StartsWith('-')) break; // depois do primeiro parâmetro nomeado, a ordem não é confiável
            list.Add(v);
            if (list.Count == 3) break;
        }
        return list;
    }

    [GeneratedRegex(@"\b(New-ItemProperty|Set-ItemProperty|Remove-ItemProperty|Set-RegistryValue|Set-Registry|Set-Policy|Set-RegValue|Remove-RegistryValue)\b(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex PsItemProperty();

    [GeneratedRegex(@"\breg(?:\.exe)?\s+(add|delete)\s+(""[^""]+""|\S+)(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex RegExe();

    [GeneratedRegex(@"\b(?:Set-Service|Stop-Service|Get-Service|Get-CimInstance\s+Win32_Service|Set-ServiceStartup)\b(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex PsService();

    [GeneratedRegex(@"\bsc(?:\.exe)?\s+(?:config|stop)\s+""?([A-Za-z0-9_.\-]+)""?", RegexOptions.IgnoreCase)]
    private static partial Regex ScExe();

    [GeneratedRegex(@"\b(?:Disable-ScheduledTask|Get-ScheduledTask|Unregister-ScheduledTask)\b(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex PsTask();

    [GeneratedRegex(@"\bschtasks(?:\.exe)?\s+/(?:change|delete|end)\b.*?/tn\s+(""[^""]+""|\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex SchTasks();

    [GeneratedRegex(@"\b(?:Get-AppxPackage|Remove-AppxPackage|Get-AppxProvisionedPackage)\b(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex PsAppx();

    [GeneratedRegex(@"(?:-FeatureName|/featurename:)\s*""?([A-Za-z0-9_.\-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex FeatureArg();

    [GeneratedRegex(@"(?:Remove-WindowsCapability|Get-WindowsCapability)\b.*?-Name\s+""?'?([A-Za-z0-9_.\-~]+)|/(?:Remove|Disable)-Capability\s+/CapabilityName:""?([A-Za-z0-9_.\-~]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CapabilityArg();

    [GeneratedRegex(@"\$(\w*)\s*=\s*@\(([^)]*)\)", RegexOptions.Singleline)]
    private static partial Regex PsArray();

    [GeneratedRegex(@"""([^""\r\n]+)""|'([^'\r\n]+)'")]
    private static partial Regex QuotedString();

    /// <summary>Extrai alvos de um script PowerShell ou cmd/batch.</summary>
    public static IEnumerable<Target> FromScript(string text)
    {
        var results = new List<Target>();
        // Junta continuações de linha (` no PowerShell, ^ no cmd).
        var joined = Regex.Replace(text, @"[`^]\r?\n\s*", " ");
        foreach (var raw in joined.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('#') || line.StartsWith("::") || line.StartsWith("rem ", StringComparison.OrdinalIgnoreCase)) continue;

            var ip = PsItemProperty().Match(line);
            if (ip.Success)
            {
                var rest = ip.Groups[2].Value;
                var path = Param(rest, "Path", "LiteralPath", "Key", "KeyPath");
                var name = Param(rest, "Name", "ValueName");
                var pos = Positional(rest);
                path ??= pos.ElementAtOrDefault(0);
                if (name is null && pos.Count > 1 && path == pos[0]) name = pos[1];
                if (ip.Groups[1].Value.Equals("Set-Policy", StringComparison.OrdinalIgnoreCase))
                {
                    var scope = Param(rest, "Scope");
                    if (path is not null && !path.StartsWith("HK", StringComparison.OrdinalIgnoreCase))
                        path = (scope?.Equals("User", StringComparison.OrdinalIgnoreCase) == true ? "HKCU\\" : "HKLM\\") + path;
                }
                if (name is not null && Targets.Registry(path, name) is { } t) results.Add(t);
                continue;
            }

            var re = RegExe().Match(line);
            if (re.Success)
            {
                var rest = re.Groups[3].Value;
                var v = Regex.Match(rest, @"/v\s+(""[^""]*""|\S+)", RegexOptions.IgnoreCase);
                var ve = Regex.IsMatch(rest, @"/ve\b", RegexOptions.IgnoreCase);
                string? name = v.Success ? v.Groups[1].Value.Trim('"') : ve ? "" : null;
                var t = Targets.Registry(re.Groups[2].Value.Trim('"'), name);
                if (t is not null) results.Add(t);
                continue;
            }

            foreach (Match m in ScExe().Matches(line))
                if (Targets.Service(m.Groups[1].Value) is { } t) results.Add(t);

            var ps = PsService().Match(line);
            if (ps.Success)
            {
                var rest = ps.Groups[1].Value;
                var name = Param(rest, "Name", "ServiceName") ?? Positional(rest).FirstOrDefault();
                var filter = Regex.Match(rest, @"Name\s*=\s*'([^']+)'", RegexOptions.IgnoreCase);
                if (filter.Success) name = filter.Groups[1].Value;
                if (name is not null)
                    foreach (var n in name.Split(','))
                        if (Targets.Service(n) is { } t) results.Add(t);
            }

            var pt = PsTask().Match(line);
            if (pt.Success)
            {
                var rest = pt.Groups[1].Value;
                var name = Param(rest, "TaskName");
                var path = Param(rest, "TaskPath");
                if (name is null)
                {
                    var pos = Positional(rest);
                    if (pos.Count > 0) name = pos[0];
                }
                if (name is not null)
                    foreach (var n in name.Split(','))
                        if (Targets.Task(path, n.Trim().Trim('"', '\'')) is { } t) results.Add(t);
            }

            foreach (Match m in SchTasks().Matches(line))
                if (Targets.Task(m.Groups[1].Value.Trim('"')) is { } t) results.Add(t);

            var pa = PsAppx().Match(line);
            if (pa.Success)
            {
                var rest = pa.Groups[1].Value;
                // Nome posicional pode vir depois de chaves como -AllUsers: pega o primeiro token que parece um pacote.
                var name = Param(rest, "Name", "PackageName")
                    ?? Regex.Matches(rest, @"(?<![\w-])['""]?\*?([A-Za-z0-9][\w\-]*\.[\w.\-]+?)\*?['""]?(?=[\s|;)]|$)")
                        .Select(m => m.Groups[1].Value).FirstOrDefault();
                if (name is not null && Targets.Appx(name) is { } t) results.Add(t);
            }

            foreach (Match m in FeatureArg().Matches(line))
                if (Targets.Feature(m.Groups[1].Value) is { } t) results.Add(t);
            foreach (Match m in CapabilityArg().Matches(line))
                if (Targets.Capability(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) is { } t) results.Add(t);
        }

        // Listas: $Apps = @('Microsoft.BingNews', ...), $Tasks = @(...), $Services = @(...).
        foreach (Match m in PsArray().Matches(text))
        {
            var var = m.Groups[1].Value.ToLowerInvariant();
            var body = Regex.Replace(m.Groups[2].Value, @"#.*", "");
            var items = QuotedString().Matches(body).Select(q => q.Groups[1].Success ? q.Groups[1].Value : q.Groups[2].Value);
            foreach (var item in items)
            {
                Target? t = null;
                if (var.Contains("task")) t = Targets.Task(item);
                else if (var.Contains("service") || var.Contains("svc")) t = Targets.Service(item);
                else if (var.Contains("app") || var.Contains("package") || var.Contains("bloat")) t = Targets.Appx(item);
                else if (var.Contains("feature")) t = Targets.Feature(item);
                else if (var.Contains("capabilit")) t = Targets.Capability(item);
                if (t is not null) results.Add(t);
            }
        }
        return results;
    }

    // ---------------------------------------------------------------- C#

    [GeneratedRegex(@"const\s+string\s+(\w+)\s*=\s*(@?)""((?:[^""\\]|\\.|"""")*)""")]
    private static partial Regex CsConst();

    /// <summary>Literal de cadeia C# (verbatim, normal) ou identificador; com a posição.</summary>
    [GeneratedRegex(@"@""((?:[^""]|"""")*)""|(?<!@)""((?:[^""\\\r\n]|\\.)*)""|\b([A-Z_][A-Z0-9_]{2,})\b")]
    private static partial Regex CsToken();

    private static string CsUnescape(string body, bool verbatim) =>
        verbatim ? body.Replace("\"\"", "\"") : Regex.Replace(body, @"\\(.)", m => m.Groups[1].Value switch { "n" => "\n", "t" => "\t", var c => c });

    /// <summary>Constantes de cadeia ("const string X = ...") de um conjunto de arquivos C#.</summary>
    public static Dictionary<string, string> CsConstants(IEnumerable<string> texts)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var text in texts)
            foreach (Match m in CsConst().Matches(text))
                map.TryAdd(m.Groups[1].Value, CsUnescape(m.Groups[3].Value, m.Groups[2].Value == "@"));
        return map;
    }

    private static bool LooksLikeRegistryPath(string s) =>
        Regex.IsMatch(s, @"^(HKEY_|HKLM|HKCU|HKCR|HKU\b|Registry::)", RegexOptions.IgnoreCase) && s.Contains('\\');

    [GeneratedRegex(@"RegistryHive\.(LocalMachine|CurrentUser|ClassesRoot|Users)\s*,\s*(@?""(?:[^""\\]|\\.)*""|\w+)\s*(?:,\s*(@?""(?:[^""\\]|\\.)*""|\w+))?")]
    private static partial Regex CsHiveCall();

    [GeneratedRegex(@"\b(?:AppxPackageName|PackageName|AppxName)\s*=\s*\[?\s*(@?""[^""]+""(?:\s*,\s*@?""[^""]+"")*)")]
    private static partial Regex CsAppxProp();

    [GeneratedRegex(@"\b(?:OptionalFeatureName|FeatureName)\s*=\s*@?""([^""]+)""")]
    private static partial Regex CsFeatureProp();

    [GeneratedRegex(@"\bCapabilityName\s*=\s*@?""([^""]+)""")]
    private static partial Regex CsCapabilityProp();

    [GeneratedRegex(@"\b(?:ServiceName|new\s+ServiceItem\s*\(|ServiceHelper\.\w+\s*\()\s*=?\s*@?""?([A-Za-z0-9_.\-]+)""?")]
    private static partial Regex CsServiceProp();

    [GeneratedRegex(@"\bTaskPath\s*=\s*@?""([^""]+)""(?:[\s\S]{0,200}?\bTaskName\s*=\s*@?""([^""]+)"")?")]
    private static partial Regex CsTaskProp();

    /// <summary>
    /// Extrai alvos de código C#: um literal (ou constante) que é caminho de registro seguido, a poucos
    /// caracteres, do literal com o nome do valor (KeyPath/ValueName, RegistryItem(path, name, ...)),
    /// chamadas com RegistryHive.X, propriedades de appx/recurso/capacidade/serviço/tarefa e
    /// cadeias que parecem caminhos do Agendador (\Microsoft\Windows\...). Scripts PowerShell
    /// embutidos em literais também passam pelo extrator de scripts.
    /// </summary>
    public static IEnumerable<Target> FromCSharp(string text, IReadOnlyDictionary<string, string> constants)
    {
        var results = new List<Target>();
        string? Resolve(Match m, out bool isIdentifier)
        {
            isIdentifier = false;
            if (m.Groups[1].Success) return CsUnescape(m.Groups[1].Value, true);
            if (m.Groups[2].Success) return CsUnescape(m.Groups[2].Value, false);
            isIdentifier = true;
            return constants.TryGetValue(m.Groups[3].Value, out var v) ? v : null;
        }

        // Ignora comentários de linha para não pegar exemplos.
        var code = Regex.Replace(text, @"(?m)^\s*//.*$", "");
        var tokens = CsToken().Matches(code).ToList();
        for (int i = 0; i < tokens.Count; i++)
        {
            var value = Resolve(tokens[i], out _);
            if (value is null) continue;
            if (LooksLikeRegistryPath(value))
            {
                string? name = null;
                for (int j = i + 1; j < tokens.Count && tokens[j].Index - tokens[i].Index < 260; j++)
                {
                    var next = Resolve(tokens[j], out _);
                    if (next is null) continue; // identificador sem constante conhecida
                    if (LooksLikeRegistryPath(next)) break;
                    if (next.Length is > 0 and < 100 && !next.Contains('\\') && !next.Contains('\n')) { name = next; }
                    break;
                }
                var t = Targets.Registry(value, name);
                if (t is not null) results.Add(t);
            }
            else if (value.StartsWith("\\Microsoft\\Windows\\", StringComparison.OrdinalIgnoreCase) && !value.EndsWith('\\'))
            {
                if (Targets.Task(value) is { } t) results.Add(t);
            }
            if (value.Contains("Set-ItemProperty", StringComparison.OrdinalIgnoreCase) || value.Contains("reg add", StringComparison.OrdinalIgnoreCase)
                || value.Contains("Get-AppxPackage", StringComparison.OrdinalIgnoreCase) || value.Contains("ScheduledTask", StringComparison.OrdinalIgnoreCase)
                || value.Contains("sc config", StringComparison.OrdinalIgnoreCase) || value.Contains("schtasks", StringComparison.OrdinalIgnoreCase))
                results.AddRange(FromScript(value));
        }

        foreach (Match m in CsHiveCall().Matches(code))
        {
            string? Arg(Group g)
            {
                if (!g.Success) return null;
                var s = g.Value;
                if (s.StartsWith("@\"")) return CsUnescape(s[2..^1], true);
                if (s.StartsWith('"')) return CsUnescape(s[1..^1], false);
                return constants.TryGetValue(s, out var v) ? v : null;
            }
            var hive = m.Groups[1].Value switch { "LocalMachine" => "HKLM", "CurrentUser" => "HKCU", "ClassesRoot" => "HKCR", _ => "HKU" };
            var path = Arg(m.Groups[2]);
            if (path is null || LooksLikeRegistryPath(path)) continue;
            var name = Arg(m.Groups[3]);
            if (name is not null && (name.Contains('\\') || name.Length > 100)) name = null;
            if (Targets.Registry(hive + "\\" + path, name) is { } t) results.Add(t);
        }

        foreach (Match m in CsAppxProp().Matches(code))
            foreach (Match q in Regex.Matches(m.Groups[1].Value, @"""([^""]+)"""))
                if (Targets.Appx(q.Groups[1].Value) is { } t) results.Add(t);
        foreach (Match m in CsFeatureProp().Matches(code))
            if (Targets.Feature(m.Groups[1].Value) is { } t) results.Add(t);
        foreach (Match m in CsCapabilityProp().Matches(code))
            if (Targets.Capability(m.Groups[1].Value) is { } t) results.Add(t);
        foreach (Match m in CsServiceProp().Matches(code))
            if (Targets.Service(m.Groups[1].Value) is { } t) results.Add(t);
        foreach (Match m in CsTaskProp().Matches(code))
            if (Targets.Task(m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value : null) is { } t) results.Add(t);
        return results;
    }
}
