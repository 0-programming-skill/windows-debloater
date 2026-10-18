using Debloater.Core.Admx;
using Debloater.Core.Catalog;
using Debloater.Core.Model;
using Debloater.Core.Reference;

namespace Debloater.Core.Tests;

public class CspMarkdownParserTests
{
    private static (CspPage Page, List<CspPolicy> Policies) ParseFixture()
    {
        var md = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Csp", "policy-csp-sample.md"));
        return CspMarkdownParser.ParsePage("policy-csp-sample", md);
    }

    [Fact]
    public void ParsesEveryPolicySectionIncludingGroupedH3()
    {
        var (page, policies) = ParseFixture();
        Assert.Equal("2026-09-10", page.MsDate);
        Assert.Equal(3, page.PolicyCount);
        Assert.Equal(["AllowTelemetry", "EnterpriseOnly", "PreviewThing"], policies.Select(p => p.Name));
        Assert.All(policies, p => Assert.Equal("Sample", p.Area));
    }

    [Fact]
    public void AllowTelemetry_ScopeEditionsValuesMappingAndNotes()
    {
        var p = ParseFixture().Policies[0];

        Assert.Equal("Policy CSP - Sample/AllowTelemetry", p.Reference);
        Assert.Equal("https://learn.microsoft.com/windows/client-management/mdm/policy-csp-sample#allowtelemetry", p.Url);
        Assert.Equal("./Device/Vendor/MSFT/Policy/Config/Sample/AllowTelemetry", p.DeviceUri);
        Assert.Equal("./User/Vendor/MSFT/Policy/Config/Sample/AllowTelemetry", p.UserUri);
        Assert.True(p.ScopeDevice);
        Assert.True(p.ScopeUser);
        Assert.Equal(["Pro", "Enterprise", "Education", "IoT Enterprise / IoT Enterprise LTSC"], p.Editions);
        Assert.Null(p.NotEditions);
        Assert.Null(p.Win11MinBuild); // Windows 10 1507 and later: all of Windows 11
        Assert.False(p.InsiderOnly);
        Assert.Equal("int", p.Format);
        Assert.Equal("1", p.DefaultValue);
        Assert.Equal(["0", "1", "3"], p.AllowedValues!.Select(v => v.Value));
        Assert.True(p.AllowedValues![1].IsDefault);
        Assert.Equal("Basic. See the docs.", p.AllowedValues[1].Description); // links become text

        var gp = Assert.Single(p.GpMappings!);
        Assert.Equal(("AllowTelemetry", "DataCollection.admx", @"Software\Policies\Microsoft\Windows\DataCollection"), (gp.Name, gp.AdmxFile, gp.RegistryKey));
        Assert.Equal("WindowsComponents > Data Collection and Preview Builds", gp.Path);
        Assert.Null(gp.RegistryValueName);
        Assert.False(p.AdmxBacked);

        Assert.Contains(p.EditionNotes!, n => n.StartsWith("[0] ", StringComparison.Ordinal) && n.Contains("Enterprise"));
        Assert.Contains(p.EditionNotes!, n => !n.StartsWith('[') && n.Contains("only supported on Enterprise"));
    }

    [Fact]
    public void EnterpriseOnly_ExcludedEditionsBuildRangeDeprecatedAndEscapedNames()
    {
        var p = ParseFixture().Policies[1];
        Assert.False(p.ScopeUser);
        Assert.Equal(["Pro"], p.NotEditions);
        Assert.DoesNotContain("Pro", p.Editions);
        Assert.Equal(2, p.Applicability.Count);
        Assert.Equal((22621, 2361), (p.Win11MinBuild!.Value, p.Win11MinUbr!.Value));
        Assert.True(p.Deprecated);
        Assert.Equal("Range: [0-365]", p.AllowedRange);
        Assert.Equal("Enterprise_Only", p.GpMappings![0].Name);
        Assert.Equal("Enterprise_Only", p.GpMappings[0].RegistryValueName);
    }

    [Fact]
    public void PreviewThing_IsInsiderOnlyAndAdmxBacked()
    {
        var p = ParseFixture().Policies[2];
        Assert.True(p.InsiderOnly);
        Assert.Null(p.Win11MinBuild);
        Assert.True(p.AdmxBacked);
        Assert.False(p.ScopeDevice);
        Assert.Equal("User Configuration", p.GpMappings![0].Location);
    }

