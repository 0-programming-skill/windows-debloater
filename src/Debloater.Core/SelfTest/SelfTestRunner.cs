using System.Diagnostics;
using Debloater.Core.Engine;
using Debloater.Core.Model;

namespace Debloater.Core.SelfTest;

/// <summary>
/// Executa o ciclo completo de cada tweak: estado antes, aplicar, estado depois (espera Applied),
/// re-aplicar (o backup original deve ficar intacto), reverter, estado depois de reverter e comparação
/// ação a ação com o estado de antes. Feito para rodar numa VM descartável: ALTERA o sistema.
/// Os tweaks são testados um por vez, em sequência, para que um não interfira na leitura do outro.
/// </summary>
public sealed class SelfTestRunner(ISelfTestHost host, SelfTestOptions? options = null)
{
    private readonly SelfTestOptions _options = options ?? new SelfTestOptions();

    /// <summary>Chamado ao terminar cada tweak (para mostrar progresso e gravar o relatório parcial).</summary>
    public event Action<TweakSelfTestResult>? TweakCompleted;

    /// <summary>Chamado ao começar cada tweak.</summary>
    public event Action<Tweak>? TweakStarting;

    public async Task<List<TweakSelfTestResult>> RunAsync(IEnumerable<Tweak> tweaks, CancellationToken ct = default)
    {
        var results = new List<TweakSelfTestResult>();
        foreach (var tweak in tweaks)
        {
            ct.ThrowIfCancellationRequested();
            TweakStarting?.Invoke(tweak);
            var result = await RunOneAsync(tweak, ct).ConfigureAwait(false);
            results.Add(result);
            TweakCompleted?.Invoke(result);
        }
        return results;
    }

