namespace Debloater.Core.Model;

/// <summary>Estado de uma única ação no sistema.</summary>
public enum ActionState
{
    /// <summary>Já está no estado desejado pelo tweak.</summary>
    Desired,
    /// <summary>Não está no estado desejado.</summary>
    NotDesired,
    /// <summary>O alvo não existe neste sistema (serviço, tarefa ou recurso ausente).</summary>
    NotApplicable,
    /// <summary>Não foi possível determinar.</summary>
    Error,
}

/// <summary>Estado agregado de um tweak.</summary>
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
    /// Regras:
    /// - qualquer ação com erro: Error;
    /// - ignorando as NotApplicable, se não sobrar nenhuma: NotApplicable;
    /// - todas Desired: Applied; nenhuma Desired: NotApplied; senão: Partial.
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
