using System.Text.Json.Serialization;

namespace Debloater.Core.Inventory;

/// <summary>Item kinds enumerated by the inventory.</summary>
public static class InventoryKinds
{
    public const string Services = "services";
    public const string Drivers = "drivers";
    public const string Tasks = "tasks";
    public const string Appx = "appx";
    public const string Features = "features";
    public const string Capabilities = "capabilities";
    public const string Autoruns = "autoruns";

    public static IReadOnlyList<string> All { get; } = [Services, Drivers, Tasks, Appx, Features, Capabilities, Autoruns];

    public static bool IsKnown(string kind) => All.Contains(kind, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// An item present on the system (service, task, package...). <see cref="Properties"/> takes part in
/// snapshot comparison; <see cref="Info"/> is informational (e.g. version) and never yields "changed".
/// </summary>
public sealed class InventoryItem
{
    public string Kind { get; set; } = "";

    /// <summary>Stable identifier within the kind (service name, task path, package name...).</summary>
    public string Id { get; set; } = "";

    public string? DisplayName { get; set; }

    public SortedDictionary<string, string> Properties { get; set; } = new(StringComparer.Ordinal);

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SortedDictionary<string, string>? Info { get; set; }

    /// <summary>Ids of the catalog tweaks that touch this item (empty = not catalogued).</summary>
    public List<string> TweakIds { get; set; } = [];

    [JsonIgnore]
    public bool IsCatalogued => TweakIds.Count > 0;

    public override string ToString() => $"{Kind}:{Id}";
}

/// <summary>Result of enumerating one kind.</summary>
public sealed class InventorySection
{
    public string Kind { get; set; } = "";

    /// <summary>false when it could not be enumerated (e.g. requires administrator).</summary>
    public bool Available { get; set; } = true;

    /// <summary>Remark (e.g. "current user only; provisioned packages require administrator").</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; set; }

    public List<InventoryItem> Items { get; set; } = [];
}

/// <summary>Full inventory snapshot (saved as JSON by 'inventory snapshot').</summary>
public sealed class InventorySnapshot
{
    public int SchemaVersion { get; set; } = 1;
    public DateTime CreatedUtc { get; set; }
    public string? WindowsBuild { get; set; }
    public string? Edition { get; set; }
    public bool Elevated { get; set; }
    public List<InventorySection> Sections { get; set; } = [];
}
