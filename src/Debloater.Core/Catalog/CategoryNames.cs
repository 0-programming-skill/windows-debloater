using Debloater.Core.Localization;
using YamlDotNet.Core;

namespace Debloater.Core.Catalog;

/// <summary>
/// Translated display names of the category tree, from catalog/i18n/categories.yaml. Category paths are
/// English identifiers ("Privacy/Telemetry"); each node of a path is translated on its own. Format:
/// <code>
/// pt-BR:
///   Privacy: Privacidade              # node "Privacy"
///   Privacy/Telemetry: Telemetria     # node "Privacy/Telemetry" (only the last segment)
/// </code>
/// </summary>
public sealed class CategoryNames
{
    /// <summary>File name inside the catalog's i18n folder.</summary>
    public const string FileName = "categories.yaml";

    public static CategoryNames Empty { get; } = new(new Dictionary<string, Dictionary<string, string>>());

    private readonly Dictionary<string, Dictionary<string, string>> _byLanguage;

    private CategoryNames(Dictionary<string, Dictionary<string, string>> byLanguage)
    {
        _byLanguage = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (lang, map) in byLanguage)
            _byLanguage[lang] = new Dictionary<string, string>(map ?? [], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Languages present in the file.</summary>
    public IEnumerable<string> Languages => _byLanguage.Keys;

    /// <summary>Parses the YAML. Throws <see cref="FormatException"/> when it is malformed.</summary>
    public static CategoryNames Parse(string yaml)
    {
        try
        {
            var map = CatalogYaml.Deserializer.Deserialize<Dictionary<string, Dictionary<string, string>>?>(yaml);
            return new CategoryNames(map ?? []);
        }
        catch (YamlException ex)
        {
            var inner = ex.InnerException is null ? "" : $" ({ex.InnerException.Message})";
            throw new FormatException($"invalid YAML at line {ex.Start.Line}, column {ex.Start.Column}: {ex.Message}{inner}", ex);
        }
    }

    /// <summary>Whether <paramref name="language"/> has a name for the node <paramref name="path"/>.</summary>
    public bool Has(string language, string path) =>
        _byLanguage.TryGetValue(language, out var map) && map.TryGetValue(path.Trim('/'), out var name) && !string.IsNullOrWhiteSpace(name);

    /// <summary>Display name of the node <paramref name="path"/> (its last segment) in the current UI language.</summary>
    public string DisplayNode(string path)
    {
        path = path.Trim('/');
        var lang = UiLanguage.Current;
        if (lang != UiLanguage.English && _byLanguage.TryGetValue(lang, out var map)
            && map.TryGetValue(path, out var name) && !string.IsNullOrWhiteSpace(name))
            return name;
        int slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    /// <summary>Full display path ("Privacidade/Telemetria") in the current UI language.</summary>
    public string DisplayPath(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var names = new List<string>(parts.Length);
        for (int i = 0; i < parts.Length; i++)
            names.Add(DisplayNode(string.Join('/', parts.Take(i + 1))));
        return string.Join('/', names);
    }

    /// <summary>Every node path of a category ("A/B/C" → "A", "A/B", "A/B/C").</summary>
    public static IEnumerable<string> NodePaths(string category)
    {
        var parts = category.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < parts.Length; i++)
            yield return string.Join('/', parts.Take(i + 1));
    }
}
