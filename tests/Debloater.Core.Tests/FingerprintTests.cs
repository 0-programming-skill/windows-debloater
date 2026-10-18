using System.Text;
using Debloater.Core.Fingerprint;
using Debloater.Core.Inventory;
using Debloater.Core.Model;

namespace Debloater.Core.Tests;

public class StringExtractorTests
{
    /// <summary>Monta um "binário" de teste: pedaços de lixo, cadeias ASCII e UTF-16LE em posições conhecidas.</summary>
    private sealed class Blob
    {
        private readonly List<byte> _bytes = [(byte)'M', (byte)'Z'];

        public Blob Junk(int count, byte seed = 0x90)
        {
            for (int i = 0; i < count; i++) _bytes.Add((byte)(seed + i % 7 == 0 ? 0x01 : (seed ^ (i * 31)) | 0x80));
            return this;
        }

        public Blob Zeros(int count) { _bytes.AddRange(new byte[count]); return this; }

        public Blob Wide(string s)
        {
            if (_bytes.Count % 2 == 1) _bytes.Add(0);
            _bytes.AddRange(Encoding.Unicode.GetBytes(s));
            _bytes.AddRange([0, 0]);
            return this;
        }

        public Blob WideOdd(string s)
        {
            if (_bytes.Count % 2 == 0) _bytes.Add(0x90);
            _bytes.AddRange(Encoding.Unicode.GetBytes(s));
            _bytes.AddRange([0, 0]);
            return this;
        }

        public Blob Ascii(string s) { _bytes.AddRange(Encoding.ASCII.GetBytes(s)); _bytes.Add(0); return this; }

        public byte[] Build() => [.. _bytes];
    }

    [Fact]
    public void Utf16_PathValueNamesAndNearNames()
    {
        const string path = @"Software\Policies\Microsoft\Windows\WindowsCopilot";
        var data = new Blob()
            .Junk(64)
            .Wide(path).Zeros(8)
            .Wide("TurnOffWindowsCopilot").Zeros(4)
            .Wide("SomeUnrelatedName").Zeros(4)
            .Junk(2048)
            .Wide("FarAwayIdentifier")
            .Junk(32)
            .Build();

        var r = StringExtractor.Extract(data);

        Assert.Contains(path, r.Strings);
        Assert.Contains("TurnOffWindowsCopilot", r.Strings);
        Assert.Contains("SomeUnrelatedName", r.NearNames);
        Assert.DoesNotContain("FarAwayIdentifier", r.NearNames);
        Assert.DoesNotContain("FarAwayIdentifier", r.Strings);
        Assert.Equal(path, r.Context["TurnOffWindowsCopilot"]);
        Assert.Equal(path, r.Context["SomeUnrelatedName"]);
    }

