using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace Debloater.Coverage;

/// <summary>One parser per tool format. All of them return only targets (names and paths).</summary>
public static partial class Parsers
{
    public static List<Target> Parse(string parser, IReadOnlyList<(string Path, string Text)> files) => parser switch
    {
        "winutil" => WinUtil(files),
        "privacysexy" => PrivacySexy(files),
        "win11debloat" => Win11Debloat(files),
        "sophia" => Scripts(files),
        "csharp" => CSharp(files),
        "playbook" => Playbook(files),
        _ => throw new InvalidOperationException($"unknown parser '{parser}'."),
    };

    private static List<Target> Dedup(IEnumerable<Target> targets) =>
        targets.GroupBy(t => (t.Kind, t.Key)).Select(g => g.First()).ToList();

    // ------------------------------------------------------------ generic

    /// <summary>.reg, .ps1/.psm1/.cmd/.bat and .yml files with commands: text extractors.</summary>
    public static List<Target> Scripts(IEnumerable<(string Path, string Text)> files)
    {
        var list = new List<Target>();
        foreach (var (path, text) in files)
        {
            if (path.EndsWith(".reg", StringComparison.OrdinalIgnoreCase)) list.AddRange(Extractors.FromRegFile(text));
            else list.AddRange(Extractors.FromScript(text));
        }
        return Dedup(list);
    }

    public static List<Target> CSharp(IReadOnlyList<(string Path, string Text)> files)
    {
        var cs = files.Where(f => f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToList();
        var constants = Extractors.CsConstants(cs.Select(f => f.Text));
        var list = cs.SelectMany(f => Extractors.FromCSharp(f.Text, constants)).ToList();
        list.AddRange(Scripts(files.Where(f => !f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))));
        return Dedup(list);
    }

    // ------------------------------------------------------------ WinUtil (config/*.json)

