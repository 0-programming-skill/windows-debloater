using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Debloater.App.Services;
using Debloater.Core.Catalog;
using Debloater.Core.Engine;
using Debloater.Core.Model;
using Debloater.Core.Planning;
using Debloater.Core.Platform;
using Debloater.Core.Profiles;

namespace Debloater.App.ViewModels;

/// <summary>
/// Estado global do app: catálogo, estados atuais (varredura), seleção desejada e perfis.
/// Vive na thread de interface; trabalho pesado roda em segundo plano (Task.Run) e só o resultado
/// volta para cá.
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
    }

    public AppSettings Settings { get; }
    public SystemInfo System { get; }
    public bool IsElevated => System.IsElevated;
    public TweakEngine Engine { get; private set; }

    public ObservableCollection<TweakItemViewModel> Tweaks { get; } = [];
    public IReadOnlyList<CategoryNode> Categories { get; private set; } = [];
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

    /// <summary>Disparado quando a varredura termina (estados novos).</summary>
    public event EventHandler? ScanCompleted;

    /// <summary>Disparado quando o catálogo é (re)carregado (categorias podem ter mudado).</summary>
    public event EventHandler? CatalogReloaded;

    /// <summary>Disparado quando a seleção desejada muda.</summary>
    public event EventHandler? DesiredChanged;

    private TweakEngine CreateEngine() => new(new TweakEngineOptions { BackupDirectory = Settings.EffectiveBackupDirectory, System = System });

    public void ReloadEngine() => Engine = CreateEngine();

    // ---------------- catálogo e varredura ----------------

    /// <summary>Carrega o catálogo e os perfis (rápido) e dispara a varredura de estados (lenta).</summary>
    public async Task LoadCatalogAsync()
    {
        var dir = Settings.EffectiveCatalogDirectory;
        var result = await Task.Run(() => CatalogLoader.LoadDirectory(dir));

        _scanCts?.Cancel();
        Tweaks.Clear();
        _byId.Clear();
        foreach (var t in result.Tweaks.OrderBy(t => t.Category, StringComparer.CurrentCulture).ThenBy(t => t.Name, StringComparer.CurrentCulture))
        {
            var vm = new TweakItemViewModel(t, this);
            Tweaks.Add(vm);
            _byId[t.Id] = vm;
        }
        // Escolhas de tweaks que sumiram do catálogo são descartadas.
        foreach (var id in _desired.Keys.Where(id => !_byId.ContainsKey(id)).ToList()) _desired.Remove(id);

        Categories = CategoryNode.Build(result.Tweaks.Select(t => t.Category));
        CatalogErrors = result.Errors;
        CatalogMessage = result.Errors.Count == 0
            ? null
            : $"O catálogo em '{dir}' tem {result.Errors.Count} erro(s); os ajustes com erro foram ignorados. Primeiro: {result.Errors[0]}";
        LoadProfiles();
        CatalogReloaded?.Invoke(this, EventArgs.Empty);
        UpdatePending();
        await ScanAsync();
    }

    /// <summary>Lê o estado atual de todos os ajustes (em paralelo, fora da thread de interface).</summary>
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

    // ---------------- seleção desejada ----------------

    public DesiredState? GetDesired(string id) => _desired.TryGetValue(id, out var d) ? d : null;

    public IReadOnlyDictionary<string, DesiredState> Desired => _desired;

    public void SetDesired(string id, DesiredState? desired)
    {
        if (desired is null) _desired.Remove(id);
        else _desired[id] = desired.Value;
        _byId.GetValueOrDefault(id)?.RaiseDesiredChanged();
        UpdatePending();
    }

    /// <summary>Substitui toda a seleção explícita.</summary>
    public void ReplaceDesired(IReadOnlyDictionary<string, DesiredState> desired)
    {
        _desired.Clear();
        foreach (var (id, d) in desired)
            if (_byId.ContainsKey(id)) _desired[id] = d;
        foreach (var t in Tweaks) t.RaiseDesiredChanged();
        UpdatePending();
    }

    public void ClearDesired() => ReplaceDesired(new Dictionary<string, DesiredState>());

    /// <summary>Mudanças pendentes (o que será aplicado e revertido).</summary>
    public IReadOnlyList<PlannedChange> PlanChanges() =>
        ChangePlanner.Plan(Tweaks.Where(t => t.Status is not null).Select(t => t.Status!), _desired, Engine.Backups.Exists);

    private void UpdatePending()
    {
        PendingCount = Tweaks.Count(t => t.IsPending);
        DesiredChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------------- perfis ----------------

    public string BuiltInProfilesDirectory => Path.Combine(Settings.EffectiveCatalogDirectory, CatalogLoader.ProfilesFolder);

    public void LoadProfiles()
    {
        ProfileErrors.Clear();
        Profiles.Clear();
        foreach (var p in ProfileStore.LoadDirectory(BuiltInProfilesDirectory, builtIn: true, ProfileErrors)) Profiles.Add(p);
        foreach (var p in ProfileStore.LoadDirectory(AppSettings.UserProfilesDirectory, builtIn: false, ProfileErrors)) Profiles.Add(p);
    }

    /// <summary>Carrega um perfil na seleção (substitui a seleção atual). Devolve um resumo.</summary>
    public string ApplyProfileToSelection(Profile profile)
    {
        var resolution = ProfileStore.Resolve(profile, Tweaks.Select(t => t.Tweak));
        ReplaceDesired(resolution.Desired);
        var msg = $"Perfil \"{profile.Name}\" carregado: {resolution.Desired.Count} ajuste(s) com estado desejado, {PendingCount} mudança(s) pendente(s).";
        if (resolution.UnknownIds.Count > 0)
            msg += $" Ignorados (não existem no catálogo): {string.Join(", ", resolution.UnknownIds)}.";
        return msg;
    }

    /// <summary>Seleção efetiva para salvar num perfil: escolhas explícitas + o que já está aplicado.</summary>
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
