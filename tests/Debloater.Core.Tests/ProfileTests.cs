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
        Name = "Name " + id,
        Description = "Description of " + id,
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
        TestTweaks.Make("p.low", RiskLevel.Low, "Privacy/Telemetry"),
        TestTweaks.Make("p.med", RiskLevel.Medium, "Privacy/Ads"),
        TestTweaks.Make("ai.app", RiskLevel.Low, "AI/Copilot", reversible: false),
        TestTweaks.Make("s.high", RiskLevel.High, "System/Services"),
        TestTweaks.Make("s.crit", RiskLevel.Critical, "System/Services"),
        TestTweaks.Make("x.other", RiskLevel.Low, "PrivacyX"),
    ];

    [Fact]
    public void Parse_RulesAndExplicitEntries()
    {
        var p = ProfileStore.Parse("""
            name: Test
            description: d
            rules:
              maxRisk: medium
              categories: [Privacy]
              excludeIds: [p.med]
            tweaks:
              - { id: s.high, desired: applied }
              - { id: p.low, desired: notApplied }
            """);

        Assert.Equal("Test", p.Name);
        Assert.Equal(RiskLevel.Medium, p.Rules!.MaxRisk);
        Assert.Equal(DesiredState.NotApplied, p.Tweaks[1].Desired);
    }

    [Theory]
    [InlineData("description: no name\nrules: { maxRisk: low }", "name")]
    [InlineData("name: Empty", "rules")]
    [InlineData("name: X\ntweaks:\n  - { id: a.b }\n  - { id: A.B }", "duplicate")]
    [InlineData("name: X\nwrongField: 1", "line")]
    [InlineData("name: X\nrules: { maxRisk: low }\ni18n: { xx-YY: { name: Y } }", "unknown language")]
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
            Rules = new ProfileRules { MaxRisk = RiskLevel.Medium, Categories = ["Privacy", "AI"], ExcludeIds = ["p.med"] },
            Tweaks = [new ProfileEntry { Id = "p.low", Desired = DesiredState.NotApplied }, new ProfileEntry { Id = "does.not-exist" }],
        };

        var r = ProfileStore.Resolve(p, Catalog);

        Assert.Equal(DesiredState.NotApplied, r.Desired["p.low"]);
        Assert.Equal(DesiredState.Applied, r.Desired["ai.app"]);
        Assert.False(r.Desired.ContainsKey("p.med"));      // excluded
        Assert.False(r.Desired.ContainsKey("s.high"));     // category
        Assert.False(r.Desired.ContainsKey("x.other"));    // "PrivacyX" is not a subcategory of "Privacy"
        Assert.Equal(["does.not-exist"], r.UnknownIds);
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
            var p = ProfileStore.FromSelection("My profile", "desc",
                [new("b.two", DesiredState.NotApplied), new("a.one", DesiredState.Applied)]);
            var file = Path.Combine(dir, ProfileStore.FileNameFor(p.Name));
            ProfileStore.Save(p, file);

            var text = File.ReadAllText(file);
            Assert.Contains("notApplied", text);
            Assert.EndsWith("my-profile.yaml", file);
            Assert.DoesNotContain("i18n", text);

            var loaded = ProfileStore.LoadDirectory(dir, builtIn: false);
            var single = Assert.Single(loaded);
            Assert.Equal("My profile", single.Name);
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
            File.WriteAllText(Path.Combine(dir, "bad.yaml"), "name: [broken");
            File.WriteAllText(Path.Combine(dir, "good.yaml"), "name: Good\nrules: { maxRisk: low }");
            var errors = new List<string>();

            var loaded = ProfileStore.LoadDirectory(dir, builtIn: true, errors);

            Assert.True(Assert.Single(loaded).IsBuiltIn);
            Assert.Contains(errors, e => e.StartsWith("bad.yaml"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("My Profile!", "my-profile.yaml")]
    [InlineData("  ", "profile.yaml")]
    [InlineData("Résumé Profile", "résumé-profile.yaml")] // accented letters are kept
    public void FileNameFor(string name, string expected) => Assert.Equal(expected, ProfileStore.FileNameFor(name));

    [Fact]
    public void BuiltInProfiles_LoadAndResolveAgainstRealCatalog()
    {
        var catalogDir = Path.Combine(AppContext.BaseDirectory, "catalog");
        var tweaks = CatalogLoader.LoadDirectory(catalogDir).Tweaks;
        var errors = new List<string>();
        var profiles = ProfileStore.LoadDirectory(Path.Combine(catalogDir, CatalogLoader.ProfilesFolder), builtIn: true, errors);

        Assert.Empty(errors);
        Assert.Equal(["Aggressive", "Privacy", "Safe"], profiles.Select(p => p.Name).Order());
        foreach (var p in profiles)
        {
            var r = ProfileStore.Resolve(p, tweaks);
            Assert.NotEmpty(r.Desired);
            Assert.Empty(r.UnknownIds);
            // No built-in profile includes critical risk.
            Assert.DoesNotContain(r.Desired.Keys, id => tweaks.First(t => t.Id == id).Risk == RiskLevel.Critical);
            // Every built-in profile has a complete pt-BR translation.
            Assert.False(string.IsNullOrWhiteSpace(p.I18n?["pt-BR"].Name), p.Name);
            Assert.False(string.IsNullOrWhiteSpace(p.I18n?["pt-BR"].Description), p.Name);
        }
        var safe = ProfileStore.Resolve(profiles.First(p => p.Name == "Safe"), tweaks);
        Assert.All(safe.Desired.Keys, id => Assert.Equal(RiskLevel.Low, tweaks.First(t => t.Id == id).Risk));
        Assert.All(safe.Desired.Keys, id => Assert.True(tweaks.First(t => t.Id == id).Reversible));
        // The rule categories of the Privacy profile are English catalog paths that exist.
        var privacy = profiles.First(p => p.Name == "Privacy");
        Assert.All(privacy.Rules!.Categories, c => Assert.Contains(tweaks, t => ProfileStore.CategoryMatches(t.Category, c)));
    }

    [Fact]
    public void DisplayTexts_FollowUiLanguage_WithEnglishFallback()
    {
        var p = ProfileStore.Parse("""
            name: Safe
            description: Only low risk.
            i18n:
              pt-BR:
                name: Seguro
            rules: { maxRisk: low }
            """);

        using (new UiCultureScope("pt-BR"))
        {
            Assert.Equal("Seguro", p.DisplayName);
            Assert.Equal("Only low risk.", p.DisplayDescription); // no pt-BR description: English
        }
        using (new UiCultureScope("en-US"))
            Assert.Equal("Safe", p.DisplayName);
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
                    name: One
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
        Assert.Contains("cannot be reverted", plan.Single(c => c.Tweak.Id == "r.irrev").BlockedReason);
        Assert.Equal(RiskLevel.High, ChangePlanner.MaxRisk(plan));
        Assert.Equal(RiskLevel.Low, ChangePlanner.MaxRisk([]));
    }
}

