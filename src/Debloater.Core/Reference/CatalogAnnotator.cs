using System.Text;
using System.Text.RegularExpressions;
using Debloater.Core.Model;

namespace Debloater.Core.Reference;

/// <summary>
/// Escreve 'confidence' e 'sources' nos arquivos YAML do catálogo por inserção de texto (sem
/// reserializar), preservando comentários, ordem e formatação. As linhas entram logo após 'risk:'.
/// Tweaks que já têm 'confidence' são mantidos, a menos que <c>force</c> seja true.
/// </summary>
public static partial class CatalogAnnotator
{
    public sealed record Annotation(Confidence Confidence, IReadOnlyList<SourceRef> Sources);

    [GeneratedRegex(@"^(?<indent>\s*)-\s+id:\s*(?<id>\S+)\s*$")]
    private static partial Regex TweakStart();

    /// <summary>Aplica as anotações a um arquivo. Devolve o texto novo e quantos tweaks mudaram.</summary>
    public static (string Text, int Changed) Annotate(string text, IReadOnlyDictionary<string, Annotation> annotations, bool force)
    {
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        int changed = 0;

        for (int i = 0; i < lines.Count; i++)
        {
            var m = TweakStart().Match(lines[i]);
            if (!m.Success || !annotations.TryGetValue(m.Groups["id"].Value, out var ann)) continue;

            var prop = new string(' ', m.Groups["indent"].Value.Length + 2);
            int end = BlockEnd(lines, i, m.Groups["indent"].Value.Length);

            int confLine = FindProp(lines, i, end, prop, "confidence");
            if (confLine >= 0 && !force) continue;

            var newLines = Render(prop, ann);

            // Remove confidence/sources existentes (force).
            int insertAt = -1;
            var removed = new List<string>();
            foreach (var name in new[] { "confidence", "sources" })
            {
                int at = FindProp(lines, i, end, prop, name);
                if (at < 0) continue;
                int stop = at + 1;
                while (stop < end && lines[stop].Trim().Length > 0 && IndentOf(lines[stop]) > prop.Length) stop++;
                removed.AddRange(lines.GetRange(at, stop - at));
                lines.RemoveRange(at, stop - at);
                end -= stop - at;
                if (insertAt < 0 || at < insertAt) insertAt = at;
            }

            if (insertAt < 0)
            {
                int risk = FindProp(lines, i, end, prop, "risk");
                insertAt = risk >= 0 ? risk + 1 : i + 1;
            }

            lines.InsertRange(insertAt, newLines);
            if (!removed.SequenceEqual(newLines)) changed++;
            i = insertAt + newLines.Count - 1;
        }
        return (string.Join(newline, lines), changed);
    }

    public static List<string> Render(string prop, Annotation ann)
    {
        var result = new List<string> { $"{prop}confidence: {ReferenceValidator.Lower(ann.Confidence)}" };
        if (ann.Sources.Count > 0)
        {
            result.Add($"{prop}sources:");
            foreach (var s in ann.Sources)
                result.Add($"{prop}  - {{ kind: {s.Kind}, ref: \"{Escape(s.Ref)}\" }}");
        }
        return result;
    }

    private static string Escape(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s)
        {
            if (ch is '"' or '\\') sb.Append('\\');
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static int BlockEnd(List<string> lines, int start, int itemIndent)
    {
        for (int j = start + 1; j < lines.Count; j++)
        {
            var l = lines[j];
            if (l.Trim().Length == 0 || l.TrimStart().StartsWith('#')) continue;
            int ind = IndentOf(l);
            if (ind < itemIndent + 2) return TrimTrailingBlank(lines, start, j);
        }
        return TrimTrailingBlank(lines, start, lines.Count);
    }

    /// <summary>Fim do bloco sem as linhas em branco/comentários finais (que pertencem ao próximo item).</summary>
    private static int TrimTrailingBlank(List<string> lines, int start, int end)
    {
        while (end - 1 > start && (lines[end - 1].Trim().Length == 0 || lines[end - 1].TrimStart().StartsWith('#'))) end--;
        return end;
    }

    private static int FindProp(List<string> lines, int start, int end, string prop, string name)
    {
        for (int j = start + 1; j < end; j++)
            if (IsPropAt(lines[j], prop) && lines[j].AsSpan(prop.Length).StartsWith(name + ":", StringComparison.Ordinal)) return j;
        return -1;
    }

    private static bool IsPropAt(string line, string prop) =>
        line.StartsWith(prop, StringComparison.Ordinal) && line.Length > prop.Length && line[prop.Length] != ' ' && line[prop.Length] != '-';

    private static int IndentOf(string line) => line.Length - line.TrimStart(' ').Length;
}