    public async Task<TweakSelfTestResult> RunOneAsync(Tweak tweak, CancellationToken ct = default)
    {
        var total = Stopwatch.StartNew();
        var r = new TweakSelfTestResult
        {
            Id = tweak.Id,
            Name = tweak.Name,
            Category = tweak.Category,
            Risk = tweak.Risk,
            Reversible = tweak.Reversible,
            ActionTypes = tweak.Actions.Select(a => a.Type).Distinct(StringComparer.Ordinal).ToList(),
            StartedUtc = DateTime.UtcNow,
            Actions = tweak.Actions.Select(a => new ActionSelfTest { Type = a.Type, Target = a.Target }).ToList(),
        };

        try
        {
            await RunStepsAsync(tweak, r, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Falha inesperada fora das etapas (ex.: ler estado): registra e segue para o próximo tweak.
            r.Notes.Add($"exceção inesperada: {Describe(ex)}");
            if (r.Verdict != SelfTestVerdict.Skipped && r.Apply is null)
                r.Apply = new StepResult { Success = false, Exception = Describe(ex) };
        }

        if (r.Verdict != SelfTestVerdict.Skipped)
        {
            SelfTestEvaluator.CompareActions(r);
            (r.Verdict, r.Reason) = SelfTestEvaluator.Evaluate(r);
            if (r.Verdict == SelfTestVerdict.NotRestored && r.RebootPending && r.ActionTypes.Contains(ActionSpec.OptionalFeature))
                r.Notes.Add("há reinicialização pendente: o estado do recurso opcional pode só se acertar depois de reiniciar.");
        }
        r.DurationMs = total.ElapsedMilliseconds;
        return r;
    }

    private async Task RunStepsAsync(Tweak tweak, TweakSelfTestResult r, CancellationToken ct)
    {
        // 0) Filtros que não exigem tocar no sistema.
        if (tweak.Risk > _options.MaxRisk)
        {
            Skip(r, $"risco {tweak.Risk} acima de --max-risk {_options.MaxRisk}");
            return;
        }
        if (!tweak.Reversible && !_options.IncludeIrreversible)
        {
            Skip(r, "irreversível (use --include-irreversible para testar só aplicar + estado)");
            return;
        }

        // 1) Estado antes.
        var before = await host.GetStatusAsync(tweak, ct).ConfigureAwait(false);
        r.StateBefore = before.State;
        Record(r, before, (a, s) => a.Before = s);
        if (before.State == TweakState.NotApplicable)
        {
            Skip(r, "não se aplica a este sistema: " + (before.Message ?? DescribeActions(before)));
            return;
        }
        if (before.State == TweakState.Error)
        {
            Skip(r, "erro ao ler o estado inicial: " + (before.Message ?? DescribeActions(before)));
            return;
        }
        if (before.State == TweakState.Applied)
            r.Notes.Add("já estava aplicado antes do teste; aplicar/reverter devem manter os valores atuais.");

        // Backup de uma execução anterior mascararia o valor original deste teste.
        if (host.ReadBackupJson(tweak.Id) is not null)
        {
            r.Backup.ExistedBefore = true;
            Skip(r, "já existe um backup deste tweak (execução anterior?); apague-o ou use outra --backup-dir");
            return;
        }

        r.RebootPendingBefore = [.. host.RebootPendingReasons()];

        // 2) Aplicar.
        r.Apply = await RunStepAsync(() => host.ApplyAsync(tweak, ct)).ConfigureAwait(false);
        r.Backup.Json = host.ReadBackupJson(tweak.Id);
        r.Backup.WrittenOnApply = r.Backup.Json is not null;

        var afterApply = await host.GetStatusAsync(tweak, ct).ConfigureAwait(false);
        r.StateAfterApply = afterApply.State;
        Record(r, afterApply, (a, s) => a.AfterApply = s);
        r.RebootPendingAfterApply = [.. host.RebootPendingReasons()];

        // 3) Re-aplicar: o backup deve continuar idêntico (guarda o valor ORIGINAL verdadeiro).
        if (_options.CheckReapply && r.Apply.Success && r.Backup.Json is not null)
        {
            r.Reapply = await RunStepAsync(() => host.ApplyAsync(tweak, ct)).ConfigureAwait(false);
            r.Backup.KeptOnReapply = string.Equals(host.ReadBackupJson(tweak.Id), r.Backup.Json, StringComparison.Ordinal);
            if (!r.Reapply.Success) r.Notes.Add($"re-aplicar falhou: {r.Reapply.Describe()}");
        }

        if (!tweak.Reversible) return;

        // 4) Reverter. Mesmo se aplicar falhou, tenta (limpeza), desde que haja backup.
        if (!r.Apply.Success && host.ReadBackupJson(tweak.Id) is null)
        {
            r.Notes.Add("aplicar falhou e não há backup: reversão não tentada.");
            return;
        }
        r.Revert = await RunStepAsync(() => host.RevertAsync(tweak, ct)).ConfigureAwait(false);
        if (r.Revert.Success) r.Backup.DeletedAfterRevert = host.ReadBackupJson(tweak.Id) is null;

        var afterRevert = await host.GetStatusAsync(tweak, ct).ConfigureAwait(false);
        r.StateAfterRevert = afterRevert.State;
        Record(r, afterRevert, (a, s) => a.AfterRevert = s);
        r.RebootPendingAfterRevert = [.. host.RebootPendingReasons()];
    }

    private static async Task<StepResult> RunStepAsync(Func<Task<OperationResult>> step)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await step().ConfigureAwait(false);
            return new StepResult
            {
                Success = result.Success,
                Message = result.Message,
                Errors = [.. result.Errors],
                DurationMs = sw.ElapsedMilliseconds,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new StepResult { Success = false, Exception = Describe(ex), DurationMs = sw.ElapsedMilliseconds };
        }
    }

    /// <summary>Copia o estado de cada ação para o resultado, casando por posição (as ações vêm na ordem do YAML).</summary>
    private static void Record(TweakSelfTestResult r, TweakStatus status, Action<ActionSelfTest, ActionSnapshot> set)
    {
        for (int i = 0; i < r.Actions.Count && i < status.Actions.Count; i++)
            set(r.Actions[i], ActionSnapshot.From(status.Actions[i]));
    }

    private static void Skip(TweakSelfTestResult r, string reason)
    {
        r.Verdict = SelfTestVerdict.Skipped;
        r.Reason = reason;
    }

    private static string DescribeActions(TweakStatus s) =>
        s.Actions.Count == 0 ? s.State.ToString() : string.Join("; ", s.Actions.Select(a => $"{a.Action}: {a.State} ({a.Detail})"));

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";
}