    [Theory]
    [InlineData(new[] { "Windows 11, version 24H2 [10.0.26100] and later" }, 26100, null)]
    [InlineData(new[] { "Windows 11, version 24H2 with KB5055627 [10.0.26100.3915] and later" }, 26100, 3915)]
    [InlineData(new[] { "Windows 10, version 2004 with KB [10.0.19041.1202] and later", "Windows 11, version 21H2 [10.0.22000] and later" }, null, null)]
    [InlineData(new[] { "Windows 11, version 23H2 [10.0.22631] and later", "Windows 11, version 22H2 [10.0.22621.2361] and later" }, 22621, 2361)]
    public void Win11MinBuild(string[] applicability, int? build, int? ubr)
    {
        var (b, u, insider) = CspMarkdownParser.ComputeWin11MinBuild(applicability);
        Assert.Equal((build, ubr, false), (b, u, insider));
    }

    [Fact]
    public void Reference_SerializesDeterministicallyAndRoundTrips()
    {
        var (page, policies) = ParseFixture();
        var reference = CspMarkdownParser.Build([(page, policies)]);
        var json = CspMarkdownParser.Serialize(reference);
        Assert.Equal(json, CspMarkdownParser.Serialize(CspMarkdownParser.Build([(page, Enumerable.Reverse(policies).ToList())])));

        var file = Path.Combine(Path.GetTempPath(), $"debloater-csp-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(file, json);
            var loaded = CspMarkdownParser.Load(file);
            Assert.Equal(3, loaded.Policies.Count);
            Assert.Equal(json, CspMarkdownParser.Serialize(loaded));
        }
        finally
        {
            File.Delete(file);
        }
    }
}

public class ReferenceMatcherTests
{
    private static AdmxCatalog Admx => AdmxParser.ParseDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Admx"), ["en-US"]);

    private static CspPolicy Csp(string area, string name, string admxFile, string gpName, string? key = null, string? valueName = null,
        string location = "Computer Configuration", Action<CspPolicy>? configure = null)
    {
        var c = new CspPolicy
        {
            Area = area,
            Name = name,
            Page = "policy-csp-" + area.ToLowerInvariant(),
            Editions = ["Pro", "Enterprise", "Education"],
            Format = "int",
            GpMappings = [new CspGpMapping { Name = gpName, AdmxFile = admxFile, RegistryKey = key, RegistryValueName = valueName, Location = location }],
        };
        configure?.Invoke(c);
        return c;
    }

    private static Tweak Tweak(string id, params ActionSpec[] actions) =>
        new() { Id = id, Name = id, Description = "d", Category = "A/B", SourceFile = "t.yaml", Actions = [.. actions] };

    private static ActionSpec Reg(string path, string name, object value, string kind = "dword") =>
        new() { Type = ActionSpec.Registry, Path = path, Name = name, Kind = kind, Value = value };

    [Fact]
    public void MatchesAdmxByKeyAndValueNameRespectingClass()
    {
        var m = new ReferenceMatcher(Admx.Policies, []);

        var machine = m.MatchAction(Reg(@"HKLM\Software\Policies\Test\Thing", "DisableThing", 1));
        var hit = Assert.Single(machine.Admx);
        Assert.Equal(("Sample.admx:DisableThing", AdmxVia.PolicyValue, true), (hit.Policy.QualifiedName, hit.Via, hit.HiveMatches));

        // Same key in HKCU: the policy is Machine → matches, but with the wrong hive.
        var user = m.MatchAction(Reg(@"HKCU\SOFTWARE\POLICIES\TEST\THING", "disablething", 1));
        Assert.False(Assert.Single(user.Admx).HiveMatches);

        // Class Both accepts both hives; enum element.
        var both = m.MatchAction(Reg(@"HKCU\Software\Policies\Test\Data", "Level", 0));
        Assert.Equal(AdmxVia.Element, Assert.Single(both.Admx).Via);

        // enabledList in a subkey; valueList of an enum option.
        Assert.Equal(AdmxVia.List, Assert.Single(m.MatchAction(Reg(@"HKCU\Software\Policies\Test\Lists\Sub", "B", "on", "string")).Admx).Via);
        Assert.Equal(AdmxVia.ElementList, Assert.Single(m.MatchAction(Reg(@"HKLM\Software\Policies\Test\Extra", "Full", "yes", "string")).Admx).Via);

        Assert.False(m.MatchAction(Reg(@"HKLM\Software\Policies\Test\Thing", "Other", 1)).HasAny);
    }

