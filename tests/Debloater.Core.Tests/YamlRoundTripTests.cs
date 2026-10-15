using Debloater.Core.Catalog;
using Debloater.Core.Model;

namespace Debloater.Core.Tests;

public class YamlRoundTripTests
{
    public static TheoryData<string, ActionSpec> Actions => new()
    {
        { "registry-dword", new ActionSpec { Type = ActionSpec.Registry, Path = @"HKLM\SOFTWARE\Policies\X", Name = "V", Kind = "dword", Value = "0" } },
        { "registry-qword", new ActionSpec { Type = ActionSpec.Registry, Path = @"HKCU\Software\X", Name = "Q", Kind = "qword", Value = "0x1FFFFFFFF" } },
        { "registry-string", new ActionSpec { Type = ActionSpec.Registry, Path = @"HKCU\Software\X", Name = "", Kind = "string", Value = "" } },
        { "registry-expand", new ActionSpec { Type = ActionSpec.Registry, Path = @"HKCU\Software\X", Name = "E", Kind = "expandString", Value = "%SystemRoot%\\x" } },
        { "registry-multi", new ActionSpec { Type = ActionSpec.Registry, Path = @"HKCU\Software\X", Name = "M", Kind = "multiString", Value = new List<object> { "a", "b c" } } },
        { "registry-binary", new ActionSpec { Type = ActionSpec.Registry, Path = @"HKCU\Software\X", Name = "B", Kind = "binary", Value = "01 0A FF" } },
        { "registry-delete", new ActionSpec { Type = ActionSpec.Registry, Path = @"HKCU\Software\X", Name = "D", Delete = true } },
        { "service", new ActionSpec { Type = ActionSpec.Service, Name = "DiagTrack", Startup = "automaticDelayed" } },
        { "task", new ActionSpec { Type = ActionSpec.ScheduledTask, Path = @"\Microsoft\Windows\Foo\Bar Baz", State = "disabled" } },
        { "appx", new ActionSpec { Type = ActionSpec.Appx, Name = "Microsoft.BingNews", State = "removed", StoreId = "9WZDNCRFHVFW" } },
        { "feature", new ActionSpec { Type = ActionSpec.OptionalFeature, Name = "SMB1Protocol", State = "disabled" } },
        { "powershell", new ActionSpec { Type = ActionSpec.PowerShell, Detect = "$true\n'True'", Apply = "Write-Host 'a: b'", Revert = "# nada" } },
    };

    [Theory]
    [MemberData(nameof(Actions))]
    public void ActionSpec_RoundTrips(string label, ActionSpec original)
    {
        var file = new CatalogFile
        {
            Category = "Teste",
            Tweaks =
            [
                new Tweak
                {
                    Id = $"test.{label}",
                    Name = "Nome com acentuação",
                    Category = "Teste/Sub",
                    Description = "Descrição",
                    Risk = RiskLevel.High,
                    MinBuild = 22000,
                    Tags = ["a", "b"],
                    Breaks = "algo",
                    References = ["https://example.com/x"],
                    Reversible = original.Type != ActionSpec.Appx,
                    Actions = [original],
                },
            ],
        };

        var yaml = CatalogYaml.Serializer.Serialize(file);
        var back = CatalogYaml.Deserializer.Deserialize<CatalogFile>(yaml);

        var tweak = Assert.Single(back.Tweaks);
        Assert.Equal($"test.{label}", tweak.Id);
        Assert.Equal("Nome com acentuação", tweak.Name);
        Assert.Equal(RiskLevel.High, tweak.Risk);
        Assert.Equal(22000, tweak.MinBuild);
        Assert.Null(tweak.MaxBuild);
        Assert.Equal(["a", "b"], tweak.Tags);

        var a = Assert.Single(tweak.Actions);
        Assert.Equal(original.Type, a.Type);
        Assert.Equal(original.Path, a.Path);
        Assert.Equal(original.Name, a.Name);
        Assert.Equal(original.Kind, a.Kind);
        Assert.Equal(original.Delete, a.Delete);
        Assert.Equal(original.Startup, a.Startup);
        Assert.Equal(original.State, a.State);
        Assert.Equal(original.StoreId, a.StoreId);
        Assert.Equal(original.Detect, a.Detect);
        Assert.Equal(original.Apply, a.Apply);
        Assert.Equal(original.Revert, a.Revert);
        Assert.Equal(original.Target, a.Target);

        // O valor deserializado precisa produzir o mesmo dado de registro.
        if (original.Type == ActionSpec.Registry && !original.Delete)
        {
            var kind = Debloater.Core.Actions.RegistryActionHandler.ParseKind(original.Kind!);
            var expected = Debloater.Core.Actions.RegistryActionHandler.ToRegistryValue(kind, original.Value!);
            var actual = Debloater.Core.Actions.RegistryActionHandler.ToRegistryValue(kind, a.Value!);
            Assert.Equal(expected, actual);
        }

        // E o tweak continua válido.
        Assert.Empty(CatalogLoader.Validate(tweak));
    }
}
