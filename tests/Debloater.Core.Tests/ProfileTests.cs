using Debloater.Core.Catalog;
using Debloater.Core.Engine;
using Debloater.Core.Model;
using Debloater.Core.Planning;
using Debloater.Core.Profiles;

namespace Debloater.Core.Tests;

internal static class TestTweaks
{
    public static Tweak Make(string id, RiskLevel risk = RiskLevel.Low, string category = "A/B", bool reversible = true,
        Confidence? confidence = null, string[]? tags = null) => new()
    {
        Id = id,
        Name = "Nome " + id,
        Description = "Descrição de " + id,
        Category = category,
        Risk = risk,
        Reversible = reversible,
        Confidence = confidence,
        Tags = [.. tags ?? []],
        Actions = [new ActionSpec { Type = ActionSpec.Registry, Path = @"HKCU\Software\X", Name = id, Kind = "dword", Value = 1 }],
    };

    public static TweakStatus Status(Tweak t, TweakState state) => new(t, state, []);
}

public class ProfileStoreTests
{
    private static readonly List<Tweak> Catalog =
    [
        TestTweaks.Make("p.low", RiskLevel.Low, "Privacidade/Telemetria"),
        TestTweaks.Make("p.med", RiskLevel.Medium, "Privacidade/Anúncios"),
        TestTweaks.Make("ai.app", RiskLevel.Low, "IA/Copilot", reversible: false),
        TestTweaks.Make("s.high", RiskLevel.High, "Sistema/Serviços"),
        TestTweaks.Make("s.crit", RiskLevel.Critical, "Sistema/Serviços"),
        TestTweaks.Make("x.other", RiskLevel.Low, "PrivacidadeX"),
    ];

    [Fact]
    public void Parse_RulesAndExplicitEntries()
    {
        var p = ProfileStore.Parse("""
            name: Teste
            description: d
            rules:
              maxRisk: medium
              categories: [Privacidade]
              excludeIds: [p.med]
            tweaks:
              - { id: s.high, desired: applied }
              - { id: p.low, desired: notApplied }
            """);

        Assert.Equal("Teste", p.Name);
        Assert.Equal(RiskLevel.Medium, p.Rules!.MaxRisk);
        Assert.Equal(DesiredState.NotApplied, p.Tweaks[1].Desired);
    }

