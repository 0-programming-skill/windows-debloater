using Debloater.Core.Admx;
using Debloater.Core.Engine;
using Debloater.Core.Model;
using Debloater.Core.Planning;
using Debloater.Core.Platform;
using Debloater.Core.Policies;
using Debloater.Core.Profiles;
using Debloater.Core.Reference;
using Microsoft.Win32;

namespace Debloater.Core.Tests;

/// <summary>
/// Gravação real no registro, mas redirecionada para a sandbox HKCU\Software\DebloaterTests\&lt;guid&gt;
/// (as raízes HKLM e HKCU viram subchaves dela). Nunca toca chaves de política reais e não exige elevação.
/// </summary>
public sealed class PolicyEngineTests : IDisposable
{
    private readonly RegistrySandbox _sandbox = new();
    private readonly string _backupDir = Path.Combine(Path.GetTempPath(), "debloater-policy-tests-" + Guid.NewGuid().ToString("N"));
    private readonly PolicyEngine _engine;
    private readonly WindowsPolicyRegistry _registry;

    private static readonly SystemInfo NotElevated = new(26200, 1, "Professional", "25H2", "Windows 11 Pro", IsElevated: false);
    private const string All = @"Software\Policies\Test\W\All";

    public PolicyEngineTests()
    {
        _registry = WindowsPolicyRegistry.Redirected(_sandbox.SubKey);
        _engine = new PolicyEngine(new PolicyEngineOptions
        {
            BackupDirectory = _backupDir,
            RequireElevation = false,
            System = NotElevated,
            Registry = _registry,
        });
    }

    public void Dispose()
    {
        _sandbox.Dispose();
        if (Directory.Exists(_backupDir)) Directory.Delete(_backupDir, true);
    }

    private static AdmxPolicy P(string name) => PolicyWriterTests.P(name);
    private static PolicySetting S(string name, PolicyTargetState state) => new() { Policy = "Writer.admx:" + name, State = state };

    /// <summary>Chave real (HKCU) correspondente a uma chave de política dentro da sandbox.</summary>
    private RegistryKey? Real(string root, string key) => Registry.CurrentUser.OpenSubKey($@"{_sandbox.SubKey}\{root}\{key}");

    private object? RealValue(string root, string key, string name)
    {
        using var k = Real(root, key);
        return k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    [Fact]
    public void Redirected_RootsStayInsideTheSandbox()
    {
        Assert.True(_registry.IsRedirected);
        Assert.Equal($@"HKCU\{_sandbox.SubKey}\HKLM", _registry.Describe(PolicyScope.Machine));
        Assert.Equal($@"HKCU\{_sandbox.SubKey}\HKCU", _registry.Describe(PolicyScope.User));
        Assert.Equal("HKLM", WindowsPolicyRegistry.Default.Describe(PolicyScope.Machine));
    }

