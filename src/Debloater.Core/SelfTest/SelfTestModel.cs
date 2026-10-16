using Debloater.Core.Model;

namespace Debloater.Core.SelfTest;

/// <summary>Veredito final do selftest de um tweak.</summary>
public enum SelfTestVerdict
{
    /// <summary>Aplicou, ficou Applied, backup ok, reverteu e cada ação voltou ao valor de antes.</summary>
    Pass,
    /// <summary>Aplicar lançou exceção ou retornou erro.</summary>
    ApplyFailed,
    /// <summary>Aplicar "deu certo", mas o estado depois não é Applied.</summary>
    NotAppliedAfterApply,
    /// <summary>Reverter lançou exceção ou retornou erro.</summary>
    RevertFailed,
    /// <summary>Reverter "deu certo", mas alguma ação não voltou ao valor detectado antes de aplicar.</summary>
    NotRestored,
    /// <summary>Apply/revert funcionaram, mas o arquivo de backup não se comportou como esperado
    /// (não foi gravado, re-aplicar o alterou, ou não foi apagado após reverter).</summary>
    BackupProblem,
    /// <summary>Não testado (motivo em <see cref="TweakSelfTestResult.Reason"/>).</summary>
    Skipped,
}

/// <summary>
/// Veredito do teste de persistência: depois de aplicar, reiniciar e esperar, o Windows manteve o tweak?
/// </summary>
public enum PersistenceVerdict
{
    /// <summary>Todas as ações que ficaram no estado desejado ao aplicar continuam nele.</summary>
    Persisted,
    /// <summary>O Windows desfez todas as ações (o tweak voltou a NotApplied).</summary>
    RevertedByWindows,
    /// <summary>O Windows desfez parte das ações.</summary>
    PartiallyReverted,
}

/// <summary>Fase do selftest. Full = ciclo completo; as outras dividem o ciclo para o teste de persistência.</summary>
public enum SelfTestPhase
{
    Full,
    /// <summary>Só lê o estado antes, aplica, confere e re-aplica. Grava o arquivo de estado.</summary>
    ApplyOnly,
    /// <summary>Relê o estado dos tweaks aplicados e compara com o lido logo depois de aplicar.</summary>
    Verify,
    /// <summary>Reverte os tweaks aplicados e compara com o estado de antes de aplicar.</summary>
    Revert,
}

/// <summary>Uma conferência de persistência (ex.: "apos-reinicio-1"), por tweak.</summary>
public sealed class PersistenceCheck
{
    public string Label { get; set; } = "";
    public DateTime TimeUtc { get; set; }
    public TweakState? State { get; set; }
    public PersistenceVerdict Verdict { get; set; }
    /// <summary>Ações que estavam no estado desejado depois de aplicar e não estão mais (tipo:alvo: antes → agora).</summary>
    public List<string> RevertedActions { get; set; } = [];
}

/// <summary>Estado de uma ação numa conferência de persistência.</summary>
public sealed class LabeledSnapshot
{
    public string Label { get; set; } = "";
    public ActionSnapshot Snapshot { get; set; } = new();
}

/// <summary>Opções do selftest.</summary>
public sealed class SelfTestOptions
{
    /// <summary>Tweaks com risco acima disto são pulados. Padrão: todos (Critical).</summary>
    public RiskLevel MaxRisk { get; init; } = RiskLevel.Critical;

    /// <summary>Testar tweaks irreversíveis (appx). Só aplica e confere o estado.</summary>
    public bool IncludeIrreversible { get; init; }

    /// <summary>Re-aplicar depois de aplicar, para conferir que o backup original é preservado.</summary>
    public bool CheckReapply { get; init; } = true;

    /// <summary>Fase apply-only (teste de persistência): para depois de aplicar, sem reverter.</summary>
    public bool ApplyOnly { get; init; }
}

/// <summary>Estado detectado de uma ação num momento do teste.</summary>
public sealed class ActionSnapshot
{
    public ActionState State { get; set; }
    public string Detail { get; set; } = "";

    public static ActionSnapshot From(ActionStatus s) => new() { State = s.State, Detail = s.Detail };

    /// <summary>Mesmo estado e mesmo valor detectado (o detalhe traz o valor atual: "atual: 1 (dword)", "ausente"...).</summary>
    public bool SameAs(ActionSnapshot? other) =>
        other is not null && other.State == State && string.Equals(other.Detail, Detail, StringComparison.Ordinal);

    public override string ToString() => $"{State} ({Detail})";
}

/// <summary>Uma ação do tweak ao longo do teste.</summary>
public sealed class ActionSelfTest
{
    public string Type { get; set; } = "";
    public string Target { get; set; } = "";
    public ActionSnapshot? Before { get; set; }
    public ActionSnapshot? AfterApply { get; set; }
    public ActionSnapshot? AfterRevert { get; set; }

