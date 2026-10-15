using System.Globalization;
using Debloater.Core.Model;
using Debloater.Core.Profiles;

namespace Debloater.Core.Planning;

/// <summary>Filtros da lista de ajustes (busca, categoria, estado, risco, confiança, "só o que difere").</summary>
public sealed class TweakQuery
{
    /// <summary>Texto procurado em id, nome, descrição e tags (todas as palavras precisam aparecer; ignora acentos).</summary>
    public string? Search { get; init; }

    /// <summary>Prefixo de categoria (ver <see cref="ProfileStore.CategoryMatches"/>). Nulo = todas.</summary>
    public string? Category { get; init; }

    public IReadOnlySet<TweakState>? States { get; init; }
    public IReadOnlySet<RiskLevel>? Risks { get; init; }

    /// <summary>Níveis de confiança; tweaks sem 'confidence' contam como <see cref="Confidence.Community"/>.</summary>
    public IReadOnlySet<Confidence>? Confidences { get; init; }

    /// <summary>Só tweaks com mudança pendente (estado atual diferente do desejado).</summary>
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
                if (!(Has(tweak.Id, w) || Has(tweak.Name, w) || Has(tweak.Description, w) || tweak.Tags.Any(t => Has(t, w))))
                    return false;
            }
        }
        return true;
    }

    /// <summary>Busca sem diferenciar maiúsculas nem acentos ("telemetria" acha "Telemetria", "anuncio" acha "anúncio").</summary>
    public static bool Has(string? text, string word) =>
        text is not null && CultureInfo.InvariantCulture.CompareInfo.IndexOf(
            text, word, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
}

/// <summary>Nó da árvore de categorias ("A/B" → A → B).</summary>
public sealed class CategoryNode
{
    public string Name { get; init; } = "";

    /// <summary>Caminho completo ("Privacidade/Telemetria").</summary>
    public string Path { get; init; } = "";

    public List<CategoryNode> Children { get; } = [];

    /// <summary>Quantas entradas estão nesta categoria e em todas as subcategorias.</summary>
    public int Count { get; set; }

    /// <summary>Monta a árvore; cada item de <paramref name="categories"/> conta uma vez (passe uma por tweak).</summary>
    public static List<CategoryNode> Build(IEnumerable<string> categories)
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
                    node = new CategoryNode { Name = part, Path = path };
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
        nodes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));
        foreach (var n in nodes) Sort(n.Children);
    }
}
