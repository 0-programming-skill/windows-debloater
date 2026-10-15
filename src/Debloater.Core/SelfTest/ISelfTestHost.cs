using Debloater.Core.Engine;
using Debloater.Core.Model;
using Debloater.Core.Platform;

namespace Debloater.Core.SelfTest;

/// <summary>
/// O que o <see cref="SelfTestRunner"/> precisa do sistema. A implementação real
/// (<see cref="EngineSelfTestHost"/>) usa o <see cref="TweakEngine"/>; os testes usam um falso em memória.
/// </summary>
public interface ISelfTestHost
{
    Task<TweakStatus> GetStatusAsync(Tweak tweak, CancellationToken ct = default);

    /// <summary>Aplica sem controle de risco (o runner já filtrou pelo --max-risk).</summary>
    Task<OperationResult> ApplyAsync(Tweak tweak, CancellationToken ct = default);

    Task<OperationResult> RevertAsync(Tweak tweak, CancellationToken ct = default);

    /// <summary>Conteúdo do arquivo de backup do tweak, ou null se não existe.</summary>
    string? ReadBackupJson(string tweakId);

    /// <summary>Motivos de reinicialização pendente (vazio = nenhuma).</summary>
    IReadOnlyList<string> RebootPendingReasons();
}

/// <summary>Host real: o engine sobre o sistema em execução.</summary>
public sealed class EngineSelfTestHost(TweakEngine engine) : ISelfTestHost
{
    public TweakEngine Engine { get; } = engine;

    // Cada chamada cria um ScanContext novo: o estado lido depois de aplicar não pode vir de cache.
    public Task<TweakStatus> GetStatusAsync(Tweak tweak, CancellationToken ct = default) => Engine.GetStatusAsync(tweak, null, ct);

    public Task<OperationResult> ApplyAsync(Tweak tweak, CancellationToken ct = default) => Engine.ApplyAsync(tweak, RiskLevel.Critical, ct);

    public Task<OperationResult> RevertAsync(Tweak tweak, CancellationToken ct = default) => Engine.RevertAsync(tweak, ct);

    public string? ReadBackupJson(string tweakId)
    {
        var path = Engine.Backups.PathFor(tweakId);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public IReadOnlyList<string> RebootPendingReasons() => RebootState.PendingReasons();
}
