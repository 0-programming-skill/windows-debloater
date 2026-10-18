using Debloater.Core.Engine;
using Debloater.Core.Model;
using Debloater.Core.Platform;

namespace Debloater.Core.SelfTest;

/// <summary>
/// What the <see cref="SelfTestRunner"/> needs from the system. The real implementation
/// (<see cref="EngineSelfTestHost"/>) uses the <see cref="TweakEngine"/>; tests use an in-memory fake.
/// </summary>
public interface ISelfTestHost
{
    Task<TweakStatus> GetStatusAsync(Tweak tweak, CancellationToken ct = default);

    /// <summary>Applies without risk gating (the runner has already filtered by --max-risk).</summary>
    Task<OperationResult> ApplyAsync(Tweak tweak, CancellationToken ct = default);

    Task<OperationResult> RevertAsync(Tweak tweak, CancellationToken ct = default);

    /// <summary>Contents of the tweak's backup file, or null if it does not exist.</summary>
    string? ReadBackupJson(string tweakId);

    /// <summary>Pending reboot reasons (empty = none).</summary>
    IReadOnlyList<string> RebootPendingReasons();
}

/// <summary>Real host: the engine over the running system.</summary>
public sealed class EngineSelfTestHost(TweakEngine engine) : ISelfTestHost
{
    public TweakEngine Engine { get; } = engine;

    // Each call creates a new ScanContext: the state read after applying must not come from a cache.
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
