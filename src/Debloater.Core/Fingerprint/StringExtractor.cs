using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;

namespace Debloater.Core.Fingerprint;

public sealed class StringExtractorOptions
{
    /// <summary>Tamanho mínimo de uma sequência ASCII.</summary>
    public int MinAscii { get; init; } = 6;

    /// <summary>Tamanho mínimo (em caracteres) de uma sequência UTF-16LE.</summary>
    public int MinUtf16 { get; init; } = 5;

    /// <summary>Sequências mais longas que isso são texto (ajuda, licença...) e são ignoradas.</summary>
    public int MaxRunChars { get; init; } = 1024;

    /// <summary>Distância máxima, em bytes, entre um nome e um caminho de registro para contar como "perto".</summary>
    public int NearWindowBytes { get; init; } = 512;

    /// <summary>Limite de cadeias que casam com os padrões, por arquivo.</summary>
    public int MaxStrings { get; init; } = 3000;

    /// <summary>Limite de nomes "perto de um caminho", por arquivo.</summary>
    public int MaxNearNames { get; init; } = 1500;

    public static StringExtractorOptions Default { get; } = new();
}

/// <summary>Resultado da extração de um arquivo.</summary>
public sealed class ExtractedStrings
{
    public List<string> Strings { get; } = [];
    public List<string> NearNames { get; } = [];

    /// <summary>Nome de valor → caminho de registro mais próximo.</summary>
    public SortedDictionary<string, string> Context { get; } = new(StringComparer.Ordinal);

    public bool Truncated { get; set; }
}

/// <summary>
/// Extrai de um binário (bytes) as cadeias ASCII e UTF-16LE que interessam para debloat: caminhos
/// de registro, nomes de valores com padrões conhecidos, caminhos de CSP, nomes WNF e de features,
/// mais identificadores que aparecem perto de um caminho de registro. Não guarda nada além disso:
/// o resultado é limitado e deduplicado (sem diferenciar maiúsculas).
/// </summary>
public static class StringExtractor
{
    private readonly record struct Run(int Offset, int Chars);

    private readonly record struct Found(string Value, StringKind Kind, int Offset);

    public static ExtractedStrings Extract(ReadOnlySpan<byte> data, StringExtractorOptions? options = null)
    {
        var o = options ?? StringExtractorOptions.Default;
        var found = new List<Found>();
        var identifiers = new List<Run>();   // UTF-16 identificadores (avaliados depois, com a proximidade)
        var pathOffsets = new List<(int Offset, string Path)>();

        ScanUtf16(data, 0, o, found, identifiers, pathOffsets);
        ScanUtf16(data, 1, o, found, identifiers, pathOffsets);
        ScanAscii(data, o, found);

        pathOffsets.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        var near = new List<Found>();
        var context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var run in identifiers)
        {
            var chars = MemoryMarshal.Cast<byte, char>(data.Slice(run.Offset, run.Chars * 2));
            var kind = Classify(chars);
            string? nearest = Nearest(pathOffsets, run.Offset, o.NearWindowBytes);
            if (kind != StringKind.None)
            {
                var s = chars.ToString();
                found.Add(new Found(s, kind, run.Offset));
                if (nearest is not null && kind == StringKind.ValueName) context.TryAdd(s, nearest);
            }
            else if (nearest is not null && StringClassifier.IsNearNameCandidate(chars))
            {
                var s = chars.ToString();
                near.Add(new Found(s, StringKind.NearName, run.Offset));
                context.TryAdd(s, nearest);
            }
        }

        var result = new ExtractedStrings();
        var kept = Dedupe(found);
        if (kept.Count > o.MaxStrings)
        {
            result.Truncated = true;
            kept = kept.OrderBy(f => StringClassifier.Priority(f.Kind)).ThenBy(f => f.Value, StringComparer.Ordinal).Take(o.MaxStrings).ToList();
        }
        result.Strings.AddRange(kept.Select(f => f.Value).Order(StringComparer.Ordinal));

        var keptSet = new HashSet<string>(result.Strings, StringComparer.OrdinalIgnoreCase);
        var nearKept = Dedupe(near).Where(f => !keptSet.Contains(f.Value)).ToList();
        if (nearKept.Count > o.MaxNearNames)
        {
            result.Truncated = true;
            nearKept = nearKept.OrderBy(f => f.Value, StringComparer.Ordinal).Take(o.MaxNearNames).ToList();
        }
        result.NearNames.AddRange(nearKept.Select(f => f.Value).Order(StringComparer.Ordinal));

