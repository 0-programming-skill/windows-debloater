using Debloater.Core.Admx;
using Debloater.Core.Model;
using Debloater.Core.Policies;

namespace Debloater.Core.Planning;

/// <summary>Uma mudança de política pedida pelo usuário: configurar (Setting) ou reverter ao backup (Setting null).</summary>
public sealed record PolicyRequest(AdmxPolicy Policy, PolicySetting? Setting)
{
    public bool IsRevert => Setting is null;
}

/// <summary>Mudança de política pendente, com o plano de gravação (prévia exata do registro), risco e avisos.</summary>
public sealed record PlannedPolicyChange(
    PolicyRequest Request,
    PolicyWritePlan? Plan,
    RiskLevel Risk,
    IReadOnlyList<PolicyWarning> Warnings,
    string? BlockedReason = null)
{
    public AdmxPolicy Policy => Request.Policy;
    public bool IsRevert => Request.IsRevert;
    public bool IsBlocked => BlockedReason is not null;

    public string Title => (IsRevert ? "Reverter política: " : $"Política → {PolicySetting.StateText(Request.Setting!.State)}: ")
        + (string.IsNullOrWhiteSpace(Policy.DisplayName) ? Policy.Name : Policy.DisplayName);

    /// <summary>Linhas da prévia do registro (ou o que o backup restaura).</summary>
    public IReadOnlyList<string> Preview { get; init; } = [];
}

/// <summary>
/// Calcula as mudanças de política pendentes. Regras:
///   configurar → plano do <see cref="PolicyWriter"/>; inválido = bloqueado (com os erros); sem efeito = omitido;
///   reverter   → exige backup; a prévia lista os valores originais que serão restaurados.
/// Risco: <see cref="PolicyAdvisor.Risk"/> (Médio por padrão; Alto para segurança/Update/Defender).
/// </summary>
public static class PolicyChangePlanner
{
    public static IReadOnlyList<PlannedPolicyChange> Plan(
        IEnumerable<PolicyRequest> requests,
        PolicyEngine engine,
        Func<AdmxPolicy, PolicyTargetState, PolicyScope, IReadOnlyList<PolicyWarning>>? warnings = null)
    {
        var result = new List<PlannedPolicyChange>();
        foreach (var req in requests)
        {
            var risk = PolicyAdvisor.Risk(req.Policy);
            if (req.Setting is not { } setting)
            {
                var backup = engine.Backups.Load(req.Policy.QualifiedName);
                if (backup is null)
                {
                    result.Add(new PlannedPolicyChange(req, null, risk, [],
                        "Sem backup: a política não foi alterada por este programa, então não há valor original para restaurar."));
                    continue;
                }
                var preview = backup.Values.Select(v => v.Existed
                        ? $@"+ {v.Root}\{v.Key}\{v.Name} = {PolicyValues.Describe(PolicyValues.FromBackup(v.Kind ?? "string", v.Data, v.MultiData))} (original)"
                        : $@"- {v.Root}\{v.Key}\{v.Name} (não existia)")
                    .Concat(backup.DeletedKeys.Select(k => $@"+ {k.Root}\{k.Key}\ (chave recriada)"))
                    .ToList();
                result.Add(new PlannedPolicyChange(req, null, risk, []) { Preview = preview });
                continue;
            }

            var plan = engine.Plan(req.Policy, setting);
            var scope = setting.EffectiveScope(req.Policy.Class);
            var warn = warnings?.Invoke(req.Policy, setting.State, scope) ?? [];
            if (!plan.IsValid)
            {
                result.Add(new PlannedPolicyChange(req, plan, risk, warn, "Configuração inválida: " + string.Join(" ", plan.Errors)));
                continue;
            }
            if (!plan.HasEffect) continue;
            result.Add(new PlannedPolicyChange(req, plan, risk, warn) { Preview = plan.Describe().ToList() });
        }
        return result
            .OrderByDescending(c => c.Risk)
            .ThenBy(c => c.Policy.DisplayName, StringComparer.CurrentCulture)
            .ToList();
    }

    public static RiskLevel MaxRisk(IEnumerable<PlannedPolicyChange> changes) =>
        changes.Where(c => !c.IsBlocked).Select(c => c.Risk).DefaultIfEmpty(RiskLevel.Low).Max();
}