    [Fact]
    public void MatchesCspThroughAdmxOrRegistryAndDisambiguates()
    {
        var csp = new[]
        {
            Csp("Test", "DisableThing", "Sample.admx", "DisableThing"),
            Csp("Test", "LevelA", "Sample.admx", "Telemetry"),
            Csp("Test", "Level", "Sample.admx", "Telemetry"),
            Csp("Other", "ByRegistry", "Missing.admx", "Missing", @"Software\Policies\Test\Reg", "Value"),
        };
        var m = new ReferenceMatcher(Admx.Policies, csp);

        Assert.Equal("Policy CSP - Test/DisableThing", Assert.Single(m.MatchAction(Reg(@"HKLM\Software\Policies\Test\Thing", "DisableThing", 1)).Csp).Reference);
        // Telemetry → two CSP entries; the one named like the value wins.
        Assert.Equal("Level", Assert.Single(m.MatchAction(Reg(@"HKLM\Software\Policies\Test\Data", "Level", 1)).Csp).Name);
        // No local ADMX: matches by the documented key + value; Location "Computer" requires HKLM.
        Assert.Single(m.MatchAction(Reg(@"HKLM\Software\Policies\Test\Reg", "Value", 1)).Csp);
        Assert.Empty(m.MatchAction(Reg(@"HKCU\Software\Policies\Test\Reg", "Value", 1)).Csp);
    }

    [Fact]
    public void SuggestsConfidenceAndSources()
    {
        var csp = new[] { Csp("Test", "DisableThing", "Sample.admx", "DisableThing") };
        var m = new ReferenceMatcher(Admx.Policies, csp);

        var official = m.Match(Tweak("t.official", Reg(@"HKLM\Software\Policies\Test\Thing", "DisableThing", 1),
            new ActionSpec { Type = ActionSpec.Service, Name = "X", Startup = "disabled" }));
        Assert.Equal(Confidence.Official, ReferenceMatcher.SuggestConfidence(official, []));
        Assert.Equal(["msdocs:Policy CSP - Test/DisableThing", "admx:Sample.admx:DisableThing"],
            ReferenceMatcher.SuggestSources(official).Select(s => s.ToString()));

        var admxOnly = m.Match(Tweak("t.admx", Reg(@"HKLM\Software\Policies\Test\Data", "Level", 1)));
        Assert.Equal(Confidence.Admx, ReferenceMatcher.SuggestConfidence(admxOnly, []));

        var none = m.Match(Tweak("t.none", Reg(@"HKCU\Software\Microsoft\Whatever", "X", 1)));
        Assert.Equal(Confidence.Community, ReferenceMatcher.SuggestConfidence(none, []));
        Assert.Empty(ReferenceMatcher.SuggestSources(none));
    }
}

public class ReferenceValidatorTests
{
    private static AdmxCatalog Admx => AdmxParser.ParseDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Admx"), ["en-US"]);

    private static Tweak Tweak(string id, params ActionSpec[] actions) =>
        new() { Id = id, Name = id, Description = "d", Category = "A/B", SourceFile = "t.yaml", Actions = [.. actions] };

    private static ActionSpec Reg(string path, string name, object? value, string kind = "dword") =>
        value is null
            ? new() { Type = ActionSpec.Registry, Path = path, Name = name, Delete = true }
            : new() { Type = ActionSpec.Registry, Path = path, Name = name, Kind = kind, Value = value };

    private static List<ReferenceFinding> Run(IEnumerable<CspPolicy> csp, params Tweak[] tweaks)
    {
        var admx = Admx.Policies;
        var cspList = csp.ToList();
        return ReferenceValidator.Validate(tweaks, new ReferenceMatcher(admx, cspList), admx, cspList);
    }

