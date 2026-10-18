using Debloater.App.Helpers;
using Debloater.Core.Admx;
using Debloater.Core.Model;
using Debloater.Core.Planning;
using Debloater.Core.Policies;
using Debloater.Core.Reference;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Debloater.App.ViewModels;

/// <summary>An ADMX policy with its current state on this PC and its Policy CSP page (if any).</summary>
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
        Risk = PolicyAdvisor.Risk(policy);
    }

    public AdmxPolicy Policy { get; }
    public IReadOnlyList<AdmxScopeState> Scopes { get; private set; }
    public CspPolicy? Csp { get; }
    public AdmxPolicyState State { get; private set; }
    public RiskLevel Risk { get; }

    /// <summary>Re-reads the state from the registry (after apply/revert).</summary>
    public void Refresh()
    {
        Scopes = AdmxPolicyStateReader.Read(Policy);
        State = AdmxPolicyStateReader.Combined(Scopes);
    }

    /// <summary>Short text of the pending change ("Pending: Enabled"), filled in by the page before display.</summary>
    public string PendingText { get; set; } = "";
    public Visibility PendingVisibility => PendingText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string DisplayName => string.IsNullOrWhiteSpace(Policy.DisplayName) ? Policy.Name : Policy.DisplayName;

    /// <summary>Name that UI Automation (screen readers) reads for the list item.</summary>
    public override string ToString() => DisplayName;
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
            if (Policy.EnabledValue is not null) lines.Add(Loc.Format("PolicyRegistry_EnabledValue", Policy.EnabledValue));
            if (Policy.DisabledValue is not null) lines.Add(Loc.Format("PolicyRegistry_DisabledValue", Policy.DisabledValue));
            foreach (var el in Policy.Elements)
                lines.Add(Loc.Format("PolicyRegistry_Element", el.Type, el.Label ?? el.Id, $"{el.Key}\\{el.ValueName}"));
            if (Policy.EnabledList is { Count: > 0 } en) lines.AddRange(en.Select(i => Loc.Format("PolicyRegistry_EnabledWrites", $"{i.Key}\\{i.ValueName}", i.Value)));
            if (Policy.DisabledList is { Count: > 0 } dis) lines.AddRange(dis.Select(i => Loc.Format("PolicyRegistry_DisabledWrites", $"{i.Key}\\{i.ValueName}", i.Value)));
            return string.Join(Environment.NewLine, lines);
        }
    }

    public string ScopesText => Scopes.Count == 0
        ? "—"
        : string.Join(Environment.NewLine, Scopes.Select(s =>
            $"{s.Scope}: {Labels.AdmxState(s.State)}" + (s.Values.Count > 0 ? " (" + string.Join("; ", s.Values) + ")" : "")));

    public string SupportedOnText => Policy.SupportedOn ?? Policy.SupportedOnRef ?? Loc.Get("Policy_NotSpecified");
    public string ExplainText => string.IsNullOrWhiteSpace(Policy.ExplainText) ? Loc.Get("Policy_NoExplainText") : Policy.ExplainText.Trim();
    public string QualifiedName => Policy.QualifiedName;
    public Uri? CspUri => Csp is null ? null : new Uri(Csp.Url);
    public string CspText => Csp is null ? "" : Csp.Reference + (Csp.Deprecated ? " " + Loc.Get("Policy_Deprecated") : "");
    public Visibility CspVisibility => Csp is null ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>All ADMX policies of the PC, loaded once (in the background) and cached.</summary>
public sealed class PolicyCatalog
{
    public required string Directory { get; init; }
    public required IReadOnlyList<PolicyItem> Items { get; init; }
    public required IReadOnlyList<CategoryNode> Tree { get; init; }
    public required int FileCount { get; init; }
    public required IReadOnlyList<string> Errors { get; init; }
    public bool CspLoaded { get; init; }

    private static Task<PolicyCatalog>? _cached;
    private static PolicyCatalog? _loaded;
    private Dictionary<string, PolicyItem>? _byName;

    public static Task<PolicyCatalog> GetAsync(bool refresh = false)
    {
        if (refresh || _cached is null || _cached.IsFaulted)
        {
            _cached = Task.Run(Load);
            _cached.ContinueWith(t => { if (t.IsCompletedSuccessfully) _loaded = t.Result; }, TaskScheduler.Default);
        }
        return _cached;
    }

    /// <summary>Item by its "File.admx:Name" name.</summary>
    public PolicyItem? Find(string qualifiedName)
    {
        _byName ??= Items.GroupBy(i => i.QualifiedName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        return _byName.GetValueOrDefault(qualifiedName);
    }

    /// <summary>Matching CSP policy (if the catalog has already been loaded).</summary>
    public static CspPolicy? CspFor(AdmxPolicy policy) => _loaded?.Find(policy.QualifiedName)?.Csp;

    /// <summary>Catalog already loaded (or null).</summary>
    public static PolicyCatalog? Loaded => _loaded;

    /// <summary>Level separator in the tree; category names that contain "/" are preserved.</summary>
    public static string PathOf(AdmxPolicy p) => string.Join("/", p.CategoryPath.Select(c => c.Replace('/', '∕')));

    private static PolicyCatalog Load()
    {
        var dir = AdmxParser.DefaultDirectory;
        var admx = AdmxParser.ParseDirectory(dir);

        var cspByAdmx = new Dictionary<string, CspPolicy>(StringComparer.OrdinalIgnoreCase);
        bool cspLoaded = false;
        // The active catalog's reference (it is updated with the catalog), else the one shipped with the program.
        const string cspName = "msdocs-policy-csp.json";
        var cspFile = App.State?.ActiveCatalog?.ReferenceDirectory is { } refDir && File.Exists(Path.Combine(refDir, cspName))
            ? Path.Combine(refDir, cspName)
            : Path.Combine(AppContext.BaseDirectory, "reference", cspName);
        if (File.Exists(cspFile))
        {
            try
            {
                var csp = CspMarkdownParser.Load(cspFile);
                cspLoaded = true;
                cspByAdmx = PolicyAdvisor.IndexCspByAdmx(csp.Policies);
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
            {
                // No CSP links; everything else works.
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