    [Theory]
    [InlineData("description: sem nome\nrules: { maxRisk: low }", "name")]
    [InlineData("name: Vazio", "rules")]
    [InlineData("name: X\ntweaks:\n  - { id: a.b }\n  - { id: A.B }", "repetido")]
    [InlineData("name: X\ncampoErrado: 1", "linha")]
    public void Parse_InvalidProfiles_ThrowFormatException(string yaml, string expectedFragment)
    {
        var ex = Assert.Throws<FormatException>(() => ProfileStore.Parse(yaml));
        Assert.Contains(expectedFragment, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_RulesSelectByRiskCategoryAndExclusions_ExplicitWins()
    {
        var p = new Profile
        {
            Name = "x",
            Rules = new ProfileRules { MaxRisk = RiskLevel.Medium, Categories = ["Privacidade", "IA"], ExcludeIds = ["p.med"] },
            Tweaks = [new ProfileEntry { Id = "p.low", Desired = DesiredState.NotApplied }, new ProfileEntry { Id = "nao.existe" }],
        };

        var r = ProfileStore.Resolve(p, Catalog);

        Assert.Equal(DesiredState.NotApplied, r.Desired["p.low"]);
        Assert.Equal(DesiredState.Applied, r.Desired["ai.app"]);
        Assert.False(r.Desired.ContainsKey("p.med"));      // excluído
        Assert.False(r.Desired.ContainsKey("s.high"));     // categoria
        Assert.False(r.Desired.ContainsKey("x.other"));    // "PrivacidadeX" não é subcategoria de "Privacidade"
        Assert.Equal(["nao.existe"], r.UnknownIds);
    }

    [Fact]
    public void Resolve_MaxRiskHigh_NeverIncludesCritical_AndIrreversibleCanBeExcluded()
    {
        var r = ProfileStore.Resolve(new Profile { Name = "a", Rules = new ProfileRules { MaxRisk = RiskLevel.High, IncludeIrreversible = false } }, Catalog);

        Assert.Contains("s.high", r.Desired.Keys);
        Assert.DoesNotContain("s.crit", r.Desired.Keys);
        Assert.DoesNotContain("ai.app", r.Desired.Keys);
    }

    [Fact]
    public void SaveAndLoad_RoundTrip_UsesCamelCaseEnums()
    {
        var dir = Path.Combine(Path.GetTempPath(), "debloater-profiles-" + Guid.NewGuid().ToString("N"));
        try
        {
            var p = ProfileStore.FromSelection("Meu perfil", "desc",
                [new("b.two", DesiredState.NotApplied), new("a.one", DesiredState.Applied)]);
            var file = Path.Combine(dir, ProfileStore.FileNameFor(p.Name));
            ProfileStore.Save(p, file);

            var text = File.ReadAllText(file);
            Assert.Contains("notApplied", text);
            Assert.EndsWith("meu-perfil.yaml", file);

            var loaded = ProfileStore.LoadDirectory(dir, builtIn: false);
            var single = Assert.Single(loaded);
            Assert.Equal("Meu perfil", single.Name);
            Assert.Equal(["a.one", "b.two"], single.Tweaks.Select(t => t.Id));
            Assert.Equal(DesiredState.NotApplied, single.Tweaks[1].Desired);
            Assert.Equal(file, single.SourceFile);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void LoadDirectory_CollectsErrorsForInvalidFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "debloater-profiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "ruim.yaml"), "name: [quebrado");
            File.WriteAllText(Path.Combine(dir, "bom.yaml"), "name: Bom\nrules: { maxRisk: low }");
            var errors = new List<string>();

            var loaded = ProfileStore.LoadDirectory(dir, builtIn: true, errors);

            Assert.True(Assert.Single(loaded).IsBuiltIn);
            Assert.Contains(errors, e => e.StartsWith("ruim.yaml"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("Meu Perfil!", "meu-perfil.yaml")]
    [InlineData("  ", "perfil.yaml")]
    [InlineData("Privacidade máxima", "privacidade-máxima.yaml")]
    public void FileNameFor(string name, string expected) => Assert.Equal(expected, ProfileStore.FileNameFor(name));

    [Fact]
    public void BuiltInProfiles_LoadAndResolveAgainstRealCatalog()
    {
        var catalogDir = Path.Combine(AppContext.BaseDirectory, "catalog");
        var tweaks = CatalogLoader.LoadDirectory(catalogDir).Tweaks;
        var errors = new List<string>();
        var profiles = ProfileStore.LoadDirectory(Path.Combine(catalogDir, CatalogLoader.ProfilesFolder), builtIn: true, errors);

        Assert.Empty(errors);
        Assert.Equal(["Agressivo", "Privacidade", "Seguro"], profiles.Select(p => p.Name).Order());
        foreach (var p in profiles)
        {
            var r = ProfileStore.Resolve(p, tweaks);
            Assert.NotEmpty(r.Desired);
            Assert.Empty(r.UnknownIds);
            // Nenhum perfil embutido inclui risco crítico.
            Assert.DoesNotContain(r.Desired.Keys, id => tweaks.First(t => t.Id == id).Risk == RiskLevel.Critical);
        }
        var seguro = ProfileStore.Resolve(profiles.First(p => p.Name == "Seguro"), tweaks);
        Assert.All(seguro.Desired.Keys, id => Assert.Equal(RiskLevel.Low, tweaks.First(t => t.Id == id).Risk));
        Assert.All(seguro.Desired.Keys, id => Assert.True(tweaks.First(t => t.Id == id).Reversible));
    }

    [Fact]
    public void CatalogLoader_IgnoresProfilesFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "debloater-cat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "profiles"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.yaml"), """
                category: A/B
                tweaks:
                  - id: test.one
                    name: Um
                    description: d
                    actions:
                      - { type: registry, path: HKCU\Software\X, name: V, kind: dword, value: 1 }
                """);
            File.WriteAllText(Path.Combine(dir, "profiles", "p.yaml"), "name: P\nrules: { maxRisk: low }");

            var result = CatalogLoader.LoadDirectory(dir);

            Assert.True(result.IsValid, string.Join("\n", result.Errors));
            Assert.Equal(1, result.FileCount);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public class ChangePlannerTests
{
    [Theory]
    [InlineData(TweakState.NotApplied, DesiredState.Applied, true)]
    [InlineData(TweakState.Partial, DesiredState.Applied, true)]
    [InlineData(TweakState.Error, DesiredState.Applied, true)]
    [InlineData(TweakState.Applied, DesiredState.Applied, false)]
    [InlineData(TweakState.NotApplicable, DesiredState.Applied, false)]
    [InlineData(TweakState.Applied, DesiredState.NotApplied, true)]
    [InlineData(TweakState.Partial, DesiredState.NotApplied, true)]
    [InlineData(TweakState.NotApplied, DesiredState.NotApplied, false)]
    [InlineData(TweakState.NotApplicable, DesiredState.NotApplied, false)]
    [InlineData(TweakState.Error, DesiredState.NotApplied, false)]
    public void NeedsChange(TweakState current, DesiredState desired, bool expected) =>
        Assert.Equal(expected, ChangePlanner.NeedsChange(current, desired));

    [Fact]
    public void Plan_ApplyAndRevert_BlocksRevertWithoutBackupOrIrreversible_SortedByRiskDesc()
    {
        var low = TestTweaks.Make("a.low");
        var high = TestTweaks.Make("a.high", RiskLevel.High);
        var revertOk = TestTweaks.Make("r.ok", RiskLevel.Medium);
        var revertNoBackup = TestTweaks.Make("r.nobackup");
        var revertIrreversible = TestTweaks.Make("r.irrev", reversible: false);
        var untouched = TestTweaks.Make("u.none");
        var statuses = new[]
        {
            TestTweaks.Status(low, TweakState.NotApplied),
            TestTweaks.Status(high, TweakState.Partial),
            TestTweaks.Status(revertOk, TweakState.Applied),
            TestTweaks.Status(revertNoBackup, TweakState.Applied),
            TestTweaks.Status(revertIrreversible, TweakState.Applied),
            TestTweaks.Status(untouched, TweakState.NotApplied),
        };
        var desired = new Dictionary<string, DesiredState>
        {
            ["a.low"] = DesiredState.Applied,
            ["a.high"] = DesiredState.Applied,
            ["r.ok"] = DesiredState.NotApplied,
            ["r.nobackup"] = DesiredState.NotApplied,
            ["r.irrev"] = DesiredState.NotApplied,
        };

        var plan = ChangePlanner.Plan(statuses, desired, id => id == "r.ok");

        Assert.Equal(5, plan.Count);
        Assert.Equal("a.high", plan[0].Tweak.Id);
        Assert.Equal(ChangeKind.Apply, plan.Single(c => c.Tweak.Id == "a.low").Kind);
        Assert.False(plan.Single(c => c.Tweak.Id == "r.ok").IsBlocked);
        Assert.Contains("backup", plan.Single(c => c.Tweak.Id == "r.nobackup").BlockedReason);
        Assert.Contains("reversível", plan.Single(c => c.Tweak.Id == "r.irrev").BlockedReason);
        Assert.Equal(RiskLevel.High, ChangePlanner.MaxRisk(plan));
        Assert.Equal(RiskLevel.Low, ChangePlanner.MaxRisk([]));
    }
}

public class TweakQueryTests
{
    private static readonly Tweak T = TestTweaks.Make("privacy.ads.id", RiskLevel.Medium, "Privacidade/Anúncios",
        confidence: Confidence.Official, tags: ["publicidade"]);

    [Theory]
    [InlineData("ADS", true)]
    [InlineData("anuncios", false)]        // categoria não entra na busca
    [InlineData("descricao privacy", true)] // sem acento, várias palavras
    [InlineData("publicidade", true)]       // tags
    [InlineData("privacy inexistente", false)]
    [InlineData("  ", true)]
    public void Search(string text, bool expected) =>
        Assert.Equal(expected, new TweakQuery { Search = text }.Matches(T, TweakState.Applied, null));

    [Fact]
    public void Filters()
    {
        Assert.True(new TweakQuery { Category = "Privacidade" }.Matches(T, null, null));
        Assert.False(new TweakQuery { Category = "Priv" }.Matches(T, null, null));
        Assert.True(new TweakQuery { States = new HashSet<TweakState> { TweakState.Applied } }.Matches(T, TweakState.Applied, null));
        Assert.False(new TweakQuery { States = new HashSet<TweakState> { TweakState.Applied } }.Matches(T, null, null));
        Assert.False(new TweakQuery { Risks = new HashSet<RiskLevel> { RiskLevel.Low } }.Matches(T, null, null));
        Assert.True(new TweakQuery { Confidences = new HashSet<Confidence> { Confidence.Official } }.Matches(T, null, null));
        // Sem 'confidence' conta como community.
        Assert.True(new TweakQuery { Confidences = new HashSet<Confidence> { Confidence.Community } }.Matches(TestTweaks.Make("a.b"), null, null));
        Assert.True(new TweakQuery { OnlyDiffering = true }.Matches(T, TweakState.NotApplied, DesiredState.Applied));
        Assert.False(new TweakQuery { OnlyDiffering = true }.Matches(T, TweakState.Applied, DesiredState.Applied));
        Assert.False(new TweakQuery { OnlyDiffering = true }.Matches(T, TweakState.Applied, null));
    }

    [Fact]
    public void CategoryTree()
    {
        var roots = CategoryNode.Build(["Sistema/Serviços", "Privacidade/Telemetria", "Privacidade/Anúncios", "Privacidade/Telemetria"]);

        Assert.Equal(["Privacidade", "Sistema"], roots.Select(r => r.Name));
        Assert.Equal(3, roots[0].Count);
        Assert.Equal(["Anúncios", "Telemetria"], roots[0].Children.Select(c => c.Name));
        Assert.Equal("Privacidade/Telemetria", roots[0].Children[1].Path);
        Assert.Equal(2, roots[0].Children[1].Count);
    }
}

public class OperationLogTests
{
    [Fact]
    public void WritesLinesToFileAndRaisesEvent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "debloater-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            string? seen = null;
            string path;
            using (var log = new OperationLog(dir, new DateTime(2026, 1, 2, 3, 4, 5)))
            {
                log.LineWritten += l => seen = l;
                log.Write("aplicando x");
                path = log.FilePath;
                Assert.Null(log.Error);
            }
            Assert.EndsWith("2026-01-02_030405.log", path);
            Assert.Contains("aplicando x", File.ReadAllText(path));
            Assert.Contains("aplicando x", seen);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
