using Debloater.Core.Model;

namespace Debloater.Core.SelfTest;

/// <summary>Final selftest verdict of a tweak.</summary>
public enum SelfTestVerdict
{
    /// <summary>Applied, ended up Applied, backup ok, reverted and each action went back to its previous value.</summary>
    Pass,
    /// <summary>Apply threw an exception or returned an error.</summary>
    ApplyFailed,
    /// <summary>Apply "succeeded", but the state afterwards is not Applied.</summary>
    NotAppliedAfterApply,
    /// <summary>Revert threw an exception or returned an error.</summary>
    RevertFailed,
    /// <summary>Revert "succeeded", but some action did not go back to the value detected before applying.</summary>
    NotRestored,
    /// <summary>Apply/revert worked, but the backup file did not behave as expected
    /// (it was not written, re-applying changed it, or it was not deleted after revert).</summary>
    BackupProblem,
    /// <summary>Not tested (reason in <see cref="TweakSelfTestResult.Reason"/>).</summary>
    Skipped,
}

/// <summary>
/// Persistence test verdict: after applying, restarting and waiting, did Windows keep the tweak?
/// </summary>
public enum PersistenceVerdict
{
    /// <summary>All actions that reached the desired state on apply are still in it.</summary>
    Persisted,
    /// <summary>Windows undid all actions (the tweak went back to NotApplied).</summary>
    RevertedByWindows,
    /// <summary>Windows undid some of the actions.</summary>
    PartiallyReverted,
}

/// <summary>Selftest phase. Full = complete cycle; the others split the cycle for the persistence test.</summary>
public enum SelfTestPhase
{
    Full,
    /// <summary>Only reads the state before, applies, checks and re-applies. Writes the state file.</summary>
    ApplyOnly,
    /// <summary>Re-reads the state of the applied tweaks and compares it with the state read right after applying.</summary>
    Verify,
    /// <summary>Reverts the applied tweaks and compares with the state before applying.</summary>
    Revert,
}

/// <summary>A persistence check (e.g. "after-restart-1"), per tweak.</summary>
public sealed class PersistenceCheck
{
    public string Label { get; set; } = "";
    public DateTime TimeUtc { get; set; }
    public TweakState? State { get; set; }
    public PersistenceVerdict Verdict { get; set; }
    /// <summary>Actions that were in the desired state after applying and no longer are (type:target: before → now).</summary>
    public List<string> RevertedActions { get; set; } = [];
}

/// <summary>State of an action in a persistence check.</summary>
public sealed class LabeledSnapshot
{
    public string Label { get; set; } = "";
    public ActionSnapshot Snapshot { get; set; } = new();
}

/// <summary>Selftest options.</summary>
public sealed class SelfTestOptions
{
    /// <summary>Tweaks with risk above this are skipped. Default: all (Critical).</summary>
    public RiskLevel MaxRisk { get; init; } = RiskLevel.Critical;

    /// <summary>Test irreversible tweaks (appx). Only applies and checks the state.</summary>
    public bool IncludeIrreversible { get; init; }

    /// <summary>Re-apply after applying, to check that the original backup is preserved.</summary>
    public bool CheckReapply { get; init; } = true;

    /// <summary>Apply-only phase (persistence test): stops after applying, without reverting.</summary>
    public bool ApplyOnly { get; init; }
}

/// <summary>Detected state of an action at a point in the test.</summary>
public sealed class ActionSnapshot
{
    public ActionState State { get; set; }
    public string Detail { get; set; } = "";

    public static ActionSnapshot From(ActionStatus s) => new() { State = s.State, Detail = s.Detail };

    /// <summary>Same state and same detected value (the detail carries the current value: "current: 1 (dword)", "absent"...).</summary>
    public bool SameAs(ActionSnapshot? other) =>
        other is not null && other.State == State && string.Equals(other.Detail, Detail, StringComparison.Ordinal);

    public override string ToString() => $"{State} ({Detail})";
}

/// <summary>A tweak action throughout the test.</summary>
public sealed class ActionSelfTest
{
    public string Type { get; set; } = "";
    public string Target { get; set; } = "";
    public ActionSnapshot? Before { get; set; }
    public ActionSnapshot? AfterApply { get; set; }
    public ActionSnapshot? AfterRevert { get; set; }

    /// <summary>State at each persistence check (verify phase), in order.</summary>
    public List<LabeledSnapshot> Checks { get; set; } = [];

    /// <summary>Value after revert equal to the one before? null when there was no revert.</summary>
    public bool? Restored { get; set; }
}