    [Fact]
    public void Ascii_PathsAreCutAtTheRegistryRoot_IdentifiersAreIgnored()
    {
        var data = new Blob()
            .Junk(16).Ascii(@"xSOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager")
            .Junk(16).Ascii(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Dsh")
            .Junk(16).Ascii("EnableWindowsWidgets")          // identificador ASCII: nome de função, ignorado
            .Junk(16).Ascii(@"C:\Windows\System32\Software\x.dll") // caminho de arquivo
            .Junk(16).Ascii("WNF_SHEL_TASKBAR_STATE")
            .Build();

        var r = StringExtractor.Extract(data);

        Assert.Contains(@"SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", r.Strings);
        Assert.Contains(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Dsh", r.Strings);
        Assert.Contains("WNF_SHEL_TASKBAR_STATE", r.Strings);
        Assert.DoesNotContain("EnableWindowsWidgets", r.Strings);
        Assert.DoesNotContain(r.Strings, s => s.Contains("x.dll"));
    }

    [Fact]
    public void Utf16_SubscribedContentCspAndOddAlignment()
    {
        var data = new Blob()
            .Junk(10).Wide("SubscribedContent-338388Enabled")
            .Junk(10).Wide("./Device/Vendor/MSFT/Policy/Config/WindowsAI/DisableAIDataAnalysis")
            .Junk(11).WideOdd("HideRecommendedSection")
            .Build();

        var r = StringExtractor.Extract(data);

        Assert.Contains("SubscribedContent-338388Enabled", r.Strings);
        Assert.Contains("./Device/Vendor/MSFT/Policy/Config/WindowsAI/DisableAIDataAnalysis", r.Strings);
        Assert.Contains("HideRecommendedSection", r.Strings);
    }

    [Fact]
    public void Dedupes_IgnoringCase_AndCapsPerFile_KeepingPathsFirst()
    {
        var blob = new Blob().Wide("DisableThing").Junk(600).Wide("DISABLETHING").Junk(600);
        for (int i = 0; i < 20; i++) blob.Wide($"EnableFeature{i:D2}x").Junk(600);
        blob.Wide(@"Software\Policies\Microsoft\Windows\Explorer");
        var data = blob.Build();

        var all = StringExtractor.Extract(data);
        Assert.Single(all.Strings, s => s.Equals("DisableThing", StringComparison.OrdinalIgnoreCase));

        var capped = StringExtractor.Extract(data, new StringExtractorOptions { MaxStrings = 5 });
        Assert.True(capped.Truncated);
        Assert.Equal(5, capped.Strings.Count);
        Assert.Contains(@"Software\Policies\Microsoft\Windows\Explorer", capped.Strings);
    }

    [Fact]
    public void LongTextRunsAreIgnored()
    {
        var text = "This is a very long license text that mentions Software\\Microsoft\\Windows but is prose " + new string('a', 1200);
        var r = StringExtractor.Extract(new Blob().Wide(text).Build());
        Assert.Empty(r.Strings);
    }

    [Theory]
    [InlineData(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", StringKind.RegistryPath)]
    [InlineData(@"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", StringKind.PolicyPath)]
    [InlineData(@"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", StringKind.PolicyPath)]
    [InlineData(@"C:\Windows\System32\drivers\etc", StringKind.None)]
    [InlineData(@"\\server\share\Software\x", StringKind.None)]
    [InlineData(@"chrome\browser\performance_manager\policies\edge\copilot_controller.cc", StringKind.None)]
    [InlineData(@"Windows\System32\Software\x.dll", StringKind.None)]
    [InlineData("ShowCopilotButton", StringKind.ValueName)]
    [InlineData("TaskbarAl", StringKind.ValueName)]
    [InlineData("IsDynamicSearchBoxEnabled", StringKind.ValueName)]
    [InlineData("AllowRecallEnablement", StringKind.ValueName)]
    [InlineData("Start_IrisRecommendations", StringKind.ValueName)]
    [InlineData("Showing", StringKind.None)]
    [InlineData("Enabled", StringKind.None)]
    [InlineData("Allowance", StringKind.None)]
    [InlineData("SubscribedContent-310093Enabled", StringKind.SubscribedContent)]
    [InlineData("SubscribedContent-Enabled", StringKind.None)]
    [InlineData("WNF_SHEL_APPLICATION_STARTED", StringKind.Wnf)]
    [InlineData("Feature_TaskbarCopilot", StringKind.Feature)]
    [InlineData("./User/Vendor/MSFT/Policy/Config/Start/HideRecommendedSection", StringKind.CspPath)]
    public void Classifier(string s, StringKind expected) => Assert.Equal(expected, StringClassifier.Classify(s));

    [Theory]
    [InlineData(@"xSOFTWARE\Policies\Microsoft'", @"SOFTWARE\Policies\Microsoft")]
    [InlineData(@"\Registry\Machine\Software\Microsoft\Windows\", @"Registry\Machine\Software\Microsoft\Windows")]
    [InlineData(@"C:\Program Files\Software\Vendor", null)]
    [InlineData(@"no registry here", null)]
    public void ExtractRegistryPath(string run, string? expected) => Assert.Equal(expected, StringClassifier.ExtractRegistryPath(run));
}

public class CandidateScorerTests
{
    private static int Score(CandidateScorer.Input i) => CandidateScorer.Score(i).Score;

    [Fact]
    public void PolicyPathWithHotWord_BeatsGenericRegistryPath()
    {
        int policy = Score(new(@"Software\Policies\Microsoft\Windows\WindowsCopilot", CandidateKinds.PolicyPath));
        int generic = Score(new(@"Software\Microsoft\Windows\CurrentVersion\Foo", CandidateKinds.RegistryPath));
        Assert.True(policy > generic + 30, $"{policy} vs {generic}");
    }

    [Fact]
    public void ContentDeliveryManagerAndExplorerAdvanced_AreHot()
    {
        int cdm = Score(new(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager\X", CandidateKinds.RegistryPath));
        int plain = Score(new(@"Software\Microsoft\Windows\CurrentVersion\Foo\X", CandidateKinds.RegistryPath));
        Assert.Equal(plain + 25, cdm);
        int advanced = Score(new("ShowSomething", CandidateKinds.ValueName, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"));
        int noContext = Score(new("ShowSomething", CandidateKinds.ValueName));
        Assert.True(advanced > noContext);
    }

    [Fact]
    public void Noise_IsPenalized()
    {
        Assert.True(Score(new("EnableWidgetsBoard", CandidateKinds.ValueName)) > Score(new("EnableDebugLogging", CandidateKinds.ValueName)));
        Assert.True(Score(new(@"Software\Classes\CLSID\{00000000-0000-0000-0000-000000000000}", CandidateKinds.RegistryPath))
                    < Score(new(@"Software\Microsoft\Windows\CurrentVersion\Foo", CandidateKinds.RegistryPath)));
        Assert.True(Score(new("SearchBoxForegroundBrush", CandidateKinds.NearName)) < Score(new("SearchBoxMode", CandidateKinds.NearName)));
    }

    [Fact]
    public void Moved_IsWeak_AndCovered_IsLower()
    {
        int fresh = Score(new("DisableAIDataAnalysis", CandidateKinds.ValueName));
        int moved = Score(new("DisableAIDataAnalysis", CandidateKinds.ValueName, Moved: true));
        int covered = Score(new("DisableAIDataAnalysis", CandidateKinds.ValueName, CoveredBy: ["ai.recall.disable-snapshots"]));
        Assert.Equal((int)Math.Round(fresh * 0.3), moved);
        Assert.Equal(Math.Max(0, fresh - 40), covered);
    }

    [Fact]
    public void NearName_ScoresBelowPatternName_WithSameContext()
    {
        const string ctx = @"Software\Policies\Microsoft\Windows\CloudContent";
        int pattern = Score(new("DisableCloudThing", CandidateKinds.ValueName, ctx, CatalogKeySameKeyTweak: "privacy.ads.consumer-features"));
        int near = Score(new("CloudThingMode", CandidateKinds.NearName, ctx, CatalogKeySameKeyTweak: "privacy.ads.consumer-features"));
        Assert.True(pattern > near + 20, $"{pattern} vs {near}");
    }

    [Fact]
    public void CatalogIndex_NormalizesHives_AndSplitsBracketForm()
    {
        Assert.Equal(@"Software\Policies\X", CatalogRegistryIndex.NormalizeRegistryPath(@"HKLM\Software\Policies\X"));
        Assert.Equal(@"SOFTWARE\Policies\X", CatalogRegistryIndex.NormalizeRegistryPath(@"\Registry\Machine\SOFTWARE\Policies\X\"));
        Assert.Equal(@"Software\X", CatalogRegistryIndex.NormalizeRegistryPath(@"HKEY_CURRENT_USER\Software\X"));
        Assert.Equal((@"HKCU\Software\X\Advanced", (string?)"Start_IrisRecommendations"),
            CatalogRegistryIndex.SplitValueSuffix(@"HKCU\Software\X\Advanced[Start_IrisRecommendations]"));
        Assert.Equal((@"Software\X", (string?)null), CatalogRegistryIndex.SplitValueSuffix(@"Software\X"));
    }
}

public class FingerprintDiffTests
{
    private static BinaryEntry Bin(string path, string version, params string[] strings) =>
        new() { Path = path, Version = version, Size = 1000, Sha256 = "00", Curated = true, Strings = [.. strings] };

    private static Tweak RegTweak(string id, string path, string name) => new()
    {
        Id = id, Name = id, Description = id, Category = "Teste",
        Actions = [new ActionSpec { Type = ActionSpec.Registry, Path = path, Name = name, Kind = "dword", Value = 0 }],
    };

    private static InventoryItem Item(string kind, string id, params (string K, string V)[] props)
    {
        var item = new InventoryItem { Kind = kind, Id = id };
        foreach (var (k, v) in props) item.Properties[k] = v;
        return item;
    }

    private static WindowsFingerprint Fp(int build, string machine, bool elevated, List<BinaryEntry> bins, List<FingerprintPolicy> policies, params InventorySection[] sections) => new()
    {
        Os = new FingerprintOs { Build = build, Ubr = 1, Edition = "Professional" },
        Live = true, MachineName = machine, Elevated = elevated, Root = @"C:\",
        Binaries = new BinaryScanInfo { Mode = BinaryScanModes.All, Files = bins },
        Policies = policies,
        Inventory = new InventorySnapshot { Sections = [.. sections] },
    };

    private static (WindowsFingerprint Old, WindowsFingerprint New, List<Tweak> Catalog) Scenario()
    {
        var old = Fp(26200, "PC", false,
            [
                Bin(@"Windows\System32\shell32.dll", "10.0.26200.1", @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "DisableOldThing", "ShowSameThing"),
                Bin(@"Windows\System32\old.dll", "10.0.26200.1", "ShowMovedThing"),
                Bin(@"Windows\System32\drivers\vendor.sys", "1.0.0.0"),
            ],
            [new FingerprintPolicy { File = "A.admx", Name = "P1", Class = "Machine", Key = @"Software\Policies\A", ValueName = "V1", SupportedOn = "At least Windows 10", SupportedOnRef = "windows:SUPPORTED_Windows_10_0", Values = [@"Software\Policies\A\V1"] }],
            new InventorySection
            {
                Kind = InventoryKinds.Services,
                Items =
                [
                    Item(InventoryKinds.Services, "VendorSvc", ("start", "automatic"), ("imagePath", @"C:\Program Files\Vendor\svc.exe")),
                    Item(InventoryKinds.Services, "Same", ("start", "manual"), ("imagePath", @"%SystemRoot%\System32\svchost.exe -k netsvcs")),
                ],
            },
            new InventorySection { Kind = InventoryKinds.Drivers, Items = [Item(InventoryKinds.Drivers, "vendor", ("start", "system"), ("imagePath", @"\SystemRoot\System32\drivers\vendor.sys"))] },
            new InventorySection { Kind = InventoryKinds.Capabilities, Items = [Item(InventoryKinds.Capabilities, "App.StepsRecorder", ("state", "installed"))] },
            new InventorySection { Kind = InventoryKinds.Appx, Items = [Item(InventoryKinds.Appx, "Microsoft.Copilot", ("currentUser", "true"), ("framework", "false"), ("publisher", "8wekyb3d8bbwe"))] });

        var newSvc = Item(InventoryKinds.Services, "NewAISvc", ("start", "manual"), ("imagePath", @"%SystemRoot%\System32\svchost.exe -k netsvcs -p"));
        newSvc.Info = new(StringComparer.Ordinal) { ["serviceDll"] = @"%SystemRoot%\System32\newai.dll" };
        var neu = Fp(26300, "VM", true,
            [
                Bin(@"Windows\System32\shell32.dll", "10.0.26300.1", @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowSameThing",
                    "DisableNewCopilotThing", @"Software\Policies\Microsoft\Windows\NewAI"),
                Bin(@"Windows\System32\newai.dll", "10.0.26300.1", "ShowMovedThing", "AllowAgentThing"),
            ],
            [
                new FingerprintPolicy { File = "A.admx", Name = "P1", Class = "Machine", Key = @"Software\Policies\A", ValueName = "V1", SupportedOn = "No mínimo, o Windows 10", SupportedOnRef = "windows:SUPPORTED_Windows_10_0", Values = [@"Software\Policies\A\V1"] },
                new FingerprintPolicy { File = "A.admx", Name = "P2", Class = "Machine", Key = @"Software\Policies\Microsoft\Windows\NewAI", ValueName = "AllowAgentThing", SupportedOnRef = "windows:SUPPORTED_Windows_11_0_26300", Values = [@"Software\Policies\Microsoft\Windows\NewAI\AllowAgentThing"] },
            ],
            new InventorySection
            {
                Kind = InventoryKinds.Services,
                Items = [newSvc, Item(InventoryKinds.Services, "Same", ("start", "disabled"), ("imagePath", @"%SystemRoot%\System32\svchost.exe -k netsvcs"))],
            },
            new InventorySection { Kind = InventoryKinds.Drivers, Items = [] },
            new InventorySection
            {
                Kind = InventoryKinds.Capabilities,
                Items = [Item(InventoryKinds.Capabilities, "App.StepsRecorder~~~~0.0.1.0", ("state", "installed")), Item(InventoryKinds.Capabilities, "Media.WindowsMediaPlayer~~~~0.0.12.0", ("state", "not-present"))],
            },
            new InventorySection
            {
                Kind = InventoryKinds.Appx,
                Items = [Item(InventoryKinds.Appx, "Microsoft.Copilot", ("currentUser", "true"), ("framework", "false"), ("publisher", "8wekyb3d8bbwe"), ("allUsers", "true"), ("provisioned", "true"))],
            });

        var catalog = new List<Tweak>
        {
            RegTweak("test.old-thing", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "DisableOldThing"),
            RegTweak("test.same-thing", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowSameThing"),
        };
        return (old, neu, catalog);
    }

    [Fact]
    public void NewStrings_AreStrongCandidates_WithBinaryAndVersion()
    {
        var (old, neu, catalog) = Scenario();
        var d = FingerprintDiff.Compare(old, neu, catalog);

        var copilot = Assert.Single(d.Candidates, c => c.Value == "DisableNewCopilotThing");
        Assert.Equal("new", copilot.Status);
        Assert.Equal(CandidateKinds.ValueName, copilot.Kind);
        Assert.Equal(@"Windows\System32\shell32.dll", copilot.Binaries[0].Path);
        Assert.Equal("10.0.26300.1", copilot.Binaries[0].Version);

        var policyPath = Assert.Single(d.Candidates, c => c.Value == @"Software\Policies\Microsoft\Windows\NewAI");
        Assert.Equal(CandidateKinds.PolicyPath, policyPath.Kind);

        Assert.DoesNotContain(d.Candidates, c => c.Value == "ShowSameThing");   // existia no mesmo binário
        Assert.Equal(d.Candidates.OrderByDescending(c => c.Score).Select(c => c.Value), d.Candidates.Select(c => c.Value));
        Assert.Equal(Enumerable.Range(1, d.Candidates.Count), d.Candidates.Select(c => c.Rank));
    }

    [Fact]
    public void StringThatChangedBinary_IsMoved_AndWeak()
    {
        var (old, neu, catalog) = Scenario();
        var d = FingerprintDiff.Compare(old, neu, catalog);
        var moved = Assert.Single(d.Candidates, c => c.Value == "ShowMovedThing");
        Assert.Equal("moved", moved.Status);
        Assert.Contains(@"Windows\System32\old.dll", moved.OldBinaries!);
        var fresh = d.Candidates.Single(c => c.Value == "AllowAgentThing");
        Assert.True(fresh.Score > moved.Score);
    }

    [Fact]
    public void CatalogTweakWhoseNameVanished_IsPossiblyObsolete()
    {
        var (old, neu, catalog) = Scenario();
        var d = FingerprintDiff.Compare(old, neu, catalog);
        var obsolete = Assert.Single(d.PossiblyObsolete);
        Assert.Equal("test.old-thing", obsolete.TweakId);
        Assert.Contains(obsolete.OldBinaries, b => b.Contains("shell32.dll"));
    }

    [Fact]
    public void Policies_NewChangedAndLanguageIndependentSupportedOn()
    {
        var (old, neu, catalog) = Scenario();
        var d = FingerprintDiff.Compare(old, neu, catalog);
        var added = Assert.Single(d.Policies, p => p.Change == DiffChange.Added);
        Assert.Equal("P2", added.Policy.Name);
        // P1 só mudou o texto traduzido do supportedOn: a referência é a mesma, então não é alteração.
        Assert.DoesNotContain(d.Policies, p => p.Change == DiffChange.Changed);

        var candidate = Assert.Single(d.Candidates, c => c.Kind == CandidateKinds.AdmxPolicy);
        Assert.Equal("A.admx:P2", candidate.Value);
        Assert.Equal("AllowAgentThing", candidate.ValueName);
        Assert.Equal(@"Windows\System32\newai.dll", Assert.Single(candidate.Binaries).Path);
    }

    [Fact]
    public void Binaries_AddedRemovedAndVersionChanged()
    {
        var (old, neu, _) = Scenario();
        var d = FingerprintDiff.Compare(old, neu);
        Assert.Contains(d.Binaries, b => b.Change == DiffChange.Added && b.Path.EndsWith("newai.dll"));
        Assert.Contains(d.Binaries, b => b.Change == DiffChange.Removed && b.Path.EndsWith("old.dll"));
        var changed = Assert.Single(d.Binaries, b => b.Change == DiffChange.Changed);
        Assert.Equal(("10.0.26200.1", "10.0.26300.1"), (changed.OldVersion, changed.NewVersion));
    }

    [Fact]
    public void Inventory_NoiseNormalizationAndServiceCandidate()
    {
        var (old, neu, catalog) = Scenario();
        var d = FingerprintDiff.Compare(old, neu, catalog);

        // Serviço de terceiros e driver de hardware que só existem no PC antigo: ruído.
        Assert.NotNull(d.Inventory.Single(i => i.Id == "VendorSvc").Noise);
        Assert.NotNull(d.Inventory.Single(i => i.Id == "vendor").Noise);
        // Elevações diferentes: appx sem allUsers/provisioned e capacidades só as instaladas, pelo nome.
        Assert.DoesNotContain(d.Inventory, i => i.Kind == InventoryKinds.Appx);
        Assert.DoesNotContain(d.Inventory, i => i.Kind == InventoryKinds.Capabilities);
        Assert.Contains(d.Warnings, w => w.Contains("elevação diferente"));

        var svc = Assert.Single(d.Candidates, c => c.Kind == CandidateKinds.Service);
        Assert.Equal("NewAISvc", svc.Value);
        Assert.Equal(@"Windows\System32\newai.dll", svc.Binaries[0].Path);
        Assert.Equal("10.0.26300.1", svc.Binaries[0].Version);
        Assert.Contains(d.Inventory, i => i.Id == "Same" && i.Change == DiffChange.Changed && i.Noise is null);
    }

    [Fact]
    public void Tasks_OnlyVisibleWhenElevated_AreUncertain_ButStillCandidates()
    {
        var (old, neu, catalog) = Scenario();
        old.Inventory!.Sections.Add(new InventorySection { Kind = InventoryKinds.Tasks, Items = [Item(InventoryKinds.Tasks, @"\Microsoft\Windows\Same\T", ("enabled", "true"))] });
        neu.Inventory!.Sections.Add(new InventorySection
        {
            Kind = InventoryKinds.Tasks,
            Items = [Item(InventoryKinds.Tasks, @"\Microsoft\Windows\Same\T", ("enabled", "true")), Item(InventoryKinds.Tasks, @"\Microsoft\Windows\WindowsAI\Recall\PolicyConfiguration", ("enabled", "true"))],
        });
        var d = FingerprintDiff.Compare(old, neu, catalog);
        var change = Assert.Single(d.Inventory, i => i.Kind == InventoryKinds.Tasks);
        Assert.Contains("elevação", change.Noise);
        var task = Assert.Single(d.Candidates, c => c.Kind == CandidateKinds.Task);
        Assert.Contains(task.Reasons, r => r.StartsWith("incerto"));
    }

    [Theory]
    [InlineData(@"%SystemRoot%\System32\svchost.exe -k netsvcs -p", @"Windows\System32\svchost.exe")]
    [InlineData(@"\SystemRoot\System32\drivers\vendor.sys", @"Windows\System32\drivers\vendor.sys")]
    [InlineData(@"system32\drivers\x.sys", @"Windows\System32\drivers\x.sys")]
    [InlineData(@"""C:\Windows\System32\foo.exe"" /arg", @"Windows\System32\foo.exe")]
    [InlineData(@"\??\C:\Windows\System32\drivers\y.sys", @"Windows\System32\drivers\y.sys")]
    public void ResolveImagePath(string raw, string expected) =>
        Assert.Equal(expected, FingerprintDiff.ResolveImagePath(raw), ignoreCase: true);

    [Fact]
    public void Store_RoundTripsGzip_AndReport_HasSections()
    {
        var (old, neu, catalog) = Scenario();
        var dir = Path.Combine(Path.GetTempPath(), "debloater-fp-" + Guid.NewGuid().ToString("N"));
        try
        {
            var file = Path.Combine(dir, "x.json.gz");
            FingerprintStore.Save(neu, file);
            var bytes = File.ReadAllBytes(file);
            Assert.Equal(0x1F, bytes[0]);
            var back = FingerprintStore.Load(file);
            Assert.Equal(neu.Binaries.Files.Count, back.Binaries.Files.Count);
            Assert.Equal("10.0.26300.1", back.Binaries.Files[0].Version);

            var md = FingerprintReport.ToMarkdown(FingerprintDiff.Compare(old, back, catalog), top: 10);
            Assert.Contains("# Diff de builds: 26200.1 → 26300.1", md);
            Assert.Contains(FingerprintReport.HumanReadingHeading, md);
            Assert.Contains("DisableNewCopilotThing", md);
            Assert.Contains("Políticas ADMX novas (1)", md);
            Assert.Contains("test.old-thing", md);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private static (WindowsFingerprint Old, WindowsFingerprint New, List<Tweak> Catalog) PrivacyScenario()
    {
        var (old, neu, catalog) = Scenario();
        old.Binaries.Files.Add(Bin(@"Windows\System32\vendorsvc.exe", "8.1.0.0", "DisableVendorThing"));
        old.Binaries.Files.Add(Bin(@"Windows\SystemApps\MicrosoftWindows.1.Old_cw5n1h2txyewy\Old.View.dll", "2604.1.0.0"));
        var svcs = old.Inventory!.Sections.Single(s => s.Kind == InventoryKinds.Services);
        var inBox = Item(InventoryKinds.Services, "InBoxGone", ("start", "manual"), ("imagePath", @"%SystemRoot%\System32\svchost.exe -k netsvcs -p"));
        inBox.Info = new(StringComparer.Ordinal) { ["serviceDll"] = @"%SystemRoot%\System32\newai.dll" };
        svcs.Items.Add(inBox);
        svcs.Items.Add(Item(InventoryKinds.Services, "HwVendorSvc", ("start", "automatic"), ("imagePath", @"%SystemRoot%\System32\vendorsvc.exe")));
        svcs.Items.Add(Item(InventoryKinds.Services, "OpaqueSvc", ("start", "manual"), ("imagePath", @"%SystemRoot%\System32\svchost.exe -k netsvcs -p")));
        old.Inventory.Sections.Add(new InventorySection
        {
            Kind = InventoryKinds.Tasks,
            Items =
            [
                Item(InventoryKinds.Tasks, @"\Microsoft\Windows\Gone\T", ("enabled", "true")),
                Item(InventoryKinds.Tasks, @"\Microsoft\Office\Office Feature Updates", ("enabled", "true")),
                Item(InventoryKinds.Tasks, @"\VendorUpdater", ("enabled", "true")),
            ],
        });
        old.Inventory.Sections.Single(s => s.Kind == InventoryKinds.Appx).Items.AddRange(
        [
            Item(InventoryKinds.Appx, "Microsoft.SomeDevTool", ("framework", "false"), ("publisher", "8wekyb3d8bbwe")),
            Item(InventoryKinds.Appx, "MicrosoftWindows.1.Old", ("framework", "false"), ("publisher", "cw5n1h2txyewy")),
            Item(InventoryKinds.Appx, "Vendor.ChatApp", ("framework", "false"), ("publisher", "abc123")),
        ]);
        old.Inventory.Sections.Add(new InventorySection { Kind = InventoryKinds.Autoruns, Items = [Item(InventoryKinds.Autoruns, @"HKCU\...\Run\VendorTray", ("command", "x.exe"))] });
        neu.Inventory!.Sections.Add(new InventorySection { Kind = InventoryKinds.Tasks, Items = [] });
        neu.Inventory.Sections.Add(new InventorySection { Kind = InventoryKinds.Autoruns, Items = [] });
        old.Policies.Add(new FingerprintPolicy { File = "vendor.admx", Name = "VendorPolicy", Class = "Machine", Key = @"Software\Policies\Vendor", Values = [@"Software\Policies\Vendor\X"] });
        return (old, neu, catalog);
    }

    [Fact]
    public void Privacy_OmitsThirdPartyAndOldOnlyItems_KeepsWindows()
    {
        var (old, neu, catalog) = PrivacyScenario();
        var raw = FingerprintDiff.Compare(old, neu, catalog);
        Assert.Contains(raw.Inventory, i => i.Id == "HwVendorSvc");          // sem filtro, tudo aparece
        Assert.Contains(raw.Binaries, b => b.Path.EndsWith("vendorsvc.exe"));
        Assert.Equal("PC", raw.Old.MachineName);

        var d = FingerprintPrivacy.Apply(FingerprintDiff.Compare(old, neu, catalog), old, neu);
        var ids = d.Inventory.Select(i => i.Id).ToHashSet();
        // Do Windows: serviço cujo binário continua no build novo, tarefa \Microsoft\Windows, pacote do sistema.
        Assert.Contains("InBoxGone", ids);
        Assert.Contains(@"\Microsoft\Windows\Gone\T", ids);
        Assert.Contains("MicrosoftWindows.1.Old", ids);
        Assert.Contains("Same", ids);                                        // alterado: existe nos dois lados
        // Terceiros/hardware/instalados à parte, ou sem evidência de serem do Windows: fora.
        foreach (var gone in new[] { "VendorSvc", "vendor", "HwVendorSvc", "OpaqueSvc", @"\Microsoft\Office\Office Feature Updates", @"\VendorUpdater",
                                     "Microsoft.SomeDevTool", "Vendor.ChatApp", @"HKCU\...\Run\VendorTray" })
            Assert.DoesNotContain(gone, ids);

        Assert.DoesNotContain(d.Binaries, b => b.Change == DiffChange.Removed && !b.Path.StartsWith(@"Windows\SystemApps\"));
        Assert.Contains(d.Binaries, b => b.Change == DiffChange.Removed && b.Path.EndsWith("Old.View.dll"));
        Assert.Contains(d.Binaries, b => b.Change == DiffChange.Added);       // o lado novo não muda
        Assert.DoesNotContain(d.RemovedStrings, c => c.Value == "DisableVendorThing");
        Assert.Empty(d.Candidates.Single(c => c.Value == "ShowMovedThing").OldBinaries!);   // old.dll não existe no novo
        Assert.DoesNotContain(d.Policies, p => p.Policy.File == "vendor.admx");
        Assert.Null(d.Old.MachineName);
        Assert.Null(d.New.MachineName);

        var p = Assert.IsType<FingerprintPrivacyInfo>(d.Privacy);
        Assert.Equal(3, p.Get(FingerprintPrivacyInfo.Inventory(InventoryKinds.Services)));
        Assert.Equal(1, p.Get(FingerprintPrivacyInfo.Inventory(InventoryKinds.Drivers)));
        Assert.Equal(2, p.Get(FingerprintPrivacyInfo.Inventory(InventoryKinds.Tasks)));
        Assert.Equal(2, p.Get(FingerprintPrivacyInfo.Inventory(InventoryKinds.Appx)));
        Assert.Equal(1, p.Get(FingerprintPrivacyInfo.Inventory(InventoryKinds.Autoruns)));
        Assert.Equal(1, p.Get(FingerprintPrivacyInfo.Policies));
        Assert.Equal(3, p.Get(FingerprintPrivacyInfo.Binaries));             // old.dll, vendorsvc.exe, vendor.sys
        Assert.True(p.Get(FingerprintPrivacyInfo.RemovedStrings) >= 1);
        Assert.True(p.Get(FingerprintPrivacyInfo.OldBinaryReferences) >= 1);
    }

    [Fact]
    public void Privacy_Report_MentionsOmissions_AndHidesNames()
    {
        var (old, neu, catalog) = PrivacyScenario();
        var plain = FingerprintReport.ToMarkdown(FingerprintDiff.Compare(old, neu, catalog));
        Assert.Contains("HwVendorSvc", plain);
        Assert.Contains("vendorsvc.exe", plain);
        Assert.DoesNotContain("**Privacidade:**", plain);

        var md = FingerprintReport.ToMarkdown(FingerprintPrivacy.Apply(FingerprintDiff.Compare(old, neu, catalog), old, neu));
        foreach (var hidden in new[] { "HwVendorSvc", "VendorSvc", "vendorsvc.exe", "VendorUpdater", "Vendor.ChatApp", "SomeDevTool", "VendorTray", "vendor.admx", "DisableVendorThing", "old.dll" })
            Assert.DoesNotContain(hidden, md);
        Assert.Contains("**Privacidade:** filtro ligado", md);
        Assert.Contains("foram omitidos (privacidade)", md);
        Assert.Contains("InBoxGone", md);
        Assert.Contains("### Inicialização automática", md);                 // a seção fica, só com a nota
    }

    [Fact]
    public void Report_EscapesTableCells()
    {
        Assert.Equal(@"a \| b", FingerprintReport.Esc("a | b"));
        Assert.Equal(@"`a \| b`", FingerprintReport.Code("a | b"));
    }
}
