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
        Assert.True(result.Tweaks.Count >= 40, $"apenas {result.Tweaks.Count} tweaks");
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
        foreach (var known in Debloater.Core.Actions.ActionHandlers.KnownTypes)
            Assert.Contains(known, types);
    }

    private static CatalogLoadResult LoadSingle(string yaml, string file = "teste.yaml") =>
        CatalogLoader.Load([(file, () => yaml)]);

    [Fact]
    public void FileCategory_IsInheritedAndRiskIsCaseInsensitive()
    {
        var result = LoadSingle("""
            category: A/B
            tweaks:
              - id: test.one
                name: Um
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
        var result = CatalogLoader.Load([("um.yaml", () => yaml), ("dois.yaml", () => yaml)]);

        var error = Assert.Single(result.Errors);
        Assert.Equal("dois.yaml", error.File);
        Assert.Contains("um.yaml", error.Message);
    }

    [Theory]
    [InlineData("type: banana", "desconhecido")]
    [InlineData("type: registry\n        path: HKCU\\Software\\X\n        name: V\n        kind: dword", "value")]
    [InlineData("type: registry\n        path: HKXX\\Software\\X\n        name: V\n        kind: dword\n        value: 1", "path inválido")]
    [InlineData("type: registry\n        path: HKCU\\Software\\X\n        name: V\n        kind: dword\n        value: abc", "número inválido")]
    [InlineData("type: service\n        name: Foo\n        startup: sometimes", "startup")]
    [InlineData("type: scheduledTask\n        path: Microsoft\\Task\n        state: disabled", "caminho completo")]
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
        var result = LoadSingle(yaml, "ruim.yaml");

        Assert.False(result.IsValid);
        Assert.All(result.Errors, e => Assert.Equal("ruim.yaml", e.File));
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
        Assert.Contains("id 'Not Valid' inválido", messages);
        Assert.Contains("'name'", messages);
        Assert.Contains("'description'", messages);
        Assert.Contains("'category'", messages);
        Assert.Contains("sem ações", messages);
    }

    [Fact]
    public void UnknownProperty_IsYamlErrorWithLine()
    {
        var result = LoadSingle("""
            category: A
            tweaks:
              - id: test.x
                nome: errado
            """, "typo.yaml");

        var error = Assert.Single(result.Errors);
        Assert.Equal("typo.yaml", error.File);
        Assert.Contains("linha 4", error.Message);
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
        var ok = LoadSingle(Template.Replace("PRE", "{ powershell: '$true', reason: sem suporte }"));
        Assert.True(ok.IsValid, string.Join('\n', ok.Errors));
        Assert.Equal("sem suporte", ok.Tweaks[0].Precondition!.Reason);

        var noScript = LoadSingle(Template.Replace("PRE", "{ reason: sem suporte }"));
        Assert.Contains(noScript.Errors, e => e.Message.Contains("'powershell'"));
        var noReason = LoadSingle(Template.Replace("PRE", "{ powershell: '$true' }"));
        Assert.Contains(noReason.Errors, e => e.Message.Contains("'reason'"));
    }
}
