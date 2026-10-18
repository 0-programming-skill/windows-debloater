using System.Text.RegularExpressions;
using Debloater.Core.Model;

namespace Debloater.Core.Fingerprint;

/// <summary>Candidate kinds (what layer 2 should investigate).</summary>
public static class CandidateKinds
{
    public const string PolicyPath = "policyPath";
    public const string RegistryPath = "registryPath";
    public const string CspPath = "cspPath";
    public const string SubscribedContent = "subscribedContent";
    public const string ValueName = "valueName";
    public const string NearName = "nearName";
    public const string Wnf = "wnf";
    public const string Feature = "feature";
    public const string AdmxPolicy = "admxPolicy";
    public const string Service = "service";
    public const string Task = "task";
    public const string Appx = "appx";
    public const string OptionalFeature = "optionalFeature";
    public const string Capability = "capability";

    public static string FromStringKind(StringKind kind) => kind switch
    {
        StringKind.PolicyPath => PolicyPath,
        StringKind.RegistryPath => RegistryPath,
        StringKind.CspPath => CspPath,
        StringKind.SubscribedContent => SubscribedContent,
        StringKind.ValueName => ValueName,
        StringKind.Wnf => Wnf,
        StringKind.Feature => Feature,
        _ => NearName,
    };

    public static bool IsString(string kind) =>
        kind is PolicyPath or RegistryPath or CspPath or SubscribedContent or ValueName or NearName or Wnf or Feature;
}

/// <summary>Keys and value names the catalog already uses (for "already covered" and "near a known key").</summary>
public sealed class CatalogRegistryIndex
{
    /// <summary>Normalized path (no hive, case-insensitive) → tweak ids.</summary>
    public Dictionary<string, List<string>> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Value name → tweak ids.</summary>
    public Dictionary<string, List<string>> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>(normalized path, name) of each registry action, with its tweak.</summary>
    public List<(string Path, string Name, Tweak Tweak)> Actions { get; } = [];

    public static CatalogRegistryIndex Build(IEnumerable<Tweak> tweaks)
    {
        var index = new CatalogRegistryIndex();
        foreach (var t in tweaks)
        {
            foreach (var a in t.Actions)
            {
                if (a.Type != ActionSpec.Registry || string.IsNullOrEmpty(a.Path)) continue;
                var path = NormalizeRegistryPath(a.Path);
                Add(index.Paths, path, t.Id);
                if (!string.IsNullOrEmpty(a.Name)) Add(index.Names, a.Name, t.Id);
                index.Actions.Add((path, a.Name ?? "", t));
            }
        }
        return index;
    }

    private static void Add(Dictionary<string, List<string>> d, string key, string id)
    {
        if (!d.TryGetValue(key, out var list)) d[key] = list = [];
        if (!list.Contains(id)) list.Add(id);
    }

    private static readonly string[] HivePrefixes =
    [
        @"HKEY_LOCAL_MACHINE\", @"HKEY_CURRENT_USER\", @"HKEY_USERS\", @"HKEY_CLASSES_ROOT\",
        @"HKLM\", @"HKCU\", @"HKU\", @"HKCR\", @"\Registry\Machine\", @"Registry\Machine\", @"\Registry\User\", @"Registry\User\", @"MACHINE\", @"USER\",
    ];

    /// <summary>Strips the hive (HKLM\, HKEY_CURRENT_USER\, \Registry\Machine\...) and leading/trailing backslashes.</summary>
    public static string NormalizeRegistryPath(string path)
    {
        var p = path.Trim().Trim('\\');
        foreach (var prefix in HivePrefixes)
        {
            var pre = prefix.Trim('\\');
            if (p.StartsWith(pre + "\\", StringComparison.OrdinalIgnoreCase)) { p = p[(pre.Length + 1)..]; break; }
        }
        return p.Trim('\\');
    }

    /// <summary>"Key[Value]" (form used by some binaries) → (key, value); without brackets → (text, null).</summary>
    public static (string Path, string? Name) SplitValueSuffix(string value)
    {
        if (value.EndsWith(']') && value.LastIndexOf('[') is var i and > 0 && i < value.Length - 2)
            return (value[..i], value[(i + 1)..^1]);
        return (value, null);
    }

