using System.Diagnostics;
using Debloater.Core.Engine;
using Debloater.Core.Model;

namespace Debloater.Core.SelfTest;

/// <summary>
/// Runs the full cycle of each tweak: state before, apply, state after (expects Applied),
/// re-apply (the original backup must stay intact), revert, state after revert and an
/// action-by-action comparison with the state before. Meant to run in a disposable VM: it CHANGES the system.
/// Tweaks are tested one at a time, in sequence, so that one does not interfere with reading another.
/// </summary>
public sealed class SelfTestRunner(ISelfTestHost host, SelfTestOptions? options = null)
{
    private readonly SelfTestOptions _options = options ?? new SelfTestOptions();

    /// <summary>Raised when each tweak finishes (to show progress and write the partial report).</summary>
    public event Action<TweakSelfTestResult>? TweakCompleted;

    /// <summary>Raised when each tweak starts.</summary>
    public event Action<Tweak>? TweakStarting;

    public async Task<List<TweakSelfTestResult>> RunAsync(IEnumerable<Tweak> tweaks, CancellationToken ct = default)
    {
        var results = new List<TweakSelfTestResult>();
        foreach (var tweak in tweaks)
        {
            ct.ThrowIfCancellationRequested();
            TweakStarting?.Invoke(tweak);
            var result = await RunOneAsync(tweak, ct).ConfigureAwait(false);
            results.Add(result);
            TweakCompleted?.Invoke(result);
        }
        return results;
    }

