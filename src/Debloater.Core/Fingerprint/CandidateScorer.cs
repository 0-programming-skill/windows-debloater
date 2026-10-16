using System.Text.RegularExpressions;
using Debloater.Core.Model;

namespace Debloater.Core.Fingerprint;

/// <summary>Tipos de candidato (o que a camada 2 deve investigar).</summary>
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

/// <summary>Chaves e nomes de valores que o catálogo já usa (para "já coberto" e "perto de chave conhecida").</summary>
public sealed class CatalogRegistryIndex
{
    /// <summary>Caminho normalizado (sem colmeia, minúsculas) → ids dos tweaks.</summary>
    public Dictionary<string, List<string>> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Nome do valor → ids dos tweaks.</summary>
    public Dictionary<string, List<string>> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>(caminho normalizado, nome) de cada ação registry, com o tweak.</summary>
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

    /// <summary>Remove a colmeia (HKLM\, HKEY_CURRENT_USER\, \Registry\Machine\...) e barras nas pontas.</summary>
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

    /// <summary>"Chave[Valor]" (forma usada por alguns binários) → (chave, valor); sem colchetes → (texto, null).</summary>
    public static (string Path, string? Name) SplitValueSuffix(string value)
    {
        if (value.EndsWith(']') && value.LastIndexOf('[') is var i and > 0 && i < value.Length - 2)
            return (value[..i], value[(i + 1)..^1]);
        return (value, null);
    }

    /// <summary>Tweaks cuja chave/valor é exatamente este candidato.</summary>
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
                // Com contexto, só conta se a chave também bate (nomes genéricos se repetem em chaves diferentes).
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
/// Pontua candidatos: mais alto para caminhos de política, Explorer\Advanced, ContentDeliveryManager
/// e SubscribedContent, nomes com IA/Copilot/Recall/Widgets/anúncios/sugestões/telemetria/diagnóstico,
/// e cadeias no mesmo binário (ou na mesma chave) que algo do catálogo; mais baixo para ruído genérico
/// (log, trace, debug, teste...). A pontuação é só uma ordem de investigação, não uma prova.
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

    // Palavras quentes (sensível a maiúsculas para siglas; o resto sem diferenciar).
    [GeneratedRegex(@"(?<![a-z])AI(?![a-z])|(?<![a-z])Agent|(?<![a-z])Mcp(?![a-z])|Copilot|Recall|Widget|Advertis|(?<![a-z])Ads(?![a-z])|Suggest|Recommend|Telemetry|Diag|Spotlight|Bing|Feedback|Promot|Sponsor|Consumer|Iris|Edge|Tracking|Census|Tips|Cortana|Search(Box|Highlight)|Chat|Teams|Gaming|GameBar", RegexOptions.CultureInvariant)]
    private static partial Regex HotWordRegex();