    [Fact]
    public void Apply_WritesRealValues_BackupKeepsFirstOriginal_RevertRestoresExactly()
    {
        // Estado original: um valor pré-existente da política e um valor alheio na mesma chave.
        using (var k = _sandbox.Create($@"HKLM\{All}"))
        {
            k.SetValue("Count", 3, RegistryValueKind.DWord);
            k.SetValue("Unrelated", "keep", RegistryValueKind.String);
        }
        var p = P("AllElements");

        var r1 = _engine.Apply(p, S("AllElements", PolicyTargetState.Enabled).Set("Count", "9").Set("Lines", "a", "b").Set("Numbered", "%TEMP%"));
        Assert.True(r1.Success, string.Join(" ", r1.Errors) + r1.Message);
        Assert.Equal(AdmxPolicyState.Enabled, r1.StateAfter);
        Assert.Equal(9, RealValue("HKLM", All, "Count"));
        Assert.Equal(new[] { "a", "b" }, RealValue("HKLM", All, "Lines"));
        using (var numbered = Real("HKLM", All + @"\Numbered"))
            Assert.Equal(RegistryValueKind.ExpandString, numbered!.GetValueKind("1"));
        Assert.Equal("%TEMP%", RealValue("HKLM", All + @"\Numbered", "1"));

        var backup = _engine.Backups.Load(p.QualifiedName)!;
        Assert.EndsWith(Path.Combine("policies", "Writer.admx__AllElements.json"), _engine.Backups.PathFor(p.QualifiedName));
        var count = backup.Find("HKLM", All, "Count")!;
        Assert.True(count.Existed);
        Assert.Equal(("dword", "3"), (count.Kind, count.Data));
        Assert.False(backup.Find("HKLM", All, "Lines")!.Existed);
        Assert.Contains(backup.CreatedKeys, k => k.Key == All + @"\Numbered");

        // Segunda aplicação: o original (3) é preservado.
        var r2 = _engine.Apply(p, S("AllElements", PolicyTargetState.Enabled).Set("Count", "20"));
        Assert.True(r2.Success, string.Join(" ", r2.Errors));
        Assert.Equal(20, RealValue("HKLM", All, "Count"));
        Assert.Null(RealValue("HKLM", All, "Lines"));
        Assert.Equal("3", _engine.Backups.Load(p.QualifiedName)!.Find("HKLM", All, "Count")!.Data);
        Assert.Equal(2, _engine.Backups.Load(p.QualifiedName)!.History.Count);

        // Reverter: volta exatamente ao original e apaga o backup e as chaves criadas.
        var rv = _engine.Revert(p.QualifiedName, p);
        Assert.True(rv.Success, string.Join(" ", rv.Errors));
        Assert.Equal(3, RealValue("HKLM", All, "Count"));
        Assert.Equal("keep", RealValue("HKLM", All, "Unrelated"));
        using (var k = Real("HKLM", All)!)
        {
            Assert.Equal(["Count", "Unrelated"], k.GetValueNames().Order(StringComparer.Ordinal));
            Assert.Empty(k.GetSubKeyNames());
        }
        Assert.False(_engine.Backups.Exists(p.QualifiedName));
        Assert.Equal(AdmxPolicyState.Enabled, rv.StateAfter);   // "Count" sozinho é evidência de habilitada
    }

    [Fact]
    public void Revert_RemovesEverythingCreatedFromScratch()
    {
        var p = P("Mixed");
        var r = _engine.Apply(p, S("Mixed", PolicyTargetState.Enabled));
        Assert.True(r.Success, string.Join(" ", r.Errors));
        Assert.Equal("on", RealValue("HKCU", @"Software\Policies\Test\W\Mixed", "Mode"));
        Assert.Equal(5000000000L, RealValue("HKCU", @"Software\Policies\Test\W\Mixed\Sub", "B"));

        Assert.True(_engine.Revert(p.QualifiedName, p).Success);
        // A raiz redirecionada (equivalente a HKCU) continua existindo, mas vazia: "Software\..." foi apagado.
        using var root = Registry.CurrentUser.OpenSubKey($@"{_sandbox.SubKey}\HKCU");
        Assert.True(root is null || (root.SubKeyCount == 0 && root.ValueCount == 0), "a raiz da sandbox deveria ficar vazia");
    }

    [Fact]
    public void NotConfigured_DeletesListKey_AndRevertRecreatesIt()
    {
        using (var k = _sandbox.Create($@"HKLM\{All}\Plain")) k.SetValue("x", "x", RegistryValueKind.String);
        var p = P("AllElements");

        var r = _engine.Apply(p, S("AllElements", PolicyTargetState.NotConfigured));
        Assert.True(r.Success, string.Join(" ", r.Errors));
        Assert.Null(Real("HKLM", All + @"\Plain"));
        Assert.Contains(_engine.Backups.Load(p.QualifiedName)!.DeletedKeys, d => d.Key == All + @"\Plain");

        Assert.True(_engine.Revert(p.QualifiedName).Success);
        Assert.Equal("x", RealValue("HKLM", All + @"\Plain", "x"));
    }

