using Debloater.Core.Admx;
using Microsoft.Win32;

namespace Debloater.Core.Tests;

public class AdmxParserTests
{
    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Admx");

    private static AdmxCatalog Parse(params string[] langs) =>
        AdmxParser.ParseDirectory(FixtureDir, langs.Length == 0 ? ["en-US"] : langs);

    private static AdmxPolicy Policy(AdmxCatalog c, string name) => Assert.Single(c.Policies, p => p.Name == name);

    [Fact]
    public void ParsesAllFilesWithoutErrors()
    {
        var c = Parse();
        Assert.Equal(3, c.FileCount);
        Assert.Empty(c.Errors);
        Assert.Equal(5, c.Policies.Count);
    }

    [Fact]
    public void EnumPolicy_ResolvesStringsCategoriesAcrossFilesSupportedOnAndPresentation()
    {
        var p = Policy(Parse(), "Telemetry");

        Assert.Equal("Sample.admx", p.File);
        Assert.Equal("Sample.admx:Telemetry", p.QualifiedName);
        Assert.Equal("Test.Policies.Sample", p.Namespace);
        Assert.Equal(AdmxClass.Both, p.Class);
        Assert.Equal("Allow diagnostic data", p.DisplayName);
        Assert.Equal("Explains telemetry.", p.ExplainText);
        Assert.Equal(["Windows Components", "Privacy", "Sample Category"], p.CategoryPath);
        Assert.Equal("At least Windows 11", p.SupportedOn);
        Assert.Equal("b:SUPPORTED_Win11", p.SupportedOnRef);
        Assert.Equal(@"Software\Policies\Test\Data", p.Key);
        Assert.Null(p.ValueName);

        var e = Assert.Single(p.Elements);
        Assert.Equal("enum", e.Type);
        Assert.Equal("Level", e.ValueName);
        Assert.Equal(@"Software\Policies\Test\Data", e.Key);
        Assert.Equal("Level:", e.Label);
        Assert.True(e.Required);
        Assert.Equal(["Off", "Required", "Optional"], e.Items!.Select(i => i.DisplayName));
        Assert.Equal(["0", "1", "3"], e.Items!.Select(i => i.Value.Data));
        var extra = Assert.Single(e.Items![2].ValueList!);
        Assert.Equal(new AdmxListItem(@"Software\Policies\Test\Extra", "Full", new AdmxValue(AdmxValueKind.String, "yes")), extra);
    }

    [Fact]
    public void PreferredLanguage_IsUsedWithFallbackToEnUs()
    {
        var c = Parse("pt-BR", "en-US");
        var p = Policy(c, "Telemetry");
        // Base.adml existe em pt-BR; Sample.adml só em en-US.
        Assert.Equal(["Componentes do Windows", "Privacidade", "Sample Category"], p.CategoryPath);
        Assert.Equal("No mínimo Windows 11", p.SupportedOn);
        Assert.Equal("Allow diagnostic data", p.DisplayName);
        Assert.Contains("pt-BR", c.Languages);
        Assert.Contains("en-US", c.Languages);
    }

    [Fact]
    public void ValueNamePolicy_HasEnabledAndDisabledValues()
    {
        var p = Policy(Parse(), "DisableThing");
        Assert.Equal(AdmxClass.Machine, p.Class);
        Assert.Equal("DisableThing", p.ValueName);
        Assert.Equal(new AdmxValue(AdmxValueKind.Decimal, "1"), p.EnabledValue);
        Assert.Equal(new AdmxValue(AdmxValueKind.Decimal, "0"), p.DisabledValue);
        Assert.Empty(p.Elements);
    }

    [Fact]
    public void ListOnlyPolicy_UsesDefaultKeyAndItemKeys()
    {
        var p = Policy(Parse(), "ListOnly");
        Assert.Equal(AdmxClass.User, p.Class);
        Assert.Null(p.ValueName);
        Assert.Equal(2, p.EnabledList!.Count);
        Assert.Equal(@"Software\Policies\Test\Lists", p.EnabledList[0].Key);
        Assert.Equal(@"Software\Policies\Test\Lists\Sub", p.EnabledList[1].Key);
        Assert.Equal(AdmxValueKind.String, p.EnabledList[1].Value.Kind);
        // disabledList sem defaultKey herda a chave da política.
        Assert.Equal(@"Software\Policies\Test\Lists", p.DisabledList![0].Key);
        Assert.Equal(AdmxValueKind.Delete, p.DisabledList[1].Value.Kind);
    }

