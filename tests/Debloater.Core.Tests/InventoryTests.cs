using Debloater.Core.Inventory;
using Debloater.Core.Model;

namespace Debloater.Core.Tests;

public class InventoryTests
{
    private static InventoryItem Item(string kind, string id, params (string K, string V)[] props)
    {
        var item = new InventoryItem { Kind = kind, Id = id };
        foreach (var (k, v) in props) item.Properties[k] = v;
        return item;
    }

    private static InventorySnapshot Snap(string build, params InventorySection[] sections) =>
        new() { WindowsBuild = build, Sections = [.. sections] };

    [Fact]
    public void Diff_ReportsAddedRemovedAndChanged()
    {
        var old = Snap("26100.1",
            new InventorySection
            {
                Kind = InventoryKinds.Services,
                Items =
                [
                    Item(InventoryKinds.Services, "DiagTrack", ("start", "automatic")),
                    Item(InventoryKinds.Services, "OldSvc", ("start", "manual")),
                    Item(InventoryKinds.Services, "Same", ("start", "manual")),
                ],
            });
        var neu = Snap("26200.1",
            new InventorySection
            {
                Kind = InventoryKinds.Services,
                Items =
                [
                    Item(InventoryKinds.Services, "diagtrack", ("start", "disabled")), // id sem diferenciar maiúsculas
                    Item(InventoryKinds.Services, "NewSvc", ("start", "automatic")),
                    Item(InventoryKinds.Services, "Same", ("start", "manual")),
                ],
            });

        var diff = InventoryDiff.Compare(old, neu);

        Assert.Equal("26100.1", diff.OldBuild);
        Assert.Equal(3, diff.Entries.Count);
        Assert.Contains(diff.Entries, e => e is { Id: "NewSvc", Change: DiffChange.Added });
        Assert.Contains(diff.Entries, e => e is { Id: "OldSvc", Change: DiffChange.Removed });
        var changed = Assert.Single(diff.Entries, e => e.Change == DiffChange.Changed);
        Assert.Equal("start: automatic → disabled", Assert.Single(changed.Details));
        Assert.Empty(diff.Warnings);
    }

    [Fact]
    public void Diff_InfoIsNotComparedAndUnavailableSectionsAreSkipped()
    {
        var a = Item(InventoryKinds.Appx, "Microsoft.BingNews", ("currentUser", "true"));
        a.Info = new() { ["version"] = "1.0" };
        var b = Item(InventoryKinds.Appx, "Microsoft.BingNews", ("currentUser", "true"));
        b.Info = new() { ["version"] = "2.0" };

        var diff = InventoryDiff.Compare(
            Snap("1", new InventorySection { Kind = InventoryKinds.Appx, Items = [a] },
                      new InventorySection { Kind = InventoryKinds.Capabilities, Available = false }),
            Snap("2", new InventorySection { Kind = InventoryKinds.Appx, Items = [b] },
                      new InventorySection { Kind = InventoryKinds.Capabilities, Items = [Item(InventoryKinds.Capabilities, "X~~~~1")] }));

        Assert.Empty(diff.Entries);
        Assert.Contains(diff.Warnings, w => w.StartsWith("capabilities", StringComparison.Ordinal));
    }