    [Fact]
    public void Apply_InvalidOrNoOp_DoesNotCreateBackup()
    {
        var p = P("AllElements");
        var bad = _engine.Apply(p, S("AllElements", PolicyTargetState.Enabled).Set("Count", "500"));
        Assert.False(bad.Success);
        Assert.False(_engine.Backups.Exists(p.QualifiedName));

        var noop = _engine.Apply(p, S("AllElements", PolicyTargetState.NotConfigured));
        Assert.True(noop.Success);
        Assert.Contains("nada", noop.Message);
        Assert.False(_engine.Backups.Exists(p.QualifiedName));
    }

    [Fact]
    public void Apply_LogsOperations()
    {
        var logDir = Path.Combine(_backupDir, "logs");
        using var log = new OperationLog(logDir);
        var r = _engine.Apply(P("Implicit"), S("Implicit", PolicyTargetState.Enabled), log);
        Assert.True(r.Success);
        Assert.Contains(log.Lines, l => l.Contains(@"+ HKLM\Software\Policies\Test\W\Implicit\Flag = 1 (REG_DWORD)"));
        Assert.Single(r.Operations);
    }

    [Fact]
    public void Revert_WithoutBackup_Fails()
    {
        var r = _engine.Revert("Writer.admx:Implicit");
        Assert.False(r.Success);
        Assert.Contains("Nenhum backup", r.Message);
    }

    [Fact]
    public void RequiresElevation_ByDefault()
    {
        var engine = new PolicyEngine(new PolicyEngineOptions { BackupDirectory = _backupDir, System = NotElevated, Registry = _registry });
        Assert.Throws<ElevationRequiredException>(() => engine.Apply(P("Implicit"), S("Implicit", PolicyTargetState.Enabled)));
        Assert.Throws<ElevationRequiredException>(() => engine.Revert("Writer.admx:Implicit"));
        // Planejar (somente leitura) não exige elevação.
        Assert.True(engine.Plan(P("Implicit"), S("Implicit", PolicyTargetState.Enabled)).HasEffect);
    }

    // ---------------- planejador ----------------

    [Fact]
    public void Planner_BlocksInvalidAndRevertWithoutBackup_OmitsNoOps_SortsByRisk()
    {
        var defender = new AdmxPolicy
        {
            File = "WindowsDefender.admx", Name = "DisableX", DisplayName = "Z Defender", Class = AdmxClass.Machine,
            Key = @"Software\Policies\Test\Defender", ValueName = "DisableX",
        };
        var requests = new[]
        {
            new PolicyRequest(P("Implicit"), S("Implicit", PolicyTargetState.Enabled)),
            new PolicyRequest(P("AllElements"), S("AllElements", PolicyTargetState.Enabled).Set("Count", "0")),
            new PolicyRequest(P("Mixed"), S("Mixed", PolicyTargetState.NotConfigured)),      // já está assim
            new PolicyRequest(P("ElementsOnly"), null),                                       // reverter sem backup
            new PolicyRequest(defender, new PolicySetting { Policy = defender.QualifiedName, State = PolicyTargetState.Enabled }),
        };
        var plan = PolicyChangePlanner.Plan(requests, _engine, (_, _, _) => [new PolicyWarning(PolicyWarningLevel.Info, "x")]);

        Assert.Equal(4, plan.Count);
        Assert.Equal("WindowsDefender.admx", plan[0].Policy.File);
        Assert.Equal(RiskLevel.High, plan[0].Risk);
        Assert.All(plan.Skip(1), c => Assert.Equal(RiskLevel.Medium, c.Risk));
        Assert.Contains(plan, c => c.Policy.Name == "AllElements" && c.IsBlocked && c.BlockedReason!.Contains("inválida"));
        Assert.Contains(plan, c => c.Policy.Name == "ElementsOnly" && c.IsRevert && c.BlockedReason!.Contains("Sem backup"));
        var ok = Assert.Single(plan, c => c.Policy.Name == "Implicit");
        Assert.False(ok.IsBlocked);
        Assert.Single(ok.Preview);
        Assert.Single(ok.Warnings);
        Assert.Equal(RiskLevel.High, PolicyChangePlanner.MaxRisk(plan));

        // Com backup, reverter vira uma mudança executável com prévia dos originais.
        Assert.True(_engine.Apply(P("ElementsOnly"), S("ElementsOnly", PolicyTargetState.Enabled).Set("Level", "1")).Success);
        var revert = Assert.Single(PolicyChangePlanner.Plan([new PolicyRequest(P("ElementsOnly"), null)], _engine));
        Assert.False(revert.IsBlocked);
        Assert.Contains(revert.Preview, l => l.Contains("Level (não existia)"));
    }
}

public class PolicyAdvisorTests
{
    private static AdmxPolicy Policy(string file = "X.admx", string key = @"Software\Policies\X", string? supported = null, params string[] category) => new()
    {
        File = file, Name = "N", Key = key, ValueName = "V", SupportedOn = supported, CategoryPath = [.. category],
    };

