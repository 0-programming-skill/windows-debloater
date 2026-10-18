using Debloater.Core.Catalog;
using Debloater.Core.Model;

namespace Debloater.Core.Tests;

public class CatalogTests
{
    private static string CatalogDir => Path.Combine(AppContext.BaseDirectory, "catalog");

    [Fact]
    public void RealCatalog_LoadsWithZeroErrors()
    {
        var result = CatalogLoader.LoadDirectory(CatalogDir);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.True(result.FileCount >= 8);
        Assert.True(result.Tweaks.Count >= 40, $"only {result.Tweaks.Count} tweaks");
    }

    [Fact]
    public void RealCatalog_IsFullyTranslated()
    {
        var result = CatalogLoader.LoadDirectory(CatalogDir);

        Assert.True(result.Warnings.Count == 0, string.Join(Environment.NewLine, result.Warnings));
        Assert.All(result.Tweaks, t => Assert.True(t.I18n?.ContainsKey("pt-BR"), t.Id));
        using var _ = new UiCultureScope("pt-BR");
        Assert.Equal("Privacidade/Telemetria", result.Categories.DisplayPath("Privacy/Telemetry"));
    }

    [Fact]
    public void RealCatalog_HasCriticalTweakAndUniqueIdsAndCategories()
    {
        var tweaks = CatalogLoader.LoadDirectory(CatalogDir).Tweaks;

        Assert.Contains(tweaks, t => t.Risk == RiskLevel.Critical);
        Assert.Equal(tweaks.Count, tweaks.Select(t => t.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(tweaks, t => Assert.False(string.IsNullOrWhiteSpace(t.Category)));
        Assert.All(tweaks, t => Assert.NotNull(t.SourceFile));
    }

    [Fact]
    public void RealCatalog_ExercisesEveryActionType()
    {
        var types = CatalogLoader.LoadDirectory(CatalogDir).Tweaks.SelectMany(t => t.Actions).Select(a => a.Type).ToHashSet();
        // featureFlag is not used by the shipped catalog yet: no hidden feature has a reverse-engineered,
        // debloat-worthy reason to be flipped. It is covered by FeatureFlagTests and the VM selftest.
        foreach (var known in Debloater.Core.Actions.ActionHandlers.KnownTypes.Where(t => t != Debloater.Core.Model.ActionSpec.FeatureFlag))
            Assert.Contains(known, types);
    }

    [Fact]
    public void RealCatalog_ReverseEngineeringSources_AreIdsWithWriteUpsWhenDocsArePresent()
    {
        var refs = CatalogLoader.LoadDirectory(CatalogDir).Tweaks
            .SelectMany(t => t.Sources.Where(s => s.Kind == SourceRef.ReKind).Select(s => (t.Id, s.Ref)))
            .ToList();
        Assert.NotEmpty(refs);

        // The write-ups live outside git, in <repo>/.claude/docs/re/<build>/<slug>.md. Check them only when that
        // folder exists (the owner's machine); in CI or a fresh clone only the id format is checked (by the loader).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Debloater.slnx"))) dir = dir.Parent;
        var reDir = dir is null ? null : Path.Combine(dir.FullName, ".claude", "docs", "re");
        if (reDir is null || !Directory.Exists(reDir)) return;
        foreach (var (id, reference) in refs)
        {
            Assert.True(File.Exists(Path.Combine(reDir, reference.Replace('/', Path.DirectorySeparatorChar) + ".md")),
                $"{id}: re source '{reference}' has no write-up in {reDir}.");
        }
    }

    private static CatalogLoadResult LoadSingle(string yaml, string file = "test.yaml") =>
        CatalogLoader.Load([(file, () => yaml)]);

    [Fact]
    public void FileCategory_IsInheritedAndRiskIsCaseInsensitive()
    {
        var result = LoadSingle("""
            category: A/B
            tweaks:
              - id: test.one
                name: One
                description: d
                risk: critical
                actions:
                  - type: registry
                    path: HKCU\Software\X
                    name: V
                    kind: dword
                    value: 1
            """);

        Assert.True(result.IsValid, string.Join("\n", result.Errors));
        Assert.Equal("A/B", result.Tweaks[0].Category);
        Assert.Equal(RiskLevel.Critical, result.Tweaks[0].Risk);
    }

    [Fact]
    public void DuplicateIds_AcrossFiles_AreReportedWithFileNames()
    {
        const string yaml = """
            category: A
            tweaks:
              - id: test.dup
                name: N
                description: d
                actions:
                  - type: service
                    name: Foo
                    startup: disabled
            """;
        var result = CatalogLoader.Load([("one.yaml", () => yaml), ("two.yaml", () => yaml)]);

        var error = Assert.Single(result.Errors);
        Assert.Equal("two.yaml", error.File);
        Assert.Contains("one.yaml", error.Message);
    }

    [Theory]
    [InlineData("type: banana", "unknown")]
    [InlineData("type: registry\n        path: HKCU\\Software\\X\n        name: V\n        kind: dword", "value")]
    [InlineData("type: registry\n        path: HKXX\\Software\\X\n        name: V\n        kind: dword\n        value: 1", "invalid path")]
    [InlineData("type: registry\n        path: HKCU\\Software\\X\n        name: V\n        kind: dword\n        value: abc", "invalid number")]
    [InlineData("type: service\n        name: Foo\n        startup: sometimes", "startup")]
    [InlineData("type: scheduledTask\n        path: Microsoft\\Task\n        state: disabled", "full task path")]
    [InlineData("type: appx\n        name: Foo\n        state: removed", "reversible: false")]
    [InlineData("type: powershell\n        detect: x\n        apply: y", "revert")]
    public void InvalidActions_ProduceClearErrors(string actionYaml, string expectedFragment)
    {
        var yaml = $"""
            category: A
            tweaks:
              - id: test.bad
                name: N
                description: d
                actions:
                  - {actionYaml}
            """;
        var result = LoadSingle(yaml, "bad.yaml");

        Assert.False(result.IsValid);
        Assert.All(result.Errors, e => Assert.Equal("bad.yaml", e.File));
        Assert.Contains(result.Errors, e => e.Message.Contains(expectedFragment, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MissingRequiredFields_AreReported()
    {
        var result = LoadSingle("""
            tweaks:
              - id: Not Valid
                actions: []
            """);

        var messages = string.Join("\n", result.Errors.Select(e => e.Message));
        Assert.Contains("invalid id 'Not Valid'", messages);
        Assert.Contains("'name'", messages);
        Assert.Contains("'description'", messages);
        Assert.Contains("'category'", messages);
        Assert.Contains("without actions", messages);
    }

    [Fact]
    public void UnknownProperty_IsYamlErrorWithLine()
    {
        var result = LoadSingle("""
            category: A
            tweaks:
              - id: test.x
                nmae: typo
            """, "typo.yaml");

        var error = Assert.Single(result.Errors);
        Assert.Equal("typo.yaml", error.File);
        Assert.Contains("line 4", error.Message);
    }

    [Fact]
    public void Precondition_RequiresScriptAndReason()
    {
        const string Template = """
            category: A
            tweaks:
              - id: test.pre
                name: P
                description: d
                precondition:
                  PRE
                actions:
                  - type: registry
                    path: HKCU\Software\X
                    name: V
                    kind: dword
                    value: 1
            """;
        var ok = LoadSingle(Template.Replace("PRE", "{ powershell: '$true', reason: not supported }"));
        Assert.True(ok.IsValid, string.Join('\n', ok.Errors));
        Assert.Equal("not supported", ok.Tweaks[0].Precondition!.Reason);

        var noScript = LoadSingle(Template.Replace("PRE", "{ reason: not supported }"));
        Assert.Contains(noScript.Errors, e => e.Message.Contains("'powershell'"));
        var noReason = LoadSingle(Template.Replace("PRE", "{ powershell: '$true' }"));
        Assert.Contains(noReason.Errors, e => e.Message.Contains("'reason'"));
    }

    private const string TranslatedTweak = """
        category: A/B
        tweaks:
          - id: test.i18n
            name: Disable the thing
            description: Disables the thing.
            breaks: The thing stops working.
            precondition: { powershell: '$true', reason: not supported here }
            I18N
            actions:
              - { type: registry, path: HKCU\Software\X, name: V, kind: dword, value: 1 }
        """;

    [Fact]
    public void I18n_ResolvesByUiCulture_WithEnglishFallback()
    {
        var result = LoadSingle(TranslatedTweak.Replace("I18N", """
            i18n:
                  pt-BR:
                    name: Desativar a coisa
                    description: Desativa a coisa.
                    reason: sem suporte aqui
            """));
        Assert.True(result.IsValid, string.Join('\n', result.Errors));
        var t = result.Tweaks[0];

        using (new UiCultureScope("pt-BR"))
        {
            Assert.Equal("Desativar a coisa", t.DisplayName);
            Assert.Equal("Desativa a coisa.", t.DisplayDescription);
            Assert.Equal("The thing stops working.", t.DisplayBreaks); // missing translation: English
            Assert.Equal("sem suporte aqui", t.DisplayPreconditionReason);
            Assert.Equal("Disable the thing", t.Name);                 // the base text is untouched
        }
        using (new UiCultureScope("pt-PT"))
            Assert.Equal("Desativar a coisa", t.DisplayName);           // same language: pt-BR
        foreach (var culture in new[] { "en-US", "de-DE", "fr-FR" })
        {
            using var _ = new UiCultureScope(culture);
            Assert.Equal("Disable the thing", t.DisplayName);
            Assert.Equal("not supported here", t.DisplayPreconditionReason);
        }

        // The missing 'breaks' translation is a warning, not an error.
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("test.i18n", warning.TweakId);
        Assert.Contains("breaks", warning.Message);
    }

    [Fact]
    public void I18n_MissingTranslation_IsWarning_UnknownField_IsError()
    {
        var missing = LoadSingle(TranslatedTweak.Replace("I18N", ""));
        Assert.True(missing.IsValid);
        Assert.Contains("missing pt-BR translation", Assert.Single(missing.Warnings).Message);

        var typo = LoadSingle(TranslatedTweak.Replace("I18N", "i18n: { pt-BR: { nome: x } }"));
        Assert.False(typo.IsValid);
    }

    [Fact]
    public void CategoryNames_TranslateEachNode_AndWarnWhenMissing()
    {
        const string yaml = """
            category: Privacy/Telemetry
            tweaks:
              - id: test.cat
                name: N
                description: d
                i18n: { pt-BR: { name: N, description: d } }
                actions:
                  - { type: service, name: Foo, startup: disabled }
            """;
        var result = CatalogLoader.Load([("a.yaml", () => yaml)], () => "pt-BR:\n  Privacy: Privacidade\n");

        Assert.True(result.IsValid, string.Join('\n', result.Errors));
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("'Privacy/Telemetry'", warning.Message);
        using (new UiCultureScope("pt-BR"))
        {
            Assert.Equal("Privacidade/Telemetry", result.Categories.DisplayPath("Privacy/Telemetry"));
            Assert.Equal("Privacidade", result.Categories.DisplayNode("Privacy"));
        }
        using (new UiCultureScope("en-US"))
            Assert.Equal("Privacy/Telemetry", result.Categories.DisplayPath("Privacy/Telemetry"));

        var badLanguage = CatalogLoader.Load([("a.yaml", () => yaml)], () => "xx-YY:\n  Privacy: P\n");
        Assert.Contains(badLanguage.Errors, e => e.Message.Contains("unknown language 'xx-YY'"));
    }

    [Theory]
    [InlineData("26300/start-show-suggested-files", true)]
    [InlineData("docs/re/26300/x.md", false)]
    [InlineData("26300/Bad_Slug", false)]
    public void ReSources_MustBeBuildSlugIds(string reference, bool valid)
    {
        var result = LoadSingle(TranslatedTweak.Replace("I18N", $"sources: [{{ kind: re, ref: \"{reference}\" }}]"));
        Assert.Equal(valid, result.IsValid);
    }
}