    private static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : [];

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static List<Target> WinUtil(IEnumerable<(string Path, string Text)> files)
    {
        var list = new List<Target>();
        var docOptions = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
        foreach (var (path, text) in files)
        {
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) { list.AddRange(Scripts([(path, text)])); continue; }
            // WinUtil's tweaks.json has raw line breaks inside the script strings.
            var fixedText = Regex.Replace(text, "\"(?:[^\"\\\\]|\\\\.)*\"", m => m.Value.Replace("\r", "").Replace("\n", "\\n").Replace("\t", "\\t"), RegexOptions.Singleline);
            using var doc = JsonDocument.Parse(fixedText, docOptions);
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                var e = entry.Value;
                foreach (var r in Array(e, "registry"))
                    if (Targets.Registry(Str(r, "Path"), Str(r, "Name")) is { } t) list.Add(t);
                foreach (var s in Array(e, "service"))
                    if (Targets.Service(Str(s, "Name")) is { } t) list.Add(t);
                foreach (var s in Array(e, "ScheduledTask"))
                    if (Targets.Task(Str(s, "Name")) is { } t) list.Add(t);
                foreach (var a in Array(e, "appx"))
                    if (Targets.Appx(a.ValueKind == JsonValueKind.String ? a.GetString() : Str(a, "Name")) is { } t) list.Add(t);
                foreach (var f in Array(e, "feature"))
                    if (f.ValueKind == JsonValueKind.String && Targets.Feature(f.GetString()) is { } t) list.Add(t);
                foreach (var script in Array(e, "InvokeScript"))
                    if (script.ValueKind == JsonValueKind.String) list.AddRange(Extractors.FromScript(script.GetString()!));
            }
        }
        return Dedup(list);
    }

    // ------------------------------------------------------------ privacy.sexy (windows.yaml)

    /// <summary>
    /// Walks the collection and reads only the function calls with parameters (targets). Names, documentation and
    /// scripts of the collection (AGPL-3.0) are not kept.
    /// </summary>
    public static List<Target> PrivacySexy(IEnumerable<(string Path, string Text)> files)
    {
        var list = new List<Target>();
        foreach (var (_, text) in files)
        {
            var yaml = new YamlStream();
            yaml.Load(new StringReader(text));
            foreach (var doc in yaml.Documents) Walk(doc.RootNode, list);
        }
        return Dedup(list);

        static void Walk(YamlNode node, List<Target> list)
        {
            switch (node)
            {
                case YamlMappingNode map:
                    if (Scalar(map, "function") is { } fn && map.Children.TryGetValue(new YamlScalarNode("parameters"), out var p) && p is YamlMappingNode pars)
                        Call(fn, pars, list);
                    if (Scalar(map, "code") is { } code) list.AddRange(Extractors.FromScript(code));
                    foreach (var child in map.Children.Values) Walk(child, list);
                    break;
                case YamlSequenceNode seq:
                    foreach (var child in seq.Children) Walk(child, list);
                    break;
            }
        }

        static string? Scalar(YamlMappingNode map, string key) =>
            map.Children.TryGetValue(new YamlScalarNode(key), out var v) && v is YamlScalarNode s ? s.Value : null;

        static void Call(string fn, YamlMappingNode p, List<Target> list)
        {
            string? P(string k) => Scalar(p, k);
            Target? t = fn switch
            {
                "SetRegistryValue" or "DeleteRegistryValue" or "SetRegistryValueAsTrustedInstaller" => Targets.Registry(P("keyPath"), P("valueName")),
                "SoftDeleteRegistryKey" or "DeleteRegistryKey" => Targets.Registry(P("keyPath"), null),
                "SetEdgePolicyViaRegistry" => Targets.Registry(@"HKLM\SOFTWARE\Policies\Microsoft\Edge", P("valueName")),
                "SetEdgeUpdatePolicyViaRegistry" => Targets.Registry(@"HKLM\SOFTWARE\Policies\Microsoft\EdgeUpdate", P("valueName")),
                "SetLegacyEdgePolicyViaRegistry" => Targets.Registry(@"HKLM\SOFTWARE\Policies\Microsoft\MicrosoftEdge\" + P("keyPath"), P("valueName")),
                "DisableService" or "DisableServiceInRegistry" or "DisablePerUserService" or "StopService" => Targets.Service(P("serviceName")),
                "DisableScheduledTask" => Targets.Task(P("taskPathPattern"), P("taskNamePattern")),
                "UninstallStoreApp" or "UninstallNonRemovableStoreApp" or "UninstallNonRemovableStoreAppWithCleanup" => Targets.Appx(P("packageName")),
                "DisableWindowsFeature" or "DisableOptionalFeature" => Targets.Feature(P("featureName")),
                "UninstallCapability" => Targets.Capability(P("capabilityName")),
                _ => null,
            };
            if (t is not null) list.Add(t);
            if (fn == "BlockUWPAccessViaGPO" && Targets.Registry(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", P("policyName")) is { } g)
                list.Add(g);
            if (fn == "BlockUWPAccessViaConsentStore" && P("appCapability") is { } cap)
                foreach (var hive in new[] { "HKLM", "HKCU" })
                    if (Targets.Registry($@"{hive}\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\{cap}", "Value") is { } c)
                        list.Add(c);
            if (fn is "RunPowerShell" or "RunInlineCode" or "RunPowerShellWithWindowsVersionConstraints" or "RunPowerShellWithOptionalElevation"
                && (P("code") ?? P("script")) is { } code)
                list.AddRange(Extractors.FromScript(code));
        }
    }

    // ------------------------------------------------------------ Win11Debloat

    public static List<Target> Win11Debloat(IReadOnlyList<(string Path, string Text)> files)
    {
        var list = new List<Target>();
        foreach (var (path, text) in files)
        {
            if (path.EndsWith("Apps.json", StringComparison.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(text);
                foreach (var app in Array(doc.RootElement, "Apps"))
                {
                    var method = Str(app, "RemovalMethod");
                    if (method is not null && !method.Equals("Appx", StringComparison.OrdinalIgnoreCase)) continue;
                    var id = app.TryGetProperty("AppId", out var a) ? a : default;
                    if (id.ValueKind == JsonValueKind.String) { if (Targets.Appx(id.GetString()) is { } t) list.Add(t); }
                    else if (id.ValueKind == JsonValueKind.Array)
                        foreach (var x in id.EnumerateArray()) if (Targets.Appx(x.GetString()) is { } t) list.Add(t);
                }
            }
            else if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            else list.AddRange(Scripts([(path, text)]));
        }
        return Dedup(list);
    }

    // ------------------------------------------------------------ AME playbooks (AtlasOS, ReviOS)

    [GeneratedRegex(@"!(registryValue|registryKey|service|scheduledTask|appx|taskKill)\s*:\s*(\{[^}]*\}|(?:\r?\n(?![ \t]*-[ \t])[ \t]+[^\r\n]*)+)")]
    private static partial Regex PlaybookAction();

    [GeneratedRegex(@"\b(path|value|name|operation|type)\s*:\s*(?:'((?:[^']|'')*)'|""((?:[^""\\]|\\.)*)""|([^,}\r\n#]+))")]
    private static partial Regex PlaybookField();

    public static List<Target> Playbook(IReadOnlyList<(string Path, string Text)> files)
    {
        var list = new List<Target>();
        foreach (var (path, text) in files)
        {
            if (!path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            {
                list.AddRange(Scripts([(path, text)]));
                continue;
            }
            foreach (Match m in PlaybookAction().Matches(text))
            {
                var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match f in PlaybookField().Matches(m.Groups[2].Value))
                {
                    var v = f.Groups[2].Success ? f.Groups[2].Value.Replace("''", "'") : f.Groups[3].Success ? Regex.Unescape(f.Groups[3].Value) : f.Groups[4].Value.Trim();
                    fields.TryAdd(f.Groups[1].Value, v);
                }
                fields.TryGetValue("path", out var p);
                fields.TryGetValue("value", out var val);
                fields.TryGetValue("name", out var name);
                Target? t = m.Groups[1].Value switch
                {
                    "registryValue" => Targets.Registry(p, val ?? ""),
                    "registryKey" => Targets.Registry(p, null),
                    "service" => Targets.Service(name),
                    "scheduledTask" => Targets.Task(p),
                    "appx" => Targets.Appx(name),
                    _ => null,
                };
                if (t is not null) list.Add(t);
            }
            // Embedded commands (!powerShell / !cmd): script extractor on the YAML itself.
            list.AddRange(Extractors.FromScript(text));
        }
        return Dedup(list);
    }
}