    [Theory]
    [InlineData(@"HKLM\Software\Policies\Test\Thing", "DisableThing", 1, null)]          // enabledValue
    [InlineData(@"HKLM\Software\Policies\Test\Thing", "DisableThing", 0, null)]          // disabledValue
    [InlineData(@"HKLM\Software\Policies\Test\Thing", "DisableThing", 2, ReferenceValidator.AdmxValue)]
    [InlineData(@"HKLM\Software\Policies\Test\Data", "Level", 3, null)]                  // enum
    [InlineData(@"HKLM\Software\Policies\Test\Data", "Level", 2, ReferenceValidator.AdmxValue)]
    [InlineData(@"HKLM\Software\Policies\Test\Elements", "Timeout", 600, null)]          // decimal min..max
    [InlineData(@"HKLM\Software\Policies\Test\Elements", "Timeout", 4, ReferenceValidator.AdmxValue)]
    [InlineData(@"HKCU\Software\Policies\Test\Thing", "DisableThing", 1, ReferenceValidator.AdmxHive)]
    [InlineData(@"HKLM\Software\Policies\Test\Thing", "DisableThing", null, null)]       // delete is always valid
    [InlineData(@"HKLM\Software\Policies\Test\Unknown", "X", 1, ReferenceValidator.PolicyUnknown)]
    public void AdmxChecks(string path, string name, object? value, string? expectedCode)
    {
        var findings = Run([], Tweak("t.x", Reg(path, name, value)));
        if (expectedCode is null) Assert.Empty(findings);
        else Assert.Equal(expectedCode, Assert.Single(findings).Code);
    }

    [Fact]
    public void AdmxKindMismatch_IsReported()
    {
        var findings = Run([], Tweak("t.kind", Reg(@"HKLM\Software\Policies\Test\Thing", "DisableThing", "1", "string")));
        Assert.Equal(ReferenceValidator.AdmxKind, Assert.Single(findings).Code);
    }

    [Fact]
    public void CspEditionValueNoteBuildAndInsiderChecks()
    {
        var csp = new CspPolicy
        {
            Area = "Test",
            Name = "Level",
            Page = "policy-csp-test",
            Editions = ["Pro", "Enterprise"],
            Format = "int",
            Win11MinBuild = 26100,
            EditionNotes = ["[0] This value is only applicable to Enterprise."],
            GpMappings = [new CspGpMapping { Name = "Telemetry", AdmxFile = "Sample.admx" }],
        };
        var findings = Run([csp], Tweak("t.level", Reg(@"HKLM\Software\Policies\Test\Data", "Level", 0)));
        Assert.Contains(findings, f => f is { Code: ReferenceValidator.CspEditionValue, Severity: FindingSeverity.Warning });
        Assert.Contains(findings, f => f is { Code: ReferenceValidator.CspBuild, Severity: FindingSeverity.Warning });

        // With the correct minBuild and an unrestricted value: nothing.
        var ok = Tweak("t.ok", Reg(@"HKLM\Software\Policies\Test\Data", "Level", 1));
        ok.MinBuild = 26100;
        Assert.Empty(Run([csp], ok));

        // Tweak that already mentions the edition restriction: informational only.
        var mentions = Tweak("t.mentions", Reg(@"HKLM\Software\Policies\Test\Data", "Level", 0));
        mentions.MinBuild = 26100;
        mentions.Description = "Value 0 only applies on Enterprise.";
        Assert.Equal(FindingSeverity.Info, Assert.Single(Run([csp], mentions)).Severity);
    }

    [Fact]
    public void QuestionableAndDeclaredConfidenceMismatch()
    {
        var bad = Tweak("t.bad", Reg(@"HKLM\Software\Policies\Test\Thing", "DisableThing", 5));
        var matcher = new ReferenceMatcher(Admx.Policies, []);
        var findings = ReferenceValidator.Validate([bad], matcher, Admx.Policies, []);
        Assert.Equal(Confidence.Questionable, ReferenceMatcher.SuggestConfidence(matcher.Match(bad), findings));

        var declared = Tweak("t.declared", Reg(@"HKCU\Software\Microsoft\Whatever", "X", 1));
        declared.Confidence = Confidence.Official;
        declared.Sources = [new SourceRef { Kind = SourceRef.MsDocs, Ref = "Policy CSP - Gone/Policy" }];
        var f2 = ReferenceValidator.Validate([declared], matcher, Admx.Policies, []);
        Assert.Contains(f2, f => f.Code == ReferenceValidator.ConfidenceMismatch);
        Assert.Contains(f2, f => f is { Code: ReferenceValidator.SourceUnknown, Severity: FindingSeverity.Warning });
    }
}

