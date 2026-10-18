using Debloater.Core.Actions;
using Debloater.Core.Engine;
using Debloater.Core.Model;
using Debloater.Core.Platform;
using Microsoft.Win32;

namespace Debloater.Core.Tests;

/// <summary>Registry handler tests, isolated in HKCU\Software\DebloaterTests.</summary>
public sealed class RegistryActionHandlerTests : IDisposable
{
    private readonly RegistrySandbox _sandbox = new();
    private readonly RegistryActionHandler _handler = new();
    private readonly ScanContext _ctx = new(SystemInfo.Current);

    public void Dispose()
    {
        _ctx.Dispose();
        _sandbox.Dispose();
    }

    private ActionSpec Spec(string name, string kind, object value, string relative = "") => new()
    {
        Type = ActionSpec.Registry,
        Path = relative.Length == 0 ? _sandbox.Path : $@"{_sandbox.Path}\{relative}",
        Name = name,
        Kind = kind,
        Value = value,
    };

    private async Task<ActionState> Detect(ActionSpec spec) => (await _handler.DetectAsync(spec, _ctx)).State;

    [Fact]
    public async Task Dword_NewValueInNewKeys_ApplyThenRestoreRemovesValueAndCreatedKeys()
    {
        using (_sandbox.Create()) { }
        var spec = Spec("Telemetry", "dword", "0", @"New\Sub");

        Assert.Equal(ActionState.NotDesired, await Detect(spec));
        var backup = await _handler.CaptureBackupAsync(spec, _ctx);
        Assert.NotNull(backup);
        Assert.False(backup.Existed);
        Assert.Equal("New", backup.Extra?.Split('\\').Last());

        await _handler.ApplyAsync(spec, _ctx);
        Assert.Equal(ActionState.Desired, await Detect(spec));
        using (var k = _sandbox.TryOpen(@"New\Sub"))
        {
            Assert.Equal(RegistryValueKind.DWord, k!.GetValueKind("Telemetry"));
            Assert.Equal(0, k.GetValue("Telemetry"));
        }

        await _handler.RestoreAsync(spec, backup, _ctx);
        Assert.Equal(ActionState.NotDesired, await Detect(spec));
        Assert.Null(_sandbox.TryOpen("New"));
        Assert.NotNull(_sandbox.TryOpen());
    }

    [Fact]
    public async Task ExistingValueOfDifferentKind_IsRestoredWithOriginalKind()
    {
        using (var k = _sandbox.Create()) k.SetValue("V", "original text", RegistryValueKind.String);
        var spec = Spec("V", "dword", "4294967295");

        Assert.Equal(ActionState.NotDesired, await Detect(spec));
        var backup = await _handler.CaptureBackupAsync(spec, _ctx);
        Assert.True(backup!.Existed);
        Assert.Equal("string", backup.Kind);

        await _handler.ApplyAsync(spec, _ctx);
        Assert.Equal(ActionState.Desired, await Detect(spec));
        using (var k = _sandbox.Open()) Assert.Equal(-1, k.GetValue("V"));

        await _handler.RestoreAsync(spec, backup, _ctx);
        using (var k = _sandbox.Open())
        {
            Assert.Equal(RegistryValueKind.String, k.GetValueKind("V"));
            Assert.Equal("original text", k.GetValue("V"));
        }
    }

    [Fact]
    public async Task Delete_RemovesValue_AndRestoreRecreatesIt()
    {
        using (var k = _sandbox.Create()) k.SetValue("Q", 123456789012L, RegistryValueKind.QWord);
        var spec = new ActionSpec { Type = ActionSpec.Registry, Path = _sandbox.Path, Name = "Q", Delete = true };

        Assert.Equal(ActionState.NotDesired, await Detect(spec));
        var backup = await _handler.CaptureBackupAsync(spec, _ctx);

        await _handler.ApplyAsync(spec, _ctx);
        Assert.Equal(ActionState.Desired, await Detect(spec));
        using (var k = _sandbox.Open()) Assert.Null(k.GetValue("Q"));

        await _handler.RestoreAsync(spec, backup, _ctx);
        using (var k = _sandbox.Open())
        {
            Assert.Equal(RegistryValueKind.QWord, k.GetValueKind("Q"));
            Assert.Equal(123456789012L, k.GetValue("Q"));
        }
    }