    /// <summary>Tweaks whose key/value is exactly this candidate.</summary>
    public List<string> CoveredBy(string kind, string value, string? contextPath)
    {
        var ids = new List<string>();
        if (kind is CandidateKinds.PolicyPath or CandidateKinds.RegistryPath)
        {
            if (Paths.TryGetValue(NormalizeRegistryPath(value), out var p)) ids.AddRange(p);
        }
        else if (kind is CandidateKinds.ValueName or CandidateKinds.NearName or CandidateKinds.SubscribedContent)
        {
            if (Names.TryGetValue(value, out var n))
            {
                // With context, it only counts if the key also matches (generic names repeat across different keys).
                if (contextPath is null) ids.AddRange(n);
                else
                {
                    var ctx = NormalizeRegistryPath(contextPath);
                    var exact = Actions.Where(x => x.Name.Equals(value, StringComparison.OrdinalIgnoreCase)
                        && x.Path.Equals(ctx, StringComparison.OrdinalIgnoreCase)).Select(x => x.Tweak.Id).ToList();
                    ids.AddRange(exact.Count > 0 ? exact : n);
                }
            }
        }
        return [.. ids.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }
}

/// <summary>
/// Scores candidates: higher for policy paths, Explorer\Advanced, ContentDeliveryManager
/// and SubscribedContent, names with AI/Copilot/Recall/Widgets/ads/suggestions/telemetry/diagnostics,
/// and strings in the same binary (or the same key) as something in the catalog; lower for generic noise
/// (log, trace, debug, test...). The score is only an investigation order, not proof.
/// </summary>
public static partial class CandidateScorer
{
    public sealed record Input(
        string Value,
        string Kind,
        string? ContextPath = null,
        bool CuratedBinary = false,
        bool NewBinary = false,
        bool Moved = false,
        string? CatalogKeyInSameBinary = null,
        string? CatalogKeySameKeyTweak = null,
        IReadOnlyList<string>? CoveredBy = null,
        string? ExtraText = null,
        string? BinaryPath = null);

    public static int BaseScore(string kind) => kind switch
    {
        CandidateKinds.AdmxPolicy => 55,
        CandidateKinds.PolicyPath => 45,
        CandidateKinds.SubscribedContent => 45,
        CandidateKinds.CspPath => 40,
        CandidateKinds.Service => 45,
        CandidateKinds.Task => 40,
        CandidateKinds.Appx => 40,
        CandidateKinds.ValueName => 25,
        CandidateKinds.RegistryPath => 20,
        CandidateKinds.OptionalFeature => 20,
        CandidateKinds.Capability => 15,
        CandidateKinds.NearName => 12,
        CandidateKinds.Feature => 12,
        CandidateKinds.Wnf => 10,
        _ => 5,
    };

    // Hot words (case-sensitive for acronyms; the rest effectively case-insensitive).
    [GeneratedRegex(@"(?<![a-z])AI(?![a-z])|(?<![a-z])Agent|(?<![a-z])Mcp(?![a-z])|Copilot|Recall|Widget|Advertis|(?<![a-z])Ads(?![a-z])|Suggest|Recommend|Telemetry|Diag|Spotlight|Bing|Feedback|Promot|Sponsor|Consumer|Iris|Edge|Tracking|Census|Tips|Cortana|Search(Box|Highlight)|Chat|Teams|Gaming|GameBar", RegexOptions.CultureInvariant)]
    private static partial Regex HotWordRegex();

