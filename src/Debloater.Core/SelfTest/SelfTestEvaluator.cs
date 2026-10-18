using Debloater.Core.Model;

namespace Debloater.Core.SelfTest;

/// <summary>
/// Pure selftest logic: compares the actions' state before/after and decides the verdict from the
/// facts recorded in the <see cref="TweakSelfTestResult"/>. Does not touch the system.
/// </summary>
public static class SelfTestEvaluator
{
    /// <summary>
    /// Fills <see cref="ActionSelfTest.Restored"/>: after reverting, each action must have the SAME state and
    /// the SAME detected value as before applying (registry value restored or deleted, service startup
    /// type, task enabled/disabled, feature state).
    /// </summary>
    public static void CompareActions(TweakSelfTestResult result)
    {
        foreach (var a in result.Actions)
            a.Restored = a.AfterRevert is null ? null : a.Before is not null && a.Before.SameAs(a.AfterRevert);
    }

    /// <summary>
    /// Decides the verdict. Severity order: ApplyFailed, NotAppliedAfterApply, RevertFailed, NotRestored,
    /// BackupProblem, Pass. Irreversible tweaks are judged only by apply + state (+ backup written).
    /// A result already marked as Skipped is kept.
    /// </summary>
    /// <param name="applyOnly">Apply-only phase (persistence test): judges only apply, state and backup
    /// written/preserved on re-apply; the revert comes later, in the revert phase.</param>
    public static (SelfTestVerdict Verdict, string? Reason) Evaluate(TweakSelfTestResult r, bool applyOnly = false)
    {
        if (r.Verdict == SelfTestVerdict.Skipped) return (SelfTestVerdict.Skipped, r.Reason);

        if (r.Apply is null) return (SelfTestVerdict.ApplyFailed, "apply was not run");
        if (!r.Apply.Success) return (SelfTestVerdict.ApplyFailed, NonEmpty(r.Apply.Describe(), "apply failed"));

        if (r.StateAfterApply != TweakState.Applied)
        {
            var pending = r.Actions
                .Where(a => a.AfterApply is { State: not (ActionState.Desired or ActionState.NotApplicable) })
                .Select(a => $"{a.Type}:{a.Target} = {a.AfterApply}");
            return (SelfTestVerdict.NotAppliedAfterApply,
                $"state after apply: {r.StateAfterApply?.ToString() ?? "?"}; " + NonEmpty(string.Join("; ", pending), "no details"));
        }

        if (applyOnly)
        {
            if (!r.Backup.WrittenOnApply) return (SelfTestVerdict.BackupProblem, "the backup was not written on apply");
            if (r.Backup.KeptOnReapply == false) return (SelfTestVerdict.BackupProblem, "re-applying changed the original backup");
            return (SelfTestVerdict.Pass, null);
        }

        if (!r.Reversible)
        {
            return r.Backup.WrittenOnApply
                ? (SelfTestVerdict.Pass, null)
                : (SelfTestVerdict.BackupProblem, "the backup was not written on apply");
        }

        if (r.Revert is null) return (SelfTestVerdict.RevertFailed, "revert was not run");
        if (!r.Revert.Success) return (SelfTestVerdict.RevertFailed, NonEmpty(r.Revert.Describe(), "revert failed"));

        var notRestored = r.Actions.Where(a => a.Restored == false).ToList();
        if (notRestored.Count > 0)
        {
            var text = string.Join("; ", notRestored.Select(a => $"{a.Type}:{a.Target} before {a.Before}, after {a.AfterRevert}"));
            return (SelfTestVerdict.NotRestored, text);
        }
        if (r.Actions.Any(a => a.Restored is null))
            return (SelfTestVerdict.NotRestored, "state after revert was not read");

        var backupIssues = new List<string>();
        if (!r.Backup.WrittenOnApply) backupIssues.Add("the backup was not written on apply");
        if (r.Backup.KeptOnReapply == false) backupIssues.Add("re-applying changed the original backup");
        if (r.Backup.DeletedAfterRevert == false) backupIssues.Add("the backup was not deleted after revert");
        if (backupIssues.Count > 0) return (SelfTestVerdict.BackupProblem, string.Join("; ", backupIssues));

        return (SelfTestVerdict.Pass, null);
    }

    /// <summary>
    /// Records a persistence check: stores each action's state with the label and compares it with the
    /// state read right after applying. Only actions that were Desired after applying count; an action that
    /// is no longer Desired (or could not be read) was undone by Windows. Updates
    /// <see cref="TweakSelfTestResult.PersistenceVerdict"/> (the last check wins) and
    /// <see cref="TweakSelfTestResult.RevertedAfter"/> (the first check with something undone).
    /// </summary>
    public static PersistenceCheck RecordPersistence(TweakSelfTestResult r, TweakStatus status, string label, DateTime timeUtc)
    {
        for (int i = 0; i < r.Actions.Count && i < status.Actions.Count; i++)
            r.Actions[i].Checks.Add(new LabeledSnapshot { Label = label, Snapshot = ActionSnapshot.From(status.Actions[i]) });

        var check = new PersistenceCheck { Label = label, TimeUtc = timeUtc, State = status.State };
        int relevant = 0;
        foreach (var a in r.Actions)
        {
            if (a.AfterApply is not { State: ActionState.Desired }) continue;
            relevant++;
            var now = a.Checks.LastOrDefault(c => c.Label == label)?.Snapshot;
            if (now is { State: ActionState.Desired }) continue;
            var nowText = now?.ToString() ?? (status.Message is null ? "not read" : $"not read: {status.Message}");
            check.RevertedActions.Add($"{a.Type}:{a.Target}: after apply {a.AfterApply.Detail}, now {nowText}");
        }
        check.Verdict = check.RevertedActions.Count == 0 ? PersistenceVerdict.Persisted
            : check.RevertedActions.Count == relevant ? PersistenceVerdict.RevertedByWindows
            : PersistenceVerdict.PartiallyReverted;

        r.Persistence.Add(check);
        r.PersistenceVerdict = check.Verdict;
        if (check.Verdict != PersistenceVerdict.Persisted) r.RevertedAfter ??= label;
        return check;
    }

    private static string NonEmpty(string s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s;
}
