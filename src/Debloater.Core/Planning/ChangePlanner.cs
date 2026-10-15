using Debloater.Core.Model;
using Debloater.Core.Profiles;

namespace Debloater.Core.Planning;

public enum ChangeKind
{
    Apply,
    Revert,
}

/// <summary>Uma mudança pendente: aplicar ou reverter um tweak.</summary>
/// <param name="BlockedReason">Preenchido quando a mudança não pode ser executada (ex.: reverter sem backup).</param>
public sealed record PlannedChange(Tweak Tweak, ChangeKind Kind, TweakState CurrentState, string? BlockedReason = null)
{
    public bool IsBlocked => BlockedReason is not null;
}

/// <summary>
/// Calcula as mudanças pendentes a partir do estado atual de cada tweak e do estado desejado.
/// Regras:
///   desejado Aplicado     + atual NãoAplicado/Parcial/Erro → aplicar;
///   desejado NãoAplicado  + atual Aplicado/Parcial         → reverter (exige backup e tweak reversível);
///   atual NãoSeAplica, ou já no estado desejado            → nada.
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
                blocked = "Este ajuste não é reversível pelo programa (ex.: app removido; reinstale pela Microsoft Store).";
            else if (!hasBackup(status.Tweak.Id))
                blocked = "Sem backup: o ajuste não foi aplicado por este programa, então não há valor original para restaurar.";
            changes.Add(new PlannedChange(status.Tweak, ChangeKind.Revert, status.State, blocked));
        }
        // Do maior para o menor risco, depois por categoria e nome: o que exige atenção aparece primeiro.
        return changes
            .OrderByDescending(c => c.Tweak.Risk)
            .ThenBy(c => c.Tweak.Category, StringComparer.CurrentCulture)
            .ThenBy(c => c.Tweak.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>Risco máximo entre as mudanças executáveis (para o controle de risco do engine).</summary>
    public static RiskLevel MaxRisk(IEnumerable<PlannedChange> changes) =>
        changes.Where(c => !c.IsBlocked).Select(c => c.Tweak.Risk).DefaultIfEmpty(RiskLevel.Low).Max();
}
