using Debloater.App.Helpers;
using Debloater.Core.Inventory;
using Debloater.Core.Model;
using Debloater.Core.Planning;
using Debloater.Core.Platform;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Debloater.App.ViewModels;

/// <summary>Linha do inventário.</summary>
public sealed class InventoryRow(InventoryItem item)
{
    public InventoryItem Item { get; } = item;
    public string Title { get; } = string.IsNullOrWhiteSpace(item.DisplayName) ? item.Id : InventoryData.ResolveIndirect(item.DisplayName!);
    public string Subtitle => string.IsNullOrWhiteSpace(Item.DisplayName) || Item.DisplayName == Item.Id ? "" : Item.Id;
    public string PropertiesText => string.Join(" · ", Item.Properties.Select(p => $"{p.Key}: {p.Value}")
        .Concat(Item.Info?.Select(p => $"{p.Key}: {p.Value}") ?? []));
    public bool IsCatalogued => Item.IsCatalogued;
    public string TweaksText => "Ajustes: " + string.Join(", ", Item.TweakIds);
    public string BadgeText => IsCatalogued ? "Catalogado" : "Não catalogado";

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

/// <summary>Inventário do PC, coletado em segundo plano e mantido em cache.</summary>
public static class InventoryData
{
    private static Task<InventorySnapshot>? _cached;

    /// <summary>
    /// Coleta (uma vez, em cache) e cruza com o catálogo atual. O cruzamento é refeito a cada chamada
    /// porque o catálogo pode ter sido carregado ou trocado depois da coleta.
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

    /// <summary>Resolve nomes indiretos de serviço ("@%SystemRoot%\x.dll,-100"); se falhar, usa o texto após ';' ou o original.</summary>
    public static string ResolveIndirect(string text)
    {
        if (!text.StartsWith('@')) return text;
        var source = Environment.ExpandEnvironmentVariables(text);
        // "@Dll.dll,-100" sem pasta: a DLL fica em System32.
        if (!source.Contains('\\') && source.Contains(".dll,", StringComparison.OrdinalIgnoreCase))
            source = "@" + Path.Combine(Environment.SystemDirectory, source[1..]);
        var sb = new System.Text.StringBuilder(512);
        if (SHLoadIndirectString(source, sb, sb.Capacity, 0) == 0 && sb.Length > 0) return sb.ToString();
        // Formato de INF ("@oem20.inf,%chave%;Texto"): usa o texto após o ';'.
        int semi = text.LastIndexOf(';');
        return semi >= 0 && semi < text.Length - 1 ? text[(semi + 1)..] : text;
    }

    public static string KindLabel(string kind) => kind switch
    {
        InventoryKinds.Services => "Serviços",
        InventoryKinds.Drivers => "Drivers",
        InventoryKinds.Tasks => "Tarefas",
        InventoryKinds.Appx => "Apps",
        InventoryKinds.Features => "Recursos",
        InventoryKinds.Capabilities => "Capacidades",
        InventoryKinds.Autoruns => "Autoruns",
        _ => kind,
    };
}
