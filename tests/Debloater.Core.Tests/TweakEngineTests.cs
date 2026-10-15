using Debloater.Core.Engine;
using Debloater.Core.Model;
using Debloater.Core.Platform;
using Microsoft.Win32;

namespace Debloater.Core.Tests;

/// <summary>
/// Testes do engine usando apenas a sandbox de registro em HKCU e um diretório de backup temporário.
/// </summary>
public sealed class TweakEngineTests : IDisposable
{
    private readonly RegistrySandbox _sandbox = new();
    private readonly string _backupDir = Path.Combine(Path.GetTempPath(), "DebloaterTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _sandbox.Dispose();
        if (Directory.Exists(_backupDir)) Directory.Delete(_backupDir, recursive: true);
        var parent = Path.GetDirectoryName(_backupDir)!;
        try { if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent); }
        catch (IOException) { /* outro teste em paralelo ainda usa a pasta */ }
    }

    private TweakEngine Engine(bool requireElevation = false, bool elevated = false) => new(new TweakEngineOptions
    {
        BackupDirectory = _backupDir,
        RequireElevation = requireElevation,
        System = SystemInfo.Current with { IsElevated = elevated },
    });

    private Tweak SandboxTweak(RiskLevel risk = RiskLevel.Low, int? minBuild = null) => new()
    {
        Id = "test.sandbox",
        Name = "Sandbox",
        Description = "d",
        Category = "Teste",
        Risk = risk,
        MinBuild = minBuild,
        Actions =
        [
            new ActionSpec { Type = ActionSpec.Registry, Path = _sandbox.Path, Name = "A", Kind = "dword", Value = "1" },
            new ActionSpec { Type = ActionSpec.Registry, Path = _sandbox.Path, Name = "B", Kind = "string", Value = "novo" },
        ],
    };

    [Fact]
    public async Task Status_ReflectsPartialAndApplied()
    {
        using (var k = _sandbox.Create()) k.SetValue("A", 1, RegistryValueKind.DWord);
        var engine = Engine();

        var status = await engine.GetStatusAsync(SandboxTweak());
        Assert.Equal(TweakState.Partial, status.State);
        Assert.Equal([ActionState.Desired, ActionState.NotDesired], status.Actions.Select(a => a.State));
    }

    [Fact]
    public async Task Status_OutOfBuildRange_IsNotApplicable()
    {
        var status = await Engine().GetStatusAsync(SandboxTweak(minBuild: 999_999));
        Assert.Equal(TweakState.NotApplicable, status.State);
        Assert.Empty(status.Actions);
    }

    [Fact]
    public async Task Apply_PersistsBackup_ReapplyKeepsTrueOriginal_RevertRestores()
    {
        using (var k = _sandbox.Create())
        {
            k.SetValue("A", 5, RegistryValueKind.DWord);
            k.SetValue("B", "original", RegistryValueKind.String);
        }
        var engine = Engine();
        var tweak = SandboxTweak();

        var applied = await engine.ApplyAsync(tweak);
        Assert.True(applied.Success, string.Join("\n", applied.Errors));
        Assert.Equal(TweakState.Applied, applied.StatusAfter!.State);
        Assert.True(File.Exists(Path.Combine(_backupDir, "test.sandbox.json")));

        // Alguém muda o valor depois; re-aplicar NÃO pode sobrescrever o backup original.
        using (var k = _sandbox.Open(writable: true)) k.SetValue("A", 9, RegistryValueKind.DWord);
        await engine.ApplyAsync(tweak);
        var backup = engine.Backups.Load(tweak.Id)!;
        Assert.Equal("5", backup.Actions.Single(b => b.Target.EndsWith("\\A")).Data);

        var reverted = await engine.RevertAsync(tweak);
        Assert.True(reverted.Success, string.Join("\n", reverted.Errors));
        using (var k = _sandbox.Open())
        {
            Assert.Equal(5, k.GetValue("A"));
            Assert.Equal("original", k.GetValue("B"));
        }
        Assert.False(engine.Backups.Exists(tweak.Id));
    }

    [Fact]
    public async Task Reapply_AfterCatalogAddsAction_BacksUpOnlyTheNewAction()
    {
        using (var k = _sandbox.Create()) k.SetValue("A", 5, RegistryValueKind.DWord);
        var engine = Engine();
        var tweak = SandboxTweak();
        tweak.Actions.RemoveAt(1);
        await engine.ApplyAsync(tweak);

        var extended = SandboxTweak();
        await engine.ApplyAsync(extended);

        var backup = engine.Backups.Load(tweak.Id)!;
        Assert.Equal(2, backup.Actions.Count);
        Assert.Equal("5", backup.Actions[0].Data);
        Assert.False(backup.Actions[1].Existed);
    }

    [Fact]
    public async Task Revert_WithoutBackup_DoesNothingAndExplains()
    {
        using (var k = _sandbox.Create()) k.SetValue("A", 1, RegistryValueKind.DWord);
        var result = await Engine().RevertAsync(SandboxTweak());

        Assert.False(result.Success);
        Assert.Contains("Nenhum backup", result.Message);
        using var key = _sandbox.Open();
        Assert.Equal(1, key.GetValue("A"));
    }

    [Fact]
    public async Task Apply_RequiresElevation()
    {
        var engine = Engine(requireElevation: true, elevated: false);
        await Assert.ThrowsAsync<ElevationRequiredException>(() => engine.ApplyAsync(SandboxTweak()));
        await Assert.ThrowsAsync<ElevationRequiredException>(() => engine.RevertAsync(SandboxTweak()));
        Assert.Null(_sandbox.TryOpen());
    }

    [Fact]
    public async Task Apply_IsRiskGated()
    {
        var engine = Engine();
        var tweak = SandboxTweak(RiskLevel.Critical);

        var ex = await Assert.ThrowsAsync<RiskNotAllowedException>(() => engine.ApplyAsync(tweak));
        Assert.Equal(RiskLevel.Medium, ex.Allowed);
        Assert.Null(_sandbox.TryOpen());

        var ok = await engine.ApplyAsync(tweak, RiskLevel.Critical);
        Assert.True(ok.Success);
    }
}
