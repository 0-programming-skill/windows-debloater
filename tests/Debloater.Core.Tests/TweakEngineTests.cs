using System.Globalization;
using Debloater.Core.Engine;
using Debloater.Core.Model;
using Debloater.Core.Platform;
using Microsoft.Win32;

namespace Debloater.Core.Tests;

/// <summary>
/// Engine tests using only the registry sandbox in HKCU and a temporary backup directory.
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
        catch (IOException) { /* another test running in parallel still uses the folder */ }
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
        Category = "Test",
        Risk = risk,
        MinBuild = minBuild,
        Actions =
        [
            new ActionSpec { Type = ActionSpec.Registry, Path = _sandbox.Path, Name = "A", Kind = "dword", Value = "1" },
            new ActionSpec { Type = ActionSpec.Registry, Path = _sandbox.Path, Name = "B", Kind = "string", Value = "new" },
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

        // Someone changes the value later; re-applying must NOT overwrite the original backup.
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
        Assert.Contains("No backup", result.Message);
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

    // ---------- precondition ----------

    private TweakEngine EngineWithPrecondition(PreconditionResult result, List<string>? calls = null) => new(new TweakEngineOptions
    {
        BackupDirectory = _backupDir,
        RequireElevation = false,
        System = SystemInfo.Current with { IsElevated = false },
        PreconditionEvaluator = (p, _) =>
        {
            calls?.Add(p.Powershell!);
            return Task.FromResult(result);
        },
    });

    private Tweak WithPrecondition(Tweak t)
    {
        t.Precondition = new Precondition { Powershell = "$false", Reason = "no hibernation support" };
        return t;
    }

    [Fact]
    public async Task Precondition_False_MakesTweakNotApplicable_AndApplyChangesNothing()
    {
        var engine = EngineWithPrecondition(PreconditionResult.False("no hibernation support"));
        var tweak = WithPrecondition(SandboxTweak());

        var status = await engine.GetStatusAsync(tweak);
        Assert.Equal(TweakState.NotApplicable, status.State);
        Assert.Equal("no hibernation support", status.Message);
        Assert.Empty(status.Actions);

        var applied = await engine.ApplyAsync(tweak);
        Assert.False(applied.Success);
        Assert.Contains("no hibernation support", applied.Message);
        Assert.Null(_sandbox.TryOpen());
        Assert.False(engine.Backups.Exists(tweak.Id));
    }

    [Theory]
    [InlineData("en-US", "no hibernation support")]
    [InlineData("pt-BR", "sem suporte a hibernação")]
    public async Task Precondition_False_ShowsTheReasonInTheUiLanguage(string uiCulture, string expected)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(uiCulture);
            var engine = EngineWithPrecondition(PreconditionResult.False("no hibernation support"));
            var tweak = WithPrecondition(SandboxTweak());
            tweak.I18n = new() { ["pt-BR"] = new TweakText { Reason = "sem suporte a hibernação" } };

            var status = await engine.GetStatusAsync(tweak);
            Assert.Equal(expected, status.Message);
            var applied = await engine.ApplyAsync(tweak);
            Assert.Contains(expected, applied.Message);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public async Task Precondition_True_DetectsNormally_AndIsEvaluatedOncePerScan()
    {
        var calls = new List<string>();
        var engine = EngineWithPrecondition(PreconditionResult.True(), calls);
        var tweak = WithPrecondition(SandboxTweak());

        var statuses = await engine.GetStatusesAsync([tweak, tweak]);
        Assert.All(statuses, s => Assert.Equal(TweakState.NotApplied, s.State));
        Assert.Single(calls);

        var applied = await engine.ApplyAsync(tweak);
        Assert.True(applied.Success, string.Join("\n", applied.Errors));
        Assert.Equal(TweakState.Applied, applied.StatusAfter!.State);
    }

    [Fact]
    public async Task Precondition_ScriptFailure_IsError()
    {
        var engine = EngineWithPrecondition(PreconditionResult.Failed("precondition returned 'x'"));
        var status = await engine.GetStatusAsync(WithPrecondition(SandboxTweak()));
        Assert.Equal(TweakState.Error, status.State);
        Assert.Contains("returned 'x'", status.Message);
    }

    [Theory]
    [InlineData("True\r\n", true)]
    [InlineData("some warning\nfalse\n", false)]
    [InlineData("", null)]
    [InlineData("maybe", null)]
    public void PreconditionEvaluator_InterpretsLastLine(string stdout, bool? expected)
    {
        var r = PreconditionEvaluator.Interpret(stdout, "", "reason");
        Assert.Equal(expected, r.Met);
        if (expected == false) Assert.Equal("reason", r.Detail);
    }

    [Fact]
    public async Task PreconditionEvaluator_RunsPowerShell()
    {
        var yes = await PreconditionEvaluator.EvaluateAsync(new Precondition { Powershell = "1 -eq 1", Reason = "r" }, CancellationToken.None);
        var no = await PreconditionEvaluator.EvaluateAsync(new Precondition { Powershell = "[bool]0", Reason = "r" }, CancellationToken.None);
        Assert.True(yes.Met);
        Assert.False(no.Met);
        Assert.Equal("r", no.Detail);
    }

    // ---------- protected registry ----------

    [Fact]
    public async Task Apply_ProtectedRegistryValue_ReportsClearError()
    {
        // Simulates a protected key: the user itself has no right to write values in it.
        using (var k = _sandbox.Create())
        {
            var acl = k.GetAccessControl();
            acl.AddAccessRule(new System.Security.AccessControl.RegistryAccessRule(
                System.Security.Principal.WindowsIdentity.GetCurrent().User!,
                System.Security.AccessControl.RegistryRights.SetValue,
                System.Security.AccessControl.AccessControlType.Deny));
            k.SetAccessControl(acl);
        }
        // The sandbox Dispose deletes the key: denying SetValue does not prevent deleting the whole tree.
        var applied = await Engine().ApplyAsync(SandboxTweak());
        Assert.False(applied.Success);
        Assert.Equal(2, applied.Errors.Count);
        Assert.All(applied.Errors, e => Assert.Contains("key protected by the system", e));
        Assert.DoesNotContain(applied.Errors, e => e.Contains("Attempted to perform", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Revert_KeyThatCannotBeDeleted_StillSucceeds_WithNote()
    {
        _sandbox.Create().Dispose();
        var tweak = new Tweak
        {
            Id = "test.sub", Name = "S", Description = "d", Category = "Test",
            Actions = [new ActionSpec { Type = ActionSpec.Registry, Path = _sandbox.Path + @"\Created", Name = "A", Kind = "dword", Value = "1" }],
        };
        var engine = Engine();
        Assert.True((await engine.ApplyAsync(tweak)).Success);

        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var sub = _sandbox.SubKey + @"\Created";
        void SetDenyDelete(bool deny)
        {
            using var k = Registry.CurrentUser.OpenSubKey(sub, RegistryKeyPermissionCheck.ReadWriteSubTree,
                System.Security.AccessControl.RegistryRights.ChangePermissions | System.Security.AccessControl.RegistryRights.ReadPermissions)!;
            var acl = k.GetAccessControl();
            var rule = new System.Security.AccessControl.RegistryAccessRule(user,
                System.Security.AccessControl.RegistryRights.Delete, System.Security.AccessControl.AccessControlType.Deny);
            if (deny) acl.AddAccessRule(rule); else acl.RemoveAccessRule(rule);
            k.SetAccessControl(acl);
        }
        SetDenyDelete(true);
        try
        {
            var reverted = await engine.RevertAsync(tweak);
            Assert.True(reverted.Success, string.Join('\n', reverted.Errors));
            Assert.Contains(reverted.Notes, n => n.Contains("empty key") && n.Contains("Created"));
            using var k = _sandbox.TryOpen("Created");
            Assert.NotNull(k);
            Assert.Null(k!.GetValue("A"));
        }
        finally
        {
            SetDenyDelete(false);
        }
    }
}
