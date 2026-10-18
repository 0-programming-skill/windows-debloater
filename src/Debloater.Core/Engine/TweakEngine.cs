using Debloater.Core.Actions;
using Debloater.Core.Model;
using Debloater.Core.Platform;

namespace Debloater.Core.Engine;

public sealed class TweakEngineOptions
{
    /// <summary>Where backups are stored. Default: %ProgramData%\Debloater\backups.</summary>
    public string BackupDirectory { get; init; } = BackupStore.DefaultDirectory;

    /// <summary>Require the process to be elevated to apply/revert. Only turn off in tests.</summary>
    public bool RequireElevation { get; init; } = true;

    /// <summary>Target system. Default: the running system.</summary>
    public SystemInfo? System { get; init; }

    /// <summary>How many tweaks to detect in parallel in a full scan.</summary>
    public int MaxParallelism { get; init; } = 8;

    /// <summary>Evaluates a tweak's precondition. Default: runs the PowerShell script. Replaceable in tests.</summary>
    public Func<Precondition, CancellationToken, Task<PreconditionResult>> PreconditionEvaluator { get; init; } = Engine.PreconditionEvaluator.EvaluateAsync;
}

/// <summary>The process must be elevated (administrator).</summary>
public sealed class ElevationRequiredException() :
    InvalidOperationException("Applying or reverting tweaks requires administrator privileges. Run the program as administrator.");

/// <summary>The tweak exceeds the risk level allowed for this operation.</summary>
public sealed class RiskNotAllowedException(Tweak tweak, RiskLevel allowed) :
    InvalidOperationException($"Tweak '{tweak.Id}' has risk {tweak.Risk}, above the allowed level ({allowed}).")
{
    public Tweak Tweak { get; } = tweak;
    public RiskLevel Allowed { get; } = allowed;
}

/// <summary>Result of applying/reverting.</summary>
public sealed record OperationResult(string TweakId, bool Success, string Message, IReadOnlyList<string> Errors, TweakStatus? StatusAfter = null)
{
    /// <summary>Notes that are not errors (e.g. "the service will only stop after a restart").</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>
/// Orchestrates detection, application and reversal of tweaks. All type-specific logic lives in the
/// <see cref="IActionHandler"/> implementations; the engine only handles aggregation, backup and policies (elevation, risk).
/// </summary>
public sealed class TweakEngine
{
    private readonly TweakEngineOptions _options;

    public TweakEngine(TweakEngineOptions? options = null)
    {
        _options = options ?? new TweakEngineOptions();
        System = _options.System ?? SystemInfo.Current;
        Backups = new BackupStore(_options.BackupDirectory);
    }

    public SystemInfo System { get; }
    public BackupStore Backups { get; }

    public ScanContext CreateContext() => new(System);

    // ---------------- status ----------------

    public async Task<TweakStatus> GetStatusAsync(Tweak tweak, ScanContext? ctx = null, CancellationToken ct = default)
    {
        using var owned = ctx is null ? CreateContext() : null;
        ctx ??= owned!;

        if (!tweak.IsBuildInRange(System.Build))
        {
            return new TweakStatus(tweak, TweakState.NotApplicable, [],
                $"build {System.Build} outside range {tweak.MinBuild?.ToString() ?? "*"}..{tweak.MaxBuild?.ToString() ?? "*"}");
        }

        if (tweak.Precondition is not null)
        {
            var pre = await EvaluatePreconditionAsync(tweak, ctx, ct).ConfigureAwait(false);
            if (pre.Met == false) return new TweakStatus(tweak, TweakState.NotApplicable, [], PreconditionDetail(tweak, pre));
            if (pre.Met is null) return new TweakStatus(tweak, TweakState.Error, [], pre.Detail);
        }

        var results = new List<ActionStatus>(tweak.Actions.Count);
        foreach (var action in tweak.Actions)
        {
            ct.ThrowIfCancellationRequested();
            Detection detection;
            try
            {
                detection = await ActionHandlers.Get(action.Type).DetectAsync(action, ctx, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                detection = Detection.Error(ex.Message);
            }
            results.Add(new ActionStatus(action, detection.State, detection.Detail));
        }
        return new TweakStatus(tweak, StatusAggregator.Aggregate(results.Select(r => r.State)), results);
    }

    /// <summary>Status of several tweaks, sharing caches (appx, features, tasks) in a single scan.</summary>
    public async Task<IReadOnlyList<TweakStatus>> GetStatusesAsync(IReadOnlyList<Tweak> tweaks, CancellationToken ct = default)
    {
        using var ctx = CreateContext();
        var results = new TweakStatus[tweaks.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, tweaks.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _options.MaxParallelism), CancellationToken = ct },
            async (i, token) => results[i] = await GetStatusAsync(tweaks[i], ctx, token).ConfigureAwait(false)).ConfigureAwait(false);
        return results;
    }

    // ---------------- apply ----------------