    private static PolicyEnvironment Env(string edition = "Professional", bool domain = false, IReadOnlyList<RegistryPolEntry>? gpo = null) =>
        new(26200, edition, domain, gpo ?? [], []);

    [Theory]
    [InlineData("WindowsDefender.admx", @"Software\Policies\X", "", RiskLevel.High)]
    [InlineData("X.admx", @"Software\Policies\Microsoft\Windows\WindowsUpdate\AU", "", RiskLevel.High)]
    [InlineData("X.admx", @"Software\Policies\X", "Componentes do Windows/Segurança do Windows", RiskLevel.High)]
    [InlineData("X.admx", @"Software\Policies\Microsoft\FVE", "", RiskLevel.High)]
    [InlineData("X.admx", @"Software\Policies\X\Collapse", "Start Menu", RiskLevel.Medium)]
    [InlineData("DataCollection.admx", @"Software\Policies\Microsoft\Windows\DataCollection", "Componentes do Windows/Coleta de Dados", RiskLevel.Medium)]
    public void Risk_IsHighOnlyForSecurityUpdateDefender(string file, string key, string category, RiskLevel expected) =>
        Assert.Equal(expected, PolicyAdvisor.Risk(Policy(file, key, null, category.Split('/', StringSplitOptions.RemoveEmptyEntries))));

    [Theory]
    [InlineData("Professional", "Pro")]
    [InlineData("Core", "Home")]
    [InlineData("CoreSingleLanguage", "Home")]
    [InlineData("EnterpriseS", "Enterprise")]
    [InlineData("IoTEnterpriseS", "IoT Enterprise")]
    [InlineData("Education", "Education")]
    [InlineData("ServerDatacenter", "Server")]
    public void EditionFamily(string id, string family) => Assert.Equal(family, PolicyAdvisor.EditionFamily(id));

