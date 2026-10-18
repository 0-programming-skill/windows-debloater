using Debloater.Core.Model;
using Debloater.Core.Profiles;

namespace Debloater.Core.Planning;

public enum ChangeKind
{
    Apply,
    Revert,
}

/// <summary>A pending change: apply or revert a tweak.</summary>
/// <param name="BlockedReason">Set when the change cannot be executed (e.g. revert without a backup).</param>
public sealed record PlannedChange(Tweak Tweak, ChangeKind Kind, TweakState CurrentState, string? BlockedReason = null)
{
    public bool IsBlocked => BlockedReason is not null;
}

/// <summary>
/// Computes the pending changes from each tweak's current state and the desired state.
/// Rules:
///   desired Applied     + current NotApplied/Partial/Error → apply;
///   desired NotApplied  + current Applied/Partial          → revert (requires a backup and a reversible tweak);
///   current NotApplicable, or already in the desired state → nothing.
/// </summary>
public static class ChangePlanner
{
    public static bool NeedsChange(TweakState current, DesiredState desired) => desired switch
    {
        DesiredState.Applied => current is TweakState.NotApplied or TweakState.Partial or TweakState.Error,
        DesiredState.NotApplied => current is TweakState.Applied or TweakState.Partial,
        _ => false,
    };

    public static IReadOnlyList<PlannedChange> Plan(
        IEnumerable<TweakStatus> statuses,
        IReadOnlyDictionary<string, DesiredState> desired,
        Func<string, bool> hasBackup)
    {
        var changes = new List<PlannedChange>();
        foreach (var status in statuses)
        {
            if (!desired.TryGetValue(status.Tweak.Id, out var want)) continue;
            if (!NeedsChange(status.State, want)) continue;

            if (want == DesiredState.Applied)
            {
                changes.Add(new PlannedChange(status.Tweak, ChangeKind.Apply, status.State));
                continue;
            }

            string? blocked = null;
            if (!status.Tweak.Reversible)
                blocked = "This tweak cannot be reverted by the program (e.g. removed app; reinstall it from the Microsoft Store).";
            else if (!hasBackup(status.Tweak.Id))
                blocked = "No backup: the tweak was not applied by this program, so there is no original value to restore.";
            changes.Add(new PlannedChange(status.Tweak, ChangeKind.Revert, status.State, blocked));
        }
        // From highest to lowest risk, then by category and name: what needs attention comes first.
        return changes
            .OrderByDescending(c => c.Tweak.Risk)
            .ThenBy(c => c.Tweak.Category, StringComparer.CurrentCulture)
            .ThenBy(c => c.Tweak.DisplayName, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>Maximum risk among the executable changes (for the engine's risk gate).</summary>
    public static RiskLevel MaxRisk(IEnumerable<PlannedChange> changes) =>
        changes.Where(c => !c.IsBlocked).Select(c => c.Tweak.Risk).DefaultIfEmpty(RiskLevel.Low).Max();
}
