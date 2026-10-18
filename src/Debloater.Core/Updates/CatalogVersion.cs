using System.Globalization;
using System.Reflection;

namespace Debloater.Core.Updates;

/// <summary>
/// Catalog version: 1 to 4 dot-separated non-negative integers, compared numerically. Published catalogs use
/// the UTC commit time of the last change under catalog/ or reference/: <c>yyyy.MM.dd.HHmmss</c>
/// (e.g. <c>2026.10.18.093512</c>). The text is kept as given (it names folders and release tags), so it is
/// restricted to digits and dots.
/// </summary>
public sealed class CatalogVersion : IComparable<CatalogVersion>, IEquatable<CatalogVersion>
{
    private readonly long[] _parts;

    private CatalogVersion(string text, long[] parts)
    {
        Text = text;
        _parts = parts;
    }

    /// <summary>The version as written (digits and dots only).</summary>
    public string Text { get; }

    /// <summary>Version 0: below any published catalog (used when the built-in version is unknown).</summary>
    public static CatalogVersion Zero { get; } = new("0", [0]);

    public static bool TryParse(string? text, out CatalogVersion version)
    {
        version = Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var pieces = text.Trim().Split('.');
        if (pieces.Length is < 1 or > 4) return false;
        var parts = new long[pieces.Length];
        for (int i = 0; i < pieces.Length; i++)
        {
            var p = pieces[i];
            if (p.Length is 0 or > 9 || !p.All(char.IsAsciiDigit)) return false;
            parts[i] = long.Parse(p, NumberStyles.None, CultureInfo.InvariantCulture);
        }
        version = new CatalogVersion(text.Trim(), parts);
        return true;
    }

    public static CatalogVersion Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException($"invalid catalog version '{text}' (expected 1 to 4 dot-separated numbers).");

    /// <summary>Version for a commit time: <c>yyyy.MM.dd.HHmmss</c> in UTC.</summary>
    public static CatalogVersion FromTimestamp(DateTimeOffset time) =>
        Parse(time.UtcDateTime.ToString("yyyy.MM.dd.HHmmss", CultureInfo.InvariantCulture));

    public int CompareTo(CatalogVersion? other)
    {
        if (other is null) return 1;
        int n = Math.Max(_parts.Length, other._parts.Length);
        for (int i = 0; i < n; i++)
        {
            long a = i < _parts.Length ? _parts[i] : 0;
            long b = i < other._parts.Length ? other._parts[i] : 0;
            if (a != b) return a.CompareTo(b);
        }
        return 0;
    }

    public bool Equals(CatalogVersion? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is CatalogVersion v && Equals(v);

    public override int GetHashCode()
    {
        // Trailing zeros do not change the value (1.2 == 1.2.0).
        int last = _parts.Length - 1;
        while (last > 0 && _parts[last] == 0) last--;
        var hash = new HashCode();
        for (int i = 0; i <= last; i++) hash.Add(_parts[i]);
        return hash.ToHashCode();
    }

    public override string ToString() => Text;

    public static bool operator <(CatalogVersion a, CatalogVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(CatalogVersion a, CatalogVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(CatalogVersion a, CatalogVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(CatalogVersion a, CatalogVersion b) => a.CompareTo(b) >= 0;

    // ---------------- built-in catalog ----------------

    /// <summary>
    /// Version of the catalog shipped next to the executable, computed at build time from the commit time of
    /// the last change under catalog/ or reference/ (assembly metadata "CatalogCommitTime"; see
    /// Debloater.Core.csproj). <see cref="Zero"/> when the build had no git history.
    /// </summary>
    public static CatalogVersion BuiltIn { get; } = ReadBuiltIn();

    /// <summary>Commit of the built-in catalog (null when unknown).</summary>
    public static string? BuiltInCommit { get; } =
        Metadata("CatalogCommit") is { Length: 40 } sha && sha.All(char.IsAsciiHexDigit) ? sha : null;

    private static CatalogVersion ReadBuiltIn()
    {
        var raw = Metadata("CatalogCommitTime");
        return long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? FromTimestamp(DateTimeOffset.FromUnixTimeSeconds(seconds))
            : Zero;
    }

    private static string? Metadata(string key)
    {
        var value = typeof(CatalogVersion).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