public class TweakQueryTests
{
    private static readonly Tweak T = WithTranslation(TestTweaks.Make("privacy.ads.id", RiskLevel.Medium, "Privacy/Ads",
        confidence: Confidence.Official, tags: ["advertising"]));

    private static Tweak WithTranslation(Tweak t)
    {
        t.I18n = new() { ["pt-BR"] = new TweakText { Name = "Anúncios personalizados", Description = "Descrição em português" } };
        return t;
    }

    [Theory]
    [InlineData("ADS", true)]
    [InlineData("Privacy/Ads", false)]           // the category is not searched
    [InlineData("description privacy", true)]    // several words
    [InlineData("advertising", true)]            // tags
    [InlineData("privacy nonexistent", false)]
    [InlineData("  ", true)]
    public void Search(string text, bool expected)
    {
        using var _ = new UiCultureScope("en-US");
        Assert.Equal(expected, new TweakQuery { Search = text }.Matches(T, TweakState.Applied, null));
    }

    [Theory]
    [InlineData("pt-BR", "anuncios personalizados", true)] // translated name, without accents
    [InlineData("pt-BR", "description", true)]             // the English text is always searched
    [InlineData("en-US", "anuncios", false)]               // a translation only in its own language
    public void Search_UsesUiLanguageAndEnglish(string culture, string text, bool expected)
    {
        using var _ = new UiCultureScope(culture);
        Assert.Equal(expected, new TweakQuery { Search = text }.Matches(T, TweakState.Applied, null));
    }

