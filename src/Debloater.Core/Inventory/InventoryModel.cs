using System.Text.Json.Serialization;

namespace Debloater.Core.Inventory;

/// <summary>Tipos de item enumerados pelo inventário.</summary>
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
/// Um item existente no sistema (serviço, tarefa, pacote...). <see cref="Properties"/> entra na
/// comparação de snapshots; <see cref="Info"/> é informativo (ex.: versão) e não gera "alterado".
/// </summary>
public sealed class InventoryItem
{
    public string Kind { get; set; } = "";

    /// <summary>Identificador estável dentro do tipo (nome do serviço, caminho da tarefa, nome do pacote...).</summary>
    public string Id { get; set; } = "";

    public string? DisplayName { get; set; }

    public SortedDictionary<string, string> Properties { get; set; } = new(StringComparer.Ordinal);

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SortedDictionary<string, string>? Info { get; set; }

    /// <summary>Ids dos tweaks do catálogo que mexem neste item (vazio = não catalogado).</summary>
    public List<string> TweakIds { get; set; } = [];

    [JsonIgnore]
    public bool IsCatalogued => TweakIds.Count > 0;

    public override string ToString() => $"{Kind}:{Id}";
}

/// <summary>Resultado da enumeração de um tipo.</summary>
public sealed class InventorySection
{
    public string Kind { get; set; } = "";

    /// <summary>false quando não foi possível enumerar (ex.: exige administrador).</summary>
    public bool Available { get; set; } = true;

    /// <summary>Observação (ex.: "somente usuário atual; provisionados exigem administrador").</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; set; }

    public List<InventoryItem> Items { get; set; } = [];
}

/// <summary>Snapshot completo do inventário (gravado em JSON por 'inventory snapshot').</summary>
public sealed class InventorySnapshot
{
    public int SchemaVersion { get; set; } = 1;
    public DateTime CreatedUtc { get; set; }
    public string? WindowsBuild { get; set; }
    public string? Edition { get; set; }
    public bool Elevated { get; set; }
    public List<InventorySection> Sections { get; set; } = [];
}