    /// <summary>
    /// Applies a tweak. First captures and persists the backup; if a backup already exists (re-apply),
    /// it is preserved to keep the true ORIGINAL value (only new actions are added).
    /// </summary>
    public async Task<OperationResult> ApplyAsync(Tweak tweak, RiskLevel maxRisk = RiskLevel.Medium, CancellationToken ct = default)
    {
        EnsureElevated();
        if (tweak.Risk > maxRisk) throw new RiskNotAllowedException(tweak, maxRisk);
        if (!tweak.IsBuildInRange(System.Build))
            return new OperationResult(tweak.Id, false, $"Does not apply to build {System.Build}.", []);

        using var ctx = CreateContext();
        if (tweak.Precondition is not null)
        {
            var pre = await EvaluatePreconditionAsync(tweak, ctx, ct).ConfigureAwait(false);
            if (pre.Met != true) return new OperationResult(tweak.Id, false, $"Does not apply to this system: {PreconditionDetail(tweak, pre)}", []);
        }

        // 1) Backup (before any change).
        var backup = Backups.Load(tweak.Id) ?? new TweakBackup
        {
            TweakId = tweak.Id,
            CreatedUtc = DateTime.UtcNow,
            WindowsBuild = System.FullBuild,
        };
        bool changed = false;
        foreach (var action in tweak.Actions)
        {
            if (FindBackup(backup, action) is not null) continue;
            var captured = await ActionHandlers.Get(action.Type).CaptureBackupAsync(action, ctx, ct).ConfigureAwait(false);
            if (captured is null) continue;
            backup.Actions.Add(captured);
            changed = true;
        }
        if (changed || !Backups.Exists(tweak.Id)) Backups.Save(backup);

        // 2) Apply.
        var errors = new List<string>();
        foreach (var action in tweak.Actions)
        {
            ct.ThrowIfCancellationRequested();
            var handler = ActionHandlers.Get(action.Type);
            try
            {
                var before = await handler.DetectAsync(action, ctx, ct).ConfigureAwait(false);
                if (before.State is ActionState.Desired or ActionState.NotApplicable) continue;
                await handler.ApplyAsync(action, ctx, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"{action}: {ex.Message}");
            }
        }

        var after = await GetStatusAsync(tweak, null, ct).ConfigureAwait(false);
        bool ok = errors.Count == 0;
        var message = ok ? $"Applied. State: {after.State}." : $"Applied with {errors.Count} error(s). State: {after.State}.";
        if (!tweak.Reversible) message += " Warning: this tweak cannot be reverted by the program.";
        return new OperationResult(tweak.Id, ok, message, errors, after) { Notes = [.. ctx.Notes] };
    }

    // ---------------- revert ----------------

    /// <summary>Reverts a tweak from its backup. Without a backup, nothing is done.</summary>
    public async Task<OperationResult> RevertAsync(Tweak tweak, CancellationToken ct = default)
    {
        EnsureElevated();
        var backup = Backups.Load(tweak.Id);
        if (backup is null)
        {
            return new OperationResult(tweak.Id, false,
                "No backup found for this tweak (it was not applied by this program, or the backup was deleted). Nothing was reverted.", []);
        }

        using var ctx = CreateContext();
        var errors = new List<string>();
        foreach (var action in Enumerable.Reverse(tweak.Actions))
        {
            ct.ThrowIfCancellationRequested();
            var handler = ActionHandlers.Get(action.Type);
            if (!handler.CanRestore)
            {
                errors.Add($"{action}: not reversible by the engine.");
                continue;
            }
            var entry = FindBackup(backup, action);
            if (entry is null && action.Type != ActionSpec.PowerShell)
            {
                errors.Add($"{action}: no backup for this action; skipped.");
                continue;
            }
            try
            {
                await handler.RestoreAsync(action, entry, ctx, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"{action}: {ex.Message}");
            }
        }

        if (errors.Count == 0) Backups.Delete(tweak.Id);
        var after = await GetStatusAsync(tweak, null, ct).ConfigureAwait(false);
        return errors.Count == 0
            ? new OperationResult(tweak.Id, true, $"Reverted. State: {after.State}.", errors, after) { Notes = [.. ctx.Notes] }
            : new OperationResult(tweak.Id, false, $"Partially reverted ({errors.Count} error(s)); backup kept. State: {after.State}.", errors, after) { Notes = [.. ctx.Notes] };
    }

    /// <summary>Evaluates the precondition once per context (scan).</summary>
    private Task<PreconditionResult> EvaluatePreconditionAsync(Tweak tweak, ScanContext ctx, CancellationToken ct) =>
        ctx.GetOrAddAsync("precondition:" + tweak.Id, () => _options.PreconditionEvaluator(tweak.Precondition!, ct));

    /// <summary>Detail shown for a precondition: when it is not met, the reason in the current UI language.</summary>
    private static string PreconditionDetail(Tweak tweak, PreconditionResult pre) =>
        pre.Met == false && tweak.DisplayPreconditionReason is { } reason && !string.IsNullOrWhiteSpace(reason)
            ? reason.Trim()
            : pre.Detail;

    private static ActionBackup? FindBackup(TweakBackup backup, ActionSpec action) =>
        backup.Actions.FirstOrDefault(b =>
            string.Equals(b.Type, action.Type, StringComparison.Ordinal) &&
            string.Equals(b.Target, action.Target, StringComparison.OrdinalIgnoreCase));

    private void EnsureElevated()
    {
        if (_options.RequireElevation && !System.IsElevated) throw new ElevationRequiredException();
    }
}