    [Fact]
    public void Filters()
    {
        Assert.True(new TweakQuery { Category = "Privacy" }.Matches(T, null, null));
        Assert.False(new TweakQuery { Category = "Priv" }.Matches(T, null, null));
        Assert.True(new TweakQuery { States = new HashSet<TweakState> { TweakState.Applied } }.Matches(T, TweakState.Applied, null));
        Assert.False(new TweakQuery { States = new HashSet<TweakState> { TweakState.Applied } }.Matches(T, null, null));
        Assert.False(new TweakQuery { Risks = new HashSet<RiskLevel> { RiskLevel.Low } }.Matches(T, null, null));
        Assert.True(new TweakQuery { Confidences = new HashSet<Confidence> { Confidence.Official } }.Matches(T, null, null));
        // Without 'confidence' it counts as community.
        Assert.True(new TweakQuery { Confidences = new HashSet<Confidence> { Confidence.Community } }.Matches(TestTweaks.Make("a.b"), null, null));
        Assert.True(new TweakQuery { OnlyDiffering = true }.Matches(T, TweakState.NotApplied, DesiredState.Applied));
        Assert.False(new TweakQuery { OnlyDiffering = true }.Matches(T, TweakState.Applied, DesiredState.Applied));
        Assert.False(new TweakQuery { OnlyDiffering = true }.Matches(T, TweakState.Applied, null));
    }

    [Fact]
    public void CategoryTree()
    {
        var roots = CategoryNode.Build(["System/Services", "Privacy/Telemetry", "Privacy/Ads", "Privacy/Telemetry"]);

        Assert.Equal(["Privacy", "System"], roots.Select(r => r.Name));
        Assert.Equal(3, roots[0].Count);
        Assert.Equal(["Ads", "Telemetry"], roots[0].Children.Select(c => c.Name));
        Assert.Equal(["Ads", "Telemetry"], roots[0].Children.Select(c => c.DisplayName));
        Assert.Equal("Privacy/Telemetry", roots[0].Children[1].Path);
        Assert.Equal(2, roots[0].Children[1].Count);
    }

    [Fact]
    public void CategoryTree_TranslatedNames_SortByDisplayName()
    {
        var names = CategoryNames.Parse("""
            pt-BR:
              Privacy: Privacidade
              Privacy/Ads: Anúncios
              Privacy/Telemetry: Telemetria
              System: Sistema
              System/Services: Serviços
            """);
        using var _ = new UiCultureScope("pt-BR");

        var roots = CategoryNode.Build(["System/Services", "Privacy/Telemetry", "Privacy/Ads"], names);

        Assert.Equal(["Privacidade", "Sistema"], roots.Select(r => r.DisplayName));
        Assert.Equal(["Privacy", "System"], roots.Select(r => r.Name));
        Assert.Equal(["Anúncios", "Telemetria"], roots[0].Children.Select(c => c.DisplayName));
        Assert.Equal("Privacy/Telemetry", roots[0].Children[1].Path);
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
                log.Write("applying x");
                path = log.FilePath;
                Assert.Null(log.Error);
            }
            Assert.EndsWith("2026-01-02_030405.log", path);
            Assert.Contains("applying x", File.ReadAllText(path));
            Assert.Contains("applying x", seen);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