    [Fact]
    public void Elements_AreParsedWithRangesFlagsAndLabels()
    {
        var p = Policy(Parse(), "Elements");
        var byId = p.Elements.ToDictionary(e => e.Id);

        Assert.Equal(("decimal", 5UL, 600UL, "Timeout (s)"), (byId["Timeout"].Type, byId["Timeout"].MinValue!.Value, byId["Timeout"].MaxValue!.Value, byId["Timeout"].Label));
        // Padrão do esquema para decimal sem limites: 0..9999.
        Assert.Equal((0UL, 9999UL), (byId["NoRange"].MinValue!.Value, byId["NoRange"].MaxValue!.Value));
        Assert.Equal("longDecimal", byId["Big"].Type);
        Assert.Equal(100000000000UL, byId["Big"].MaxValue);
        Assert.Equal(new AdmxValue(AdmxValueKind.String, "enabled"), byId["Flag"].TrueValue);
        Assert.Equal(AdmxValueKind.Delete, byId["Flag"].FalseValue!.Kind);
        Assert.Equal("Turn on flag", byId["Flag"].Label);
        Assert.True(byId["Server"].Expandable);
        Assert.Equal(255, byId["Server"].MaxLength);
        Assert.Equal("Server name", byId["Server"].Label);
        Assert.Equal("multiText", byId["Lines"].Type);
        Assert.Equal(("list", @"Software\Policies\Test\Elements\Hosts", "Host", true),
            (byId["Hosts"].Type, byId["Hosts"].Key, byId["Hosts"].ValuePrefix, byId["Hosts"].Additive));
    }

    [Fact]
    public void FileWithoutDefaultNamespace_IsParsedAndMissingStringsStayAsReferences()
    {
        var p = Policy(Parse(), "Camera");
        Assert.Equal("Allow camera", p.DisplayName);
        Assert.Equal("$(string.Missing_Explain)", p.ExplainText);
        Assert.Equal(["Windows Components"], p.CategoryPath);
        Assert.Equal("AllowCamera", p.ValueName);
    }

    [Fact]
    public void MissingDirectory_ReportsError()
    {
        var c = AdmxParser.ParseDirectory(Path.Combine(FixtureDir, "nao-existe"));
        Assert.Equal(0, c.FileCount);
        Assert.NotEmpty(c.Errors);
    }

    // ---------------- estado ----------------

    private static AdmxScopeState Eval(AdmxPolicy p, Dictionary<string, AdmxPolicyStateReader.RegValue> values) =>
        AdmxPolicyStateReader.Evaluate(p, "HKLM",
            (key, name) => values.TryGetValue($@"{key}\{name}", out var v) ? v : null,
            key => values.Where(kv => kv.Key.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase))
                         .Select(kv => (kv.Key[(key.Length + 1)..], kv.Value)).ToList());

    private static AdmxPolicyStateReader.RegValue Dword(int v) => new(RegistryValueKind.DWord, v);
    private static AdmxPolicyStateReader.RegValue Sz(string v) => new(RegistryValueKind.String, v);

    [Fact]
    public void State_ValueNamePolicy()
    {
        var p = Policy(Parse(), "DisableThing");
        const string k = @"Software\Policies\Test\Thing\DisableThing";
        Assert.Equal(AdmxPolicyState.NotConfigured, Eval(p, []).State);
        Assert.Equal(AdmxPolicyState.Enabled, Eval(p, new() { [k] = Dword(1) }).State);
        Assert.Equal(AdmxPolicyState.Disabled, Eval(p, new() { [k] = Dword(0) }).State);
        var other = Eval(p, new() { [k] = Dword(7) });
        Assert.Equal(AdmxPolicyState.Unknown, other.State);
        Assert.Equal(["DisableThing=7"], other.Values);
    }

    [Fact]
    public void State_ListsAndElements()
    {
        var lists = Policy(Parse(), "ListOnly");
        Assert.Equal(AdmxPolicyState.Enabled, Eval(lists, new()
        {
            [@"Software\Policies\Test\Lists\A"] = Dword(1),
            [@"Software\Policies\Test\Lists\Sub\B"] = Sz("on"),
        }).State);
        Assert.Equal(AdmxPolicyState.Disabled, Eval(lists, new() { [@"Software\Policies\Test\Lists\A"] = Dword(0) }).State);

        var enumPolicy = Policy(Parse(), "Telemetry");
        var s = Eval(enumPolicy, new() { [@"Software\Policies\Test\Data\Level"] = Dword(0) });
        Assert.Equal(AdmxPolicyState.Enabled, s.State);
        Assert.Equal(["Level=0"], s.Values);

        var elements = Policy(Parse(), "Elements");
        var e = Eval(elements, new() { [@"Software\Policies\Test\Elements\Hosts\Host1"] = Sz("a"), [@"Software\Policies\Test\Elements\Hosts\Other"] = Sz("x") });
        Assert.Equal(AdmxPolicyState.Enabled, e.State);
        Assert.Equal([@"Software\Policies\Test\Elements\Hosts\Host1=a"], e.Values);
    }

    [Fact]
    public void State_ReadsTheRealRegistryInTheSandbox()
    {
        using var sandbox = new RegistrySandbox();
        var policy = new AdmxPolicy
        {
            File = "Sandbox.admx",
            Name = "Sandbox",
            Class = AdmxClass.User,
            Key = sandbox.SubKey,
            ValueName = "Value",
            EnabledValue = new AdmxValue(AdmxValueKind.Decimal, "1"),
            DisabledValue = new AdmxValue(AdmxValueKind.Decimal, "0"),
        };

        Assert.Equal(AdmxPolicyState.NotConfigured, Assert.Single(AdmxPolicyStateReader.Read(policy)).State);
        using (var key = sandbox.Create()) key.SetValue("Value", 0, RegistryValueKind.DWord);
        var state = Assert.Single(AdmxPolicyStateReader.Read(policy));
        Assert.Equal(("HKCU", AdmxPolicyState.Disabled), (state.Scope, state.State));
    }
}
