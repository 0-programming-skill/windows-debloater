using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Debloater.App.Helpers;
using Debloater.App.Services;
using Debloater.Core.Admx;
using Debloater.Core.Catalog;
using Debloater.Core.Engine;
using Debloater.Core.Model;
using Debloater.Core.Planning;
using Debloater.Core.Platform;
using Debloater.Core.Policies;
using Debloater.Core.Profiles;

namespace Debloater.App.ViewModels;

/// <summary>
/// Global app state: catalog, current states (scan), desired selection and profiles.
/// Lives on the UI thread; heavy work runs in the background (Task.Run) and only the result
/// comes back here.
/// </summary>
public sealed partial class AppState : ObservableObject
{
    private readonly Dictionary<string, DesiredState> _desired = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TweakItemViewModel> _byId = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _scanCts;

    public AppState(AppSettings settings)
    {
        Settings = settings;
        System = SystemInfo.Current;
        Engine = CreateEngine();
        PolicyEngine = CreatePolicyEngine();
    }

    public AppSettings Settings { get; }
    public SystemInfo System { get; }
    public bool IsElevated => System.IsElevated;
    public TweakEngine Engine { get; private set; }

    public ObservableCollection<TweakItemViewModel> Tweaks { get; } = [];
    public IReadOnlyList<CategoryNode> Categories { get; private set; } = [];

    /// <summary>Translated category names of the loaded catalog (display only; paths stay the identifiers).</summary>
    public CategoryNames CategoryNames { get; private set; } = CategoryNames.Empty;

    /// <summary>Category path as shown to the user ("Privacy/Telemetry" → "Privacidade/Telemetria" in pt-BR).</summary>
    public string DisplayCategory(string path) => CategoryNames.DisplayPath(path);
    public IReadOnlyList<CatalogError> CatalogErrors { get; private set; } = [];

    public ObservableCollection<Profile> Profiles { get; } = [];
    public List<string> ProfileErrors { get; } = [];

    [ObservableProperty]
    public partial bool IsScanning { get; set; }

    [ObservableProperty]
    public partial int ScanDone { get; set; }

    [ObservableProperty]
    public partial int ScanTotal { get; set; }

    [ObservableProperty]
    public partial DateTime? LastScan { get; set; }

    [ObservableProperty]
    public partial int PendingCount { get; set; }

    [ObservableProperty]
    public partial string? CatalogMessage { get; set; }

    /// <summary>Raised when the scan finishes (new states).</summary>
    public event EventHandler? ScanCompleted;

    /// <summary>Raised when the catalog is (re)loaded (categories may have changed).</summary>
    public event EventHandler? CatalogReloaded;

    /// <summary>Raised when the desired selection changes.</summary>
    public event EventHandler? DesiredChanged;

    private TweakEngine CreateEngine() => new(new TweakEngineOptions { BackupDirectory = Settings.EffectiveBackupDirectory, System = System });

    private PolicyEngine CreatePolicyEngine() => new(new PolicyEngineOptions { BackupDirectory = Settings.EffectiveBackupDirectory, System = System });

    public void ReloadEngine()
    {
        Engine = CreateEngine();
        PolicyEngine = CreatePolicyEngine();
    }

    /// <summary>ADMX policy writing (real registry, backups in backups\policies).</summary>
    public PolicyEngine PolicyEngine { get; private set; } = null!;

    // ---------------- pending policies ----------------

    private readonly Dictionary<string, PolicyRequest> _policyRequests = new(StringComparer.OrdinalIgnoreCase);
    private PolicyEnvironment? _policyEnvironment;

    /// <summary>Context for the policy warnings (edition, domain, local GPO), read once.</summary>
    public PolicyEnvironment PolicyEnvironment => _policyEnvironment ??= PolicyEnvironment.Current(System);

    /// <summary>Chosen policy changes (configure or revert), by "File.admx:Name".</summary>
    public IReadOnlyCollection<PolicyRequest> PolicyRequests => _policyRequests.Values;

    public PolicyRequest? GetPolicyRequest(string qualifiedName) => _policyRequests.GetValueOrDefault(qualifiedName);

    /// <summary>Adds/replaces (request) or removes (null) the pending change of a policy.</summary>
    public void SetPolicyRequest(string qualifiedName, PolicyRequest? request)
    {
        if (request is null) _policyRequests.Remove(qualifiedName);
        else _policyRequests[qualifiedName] = request;
        UpdatePending();
    }

    public void ClearPolicyRequests()
    {
        _policyRequests.Clear();
        UpdatePending();
    }