    [Fact]
    public void Snapshot_RoundTripsThroughJson()
    {
        var file = Path.Combine(Path.GetTempPath(), $"debloater-inv-{Guid.NewGuid():N}.json");
        try
        {
            var snap = Snap("26200.1", new InventorySection
            {
                Kind = InventoryKinds.Tasks,
                Note = "nota",
                Items = [new InventoryItem { Kind = InventoryKinds.Tasks, Id = @"\A\B", TweakIds = ["t.one"], Properties = { ["enabled"] = "true" } }],
            });
            InventoryDiff.Save(snap, file);
            var loaded = InventoryDiff.Load(file);

            var section = Assert.Single(loaded.Sections);
            Assert.Equal("nota", section.Note);
            var item = Assert.Single(section.Items);
            Assert.Equal(@"\A\B", item.Id);
            Assert.Equal("true", item.Properties["enabled"]);
            Assert.Equal(["t.one"], item.TweakIds);
            Assert.Empty(InventoryDiff.Compare(snap, loaded).Entries);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void CrossReference_MatchesEachActionTypeToItsKind()
    {
        var tweaks = new List<Tweak>
        {
            new() { Id = "t.svc", Actions = [new ActionSpec { Type = ActionSpec.Service, Name = "DiagTrack", Startup = "disabled" }] },
            new() { Id = "t.task", Actions = [new ActionSpec { Type = ActionSpec.ScheduledTask, Path = @"\Microsoft\Windows\Autochk\Proxy", State = "disabled" }] },
            new() { Id = "t.appx", Actions = [new ActionSpec { Type = ActionSpec.Appx, Name = "Microsoft.Bing*", State = "removed" }] },
            new() { Id = "t.feat", Actions = [new ActionSpec { Type = ActionSpec.OptionalFeature, Name = "SMB1Protocol", State = "disabled" }] },
            new() { Id = "t.run", Actions = [new ActionSpec { Type = ActionSpec.Registry, Path = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", Name = "OneDrive", Delete = true }] },
            new() { Id = "t.ps", Actions = [new ActionSpec { Type = ActionSpec.PowerShell, Apply = "Remove-WindowsCapability -Online -Name 'Browser.InternetExplorer~~~~0.0.11.0'" }] },
        };
        var snap = Snap("1",
            new InventorySection { Kind = InventoryKinds.Services, Items = [Item(InventoryKinds.Services, "diagtrack"), Item(InventoryKinds.Services, "Other")] },
            new InventorySection { Kind = InventoryKinds.Tasks, Items = [Item(InventoryKinds.Tasks, @"\Microsoft\Windows\Autochk\Proxy")] },
            new InventorySection { Kind = InventoryKinds.Appx, Items = [Item(InventoryKinds.Appx, "Microsoft.BingNews"), Item(InventoryKinds.Appx, "Microsoft.Paint")] },
            new InventorySection { Kind = InventoryKinds.Features, Items = [Item(InventoryKinds.Features, "SMB1Protocol")] },
            new InventorySection { Kind = InventoryKinds.Autoruns, Items = [new InventoryItem { Kind = InventoryKinds.Autoruns, Id = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run\OneDrive", DisplayName = "OneDrive" }] },
            new InventorySection { Kind = InventoryKinds.Capabilities, Items = [Item(InventoryKinds.Capabilities, "Browser.InternetExplorer~~~~0.0.11.0")] });

        CatalogCrossReference.Annotate(snap, tweaks);

        string[] Ids(string kind, string id) => [.. snap.Sections.Single(s => s.Kind == kind).Items.Single(i => i.Id == id).TweakIds];
        Assert.Equal(["t.svc"], Ids(InventoryKinds.Services, "diagtrack"));
        Assert.Empty(Ids(InventoryKinds.Services, "Other"));
        Assert.Equal(["t.task"], Ids(InventoryKinds.Tasks, @"\Microsoft\Windows\Autochk\Proxy"));
        Assert.Equal(["t.appx"], Ids(InventoryKinds.Appx, "Microsoft.BingNews"));
        Assert.Empty(Ids(InventoryKinds.Appx, "Microsoft.Paint"));
        Assert.Equal(["t.feat"], Ids(InventoryKinds.Features, "SMB1Protocol"));
        Assert.Equal(["t.run"], Ids(InventoryKinds.Autoruns, @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run\OneDrive"));
        Assert.Equal(["t.ps"], Ids(InventoryKinds.Capabilities, "Browser.InternetExplorer~~~~0.0.11.0"));
    }

    [Theory]
    [InlineData(2, false, "automatic")]
    [InlineData(2, true, "automaticDelayed")]
    [InlineData(4, false, "disabled")]
    [InlineData(0, false, "boot")]
    public void StartType_IsDescribedLikeTheCatalog(int start, bool delayed, string expected) =>
        Assert.Equal(expected, InventoryCollector.StartToString(start, delayed));
}