    public async Task<TweakSelfTestResult> RunOneAsync(Tweak tweak, CancellationToken ct = default)
    {
        var total = Stopwatch.StartNew();
        var r = new TweakSelfTestResult
        {
            Id = tweak.Id,
            Name = tweak.Name,
            Category = tweak.Category,
            Risk = tweak.Risk,
            Reversible = tweak.Reversible,
            ActionTypes = tweak.Actions.Select(a => a.Type).Distinct(StringComparer.Ordinal).ToList(),
            StartedUtc = DateTime.UtcNow,
            Actions = tweak.Actions.Select(a => new ActionSelfTest { Type = a.Type, Target = a.Target }).ToList(),
        };

        try
        {
            await RunStepsAsync(tweak, r, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unexpected failure outside the steps (e.g. reading state): record it and move on to the next tweak.
            r.Notes.Add($"unexpected exception: {Describe(ex)}");
            if (r.Verdict != SelfTestVerdict.Skipped && r.Apply is null)
                r.Apply = new StepResult { Success = false, Exception = Describe(ex) };
        }

        if (r.Verdict != SelfTestVerdict.Skipped) Judge(r, _options.ApplyOnly);
        r.DurationMs = total.ElapsedMilliseconds;
        return r;
    }

    private static void Judge(TweakSelfTestResult r, bool applyOnly)
    {
        SelfTestEvaluator.CompareActions(r);
        (r.Verdict, r.Reason) = SelfTestEvaluator.Evaluate(r, applyOnly);
        if (r.Verdict == SelfTestVerdict.NotRestored && r.RebootPending && r.ActionTypes.Contains(ActionSpec.OptionalFeature))
            r.Notes.Add("a reboot is pending: the optional feature state may only settle after a restart.");
    }

    /// <summary>
    /// Verify phase of the persistence test: re-reads the state of each tweak successfully applied in the
    /// apply-only phase and records, with the label, whether Windows kept it (see <see cref="SelfTestEvaluator.RecordPersistence"/>).
    /// </summary>
    public async Task VerifyAsync(IReadOnlyList<TweakSelfTestResult> results, IReadOnlyDictionary<string, Tweak> tweaks, string label,
        Action<TweakSelfTestResult, PersistenceCheck>? onChecked = null, CancellationToken ct = default)
    {
        foreach (var r in results)
        {
            ct.ThrowIfCancellationRequested();
            if (r.Verdict != SelfTestVerdict.Pass || r.StateAfterApply != TweakState.Applied) continue;
            if (!tweaks.TryGetValue(r.Id, out var tweak))
            {
                r.Notes.Add($"{label}: tweak no longer exists in the catalog; not checked.");
                continue;
            }
            var status = await host.GetStatusAsync(tweak, ct).ConfigureAwait(false);
            var check = SelfTestEvaluator.RecordPersistence(r, status, label, DateTime.UtcNow);
            onChecked?.Invoke(r, check);
        }
    }

    /// <summary>
    /// Revert phase of the persistence test: reverts each reversible tweak applied in the apply-only phase,
    /// re-reads the state, compares action by action with the state before and recomputes the full-cycle verdict.
    /// </summary>
    public async Task RevertAppliedAsync(IReadOnlyList<TweakSelfTestResult> results, IReadOnlyDictionary<string, Tweak> tweaks,
        CancellationToken ct = default)
    {
        foreach (var r in results)
        {
            ct.ThrowIfCancellationRequested();
            if (r.Verdict == SelfTestVerdict.Skipped || !r.Reversible || r.Apply is null) continue;
            if (!tweaks.TryGetValue(r.Id, out var tweak))
            {
                r.Notes.Add("revert: tweak no longer exists in the catalog; not reverted.");
                continue;
            }
            var sw = Stopwatch.StartNew();
            TweakStarting?.Invoke(tweak);
            try
            {
                await RevertStepsAsync(tweak, r, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                r.Notes.Add($"unexpected exception while reverting: {Describe(ex)}");
            }
            Judge(r, applyOnly: false);
            r.DurationMs += sw.ElapsedMilliseconds;
            TweakCompleted?.Invoke(r);
        }
    }

    private async Task RunStepsAsync(Tweak tweak, TweakSelfTestResult r, CancellationToken ct)
    {
        // 0) Filters that do not require touching the system.
        if (tweak.Risk > _options.MaxRisk)
        {
            Skip(r, $"risk {tweak.Risk} above --max-risk {_options.MaxRisk}");
            return;
        }
        if (!tweak.Reversible && !_options.IncludeIrreversible)
        {
            Skip(r, "irreversible (use --include-irreversible to test apply + state only)");
            return;
        }

        // 1) State before.
        var before = await host.GetStatusAsync(tweak, ct).ConfigureAwait(false);
        r.StateBefore = before.State;
        Record(r, before, (a, s) => a.Before = s);
        if (before.State == TweakState.NotApplicable)
        {
            Skip(r, "does not apply to this system: " + (before.Message ?? DescribeActions(before)));
            return;
        }
        if (before.State == TweakState.Error)
        {
            Skip(r, "error reading the initial state: " + (before.Message ?? DescribeActions(before)));
            return;
        }
        if (before.State == TweakState.Applied)
            r.Notes.Add("already applied before the test; apply/revert must keep the current values.");

        // A backup from a previous run would mask this test's original value.
        if (host.ReadBackupJson(tweak.Id) is not null)
        {
            r.Backup.ExistedBefore = true;
            Skip(r, "a backup of this tweak already exists (previous run?); delete it or use another --backup-dir");
            return;
        }

        r.RebootPendingBefore = [.. host.RebootPendingReasons()];

        // 2) Apply.
        r.Apply = await RunStepAsync(() => host.ApplyAsync(tweak, ct)).ConfigureAwait(false);
        r.Backup.Json = host.ReadBackupJson(tweak.Id);
        r.Backup.WrittenOnApply = r.Backup.Json is not null;

        var afterApply = await host.GetStatusAsync(tweak, ct).ConfigureAwait(false);
        r.StateAfterApply = afterApply.State;
        Record(r, afterApply, (a, s) => a.AfterApply = s);
        r.RebootPendingAfterApply = [.. host.RebootPendingReasons()];

        // 3) Re-apply: the backup must stay identical (it keeps the true ORIGINAL value).
        if (_options.CheckReapply && r.Apply.Success && r.Backup.Json is not null)
        {
            r.Reapply = await RunStepAsync(() => host.ApplyAsync(tweak, ct)).ConfigureAwait(false);
            r.Backup.KeptOnReapply = string.Equals(host.ReadBackupJson(tweak.Id), r.Backup.Json, StringComparison.Ordinal);
            if (!r.Reapply.Success) r.Notes.Add($"re-apply failed: {r.Reapply.Describe()}");
        }

        if (!tweak.Reversible || _options.ApplyOnly) return;
        await RevertStepsAsync(tweak, r, ct).ConfigureAwait(false);
    }

    private async Task RevertStepsAsync(Tweak tweak, TweakSelfTestResult r, CancellationToken ct)
    {
        // 4) Revert. Even if apply failed, try it (cleanup), as long as there is a backup.
        if (host.ReadBackupJson(tweak.Id) is null)
        {
            r.Notes.Add(r.Apply is { Success: true }
                ? "no backup at revert time: revert not attempted."
                : "apply failed and there is no backup: revert not attempted.");
            return;
        }
        r.Revert = await RunStepAsync(() => host.RevertAsync(tweak, ct)).ConfigureAwait(false);
        if (r.Revert.Success) r.Backup.DeletedAfterRevert = host.ReadBackupJson(tweak.Id) is null;

        var afterRevert = await host.GetStatusAsync(tweak, ct).ConfigureAwait(false);
        r.StateAfterRevert = afterRevert.State;
        Record(r, afterRevert, (a, s) => a.AfterRevert = s);
        r.RebootPendingAfterRevert = [.. host.RebootPendingReasons()];
    }

    private static async Task<StepResult> RunStepAsync(Func<Task<OperationResult>> step)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await step().ConfigureAwait(false);
            return new StepResult
            {
                Success = result.Success,
                Message = result.Message,
                Errors = [.. result.Errors],
                Notes = [.. result.Notes],
                DurationMs = sw.ElapsedMilliseconds,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new StepResult { Success = false, Exception = Describe(ex), DurationMs = sw.ElapsedMilliseconds };
        }
    }

    /// <summary>Copies each action's state into the result, matching by position (actions come in YAML order).</summary>
    private static void Record(TweakSelfTestResult r, TweakStatus status, Action<ActionSelfTest, ActionSnapshot> set)
    {
        for (int i = 0; i < r.Actions.Count && i < status.Actions.Count; i++)
            set(r.Actions[i], ActionSnapshot.From(status.Actions[i]));
    }

    private static void Skip(TweakSelfTestResult r, string reason)
    {
        r.Verdict = SelfTestVerdict.Skipped;
        r.Reason = reason;
    }

    private static string DescribeActions(TweakStatus s) =>
        s.Actions.Count == 0 ? s.State.ToString() : string.Join("; ", s.Actions.Select(a => $"{a.Action}: {a.State} ({a.Detail})"));

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";
}
