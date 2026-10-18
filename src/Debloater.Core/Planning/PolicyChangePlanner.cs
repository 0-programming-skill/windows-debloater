using Debloater.Core.Admx;
using Debloater.Core.Model;
using Debloater.Core.Policies;

namespace Debloater.Core.Planning;

/// <summary>A policy change requested by the user: configure (Setting) or revert to the backup (Setting null).</summary>
public sealed record PolicyRequest(AdmxPolicy Policy, PolicySetting? Setting)
{
    public bool IsRevert => Setting is null;
}

/// <summary>Pending policy change, with the write plan (exact registry preview), risk and warnings.</summary>
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

    public string Title => (IsRevert ? "Revert policy: " : $"Policy → {PolicySetting.StateText(Request.Setting!.State)}: ")
        + (string.IsNullOrWhiteSpace(Policy.DisplayName) ? Policy.Name : Policy.DisplayName);

    /// <summary>Registry preview lines (or what the backup restores).</summary>
    public IReadOnlyList<string> Preview { get; init; } = [];
}

/// <summary>
/// Computes the pending policy changes. Rules:
///   configure → plan from <see cref="PolicyWriter"/>; invalid = blocked (with the errors); no effect = omitted;
///   revert    → requires a backup; the preview lists the original values that will be restored.
/// Risk: <see cref="PolicyAdvisor.Risk"/> (Medium by default; High for security/Update/Defender).
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
                        "No backup: the policy was not changed by this program, so there is no original value to restore."));
                    continue;
                }
                var preview = backup.Values.Select(v => v.Existed
                        ? $@"+ {v.Root}\{v.Key}\{v.Name} = {PolicyValues.Describe(PolicyValues.FromBackup(v.Kind ?? "string", v.Data, v.MultiData))} (original)"
                        : $@"- {v.Root}\{v.Key}\{v.Name} (did not exist)")
                    .Concat(backup.DeletedKeys.Select(k => $@"+ {k.Root}\{k.Key}\ (key recreated)"))
                    .ToList();
                result.Add(new PlannedPolicyChange(req, null, risk, []) { Preview = preview });
                continue;
            }

            var plan = engine.Plan(req.Policy, setting);
            var scope = setting.EffectiveScope(req.Policy.Class);
            var warn = warnings?.Invoke(req.Policy, setting.State, scope) ?? [];
            if (!plan.IsValid)
            {
                result.Add(new PlannedPolicyChange(req, plan, risk, warn, "Invalid setting: " + string.Join(" ", plan.Errors)));
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
