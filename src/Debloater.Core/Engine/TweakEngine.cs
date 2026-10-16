using Debloater.Core.Actions;
using Debloater.Core.Model;
using Debloater.Core.Platform;

namespace Debloater.Core.Engine;

public sealed class TweakEngineOptions
{
    /// <summary>Onde ficam os backups. Padrão: %ProgramData%\Debloater\backups.</summary>
    public string BackupDirectory { get; init; } = BackupStore.DefaultDirectory;

    /// <summary>Exigir que o processo esteja elevado para aplicar/reverter. Só desligue em testes.</summary>
    public bool RequireElevation { get; init; } = true;

    /// <summary>Sistema alvo. Padrão: o sistema em execução.</summary>
    public SystemInfo? System { get; init; }

    /// <summary>Quantos tweaks detectar em paralelo numa varredura completa.</summary>
    public int MaxParallelism { get; init; } = 8;

    /// <summary>Avalia a pré-condição de um tweak. Padrão: roda o script PowerShell. Substituível em testes.</summary>
    public Func<Precondition, CancellationToken, Task<PreconditionResult>> PreconditionEvaluator { get; init; } = Engine.PreconditionEvaluator.EvaluateAsync;
}

/// <summary>O processo precisa estar elevado (administrador).</summary>
public sealed class ElevationRequiredException() :
    InvalidOperationException("Aplicar ou reverter tweaks requer privilégios de administrador. Execute o programa como administrador.");

/// <summary>O tweak excede o nível de risco permitido para esta operação.</summary>
public sealed class RiskNotAllowedException(Tweak tweak, RiskLevel allowed) :
    InvalidOperationException($"O tweak '{tweak.Id}' tem risco {tweak.Risk}, acima do permitido ({allowed}).")
{
    public Tweak Tweak { get; } = tweak;
    public RiskLevel Allowed { get; } = allowed;
}

/// <summary>Resultado de aplicar/reverter.</summary>
public sealed record OperationResult(string TweakId, bool Success, string Message, IReadOnlyList<string> Errors, TweakStatus? StatusAfter = null)
{
    /// <summary>Avisos que não são erros (ex.: "o serviço só vai parar depois de reiniciar").</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>
/// Orquestra detecção, aplicação e reversão de tweaks. Toda a lógica específica de tipo fica nos
/// <see cref="IActionHandler"/>; o engine só cuida de agregação, backup e políticas (elevação, risco).
/// </summary>
public sealed class TweakEngine
{
    private readonly TweakEngineOptions _options;

    public TweakEngine(TweakEngineOptions? options = null)
    {
        _options = options ?? new TweakEngineOptions();
        System = _options.System ?? SystemInfo.Current;
        Backups = new BackupStore(_options.BackupDirectory);
    }

    public SystemInfo System { get; }
    public BackupStore Backups { get; }

    public ScanContext CreateContext() => new(System);

    // ---------------- status ----------------

    public async Task<TweakStatus> GetStatusAsync(Tweak tweak, ScanContext? ctx = null, CancellationToken ct = default)
    {
        using var owned = ctx is null ? CreateContext() : null;
        ctx ??= owned!;

        if (!tweak.IsBuildInRange(System.Build))
        {
            return new TweakStatus(tweak, TweakState.NotApplicable, [],
                $"build {System.Build} fora do intervalo {tweak.MinBuild?.ToString() ?? "*"}..{tweak.MaxBuild?.ToString() ?? "*"}");
        }

        if (tweak.Precondition is not null)
        {
            var pre = await EvaluatePreconditionAsync(tweak, ctx, ct).ConfigureAwait(false);
            if (pre.Met == false) return new TweakStatus(tweak, TweakState.NotApplicable, [], pre.Detail);
            if (pre.Met is null) return new TweakStatus(tweak, TweakState.Error, [], pre.Detail);
        }

        var results = new List<ActionStatus>(tweak.Actions.Count);
        foreach (var action in tweak.Actions)
        {
            ct.ThrowIfCancellationRequested();
            Detection detection;
            try
            {
                detection = await ActionHandlers.Get(action.Type).DetectAsync(action, ctx, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                detection = Detection.Error(ex.Message);
            }
            results.Add(new ActionStatus(action, detection.State, detection.Detail));
        }
        return new TweakStatus(tweak, StatusAggregator.Aggregate(results.Select(r => r.State)), results);
    }

    /// <summary>Status de vários tweaks, compartilhando caches (appx, recursos, tarefas) numa única varredura.</summary>
    public async Task<IReadOnlyList<TweakStatus>> GetStatusesAsync(IReadOnlyList<Tweak> tweaks, CancellationToken ct = default)
    {
        using var ctx = CreateContext();
        var results = new TweakStatus[tweaks.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, tweaks.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _options.MaxParallelism), CancellationToken = ct },
            async (i, token) => results[i] = await GetStatusAsync(tweaks[i], ctx, token).ConfigureAwait(false)).ConfigureAwait(false);
        return results;
    }

    // ---------------- apply ----------------