    /// <summary>Pending policy changes with registry preview, risk and warnings.</summary>
    public IReadOnlyList<PlannedPolicyChange> PlanPolicyChanges() =>
        PolicyChangePlanner.Plan(_policyRequests.Values, PolicyEngine, PolicyWarnings);

    public IReadOnlyList<PolicyWarning> PolicyWarnings(AdmxPolicy policy, PolicyTargetState target, PolicyScope scope) =>
        PolicyAdvisor.Warnings(policy, PolicyCatalog.CspFor(policy), PolicyEnvironment, target, scope);

    // ---------------- catalog and scan ----------------

    /// <summary>Loads the catalog and profiles (fast) and starts the state scan (slow).</summary>
    public async Task LoadCatalogAsync()
    {
        var dir = Settings.EffectiveCatalogDirectory;
        var result = await Task.Run(() => CatalogLoader.LoadDirectory(dir));

        _scanCts?.Cancel();
        Tweaks.Clear();
        _byId.Clear();
        var names = result.Categories;
        foreach (var t in result.Tweaks.OrderBy(t => names.DisplayPath(t.Category), StringComparer.CurrentCulture).ThenBy(t => t.DisplayName, StringComparer.CurrentCulture))
        {
            var vm = new TweakItemViewModel(t, this);
            Tweaks.Add(vm);
            _byId[t.Id] = vm;
        }
        // Choices for tweaks that disappeared from the catalog are discarded.
        foreach (var id in _desired.Keys.Where(id => !_byId.ContainsKey(id)).ToList()) _desired.Remove(id);

        CategoryNames = names;
        Categories = CategoryNode.Build(result.Tweaks.Select(t => t.Category), names);
        CatalogErrors = result.Errors;
        CatalogMessage = result.Errors.Count == 0
            ? null
            : Loc.Format("Catalog_ErrorsMessage", dir, result.Errors.Count, result.Errors[0]);
        LoadProfiles();
        CatalogReloaded?.Invoke(this, EventArgs.Empty);
        UpdatePending();
        await ScanAsync();
    }

