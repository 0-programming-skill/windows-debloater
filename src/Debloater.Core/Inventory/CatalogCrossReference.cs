using Debloater.Core.Actions;
using Debloater.Core.Model;

namespace Debloater.Core.Inventory;

/// <summary>
/// Cross-references inventory items with the catalog: for each item, which tweaks touch it.
/// Rules per action type:
///   service          → services/drivers by name
///   scheduledTask    → tasks by full path
///   appx             → appx by name (accepts a trailing "*")
///   optionalFeature  → features by name
///   registry         → autoruns (value in a Run/RunOnce key)
///   featureFlag      → featureflags by feature id
///   powershell       → any item whose name (≥ 5 characters) appears in the script
/// </summary>
public static class CatalogCrossReference
{
    public static void Annotate(InventorySnapshot snapshot, IEnumerable<Tweak> tweaks)
    {
        foreach (var section in snapshot.Sections) Annotate(section, tweaks);
    }

    public static void Annotate(InventorySection section, IEnumerable<Tweak> tweaks)
    {
        var list = tweaks.ToList();
        foreach (var item in section.Items)
        {
            item.TweakIds = list
                .Where(t => t.Actions.Any(a => Touches(a, item)))
                .Select(t => t.Id)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();
        }
    }

    public static bool Touches(ActionSpec action, InventoryItem item)
    {
        switch (action.Type)
        {
            case ActionSpec.Service:
                return item.Kind is InventoryKinds.Services or InventoryKinds.Drivers && Eq(action.Name, item.Id);
            case ActionSpec.ScheduledTask:
                return item.Kind == InventoryKinds.Tasks && Eq(action.Path, item.Id);
            case ActionSpec.Appx:
                return item.Kind == InventoryKinds.Appx && action.Name is not null && AppxActionHandler.NameMatches(action.Name, item.Id);
            case ActionSpec.OptionalFeature:
                return item.Kind == InventoryKinds.Features && Eq(action.Name, item.Id);
            case ActionSpec.Registry:
                return item.Kind == InventoryKinds.Autoruns && action.Path is not null && action.Name is not null
                    && Eq(NormalizeRegistryPath(action.Path) + "\\" + action.Name, NormalizeRegistryPath(item.Id));
            case ActionSpec.FeatureFlag:
                return item.Kind == InventoryKinds.FeatureFlags && action.FeatureId is { } id
                    && string.Equals(id.ToString(System.Globalization.CultureInfo.InvariantCulture), item.Id, StringComparison.Ordinal);
            case ActionSpec.PowerShell:
                var name = SearchableName(item);
                if (name.Length < 5) return false;
                return Contains(action.Detect, name) || Contains(action.Apply, name) || Contains(action.Revert, name);
            default:
                return false;
        }
    }

    /// <summary>Name to look for in scripts: capabilities without the version ("Browser.InternetExplorer~~~~0.0.11.0" → "Browser.InternetExplorer").</summary>
    private static string SearchableName(InventoryItem item)
    {
        var id = item.Id;
        if (item.Kind == InventoryKinds.Capabilities)
        {
            int idx = id.IndexOf('~');
            if (idx > 0) id = id[..idx];
        }
        if (item.Kind == InventoryKinds.Autoruns) id = item.DisplayName ?? id;
        return id;
    }

    /// <summary>HKEY_LOCAL_MACHINE → HKLM, upper case, no trailing backslash.</summary>
    public static string NormalizeRegistryPath(string path)
    {
        var p = path.Trim().TrimEnd('\\');
        foreach (var (longName, shortName) in new[] { ("HKEY_LOCAL_MACHINE", "HKLM"), ("HKEY_CURRENT_USER", "HKCU"), ("HKEY_USERS", "HKU"), ("HKEY_CLASSES_ROOT", "HKCR") })
            if (p.StartsWith(longName, StringComparison.OrdinalIgnoreCase)) p = shortName + p[longName.Length..];
        return p.ToUpperInvariant();
    }

    private static bool Eq(string? a, string? b) => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string? text, string value) => text is not null && text.Contains(value, StringComparison.OrdinalIgnoreCase);
}