    [Fact]
    public async Task Delete_WhenKeyMissing_IsDesired()
    {
        var spec = new ActionSpec { Type = ActionSpec.Registry, Path = $@"{_sandbox.Path}\DoesNotExist", Name = "X", Delete = true };
        Assert.Equal(ActionState.Desired, await Detect(spec));
    }

    [Theory]
    [InlineData("string", "abc")]
    [InlineData("expandString", "%SystemRoot%\\Temp")]
    [InlineData("binary", "01 0A ff 00")]
    [InlineData("qword", "0x1FFFFFFFF")]
    public async Task ScalarKinds_ApplyDetectRestore(string kind, string value)
    {
        using (var k = _sandbox.Create()) k.SetValue("Orig", 7, RegistryValueKind.DWord);
        var spec = Spec("Orig", kind, value);

        var backup = await _handler.CaptureBackupAsync(spec, _ctx);
        await _handler.ApplyAsync(spec, _ctx);
        Assert.Equal(ActionState.Desired, await Detect(spec));

        await _handler.RestoreAsync(spec, backup, _ctx);
        using var key = _sandbox.Open();
        Assert.Equal(RegistryValueKind.DWord, key.GetValueKind("Orig"));
        Assert.Equal(7, key.GetValue("Orig"));
    }

    [Fact]
    public async Task ExpandString_IsComparedUnexpanded()
    {
        var spec = Spec("P", "expandString", "%SystemRoot%\\x");
        using (_sandbox.Create()) { }
        await _handler.ApplyAsync(spec, _ctx);
        Assert.Equal(ActionState.Desired, await Detect(spec));
    }

    [Fact]
    public async Task MultiString_RoundTripsThroughBackup()
    {
        using (var k = _sandbox.Create()) k.SetValue("M", new[] { "one", "two" }, RegistryValueKind.MultiString);
        var spec = Spec("M", "multiString", new List<object> { "three" });

        var backup = await _handler.CaptureBackupAsync(spec, _ctx);
        Assert.Equal(["one", "two"], backup!.MultiData!);

        await _handler.ApplyAsync(spec, _ctx);
        Assert.Equal(ActionState.Desired, await Detect(spec));

        await _handler.RestoreAsync(spec, backup, _ctx);
        using var key = _sandbox.Open();
        Assert.Equal(new[] { "one", "two" }, (string[])key.GetValue("M")!);
    }

    [Fact]
    public async Task DefaultValue_EmptyString_ClassicContextMenuStyle()
    {
        var spec = Spec("", "string", "", "InprocServer32");
        using (_sandbox.Create()) { }

        var backup = await _handler.CaptureBackupAsync(spec, _ctx);
        await _handler.ApplyAsync(spec, _ctx);
        Assert.Equal(ActionState.Desired, await Detect(spec));

        await _handler.RestoreAsync(spec, backup, _ctx);
        Assert.Null(_sandbox.TryOpen("InprocServer32"));
    }

    [Fact]
    public async Task Restore_DoesNotDeleteCreatedKeyIfSomethingElseWasAddedLater()
    {
        using (_sandbox.Create()) { }
        var spec = Spec("A", "dword", "1", "Created");
        var backup = await _handler.CaptureBackupAsync(spec, _ctx);
        await _handler.ApplyAsync(spec, _ctx);
        using (var k = _sandbox.Create("Created")) k.SetValue("Other", 1);

        await _handler.RestoreAsync(spec, backup, _ctx);
        using var key = _sandbox.TryOpen("Created");
        Assert.NotNull(key);
        Assert.Null(key.GetValue("A"));
        Assert.Equal(1, key.GetValue("Other"));
    }
}