    /// <summary>
    /// Aplica um tweak. Antes captura e persiste o backup; se já existir backup (re-aplicação),
    /// ele é preservado para manter o valor ORIGINAL verdadeiro (apenas ações novas são acrescentadas).
    /// </summary>
    public async Task<OperationResult> ApplyAsync(Tweak tweak, RiskLevel maxRisk = RiskLevel.Medium, CancellationToken ct = default)
    {
        EnsureElevated();
        if (tweak.Risk > maxRisk) throw new RiskNotAllowedException(tweak, maxRisk);
        if (!tweak.IsBuildInRange(System.Build))
            return new OperationResult(tweak.Id, false, $"Não se aplica ao build {System.Build}.", []);

        using var ctx = CreateContext();
        if (tweak.Precondition is not null)
        {
            var pre = await EvaluatePreconditionAsync(tweak, ctx, ct).ConfigureAwait(false);
            if (pre.Met != true) return new OperationResult(tweak.Id, false, $"Não se aplica a este sistema: {pre.Detail}", []);
        }

        // 1) Backup (antes de qualquer alteração).
        var backup = Backups.Load(tweak.Id) ?? new TweakBackup
        {
            TweakId = tweak.Id,
            CreatedUtc = DateTime.UtcNow,
            WindowsBuild = System.FullBuild,
        };
        bool changed = false;
        foreach (var action in tweak.Actions)
        {
            if (FindBackup(backup, action) is not null) continue;
            var captured = await ActionHandlers.Get(action.Type).CaptureBackupAsync(action, ctx, ct).ConfigureAwait(false);
            if (captured is null) continue;
            backup.Actions.Add(captured);
            changed = true;
        }
        if (changed || !Backups.Exists(tweak.Id)) Backups.Save(backup);

        // 2) Aplicação.
        var errors = new List<string>();
        foreach (var action in tweak.Actions)
        {
            ct.ThrowIfCancellationRequested();
            var handler = ActionHandlers.Get(action.Type);
            try
            {
                var before = await handler.DetectAsync(action, ctx, ct).ConfigureAwait(false);
                if (before.State is ActionState.Desired or ActionState.NotApplicable) continue;
                await handler.ApplyAsync(action, ctx, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"{action}: {ex.Message}");
            }
        }

        var after = await GetStatusAsync(tweak, null, ct).ConfigureAwait(false);
        bool ok = errors.Count == 0;
        var message = ok ? $"Aplicado. Estado: {after.State}." : $"Aplicado com {errors.Count} erro(s). Estado: {after.State}.";
        if (!tweak.Reversible) message += " Atenção: este tweak não é reversível pelo programa.";
        return new OperationResult(tweak.Id, ok, message, errors, after) { Notes = [.. ctx.Notes] };
    }

    // ---------------- revert ----------------

    /// <summary>Reverte um tweak a partir do backup. Sem backup, nada é feito.</summary>
    public async Task<OperationResult> RevertAsync(Tweak tweak, CancellationToken ct = default)
    {
        EnsureElevated();
        var backup = Backups.Load(tweak.Id);
        if (backup is null)
        {
            return new OperationResult(tweak.Id, false,
                "Nenhum backup encontrado para este tweak (ele não foi aplicado por este programa, ou o backup foi apagado). Nada foi revertido.", []);
        }

        using var ctx = CreateContext();
        var errors = new List<string>();
        foreach (var action in Enumerable.Reverse(tweak.Actions))
        {
            ct.ThrowIfCancellationRequested();
            var handler = ActionHandlers.Get(action.Type);
            if (!handler.CanRestore)
            {
                errors.Add($"{action}: não reversível pelo engine.");
                continue;
            }
            var entry = FindBackup(backup, action);
            if (entry is null && action.Type != ActionSpec.PowerShell)
            {
                errors.Add($"{action}: sem backup para esta ação; ignorada.");
                continue;
            }
            try
            {
                await handler.RestoreAsync(action, entry, ctx, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"{action}: {ex.Message}");
            }
        }

        if (errors.Count == 0) Backups.Delete(tweak.Id);
        var after = await GetStatusAsync(tweak, null, ct).ConfigureAwait(false);
        return errors.Count == 0
            ? new OperationResult(tweak.Id, true, $"Revertido. Estado: {after.State}.", errors, after) { Notes = [.. ctx.Notes] }
            : new OperationResult(tweak.Id, false, $"Revertido parcialmente ({errors.Count} erro(s)); backup mantido. Estado: {after.State}.", errors, after) { Notes = [.. ctx.Notes] };
    }

    /// <summary>Avalia a pré-condição uma vez por contexto (varredura).</summary>
    private Task<PreconditionResult> EvaluatePreconditionAsync(Tweak tweak, ScanContext ctx, CancellationToken ct) =>
        ctx.GetOrAddAsync("precondition:" + tweak.Id, () => _options.PreconditionEvaluator(tweak.Precondition!, ct));

    private static ActionBackup? FindBackup(TweakBackup backup, ActionSpec action) =>
        backup.Actions.FirstOrDefault(b =>
            string.Equals(b.Type, action.Type, StringComparison.Ordinal) &&
            string.Equals(b.Target, action.Target, StringComparison.OrdinalIgnoreCase));

    private void EnsureElevated()
    {
        if (_options.RequireElevation && !System.IsElevated) throw new ElevationRequiredException();
    }
}
