using System.Text.Json;
using Debloater.Core.Catalog;
using Debloater.Core.Inventory;

namespace Debloater.Cli;

internal static partial class Program
{
    private static async Task<int> InventoryAsync(Args a)
    {
        var sub = a.Positional.Count > 0 ? a.Positional[0].ToLowerInvariant() : null;
        return sub switch
        {
            null => await InventoryShowAsync(a),
            "snapshot" => await InventorySnapshotAsync(a),
            "diff" => InventoryDiffCommand(a),
            _ => Fail(L.F("Inv_UnknownSub", sub)),
        };
    }

    private static IReadOnlyList<string>? ParseKinds(Args a)
    {
        if (a.Kinds.Count == 0) return null;
        var unknown = a.Kinds.Where(k => !InventoryKinds.IsKnown(k)).ToList();
        if (unknown.Count > 0)
            throw new ArgumentException(L.F("Inv_InvalidKind", string.Join(", ", unknown), string.Join(", ", InventoryKinds.All)));
        return a.Kinds.Select(k => k.ToLowerInvariant()).ToList();
    }

    private static async Task<InventorySnapshot> CollectInventoryAsync(Args a)
    {
        var snapshot = await new InventoryCollector().CollectAsync(ParseKinds(a));
        var catalog = CatalogLoader.LoadDirectory(a.CatalogDir);
        if (!catalog.IsValid)
            Console.Error.WriteLine(L.F("Inv_CatalogErrors", catalog.Errors.Count));
        CatalogCrossReference.Annotate(snapshot, catalog.Tweaks);
        return snapshot;
    }

    private static async Task<int> InventoryShowAsync(Args a)
    {
        var snapshot = await CollectInventoryAsync(a);
        var totals = snapshot.Sections.ToDictionary(s => s.Kind, s => (Total: s.Items.Count, Catalogued: s.Items.Count(i => i.IsCatalogued)));
        if (a.Uncatalogued)
            foreach (var section in snapshot.Sections)
                section.Items.RemoveAll(i => i.IsCatalogued);

        if (a.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(snapshot, InventoryDiff.JsonOptions));
            return 0;
        }

        foreach (var section in snapshot.Sections)
        {
            Console.WriteLine($"\n[{section.Kind}]{(section.Available ? "" : " " + L.T("Inv_UnavailableUpper"))}{(section.Note is null ? "" : $" ({section.Note})")}");
            foreach (var item in section.Items)
            {
                var mark = L.T(item.IsCatalogued ? "Inv_Catalogued" : "Inv_NotCatalogued");
                var name = item.DisplayName is { Length: > 0 } dn && !dn.Equals(item.Id, StringComparison.OrdinalIgnoreCase) ? $"  {dn}" : "";
                Console.WriteLine($"  {item.Id,-60} {mark,-15}{name}");
                if (a.Verbose)
                {
                    foreach (var (k, v) in item.Properties) Console.WriteLine($"      {k}: {v}");
                    if (item.IsCatalogued) Console.WriteLine($"      tweaks: {string.Join(", ", item.TweakIds)}");
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine(L.T(a.Uncatalogued ? "Inv_SummaryUncatalogued" : "Inv_Summary"));
        foreach (var section in snapshot.Sections)
        {
            if (!section.Available) { Console.WriteLine($"  {section.Kind,-13} {L.T("Inv_Unavailable")}"); continue; }
            var (total, cat) = totals[section.Kind];
            Console.WriteLine($"  {section.Kind,-13} {L.F("Inv_SectionSummary", total, cat, total - cat)}");
        }
        return 0;
    }

    private static async Task<int> InventorySnapshotAsync(Args a)
    {
        if (a.Positional.Count < 2) return Fail(L.T("InvSnapshot_Usage"));
        var file = Path.GetFullPath(a.Positional[1]);
        var snapshot = await CollectInventoryAsync(a);
        InventoryDiff.Save(snapshot, file);
        Console.WriteLine(L.F("InvSnapshot_Saved", file, snapshot.WindowsBuild, snapshot.Sections.Sum(s => s.Items.Count)));
        foreach (var s in snapshot.Sections.Where(s => !s.Available || s.Note is not null))
            Console.WriteLine($"  {s.Kind}: {(s.Available ? "" : L.T("Inv_UnavailableUpper") + " ")}{s.Note}");
        return 0;
    }

    private static int InventoryDiffCommand(Args a)
    {
        if (a.Positional.Count < 3) return Fail(L.T("InvDiff_Usage"));
        var oldSnap = InventoryDiff.Load(a.Positional[1]);
        var newSnap = InventoryDiff.Load(a.Positional[2]);
        var diff = InventoryDiff.Compare(oldSnap, newSnap);

        if (a.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(diff, InventoryDiff.JsonOptions));
            return 0;
        }

        Console.WriteLine(L.F("InvDiff_Header", diff.OldBuild ?? "?", diff.NewBuild ?? "?"));
        foreach (var w in diff.Warnings) Console.WriteLine($"  {L.T("Label_Warning")}: {w}");
        foreach (var group in diff.Entries.GroupBy(e => e.Kind))
        {
            Console.WriteLine($"\n[{group.Key}]");
            foreach (var e in group)
            {
                var sign = e.Change switch { DiffChange.Added => "+", DiffChange.Removed => "-", _ => "~" };
                var tweaks = e.TweakIds.Count > 0 ? $"  (tweaks: {string.Join(", ", e.TweakIds)})" : "";
                Console.WriteLine($"  {sign} {e.Id}{(e.DisplayName is null ? "" : $"  {e.DisplayName}")}{tweaks}");
                if (e.Change == DiffChange.Changed || a.Verbose)
                    foreach (var d in e.Details) Console.WriteLine($"      {d}");
            }
        }
        var counts = diff.Entries.GroupBy(e => e.Change).Select(g => $"{g.Key}: {g.Count()}");
        Console.WriteLine(diff.Entries.Count == 0 ? "\n" + L.T("InvDiff_None") : $"\nTotal {diff.Entries.Count} | {string.Join(" | ", counts)}");
        return 0;
    }
}