public class CatalogAnnotatorTests
{
    // Normalized to LF: the Windows checkout (autocrlf) may bring CRLF into the literal.
    private static readonly string Yaml = """
        # File comment.
        category: A/B

        tweaks:
          - id: test.one
            name: One
            description: >
              Long text
              on two lines.
            risk: low
            tags: [x]   # inline comment
            actions:
              - type: registry
                path: HKLM\SOFTWARE\Policies\X
                name: A
                kind: dword
                value: 1

          # comment between tweaks
          - id: test.two
            name: Two
            description: d
            risk: medium
            confidence: community
            actions:
              - type: service
                name: X
                startup: disabled
        """.ReplaceLineEndings("\n");

    private static readonly Dictionary<string, CatalogAnnotator.Annotation> Annotations = new()
    {
        ["test.one"] = new(Confidence.Official, [new SourceRef { Kind = "msdocs", Ref = "Policy CSP - X/A" }, new SourceRef { Kind = "admx", Ref = "X.admx:A \"q\"" }]),
        ["test.two"] = new(Confidence.Admx, [new SourceRef { Kind = "admx", Ref = "X.admx:B" }]),
    };

    [Fact]
    public void InsertsAfterRiskPreservingCommentsAndSkipsExistingWithoutForce()
    {
        var (text, changed) = CatalogAnnotator.Annotate(Yaml, Annotations, force: false);
        Assert.Equal(1, changed);
        Assert.Contains("    risk: low\n    confidence: official\n    sources:\n      - { kind: msdocs, ref: \"Policy CSP - X/A\" }\n      - { kind: admx, ref: \"X.admx:A \\\"q\\\"\" }\n    tags: [x]   # inline comment", text);
        Assert.Contains("# comment between tweaks", text);
        Assert.Contains("    confidence: community\n", text); // test.two untouched

        var loaded = CatalogLoader.Load([("a.yaml", () => text)]);
        Assert.True(loaded.IsValid, string.Join("\n", loaded.Errors));
        var one = loaded.Tweaks.Single(t => t.Id == "test.one");
        Assert.Equal(Confidence.Official, one.Confidence);
        Assert.Equal("X.admx:A \"q\"", one.Sources[1].Ref);
    }

    [Fact]
    public void ForceReplacesExistingBlockAndIsIdempotent()
    {
        var (first, _) = CatalogAnnotator.Annotate(Yaml, Annotations, force: true);
        Assert.DoesNotContain("confidence: community", first);
        Assert.Contains("    confidence: admx\n    sources:\n      - { kind: admx, ref: \"X.admx:B\" }\n    actions:", first);

        var (second, changed) = CatalogAnnotator.Annotate(first, Annotations, force: true);
        Assert.Equal(0, changed);
        Assert.Equal(first, second);

        var loaded = CatalogLoader.Load([("a.yaml", () => second)]);
        Assert.True(loaded.IsValid, string.Join("\n", loaded.Errors));
        Assert.Equal(2, loaded.Tweaks.Count);
    }

    [Fact]
    public void PreservesCrLf()
    {
        var crlf = Yaml.Replace("\n", "\r\n");
        var (text, _) = CatalogAnnotator.Annotate(crlf, Annotations, force: false);
        Assert.DoesNotContain("\r\r", text);
        Assert.Equal(text.Split("\r\n").Length - 1, text.Count(c => c == '\n'));
    }

    [Fact]
    public void Loader_RejectsUnknownSourceKind()
    {
        var bad = Yaml.Replace("    risk: low\n", "    risk: low\n    confidence: official\n    sources:\n      - { kind: blog, ref: x }\n");
        var result = CatalogLoader.Load([("a.yaml", () => bad)]);
        Assert.Contains(result.Errors, e => e.Message.Contains("invalid kind"));
    }

    [Fact]
    public void Loader_AcceptsReverseEngineeringSourceKind()
    {
        var yaml = Yaml.Replace("    risk: low\n", "    risk: low\n    confidence: community\n    sources:\n      - { kind: re, ref: \"26300/x\" }\n");
        var result = CatalogLoader.Load([("a.yaml", () => yaml)]);
        Assert.True(result.IsValid, string.Join("\n", result.Errors));
        Assert.Contains(result.Tweaks, t => t.Sources.Any(s => s.Kind == SourceRef.ReKind && s.Ref == "26300/x"));
    }
}