    [Fact]
    public void Warnings_EditionBuildSupportedOnDomainAndLocalGpo()
    {
        var csp = new CspPolicy { Area = "A", Name = "N", Page = "p", Editions = ["Enterprise", "Education"], Win11MinBuild = 26300, Deprecated = true };
        var w = PolicyAdvisor.Warnings(Policy(), csp, Env(), PolicyTargetState.Enabled, PolicyScope.Machine).Select(x => x.Text).ToList();
        Assert.Contains(w, t => t.Contains("só tem efeito em: Enterprise, Education") && t.Contains("Pro"));
        Assert.Contains(w, t => t.Contains("26300"));
        Assert.Contains(w, t => t.Contains("obsoleta"));
        Assert.Contains(w, t => t.Contains("gpedit.msc continua"));

        var pro = new CspPolicy { Area = "A", Name = "N", Page = "p", Editions = ["Pro", "Enterprise"] };
        Assert.DoesNotContain(PolicyAdvisor.Warnings(Policy(), pro, Env(), PolicyTargetState.Enabled, PolicyScope.Machine),
            x => x.Level == PolicyWarningLevel.Warning);

        Assert.Contains(PolicyAdvisor.Warnings(Policy(), null, Env("Core"), PolicyTargetState.Enabled, PolicyScope.Machine), x => x.Text.Contains("Home"));
        Assert.Contains(PolicyAdvisor.Warnings(Policy(supported: "Windows Server 2003 only"), null, Env(), PolicyTargetState.Enabled, PolicyScope.Machine),
            x => x.Text.Contains("sem efeito"));
        Assert.Contains(PolicyAdvisor.Warnings(Policy(supported: "At least Windows Server 2012"), null, Env(), PolicyTargetState.Enabled, PolicyScope.Machine),
            x => x.Text.Contains("Windows Server"));
        Assert.DoesNotContain(PolicyAdvisor.Warnings(Policy(supported: "At least Windows Server 2012, Windows 8 or Windows RT"), null, Env(), PolicyTargetState.Enabled, PolicyScope.Machine),
            x => x.Level == PolicyWarningLevel.Warning);
        Assert.Contains(PolicyAdvisor.Warnings(Policy(supported: "At least Windows 10 Enterprise"), null, Env(), PolicyTargetState.Enabled, PolicyScope.Machine),
            x => x.Text.Contains("pode ignorar"));
        Assert.Contains(PolicyAdvisor.Warnings(Policy(), null, Env(domain: true), PolicyTargetState.Enabled, PolicyScope.Machine), x => x.Text.Contains("domínio"));

        var gpo = new[] { new RegistryPolEntry(@"Software\Policies\X", "**del.V", 1, []) };
        Assert.Contains(PolicyAdvisor.Warnings(Policy(), null, Env(gpo: gpo), PolicyTargetState.Disabled, PolicyScope.Machine), x => x.Text.Contains("GPO local"));
        Assert.DoesNotContain(PolicyAdvisor.Warnings(Policy(), null, Env(gpo: gpo), PolicyTargetState.Disabled, PolicyScope.User), x => x.Text.Contains("GPO local"));
    }

