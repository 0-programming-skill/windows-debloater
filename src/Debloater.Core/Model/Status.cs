namespace Debloater.Core.Model;

/// <summary>State of a single action on the system.</summary>
public enum ActionState
{
    /// <summary>Already in the state the tweak wants.</summary>
    Desired,
    /// <summary>Not in the desired state.</summary>
    NotDesired,
    /// <summary>The target does not exist on this system (missing service, task or feature).</summary>
    NotApplicable,
    /// <summary>Could not be determined.</summary>
    Error,
}

/// <summary>Aggregate state of a tweak.</summary>
public enum TweakState
{
    Applied,
    NotApplied,
    Partial,
    NotApplicable,
    Error,
}

public sealed record ActionStatus(ActionSpec Action, ActionState State, string Detail);

public sealed record TweakStatus(Tweak Tweak, TweakState State, IReadOnlyList<ActionStatus> Actions, string? Message = null);

public static class StatusAggregator
{
    /// <summary>
    /// Rules:
    /// - any action with an error: Error;
    /// - ignoring NotApplicable ones, if none are left: NotApplicable;
    /// - all Desired: Applied; none Desired: NotApplied; otherwise: Partial.
    /// </summary>
    public static TweakState Aggregate(IEnumerable<ActionState> states)
    {
        var list = states.ToList();
        if (list.Contains(ActionState.Error)) return TweakState.Error;
        var relevant = list.Where(s => s != ActionState.NotApplicable).ToList();
        if (relevant.Count == 0) return TweakState.NotApplicable;
        int desired = relevant.Count(s => s == ActionState.Desired);
        if (desired == relevant.Count) return TweakState.Applied;
        if (desired == 0) return TweakState.NotApplied;
        return TweakState.Partial;
    }
}
