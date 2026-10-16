using Debloater.Core.Model;

namespace Debloater.Core.SelfTest;

/// <summary>
/// Lógica pura do selftest: compara o estado das ações antes/depois e decide o veredito a partir dos
/// fatos registrados no <see cref="TweakSelfTestResult"/>. Não toca no sistema.
/// </summary>
public static class SelfTestEvaluator
{
    /// <summary>
    /// Preenche <see cref="ActionSelfTest.Restored"/>: depois de reverter, cada ação deve ter o MESMO estado e
    /// o MESMO valor detectado de antes de aplicar (valor de registro restaurado ou apagado, tipo de
    /// inicialização do serviço, tarefa habilitada/desabilitada, estado do recurso).
    /// </summary>
    public static void CompareActions(TweakSelfTestResult result)
    {
        foreach (var a in result.Actions)
            a.Restored = a.AfterRevert is null ? null : a.Before is not null && a.Before.SameAs(a.AfterRevert);
    }

    /// <summary>
    /// Decide o veredito. Ordem de gravidade: ApplyFailed, NotAppliedAfterApply, RevertFailed, NotRestored,
    /// BackupProblem, Pass. Irreversíveis são julgados só por aplicar + estado (+ backup gravado).
    /// Um resultado já marcado como Skipped é mantido.
    /// </summary>
    /// <param name="applyOnly">Fase apply-only (teste de persistência): julga só aplicar, estado e backup
    /// gravado/preservado ao re-aplicar; a reversão vem depois, na fase revert.</param>
    public static (SelfTestVerdict Verdict, string? Reason) Evaluate(TweakSelfTestResult r, bool applyOnly = false)
    {
        if (r.Verdict == SelfTestVerdict.Skipped) return (SelfTestVerdict.Skipped, r.Reason);

        if (r.Apply is null) return (SelfTestVerdict.ApplyFailed, "aplicar não foi executado");
        if (!r.Apply.Success) return (SelfTestVerdict.ApplyFailed, NonEmpty(r.Apply.Describe(), "aplicar falhou"));

        if (r.StateAfterApply != TweakState.Applied)
        {
            var pending = r.Actions
                .Where(a => a.AfterApply is { State: not (ActionState.Desired or ActionState.NotApplicable) })
                .Select(a => $"{a.Type}:{a.Target} = {a.AfterApply}");
            return (SelfTestVerdict.NotAppliedAfterApply,
                $"estado depois de aplicar: {r.StateAfterApply?.ToString() ?? "?"}; " + NonEmpty(string.Join("; ", pending), "sem detalhes"));
        }

        if (applyOnly)
        {
            if (!r.Backup.WrittenOnApply) return (SelfTestVerdict.BackupProblem, "o backup não foi gravado ao aplicar");
            if (r.Backup.KeptOnReapply == false) return (SelfTestVerdict.BackupProblem, "re-aplicar alterou o backup original");
            return (SelfTestVerdict.Pass, null);
        }

        if (!r.Reversible)
        {
            return r.Backup.WrittenOnApply
                ? (SelfTestVerdict.Pass, null)
                : (SelfTestVerdict.BackupProblem, "o backup não foi gravado ao aplicar");
        }

        if (r.Revert is null) return (SelfTestVerdict.RevertFailed, "reverter não foi executado");
        if (!r.Revert.Success) return (SelfTestVerdict.RevertFailed, NonEmpty(r.Revert.Describe(), "reverter falhou"));

        var notRestored = r.Actions.Where(a => a.Restored == false).ToList();
        if (notRestored.Count > 0)
        {
            var text = string.Join("; ", notRestored.Select(a => $"{a.Type}:{a.Target} antes {a.Before}, depois {a.AfterRevert}"));
            return (SelfTestVerdict.NotRestored, text);
        }
        if (r.Actions.Any(a => a.Restored is null))
            return (SelfTestVerdict.NotRestored, "estado depois de reverter não foi lido");

        var backupIssues = new List<string>();
        if (!r.Backup.WrittenOnApply) backupIssues.Add("o backup não foi gravado ao aplicar");
        if (r.Backup.KeptOnReapply == false) backupIssues.Add("re-aplicar alterou o backup original");
        if (r.Backup.DeletedAfterRevert == false) backupIssues.Add("o backup não foi apagado depois de reverter");
        if (backupIssues.Count > 0) return (SelfTestVerdict.BackupProblem, string.Join("; ", backupIssues));

        return (SelfTestVerdict.Pass, null);
    }

    /// <summary>
    /// Registra uma conferência de persistência: grava o estado de cada ação com o rótulo e compara com o
    /// estado lido logo depois de aplicar. Só contam as ações que ficaram Desired ao aplicar; uma ação que
    /// não está mais Desired (ou não pôde ser lida) foi desfeita pelo Windows. Atualiza
    /// <see cref="TweakSelfTestResult.PersistenceVerdict"/> (a última conferência vale) e
    /// <see cref="TweakSelfTestResult.RevertedAfter"/> (a primeira conferência com algo desfeito).
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
            var nowText = now?.ToString() ?? (status.Message is null ? "não lido" : $"não lido: {status.Message}");
            check.RevertedActions.Add($"{a.Type}:{a.Target}: depois de aplicar {a.AfterApply.Detail}, agora {nowText}");
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
