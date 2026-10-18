using System.Globalization;
using Debloater.Core.Catalog;
using Debloater.Core.Model;
using Debloater.Core.Profiles;

namespace Debloater.Core.Planning;

/// <summary>Filters of the tweak list (search, category, state, risk, confidence, "only what differs").</summary>
public sealed class TweakQuery
{
    /// <summary>
    /// Text searched in id, name, description and tags (every word must appear; ignores accents). Both the
    /// English text and the text in the current UI language are searched.
    /// </summary>
    public string? Search { get; init; }

    /// <summary>Category prefix (see <see cref="ProfileStore.CategoryMatches"/>). Null = all.</summary>
    public string? Category { get; init; }

    public IReadOnlySet<TweakState>? States { get; init; }
    public IReadOnlySet<RiskLevel>? Risks { get; init; }

    /// <summary>Confidence levels; tweaks without 'confidence' count as <see cref="Confidence.Community"/>.</summary>
    public IReadOnlySet<Confidence>? Confidences { get; init; }

    /// <summary>Only tweaks with a pending change (current state different from the desired one).</summary>
    public bool OnlyDiffering { get; init; }

    public bool Matches(Tweak tweak, TweakState? state, DesiredState? desired)
    {
        if (Category is not null && !ProfileStore.CategoryMatches(tweak.Category, Category)) return false;
        if (States is { Count: > 0 } && (state is null || !States.Contains(state.Value))) return false;
        if (Risks is { Count: > 0 } && !Risks.Contains(tweak.Risk)) return false;
        if (Confidences is { Count: > 0 } && !Confidences.Contains(tweak.Confidence ?? Confidence.Community)) return false;
        if (OnlyDiffering && (state is null || desired is null || !ChangePlanner.NeedsChange(state.Value, desired.Value))) return false;
        if (!string.IsNullOrWhiteSpace(Search))
        {
            foreach (var w in Search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!(Has(tweak.Id, w) || Has(tweak.Name, w) || Has(tweak.Description, w)
                    || Has(tweak.DisplayName, w) || Has(tweak.DisplayDescription, w) || tweak.Tags.Any(t => Has(t, w))))
                    return false;
            }
        }
        return true;
    }

    /// <summary>Search ignoring case and accents ("telemetria" finds "Telemetria", "anuncio" finds "anúncio").</summary>
    public static bool Has(string? text, string word) =>
        text is not null && CultureInfo.InvariantCulture.CompareInfo.IndexOf(
            text, word, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
}

/// <summary>Node of the category tree ("A/B" → A → B).</summary>
public sealed class CategoryNode
{
    /// <summary>Path segment (identifier; English for catalog categories).</summary>
    public string Name { get; init; } = "";

    /// <summary>Name shown to the user (translated when category names are given; otherwise <see cref="Name"/>).</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>Full path ("Privacy/Telemetry").</summary>
    public string Path { get; init; } = "";

    public List<CategoryNode> Children { get; } = [];

    /// <summary>How many entries are in this category and all its subcategories.</summary>
    public int Count { get; set; }

    /// <summary>
    /// Builds the tree; each item of <paramref name="categories"/> counts once (pass one per tweak).
    /// <paramref name="names"/> translates the catalog's category nodes (current UI language); siblings are sorted by display name.
    /// </summary>
    public static List<CategoryNode> Build(IEnumerable<string> categories, CategoryNames? names = null)
    {
        var roots = new List<CategoryNode>();
        foreach (var category in categories)
        {
            var parts = category.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var level = roots;
            var path = "";
            foreach (var part in parts)
            {
                path = path.Length == 0 ? part : path + "/" + part;
                var node = level.FirstOrDefault(n => n.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
                if (node is null)
                {
                    node = new CategoryNode { Name = part, Path = path, DisplayName = names?.DisplayNode(path) ?? part };
                    level.Add(node);
                }
                node.Count++;
                level = node.Children;
            }
        }
        Sort(roots);
        return roots;
    }

    private static void Sort(List<CategoryNode> nodes)
    {
        nodes.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCulture));
        foreach (var n in nodes) Sort(n.Children);
    }
}