        foreach (var name in result.Strings.Concat(result.NearNames))
            if (context.TryGetValue(name, out var path)) result.Context[name] = path;
        return result;
    }

    private static List<Found> Dedupe(List<Found> items)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<Found>(items.Count);
        foreach (var f in items.OrderBy(f => f.Offset))
            if (seen.Add(f.Value)) list.Add(f);
        return list;
    }

    private static StringKind Classify(ReadOnlySpan<char> identifier)
    {
        // Identificadores puros: só os tipos sem '\' ou '/'.
        if (identifier.StartsWith("WNF_") || identifier.StartsWith("Feature_"))
            return StringClassifier.Classify(identifier.ToString());
        return StringClassifier.IsValueNameLike(identifier) ? StringKind.ValueName : StringKind.None;
    }

    private static string? Nearest(List<(int Offset, string Path)> paths, int offset, int window)
    {
        if (paths.Count == 0) return null;
        int lo = 0, hi = paths.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (paths[mid].Offset < offset) lo = mid + 1; else hi = mid;
        }
        string? best = null;
        int bestDist = int.MaxValue;
        for (int i = Math.Max(0, lo - 1); i <= Math.Min(paths.Count - 1, lo + 1); i++)
        {
            int d = Math.Abs(paths[i].Offset - offset);
            if (d <= window && d < bestDist) { bestDist = d; best = paths[i].Path; }
        }
        return best;
    }

    private static bool Printable(byte b) => b >= 0x20 && b <= 0x7E;

    private static void ScanUtf16(ReadOnlySpan<byte> data, int alignment, StringExtractorOptions o,
        List<Found> found, List<Run> identifiers, List<(int, string)> pathOffsets)
    {
        if (data.Length - alignment < 2) return;
        // Caractere UTF-16LE imprimível = ushort entre 0x20 e 0x7E (byte alto zero): busca vetorizada.
        var units = MemoryMarshal.Cast<byte, ushort>(data[alignment..]);
        int pos = 0;
        while (pos < units.Length)
        {
            int rel = units[pos..].IndexOfAnyInRange((ushort)0x20, (ushort)0x7E);
            if (rel < 0) break;
            int start = pos + rel;
            int chars = units[start..].IndexOfAnyExceptInRange((ushort)0x20, (ushort)0x7E);
            if (chars < 0) chars = units.Length - start;
            pos = start + chars;
            if (chars < o.MinUtf16 || chars > o.MaxRunChars) continue;
            int offset = alignment + start * 2;
            var span = MemoryMarshal.Cast<ushort, char>(units.Slice(start, chars));
            if (StringClassifier.IsIdentifier(span))
            {
                identifiers.Add(new Run(offset, chars));
                continue;
            }
            HandleComplex(span, offset, found, pathOffsets);
        }
    }

    private static readonly SearchValues<byte> AsciiAnchors = SearchValues.Create("\\/_-"u8);

    private static void ScanAscii(ReadOnlySpan<byte> data, StringExtractorOptions o, List<Found> found)
    {
        // Em ASCII só interessam caminhos (\ ou /) e nomes com marcadores (WNF_, Feature_,
        // SubscribedContent-): identificadores soltos em ASCII são quase sempre nomes de funções
        // importadas/exportadas. Então a busca parte dos separadores e expande até as bordas da sequência.
        Span<char> buffer = stackalloc char[o.MaxRunChars];
        int pos = 0;
        while (pos < data.Length)
        {
            int rel = data[pos..].IndexOfAny(AsciiAnchors);
            if (rel < 0) break;
            int anchor = pos + rel;
            int start = anchor;
            while (start > 0 && Printable(data[start - 1])) start--;
            int end = anchor + 1;
            while (end < data.Length && Printable(data[end])) end++;
            pos = end;
            int runLen = end - start;
            if (runLen < o.MinAscii || runLen > o.MaxRunChars) continue;
            var run = data[start..end];
            if (run.IndexOfAny((byte)'\\', (byte)'/') < 0
                && run.IndexOf("WNF_"u8) < 0 && run.IndexOf("Feature_"u8) < 0 && run.IndexOf("SubscribedContent-"u8) < 0)
                continue;
            int n = Encoding.ASCII.GetChars(run, buffer);
            var chars = buffer[..n];
            if (StringClassifier.IsIdentifier(chars) || chars.StartsWith("SubscribedContent-"))
            {
                var s = chars.ToString();
                var kind = StringClassifier.Classify(s);
                if (kind is StringKind.Wnf or StringKind.Feature or StringKind.SubscribedContent)
                    found.Add(new Found(s, kind, start));
                continue;
            }
            HandleComplex(chars, start, found, null);
        }
    }
    /// <summary>Sequência com separadores: caminho de registro, caminho de CSP ou SubscribedContent.</summary>
    private static void HandleComplex(ReadOnlySpan<char> span, int offset, List<Found> found, List<(int, string)>? pathOffsets)
    {
        if (span.Contains('\\'))
        {
            var path = StringClassifier.ExtractRegistryPath(span);
            if (path is null) return;
            found.Add(new Found(path, StringClassifier.IsPolicyPath(path) ? StringKind.PolicyPath : StringKind.RegistryPath, offset));
            pathOffsets?.Add((offset, path));
            return;
        }
        if (span.Contains('/'))
        {
            var t = span.Trim();
            int idx = t.IndexOf("./", StringComparison.Ordinal);
            if (idx > 0) t = t[idx..];
            if (StringClassifier.IsCspPath(t) && t.Length <= StringClassifier.MaxStringLength)
                found.Add(new Found(t.ToString(), StringKind.CspPath, offset));
            return;
        }
        var trimmed = span.Trim();
        if (trimmed.StartsWith("SubscribedContent-"))
        {
            var s = trimmed.ToString();
            if (StringClassifier.Classify(s) == StringKind.SubscribedContent) found.Add(new Found(s, StringKind.SubscribedContent, offset));
        }
    }
}