    /// <summary>Estado em cada conferência de persistência (fase verify), na ordem.</summary>
    public List<LabeledSnapshot> Checks { get; set; } = [];

    /// <summary>Valor depois de reverter igual ao de antes? null quando não houve reversão.</summary>
    public bool? Restored { get; set; }
}

/// <summary>Resultado de uma etapa (aplicar, re-aplicar, reverter).</summary>
public sealed class StepResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public List<string> Errors { get; set; } = [];
    /// <summary>Avisos que não são erros (ex.: serviço que só para depois de reiniciar).</summary>
    public List<string> Notes { get; set; } = [];
    /// <summary>Exceção lançada pela etapa (tipo: mensagem), se houve.</summary>
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

/// <summary>O que aconteceu com o arquivo de backup do tweak.</summary>
public sealed class BackupCheck
{
    /// <summary>Já havia backup antes do teste (o tweak é então pulado, para não confundir o original).</summary>
    public bool ExistedBefore { get; set; }
    public bool WrittenOnApply { get; set; }
    /// <summary>Re-aplicar manteve o backup byte a byte? null se não houve re-aplicação.</summary>
    public bool? KeptOnReapply { get; set; }
    /// <summary>Reverter com sucesso apagou o backup? null se não houve reversão com sucesso.</summary>
    public bool? DeletedAfterRevert { get; set; }
    /// <summary>Conteúdo do backup gravado ao aplicar.</summary>
    public string? Json { get; set; }
}

/// <summary>Resultado completo do selftest de um tweak.</summary>
public sealed class TweakSelfTestResult
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public RiskLevel Risk { get; set; }
    public bool Reversible { get; set; }
    public List<string> ActionTypes { get; set; } = [];

    public SelfTestVerdict Verdict { get; set; }
    /// <summary>Por que não passou (ou por que foi pulado).</summary>
    public string? Reason { get; set; }

    public TweakState? StateBefore { get; set; }
    public TweakState? StateAfterApply { get; set; }
    public TweakState? StateAfterRevert { get; set; }

    public StepResult? Apply { get; set; }
    public StepResult? Reapply { get; set; }
    public StepResult? Revert { get; set; }
    public BackupCheck Backup { get; set; } = new();

    /// <summary>Conferências de persistência (fase verify), na ordem em que foram feitas.</summary>
    public List<PersistenceCheck> Persistence { get; set; } = [];

    /// <summary>Veredito da última conferência de persistência (null = não conferido).</summary>
    public PersistenceVerdict? PersistenceVerdict { get; set; }

    /// <summary>Rótulo da primeira conferência em que o Windows desfez alguma ação (ex.: "apos-reinicio-1").</summary>
    public string? RevertedAfter { get; set; }

    public List<string> RebootPendingBefore { get; set; } = [];
    public List<string> RebootPendingAfterApply { get; set; } = [];
    public List<string> RebootPendingAfterRevert { get; set; } = [];

    /// <summary>Uma reinicialização ficou pendente por causa do teste (não estava antes).</summary>
    public bool RebootPending =>
        RebootPendingAfterApply.Except(RebootPendingBefore).Any() || RebootPendingAfterRevert.Except(RebootPendingBefore).Any();

    public List<ActionSelfTest> Actions { get; set; } = [];
    public List<string> Notes { get; set; } = [];
    public DateTime StartedUtc { get; set; }
    public long DurationMs { get; set; }
}

/// <summary>Resultado de criar o ponto de restauração antes do teste (opcional).</summary>
public sealed class RestorePointCheck
{
    public bool Created { get; set; }
    public string Message { get; set; } = "";
    public long DurationMs { get; set; }
}

/// <summary>Relatório do selftest (gravado em JSON com --json).</summary>
public sealed class SelfTestReport
{
    public DateTime StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    /// <summary>Última fase executada sobre este relatório (o arquivo de estado do teste de persistência é o próprio relatório).</summary>
    public SelfTestPhase Phase { get; set; }
    /// <summary>Fases executadas, com data (ex.: "ApplyOnly 2026-10-16T12:00:00Z", "Verify apos-reinicio-1 ...").</summary>
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

    /// <summary>Algum tweak testado falhou (qualquer veredito que não seja Pass ou Skipped).</summary>
    public bool HasFailures => Results.Any(r => r.Verdict is not (SelfTestVerdict.Pass or SelfTestVerdict.Skipped));

    /// <summary>O Windows desfez algum tweak (total ou parcialmente) numa conferência de persistência.</summary>
    public bool HasPersistenceFailures => Results.Any(r => r.PersistenceVerdict is not (null or SelfTest.PersistenceVerdict.Persisted));
}