    [Fact]
    public void RegistryPol_RoundTripAndValidation()
    {
        var entries = new[]
        {
            new RegistryPolEntry(@"Software\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 4, BitConverter.GetBytes(0)),
            new RegistryPolEntry(@"Software\Policies\X\List", "**delvals.", 1, [0x20, 0]),
            new RegistryPolEntry(@"Software\Policies\X", "Name", 1, System.Text.Encoding.Unicode.GetBytes("valor;[]\0")),
        };
        var bytes = RegistryPolFile.Build(entries);
        var parsed = RegistryPolFile.Parse(bytes);
        Assert.Equal(3, parsed.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal((entries[i].Key, entries[i].ValueName, entries[i].Type), (parsed[i].Key, parsed[i].ValueName, parsed[i].Type));
            Assert.Equal(entries[i].Data, parsed[i].Data);
        }
        Assert.True(RegistryPolFile.Touches(parsed[1], @"Software\Policies\X\List", "qualquer"));
        Assert.True(RegistryPolFile.Touches(parsed[0], @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "allowtelemetry"));
        Assert.False(RegistryPolFile.Touches(parsed[0], @"Software\Policies\Microsoft\Windows\DataCollection", "Other"));

        Assert.Empty(RegistryPolFile.Parse(RegistryPolFile.Build([])));
        Assert.Throws<FormatException>(() => RegistryPolFile.Parse(new byte[] { 1, 2, 3, 4, 1, 0, 0, 0 }));
        Assert.Throws<FormatException>(() => RegistryPolFile.Parse(bytes.AsSpan(0, bytes.Length - 3)));
        Assert.Empty(RegistryPolFile.TryRead(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pol")));
    }
}

public class ProfilePolicyTests
{
    private const string Yaml = """
        name: Políticas
        policies:
          - { policy: "Writer.admx:Implicit", state: enabled }
          - policy: writer.admx:allelements
            state: enabled
            scope: user
            values:
              Count: 12
              Check: false
              Lines: [a, b]
              Pairs: { k1: v1, k2: "2" }
          - { policy: "Writer.admx:Mixed", state: notConfigured }
          - { policy: "Nope.admx:Missing", state: disabled }
        """;

    [Fact]
    public void Parse_PoliciesOnlyProfile_WithScalarSequenceAndMapValues()
    {
        var p = ProfileStore.Parse(Yaml);
        Assert.Equal(4, p.Policies!.Count);
        var all = p.Policies[1].ToSetting();
        Assert.Equal(PolicyTargetState.Enabled, all.State);
        Assert.Equal(PolicyScope.User, all.Scope);
        Assert.Equal(["12"], all.Values["Count"]);
        Assert.Equal(["false"], all.Values["Check"]);
        Assert.Equal(["a", "b"], all.Values["Lines"]);
        Assert.Equal(["k1=v1", "k2=2"], all.Values["Pairs"]);
        Assert.Equal(PolicyTargetState.NotConfigured, p.Policies[2].State);

        var (requests, unknown) = ProfileStore.ResolvePolicies(p, PolicyWriterTests.Catalog.Policies);
        Assert.Equal(["Nope.admx:Missing"], unknown);
        Assert.Equal(3, requests.Count);
        Assert.Equal("Writer.admx:AllElements", requests[1].Setting!.Policy);   // nome normalizado pela definição
        Assert.True(PolicyWriter.Plan(requests[1].Policy, requests[1].Setting!, new MemoryPolicyRegistry()).IsValid);

        Assert.Equal(4, ProfileStore.Resolve(p, []).Policies.Count);
    }

    [Theory]
    [InlineData("name: X\npolicies:\n  - { policy: SemDoisPontos, state: enabled }", "'policy' inválido")]
    [InlineData("name: X\npolicies:\n  - { policy: \"A.admx:B\" }\n  - { policy: \"a.admx:b\" }", "repetida")]
    [InlineData("name: X\npolicies:\n  - { policy: \"A.admx:B\", state: talvez }", "Perfil inválido")]
    [InlineData("name: X\npolicies: []", "'policies'")]
    public void Parse_InvalidPolicies(string yaml, string fragment)
    {
        var ex = Assert.Throws<FormatException>(() => ProfileStore.Parse(yaml));
        Assert.Contains(fragment, ex.Message);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsPolicySettings()
    {
        var settings = new[]
        {
            new PolicySetting { Policy = "Writer.admx:AllElements", State = PolicyTargetState.Enabled, Scope = PolicyScope.User }
                .Set("Count", "7").Set("Lines", "a", "b").Set("Path", "%SystemRoot%").Set("Pairs", "x=1"),
            new PolicySetting { Policy = "Writer.admx:Implicit", State = PolicyTargetState.Disabled },
        };
        var profile = ProfileStore.FromSelection("Com políticas", null, [new("a.one", DesiredState.Applied)], settings);
        var text = ProfileStore.Serialize(profile);
        Assert.Contains("policies:", text);
        Assert.Contains("state: disabled", text);
        Assert.Contains("scope: user", text);

        var loaded = ProfileStore.Parse(text);
        var back = loaded.Policies!.Select(e => e.ToSetting()).OrderBy(s => s.Policy, StringComparer.Ordinal).ToList();
        Assert.Equal(2, back.Count);
        Assert.True(back[0].SameAs(settings[0], AdmxClass.Both));
        Assert.True(back[1].SameAs(settings[1], AdmxClass.Machine));
        Assert.Equal(["x=1"], back[0].Values["Pairs"]);

        // Sem políticas, o YAML não ganha a seção.
        Assert.DoesNotContain("policies", ProfileStore.Serialize(ProfileStore.FromSelection("Só tweaks", null, [new("a", DesiredState.Applied)])));
    }
}