    [GeneratedRegex(@"Explorer\\Advanced|ContentDeliveryManager|SubscribedContent|UserProfileEngagement|\\Search\\|\\Feeds|\\Dsh|WindowsCopilot|WindowsAI|\\Start\\|\\Taskbar|DataCollection|AdvertisingInfo|\\Privacy|CloudContent|ActivityHistory|\\Mcp\\|AgentGovernance|IsolationSession|CopilotUpdate|AgentSuggestions", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HotPathRegex();

    [GeneratedRegex(@"(^|[^A-Za-z])(Log|Logs|Logging|Logger|Trace|Tracing|Debug|Etw|Wpp|Test|Tests|Assert|Verbose|Dump|Crash|Perf|Counter|Mock|Fake|Stub|Leak)([^a-z]|$)|(?<=[a-z])(Log|Logging|Trace|Tracing|Debug|Test|Assert|Verbose|Dump|Perf|Mock)([A-Z_]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex NoiseRegex();

    // UI resource names (XAML): near a key by chance, almost never registry values.
    [GeneratedRegex(@"(Brush|Thickness|Margin|Padding|Style|Template|Storyboard|Animation|FontSize|FontWeight|Width|Height|Opacity|Glyph|CornerRadius|Duration)\d*$", RegexOptions.CultureInvariant)]
    private static partial Regex UiResourceRegex();

    [GeneratedRegex(@"\\Classes\\|CLSID|\\Interface\\|TypeLib|\\AppID\\|\\Wow6432Node\\|\\Enum\\|\\Control\\Class\\|\\DeviceClasses\\|\{[0-9A-Fa-f-]{36}\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RegistryNoiseRegex();

    public static (int Score, List<string> Reasons) Score(Input c)
    {
        var reasons = new List<string>();
        int score = BaseScore(c.Kind);
        reasons.Add(KindLabel(c.Kind));

        bool isPathKind = c.Kind is CandidateKinds.PolicyPath or CandidateKinds.RegistryPath or CandidateKinds.CspPath;
        string text = c.Value + (c.ExtraText is null ? "" : " " + c.ExtraText);

        // Hot key in the path itself or in the context (name near a hot key). For names that are
        // only near a key (nearName), proximity is weak evidence: half the bonus.
        bool near = c.Kind == CandidateKinds.NearName;
        var hotPath = HotPathRegex().Match(isPathKind ? c.Value : c.ContextPath ?? c.ExtraText ?? "");
        if (hotPath.Success)
        {
            score += isPathKind ? 25 : near ? 10 : 20;
            reasons.Add($"relevant key ({hotPath.Value.Trim('\\')})");
        }
        if (!isPathKind && c.Kind != CandidateKinds.AdmxPolicy && c.ContextPath is not null && StringClassifier.IsPolicyPath(c.ContextPath))
        {
            score += near ? 5 : 15;
            reasons.Add("near a policy key");
        }

        var hot = HotWordRegex().Matches(text).Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (hot.Count > 0)
        {
            score += Math.Min(30, 20 + 5 * (hot.Count - 1));
            reasons.Add($"keyword: {string.Join(", ", hot)}");
        }

        if (c.CatalogKeySameKeyTweak is not null)
        {
            score += near ? 10 : 25;
            reasons.Add($"same key as a tweak ({c.CatalogKeySameKeyTweak})");
        }
        else if (c.CatalogKeyInSameBinary is not null)
        {
            score += 10;
            reasons.Add($"binary also uses a catalog key ({c.CatalogKeyInSameBinary})");
        }
        if (c.CuratedBinary) { score += 5; reasons.Add("binary from the curated list"); }
        if (c.NewBinary) { score += 5; reasons.Add("new binary"); }

        if (c.Value.Length < 8 && !isPathKind) { score -= 10; reasons.Add("short"); }
        if (NoiseRegex().IsMatch(c.Value)) { score -= 15; reasons.Add("likely noise (log/trace/debug/test)"); }
        if (isPathKind && RegistryNoiseRegex().IsMatch(c.Value)) { score -= 15; reasons.Add("generic key (COM/classes/devices)"); }
        if (near && UiResourceRegex().IsMatch(c.Value)) { score -= 15; reasons.Add("UI resource (XAML)"); }
        if (!isPathKind && c.Value.Count(ch => ch == '_') >= 2) { score -= 20; reasons.Add("looks like a string resource key (several _)"); }
        if (c.BinaryPath is not null && c.BinaryPath.Contains("Microsoft-Edge-WebView", StringComparison.OrdinalIgnoreCase))
        {
            score -= 20;
            reasons.Add("WebView2/Edge (updates separately from Windows)");
        }

        if (c.Moved)
        {
            score = (int)Math.Round(score * 0.3);
            reasons.Add("already existed, moved to another binary (weak)");
        }
        if (c.CoveredBy is { Count: > 0 })
        {
            score -= 40;
            reasons.Add($"already covered: {string.Join(", ", c.CoveredBy)}");
        }
        return (Math.Max(0, score), reasons);
    }

    public static string KindLabel(string kind) => kind switch
    {
        CandidateKinds.AdmxPolicy => "new ADMX policy",
        CandidateKinds.PolicyPath => "policy path",
        CandidateKinds.RegistryPath => "registry path",
        CandidateKinds.CspPath => "CSP path",
        CandidateKinds.SubscribedContent => "SubscribedContent",
        CandidateKinds.ValueName => "value name (pattern)",
        CandidateKinds.NearName => "name near a key",
        CandidateKinds.Wnf => "WNF name",
        CandidateKinds.Feature => "feature name",
        CandidateKinds.Service => "new service",
        CandidateKinds.Task => "new task",
        CandidateKinds.Appx => "new app",
        CandidateKinds.OptionalFeature => "new optional feature",
        CandidateKinds.Capability => "new capability",
        _ => kind,
    };
}