    /// <summary>Reads the current state of every tweak (in parallel, off the UI thread).</summary>
    public async Task ScanAsync()
    {
        _scanCts?.Cancel();
        var cts = _scanCts = new CancellationTokenSource();
        var items = Tweaks.ToList();
        var tweaks = items.Select(i => i.Tweak).ToList();
        var engine = Engine;

        IsScanning = true;
        ScanDone = 0;
        ScanTotal = tweaks.Count;
        var progress = new Progress<int>(done => { if (!cts.IsCancellationRequested) ScanDone = done; });
        try
        {
            var results = await Task.Run(async () =>
            {
                var statuses = new TweakStatus[tweaks.Count];
                int done = 0;
                using var ctx = engine.CreateContext();
                await Parallel.ForEachAsync(Enumerable.Range(0, tweaks.Count),
                    new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cts.Token },
                    async (i, token) =>
                    {
                        statuses[i] = await engine.GetStatusAsync(tweaks[i], ctx, token).ConfigureAwait(false);
                        ((IProgress<int>)progress).Report(Interlocked.Increment(ref done));
                    }).ConfigureAwait(false);
                return statuses;
            }, cts.Token);

            for (int i = 0; i < items.Count; i++) items[i].Status = results[i];
            LastScan = DateTime.Now;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (_scanCts == cts) IsScanning = false;
        }
        UpdatePending();
        ScanCompleted?.Invoke(this, EventArgs.Empty);
    }

    public TweakItemViewModel? Find(string id) => _byId.GetValueOrDefault(id);

    public int CountByState(TweakState s) => Tweaks.Count(t => t.State == s);
    public int CountByRisk(RiskLevel r) => Tweaks.Count(t => t.Tweak.Risk == r);

    // ---------------- desired selection ----------------

    public DesiredState? GetDesired(string id) => _desired.TryGetValue(id, out var d) ? d : null;

    public IReadOnlyDictionary<string, DesiredState> Desired => _desired;

    public void SetDesired(string id, DesiredState? desired)
    {
        if (desired is null) _desired.Remove(id);
        else _desired[id] = desired.Value;
        _byId.GetValueOrDefault(id)?.RaiseDesiredChanged();
        UpdatePending();
    }

    /// <summary>Replaces the whole explicit selection.</summary>
    public void ReplaceDesired(IReadOnlyDictionary<string, DesiredState> desired)
    {
        _desired.Clear();
        foreach (var (id, d) in desired)
            if (_byId.ContainsKey(id)) _desired[id] = d;
        foreach (var t in Tweaks) t.RaiseDesiredChanged();
        UpdatePending();
    }

    public void ClearDesired() => ReplaceDesired(new Dictionary<string, DesiredState>());

    /// <summary>Pending changes (what will be applied and reverted).</summary>
    public IReadOnlyList<PlannedChange> PlanChanges() =>
        ChangePlanner.Plan(Tweaks.Where(t => t.Status is not null).Select(t => t.Status!), _desired, Engine.Backups.Exists);

    private void UpdatePending()
    {
        PendingCount = Tweaks.Count(t => t.IsPending) + _policyRequests.Count;
        DesiredChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------------- profiles ----------------

    public string BuiltInProfilesDirectory => Path.Combine(Settings.EffectiveCatalogDirectory, CatalogLoader.ProfilesFolder);

    public void LoadProfiles()
    {
        ProfileErrors.Clear();
        Profiles.Clear();
        foreach (var p in ProfileStore.LoadDirectory(BuiltInProfilesDirectory, builtIn: true, ProfileErrors)) Profiles.Add(p);
        foreach (var p in ProfileStore.LoadDirectory(AppSettings.UserProfilesDirectory, builtIn: false, ProfileErrors)) Profiles.Add(p);
    }

    /// <summary>Loads a profile into the selection (replaces the current selection, including pending policies). Returns a summary.</summary>
    public async Task<string> ApplyProfileToSelectionAsync(Profile profile)
    {
        var resolution = ProfileStore.Resolve(profile, Tweaks.Select(t => t.Tweak));
        List<PolicyRequest> policyRequests = [];
        List<string> unknownPolicies = [];
        if (profile.Policies is { Count: > 0 })
        {
            var catalog = await PolicyCatalog.GetAsync();
            (policyRequests, unknownPolicies) = ProfileStore.ResolvePolicies(profile, catalog.Items.Select(i => i.Policy));
        }
        _policyRequests.Clear();
        foreach (var r in policyRequests) _policyRequests[r.Policy.QualifiedName] = r;
        ReplaceDesired(resolution.Desired);

        var msg = Loc.Format("Profile_Loaded", profile.DisplayName, resolution.Desired.Count);
        if (policyRequests.Count > 0) msg += Loc.Format("Profile_LoadedPolicies", policyRequests.Count);
        msg += Loc.Format("Profile_LoadedPending", PendingCount);
        if (resolution.UnknownIds.Count > 0)
            msg += " " + Loc.Format("Profile_UnknownIds", string.Join(", ", resolution.UnknownIds));
        if (unknownPolicies.Count > 0)
            msg += " " + Loc.Format("Profile_UnknownPolicies", string.Join(", ", unknownPolicies));
        return msg;
    }

    /// <summary>
    /// Policies to save in a profile: those this program configured (they have a backup), with the current
    /// registry values, overridden by the pending changes (a pending revert = left out).
    /// </summary>
    public async Task<List<PolicySetting>> EffectivePolicySettingsAsync()
    {
        var result = new Dictionary<string, PolicySetting>(StringComparer.OrdinalIgnoreCase);
        var names = PolicyEngine.Backups.List();
        if (names.Count > 0 || _policyRequests.Count > 0)
        {
            var catalog = await PolicyCatalog.GetAsync();
            foreach (var name in names)
            {
                if (catalog.Find(name) is not { } item) continue;
                var backup = PolicyEngine.Backups.Load(name);
                var scope = backup?.Values.FirstOrDefault() is { } v && v.Root.Equals("HKCU", StringComparison.OrdinalIgnoreCase)
                    ? PolicyScope.User : PolicySetting.DefaultScope(item.Policy.Class);
                result[name] = PolicyEngine.ReadCurrent(item.Policy, scope).Setting;
            }
        }
        foreach (var r in _policyRequests.Values)
        {
            if (r.Setting is { } s) result[r.Policy.QualifiedName] = s;
            else result.Remove(r.Policy.QualifiedName);
        }
        return [.. result.Values];
    }

    /// <summary>Effective selection to save in a profile: explicit choices + what is already applied.</summary>
    public Dictionary<string, DesiredState> EffectiveSelection()
    {
        var result = new Dictionary<string, DesiredState>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in Tweaks)
        {
            if (t.ExplicitDesired is { } d) result[t.Id] = d;
            else if (t.State == TweakState.Applied) result[t.Id] = DesiredState.Applied;
        }
        return result;
    }
}