/// <summary>Result of a step (apply, re-apply, revert).</summary>
public sealed class StepResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public List<string> Errors { get; set; } = [];
    /// <summary>Warnings that are not errors (e.g. a service that only stops after a restart).</summary>
    public List<string> Notes { get; set; } = [];
    /// <summary>Exception thrown by the step (type: message), if any.</summary>
    public string? Exception { get; set; }
    public long DurationMs { get; set; }

    public string Describe()
    {
        if (Exception is not null) return Exception;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(Message)) parts.Add(Message!);
        parts.AddRange(Errors);
        return string.Join(" | ", parts);
    }
}

/// <summary>What happened to the tweak's backup file.</summary>
public sealed class BackupCheck
{
    /// <summary>A backup already existed before the test (the tweak is then skipped, so as not to mix up the original).</summary>
    public bool ExistedBefore { get; set; }
    public bool WrittenOnApply { get; set; }
    /// <summary>Did re-applying keep the backup byte for byte? null if there was no re-apply.</summary>
    public bool? KeptOnReapply { get; set; }
    /// <summary>Did a successful revert delete the backup? null if there was no successful revert.</summary>
    public bool? DeletedAfterRevert { get; set; }
    /// <summary>Contents of the backup written on apply.</summary>
    public string? Json { get; set; }
}

/// <summary>Complete selftest result of a tweak.</summary>
public sealed class TweakSelfTestResult
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public RiskLevel Risk { get; set; }
    public bool Reversible { get; set; }
    public List<string> ActionTypes { get; set; } = [];

    public SelfTestVerdict Verdict { get; set; }
    /// <summary>Why it did not pass (or why it was skipped).</summary>
    public string? Reason { get; set; }

    public TweakState? StateBefore { get; set; }
    public TweakState? StateAfterApply { get; set; }
    public TweakState? StateAfterRevert { get; set; }

    public StepResult? Apply { get; set; }
    public StepResult? Reapply { get; set; }
    public StepResult? Revert { get; set; }
    public BackupCheck Backup { get; set; } = new();

    /// <summary>Persistence checks (verify phase), in the order they were made.</summary>
    public List<PersistenceCheck> Persistence { get; set; } = [];

    /// <summary>Verdict of the last persistence check (null = not checked).</summary>
    public PersistenceVerdict? PersistenceVerdict { get; set; }

    /// <summary>Label of the first check in which Windows undid some action (e.g. "after-restart-1").</summary>
    public string? RevertedAfter { get; set; }

    public List<string> RebootPendingBefore { get; set; } = [];
    public List<string> RebootPendingAfterApply { get; set; } = [];
    public List<string> RebootPendingAfterRevert { get; set; } = [];

    /// <summary>A reboot became pending because of the test (it was not pending before).</summary>
    public bool RebootPending =>
        RebootPendingAfterApply.Except(RebootPendingBefore).Any() || RebootPendingAfterRevert.Except(RebootPendingBefore).Any();

    public List<ActionSelfTest> Actions { get; set; } = [];
    public List<string> Notes { get; set; } = [];
    public DateTime StartedUtc { get; set; }
    public long DurationMs { get; set; }
}

/// <summary>Result of creating the restore point before the test (optional).</summary>
public sealed class RestorePointCheck
{
    public bool Created { get; set; }
    public string Message { get; set; } = "";
    public long DurationMs { get; set; }
}

/// <summary>Selftest report (written as JSON with --json).</summary>
public sealed class SelfTestReport
{
    public DateTime StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    /// <summary>Last phase run on this report (the persistence test's state file is the report itself).</summary>
    public SelfTestPhase Phase { get; set; }
    /// <summary>Phases run, with date (e.g. "ApplyOnly 2026-10-16T12:00:00Z", "Verify after-restart-1 ...").</summary>
    public List<string> PhaseLog { get; set; } = [];
    public string Machine { get; set; } = Environment.MachineName;
    public string Windows { get; set; } = "";
    public string VmCheck { get; set; } = "";
    public bool VmCheckOverridden { get; set; }
    public string BackupDirectory { get; set; } = "";
    public RiskLevel MaxRisk { get; set; }
    public bool IncludeIrreversible { get; set; }
    public RestorePointCheck? RestorePoint { get; set; }
    public List<TweakSelfTestResult> Results { get; set; } = [];

    public Dictionary<SelfTestVerdict, int> Summary =>
        Results.GroupBy(r => r.Verdict).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count());

    public Dictionary<PersistenceVerdict, int> PersistenceSummary =>
        Results.Where(r => r.PersistenceVerdict is not null).GroupBy(r => r.PersistenceVerdict!.Value)
            .OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>Some tested tweak failed (any verdict other than Pass or Skipped).</summary>
    public bool HasFailures => Results.Any(r => r.Verdict is not (SelfTestVerdict.Pass or SelfTestVerdict.Skipped));

    /// <summary>Windows undid some tweak (fully or partially) in a persistence check.</summary>
    public bool HasPersistenceFailures => Results.Any(r => r.PersistenceVerdict is not (null or SelfTest.PersistenceVerdict.Persisted));
}
