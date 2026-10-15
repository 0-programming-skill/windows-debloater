using Debloater.App.Helpers;
using Debloater.Core.Admx;
using Debloater.Core.Planning;
using Debloater.Core.Reference;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Debloater.App.ViewModels;

/// <summary>Uma política ADMX com o estado atual neste PC e a página da Policy CSP (se houver).</summary>
public sealed class PolicyItem
{
    public PolicyItem(AdmxPolicy policy, IReadOnlyList<AdmxScopeState> scopes, CspPolicy? csp)
    {
        Policy = policy;
        Scopes = scopes;
        Csp = csp;
        State = AdmxPolicyStateReader.Combined(scopes);
        CategoryText = string.Join(" › ", policy.CategoryPath);
        SearchText = $"{policy.DisplayName} {policy.Name} {policy.File} {policy.Key} {policy.ValueName} {CategoryText}";
    }

    public AdmxPolicy Policy { get; }
    public IReadOnlyList<AdmxScopeState> Scopes { get; }
    public CspPolicy? Csp { get; }
    public AdmxPolicyState State { get; }

    public string DisplayName => string.IsNullOrWhiteSpace(Policy.DisplayName) ? Policy.Name : Policy.DisplayName;
    public string CategoryText { get; }
    public string SearchText { get; }
    public string StateText => Labels.AdmxState(State);
    public SolidColorBrush StateBrush => Labels.Brush(Labels.AdmxStateColor(State));
    public SolidColorBrush StateForeground => Labels.ForegroundOn(Labels.AdmxStateColor(State));
    public string ClassText => Labels.AdmxClassText(Policy.Class);
    public bool IsConfigured => State != AdmxPolicyState.NotConfigured;

    public string RegistryText
    {
        get
        {
            var hives = Policy.Class switch
            {
                AdmxClass.Machine => "HKLM",
                AdmxClass.User => "HKCU",
                _ => "HKLM/HKCU",
            };
            var lines = new List<string> { $"{hives}\\{Policy.Key}" + (Policy.ValueName is null ? "" : $"\\{Policy.ValueName}") };
            if (Policy.EnabledValue is not null) lines.Add($"Habilitada = {Policy.EnabledValue}");
            if (Policy.DisabledValue is not null) lines.Add($"Desabilitada = {Policy.DisabledValue}");
            foreach (var el in Policy.Elements)
                lines.Add($"Elemento {el.Type} '{el.Label ?? el.Id}': {el.Key}\\{el.ValueName}");
            if (Policy.EnabledList is { Count: > 0 } en) lines.AddRange(en.Select(i => $"Habilitada grava {i.Key}\\{i.ValueName} = {i.Value}"));
            if (Policy.DisabledList is { Count: > 0 } dis) lines.AddRange(dis.Select(i => $"Desabilitada grava {i.Key}\\{i.ValueName} = {i.Value}"));
            return string.Join(Environment.NewLine, lines);
        }
    }

    public string ScopesText => Scopes.Count == 0
        ? "—"
        : string.Join(Environment.NewLine, Scopes.Select(s =>
            $"{s.Scope}: {Labels.AdmxState(s.State)}" + (s.Values.Count > 0 ? " (" + string.Join("; ", s.Values) + ")" : "")));

    public string SupportedOnText => Policy.SupportedOn ?? Policy.SupportedOnRef ?? "Não informado";
    public string ExplainText => string.IsNullOrWhiteSpace(Policy.ExplainText) ? "(sem texto de explicação no ADML)" : Policy.ExplainText.Trim();
    public string QualifiedName => Policy.QualifiedName;
    public Uri? CspUri => Csp is null ? null : new Uri(Csp.Url);
    public string CspText => Csp is null ? "" : Csp.Reference + (Csp.Deprecated ? " (obsoleta)" : "");
    public Visibility CspVisibility => Csp is null ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>Todas as políticas ADMX do PC, carregadas uma vez (em segundo plano) e mantidas em cache.</summary>
public sealed class PolicyCatalog
{
    public required string Directory { get; init; }
    public required IReadOnlyList<PolicyItem> Items { get; init; }
    public required IReadOnlyList<CategoryNode> Tree { get; init; }
    public required int FileCount { get; init; }
    public required IReadOnlyList<string> Errors { get; init; }
    public bool CspLoaded { get; init; }

    private static Task<PolicyCatalog>? _cached;

    public static Task<PolicyCatalog> GetAsync(bool refresh = false)
    {
        if (refresh || _cached is null || _cached.IsFaulted) _cached = Task.Run(Load);
        return _cached;
    }

    /// <summary>Separador de níveis na árvore; nomes de categoria que contêm "/" são preservados.</summary>
    public static string PathOf(AdmxPolicy p) => string.Join("/", p.CategoryPath.Select(c => c.Replace('/', '∕')));

    private static PolicyCatalog Load()
    {
        var dir = AdmxParser.DefaultDirectory;
        var admx = AdmxParser.ParseDirectory(dir);

        var cspByAdmx = new Dictionary<string, CspPolicy>(StringComparer.OrdinalIgnoreCase);
        bool cspLoaded = false;
        var cspFile = Path.Combine(AppContext.BaseDirectory, "reference", "msdocs-policy-csp.json");
        if (File.Exists(cspFile))
        {
            try
            {
                var csp = CspMarkdownParser.Load(cspFile);
                cspLoaded = true;
                foreach (var p in csp.Policies)
                    foreach (var m in p.GpMappings ?? [])
                        if (m.AdmxFile is not null && m.Name is not null)
                            cspByAdmx.TryAdd(m.AdmxFile + ":" + m.Name, p);
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
            {
                // Sem links da CSP; o resto funciona.
            }
        }

        var items = admx.Policies
            .Select(p => new PolicyItem(p, AdmxPolicyStateReader.Read(p), cspByAdmx.GetValueOrDefault(p.File + ":" + p.Name)))
            .OrderBy(i => i.DisplayName, StringComparer.CurrentCulture)
            .ToList();
        return new PolicyCatalog
        {
            Directory = dir,
            Items = items,
            Tree = CategoryNode.Build(items.Select(i => PathOf(i.Policy))),
            FileCount = admx.FileCount,
            Errors = admx.Errors,
            CspLoaded = cspLoaded,
        };
    }
}
