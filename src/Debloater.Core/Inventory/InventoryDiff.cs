using System.Text.Json;
using System.Text.Json.Serialization;

namespace Debloater.Core.Inventory;

public enum DiffChange
{
    Added,
    Removed,
    Changed,
}

/// <summary>Uma diferença entre dois snapshots.</summary>
public sealed record InventoryDiffEntry(
    string Kind,
    string Id,
    DiffChange Change,
    string? DisplayName,
    IReadOnlyList<string> Details,
    IReadOnlyList<string> TweakIds);

public sealed class InventoryDiffResult
{
    public string? OldBuild { get; init; }
    public string? NewBuild { get; init; }
    public required IReadOnlyList<InventoryDiffEntry> Entries { get; init; }

    /// <summary>Tipos que não puderam ser comparados (indisponíveis em um dos snapshots ou ausentes).</summary>
    public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>
/// Compara dois snapshots de inventário. É o que o mantenedor roda quando sai um build novo do
/// Windows: o que apareceu (candidato a tweak novo), sumiu (tweak a marcar maxBuild) ou mudou.
/// </summary>
public static class InventoryDiff
{
    public static InventoryDiffResult Compare(InventorySnapshot oldSnap, InventorySnapshot newSnap)
    {
        var entries = new List<InventoryDiffEntry>();
        var warnings = new List<string>();
        var oldSections = oldSnap.Sections.ToDictionary(s => s.Kind, StringComparer.OrdinalIgnoreCase);
        var newSections = newSnap.Sections.ToDictionary(s => s.Kind, StringComparer.OrdinalIgnoreCase);

        foreach (var kind in oldSections.Keys.Union(newSections.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(KindOrder))
        {
            if (!oldSections.TryGetValue(kind, out var o) || !newSections.TryGetValue(kind, out var n))
            {
                warnings.Add($"{kind}: presente em apenas um dos snapshots; não comparado.");
                continue;
            }
            if (!o.Available || !n.Available)
            {
                warnings.Add($"{kind}: indisponível em um dos snapshots; não comparado.");
                continue;
            }
            if (o.Note != n.Note && (o.Note is not null || n.Note is not null))
                warnings.Add($"{kind}: coletado em condições diferentes (antes: {o.Note ?? "completo"}; depois: {n.Note ?? "completo"}).");

            var oldItems = ToDictionary(o.Items);
            var newItems = ToDictionary(n.Items);

            foreach (var (id, item) in newItems)
            {
                if (!oldItems.TryGetValue(id, out var before))
                {
                    entries.Add(new InventoryDiffEntry(kind, item.Id, DiffChange.Added, item.DisplayName, Describe(item.Properties), item.TweakIds));
                    continue;
                }
                var details = CompareProperties(before.Properties, item.Properties);
                if (details.Count > 0)
                    entries.Add(new InventoryDiffEntry(kind, item.Id, DiffChange.Changed, item.DisplayName ?? before.DisplayName, details,
                        item.TweakIds.Union(before.TweakIds).ToList()));
            }
            foreach (var (id, item) in oldItems)
            {
                if (!newItems.ContainsKey(id))
                    entries.Add(new InventoryDiffEntry(kind, item.Id, DiffChange.Removed, item.DisplayName, Describe(item.Properties), item.TweakIds));
            }
        }

        return new InventoryDiffResult
        {
            OldBuild = oldSnap.WindowsBuild,
            NewBuild = newSnap.WindowsBuild,
            Entries = entries
                .OrderBy(e => KindOrder(e.Kind))
                .ThenBy(e => e.Change)
                .ThenBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Warnings = warnings,
        };
    }

    private static int KindOrder(string kind)
    {
        int idx = InventoryKinds.All.ToList().FindIndex(k => k.Equals(kind, StringComparison.OrdinalIgnoreCase));
        return idx < 0 ? int.MaxValue : idx;
    }

    private static Dictionary<string, InventoryItem> ToDictionary(IEnumerable<InventoryItem> items)
    {
        var dict = new Dictionary<string, InventoryItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items) dict.TryAdd(item.Id, item);
        return dict;
    }

    private static List<string> CompareProperties(IDictionary<string, string> before, IDictionary<string, string> after)
    {
        var changes = new List<string>();
        foreach (var key in before.Keys.Union(after.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            before.TryGetValue(key, out var b);
            after.TryGetValue(key, out var a);
            if (!string.Equals(a, b, StringComparison.Ordinal))
                changes.Add($"{key}: {b ?? "(ausente)"} → {a ?? "(ausente)"}");
        }
        return changes;
    }

    private static List<string> Describe(IDictionary<string, string> props) =>
        props.Select(p => $"{p.Key}={p.Value}").ToList();

    // ---------------- persistência ----------------

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static void Save(InventorySnapshot snapshot, string file)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(file));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(file, JsonSerializer.Serialize(snapshot, JsonOptions));
    }

    public static InventorySnapshot Load(string file) =>
        JsonSerializer.Deserialize<InventorySnapshot>(File.ReadAllText(file), JsonOptions)
        ?? throw new InvalidDataException($"snapshot vazio: {file}");
}