    [GeneratedRegex(@"Explorer\\Advanced|ContentDeliveryManager|SubscribedContent|UserProfileEngagement|\\Search\\|\\Feeds|\\Dsh|WindowsCopilot|WindowsAI|\\Start\\|\\Taskbar|DataCollection|AdvertisingInfo|\\Privacy|CloudContent|ActivityHistory|\\Mcp\\|AgentGovernance|IsolationSession|CopilotUpdate|AgentSuggestions", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HotPathRegex();

    [GeneratedRegex(@"(^|[^A-Za-z])(Log|Logs|Logging|Logger|Trace|Tracing|Debug|Etw|Wpp|Test|Tests|Assert|Verbose|Dump|Crash|Perf|Counter|Mock|Fake|Stub|Leak)([^a-z]|$)|(?<=[a-z])(Log|Logging|Trace|Tracing|Debug|Test|Assert|Verbose|Dump|Perf|Mock)([A-Z_]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex NoiseRegex();

    // Nomes de recursos de interface (XAML): perto de uma chave por acaso, quase nunca são valores de registro.
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

        // Chave quente no próprio caminho ou no contexto (nome perto de uma chave quente). Para nomes
        // que só estão perto de uma chave (nearName), a proximidade é indício fraco: metade do bônus.
        bool near = c.Kind == CandidateKinds.NearName;
        var hotPath = HotPathRegex().Match(isPathKind ? c.Value : c.ContextPath ?? c.ExtraText ?? "");
        if (hotPath.Success)
        {
            score += isPathKind ? 25 : near ? 10 : 20;
            reasons.Add($"chave relevante ({hotPath.Value.Trim('\\')})");
        }
        if (!isPathKind && c.Kind != CandidateKinds.AdmxPolicy && c.ContextPath is not null && StringClassifier.IsPolicyPath(c.ContextPath))
        {
            score += near ? 5 : 15;
            reasons.Add("perto de uma chave de política");
        }

        var hot = HotWordRegex().Matches(text).Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (hot.Count > 0)
        {
            score += Math.Min(30, 20 + 5 * (hot.Count - 1));
            reasons.Add($"palavra-chave: {string.Join(", ", hot)}");
        }

        if (c.CatalogKeySameKeyTweak is not null)
        {
            score += near ? 10 : 25;
            reasons.Add($"mesma chave de um tweak ({c.CatalogKeySameKeyTweak})");
        }
        else if (c.CatalogKeyInSameBinary is not null)
        {
            score += 10;
            reasons.Add($"binário também usa uma chave do catálogo ({c.CatalogKeyInSameBinary})");
        }
        if (c.CuratedBinary) { score += 5; reasons.Add("binário da lista curada"); }
        if (c.NewBinary) { score += 5; reasons.Add("binário novo"); }

        if (c.Value.Length < 8 && !isPathKind) { score -= 10; reasons.Add("curto"); }
        if (NoiseRegex().IsMatch(c.Value)) { score -= 15; reasons.Add("ruído provável (log/trace/debug/teste)"); }
        if (isPathKind && RegistryNoiseRegex().IsMatch(c.Value)) { score -= 15; reasons.Add("chave genérica (COM/classes/dispositivos)"); }
        if (near && UiResourceRegex().IsMatch(c.Value)) { score -= 15; reasons.Add("recurso de interface (XAML)"); }
        if (!isPathKind && c.Value.Count(ch => ch == '_') >= 2) { score -= 20; reasons.Add("parece chave de recurso de texto (vários _)"); }
        if (c.BinaryPath is not null && c.BinaryPath.Contains("Microsoft-Edge-WebView", StringComparison.OrdinalIgnoreCase))
        {
            score -= 20;
            reasons.Add("WebView2/Edge (atualiza à parte do Windows)");
        }

        if (c.Moved)
        {
            score = (int)Math.Round(score * 0.3);
            reasons.Add("já existia, mudou de binário (fraco)");
        }
        if (c.CoveredBy is { Count: > 0 })
        {
            score -= 40;
            reasons.Add($"já coberto: {string.Join(", ", c.CoveredBy)}");
        }
        return (Math.Max(0, score), reasons);
    }

    public static string KindLabel(string kind) => kind switch
    {
        CandidateKinds.AdmxPolicy => "política ADMX nova",
        CandidateKinds.PolicyPath => "caminho de política",
        CandidateKinds.RegistryPath => "caminho de registro",
        CandidateKinds.CspPath => "caminho de CSP",
        CandidateKinds.SubscribedContent => "SubscribedContent",
        CandidateKinds.ValueName => "nome de valor (padrão)",
        CandidateKinds.NearName => "nome perto de chave",
        CandidateKinds.Wnf => "nome WNF",
        CandidateKinds.Feature => "nome de feature",
        CandidateKinds.Service => "serviço novo",
        CandidateKinds.Task => "tarefa nova",
        CandidateKinds.Appx => "app novo",
        CandidateKinds.OptionalFeature => "recurso opcional novo",
        CandidateKinds.Capability => "capacidade nova",
        _ => kind,
    };
}
