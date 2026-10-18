using Debloater.App.Helpers;
using Debloater.Core.Inventory;
using Debloater.Core.Model;
using Debloater.Core.Planning;
using Debloater.Core.Platform;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Debloater.App.ViewModels;

/// <summary>Inventory row.</summary>
public sealed class InventoryRow(InventoryItem item)
{
    public InventoryItem Item { get; } = item;
    public string Title { get; } = string.IsNullOrWhiteSpace(item.DisplayName) ? item.Id : InventoryData.ResolveIndirect(item.DisplayName!);
    public string Subtitle => string.IsNullOrWhiteSpace(Item.DisplayName) || Item.DisplayName == Item.Id ? "" : Item.Id;
    public string PropertiesText => string.Join(" · ", Item.Properties.Select(p => $"{p.Key}: {p.Value}")
        .Concat(Item.Info?.Select(p => $"{p.Key}: {p.Value}") ?? []));
    public bool IsCatalogued => Item.IsCatalogued;

    /// <summary>Name that UI Automation (screen readers) reads for the list item.</summary>
    public override string ToString() => Title;
    public string TweaksText => Loc.Format("Inventory_Tweaks", string.Join(", ", Item.TweakIds));
    public string BadgeText => IsCatalogued ? Loc.Get("Inventory_Catalogued") : Loc.Get("Inventory_NotCatalogued");

    private Color BadgeColor => IsCatalogued ? Labels.StateColor(TweakState.Applied) : Labels.StateColor(TweakState.NotApplicable);
    public SolidColorBrush BadgeBrush => Labels.Brush(BadgeColor);
    public SolidColorBrush BadgeForeground => Labels.ForegroundOn(BadgeColor);

    public bool Matches(string search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        return search.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w =>
            TweakQuery.Has(Item.Id, w) || TweakQuery.Has(Item.DisplayName, w) ||
            Item.Properties.Values.Any(v => TweakQuery.Has(v, w)) || Item.TweakIds.Any(t => TweakQuery.Has(t, w)));
    }
}

/// <summary>PC inventory, collected in the background and cached.</summary>
public static class InventoryData
{
    private static Task<InventorySnapshot>? _cached;

    /// <summary>
    /// Collects (once, cached) and cross-references with the current catalog. The cross-reference is redone on
    /// every call because the catalog may have been loaded or replaced after the collection.
    /// </summary>
    public static async Task<InventorySnapshot> GetAsync(IReadOnlyList<Tweak> tweaks, bool refresh = false)
    {
        if (refresh || _cached is null || _cached.IsFaulted)
            _cached = Task.Run(() => new InventoryCollector(SystemInfo.Current).CollectAsync());
        var snapshot = await _cached;
        await Task.Run(() => CatalogCrossReference.Annotate(snapshot, tweaks));
        return snapshot;
    }

    [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, System.Text.StringBuilder output, int size, nint reserved);

    /// <summary>Resolves indirect service names ("@%SystemRoot%\x.dll,-100"); if that fails, uses the text after ';' or the original.</summary>
    public static string ResolveIndirect(string text)
    {
        if (!text.StartsWith('@')) return text;
        var source = Environment.ExpandEnvironmentVariables(text);
        // "@Dll.dll,-100" without a folder: the DLL is in System32.
        if (!source.Contains('\\') && source.Contains(".dll,", StringComparison.OrdinalIgnoreCase))
            source = "@" + Path.Combine(Environment.SystemDirectory, source[1..]);
        var sb = new System.Text.StringBuilder(512);
        if (SHLoadIndirectString(source, sb, sb.Capacity, 0) == 0 && sb.Length > 0) return sb.ToString();
        // INF format ("@oem20.inf,%key%;Text"): use the text after the ';'.
        int semi = text.LastIndexOf(';');
        return semi >= 0 && semi < text.Length - 1 ? text[(semi + 1)..] : text;
    }

    public static string KindLabel(string kind) => kind switch
    {
        InventoryKinds.Services => Loc.Get("InventoryKind_Services"),
        InventoryKinds.Drivers => Loc.Get("InventoryKind_Drivers"),
        InventoryKinds.Tasks => Loc.Get("InventoryKind_Tasks"),
        InventoryKinds.Appx => Loc.Get("InventoryKind_Apps"),
        InventoryKinds.Features => Loc.Get("InventoryKind_Features"),
        InventoryKinds.Capabilities => Loc.Get("InventoryKind_Capabilities"),
        InventoryKinds.Autoruns => Loc.Get("InventoryKind_Autoruns"),
        InventoryKinds.FeatureFlags => Loc.Get("InventoryKind_FeatureFlags"),
        _ => kind,
    };
}
