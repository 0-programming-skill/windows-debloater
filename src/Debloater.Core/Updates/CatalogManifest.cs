using System.Text.Json;
using System.Text.Json.Serialization;

namespace Debloater.Core.Updates;

/// <summary>A file of a catalog bundle: path relative to the bundle root ("catalog/x.yaml"), SHA-256 (hex) and size.</summary>
public sealed record CatalogFileEntry(string Path, string Sha256, long Size);

/// <summary>
/// manifest.json at the root of a catalog bundle. It is covered by the bundle signature (it lives inside the
/// signed zip), so its hashes authenticate every other file.
/// </summary>
public sealed class CatalogManifest
{
    public const string FileName = "manifest.json";

    /// <summary>Catalog version (see <see cref="CatalogVersion"/>).</summary>
    public string Version { get; set; } = "";

    /// <summary>Catalog schema the bundle was packed with (see <see cref="CatalogSchema"/>).</summary>
    public int SchemaVersion { get; set; }

    /// <summary>Lowest program (engine) version that may use this catalog, e.g. "1.0.0".</summary>
    public string? MinAppVersion { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public string? GitCommit { get; set; }

    public List<CatalogFileEntry> Files { get; set; } = [];

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public CatalogVersion ParsedVersion => CatalogVersion.Parse(Version);

    public byte[] ToJsonBytes() => JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);

    public static CatalogManifest Parse(ReadOnlySpan<byte> json)
    {
        CatalogManifest? m;
        try
        {
            m = JsonSerializer.Deserialize<CatalogManifest>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new CatalogBundleException($"invalid manifest: {ex.Message}");
        }
        if (m is null) throw new CatalogBundleException("empty manifest.");
        if (!CatalogVersion.TryParse(m.Version, out _)) throw new CatalogBundleException($"invalid manifest version '{m.Version}'.");
        if (m.SchemaVersion <= 0) throw new CatalogBundleException("manifest without schemaVersion.");
        if (m.Files.Count == 0) throw new CatalogBundleException("manifest without files.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in m.Files)
        {
            if (!CatalogBundle.IsAllowedPath(f.Path)) throw new CatalogBundleException($"manifest path not allowed: '{f.Path}'.");
            if (!seen.Add(f.Path)) throw new CatalogBundleException($"duplicate manifest path '{f.Path}'.");
            if (f.Sha256 is not { Length: 64 } || !f.Sha256.All(char.IsAsciiHexDigit)) throw new CatalogBundleException($"invalid hash for '{f.Path}'.");
            if (f.Size < 0) throw new CatalogBundleException($"invalid size for '{f.Path}'.");
        }
        return m;
    }
}

/// <summary>
/// Small pointer file published at a fixed URL (release "catalog-latest", asset latest.json) naming the newest
/// bundle. It is not signed: it only says which version to fetch; the bundle itself is verified, and an older
/// version than the active one is never installed, so a forged pointer can at most cause a failed check.
/// </summary>
public sealed class CatalogPointer
{
    public string Version { get; set; } = "";
    public int SchemaVersion { get; set; }
    public string? MinAppVersion { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public string? GitCommit { get; set; }

    /// <summary>Release tag holding the bundle ("catalog-v&lt;version&gt;").</summary>
    public string Tag { get; set; } = "";

    /// <summary>Asset names (validated against the version before use).</summary>
    public string Zip { get; set; } = "";
    public string Sig { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }

    public static CatalogPointer For(CatalogManifest manifest, byte[] zip) => new()
    {
        Version = manifest.Version,
        SchemaVersion = manifest.SchemaVersion,
        MinAppVersion = manifest.MinAppVersion,
        CreatedUtc = manifest.CreatedUtc,
        GitCommit = manifest.GitCommit,
        Tag = CatalogBundle.TagFor(manifest.Version),
        Zip = CatalogBundle.ZipNameFor(manifest.Version),
        Sig = CatalogBundle.ZipNameFor(manifest.Version) + CatalogBundle.SignatureExtension,
        Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(zip)),
        Size = zip.LongLength,
    };

    public byte[] ToJsonBytes() => JsonSerializer.SerializeToUtf8Bytes(this, CatalogManifest.JsonOptions);

    public static CatalogPointer Parse(ReadOnlySpan<byte> json)
    {
        CatalogPointer? p;
        try
        {
            p = JsonSerializer.Deserialize<CatalogPointer>(json, CatalogManifest.JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new CatalogBundleException($"invalid latest.json: {ex.Message}");
        }
        if (p is null || !CatalogVersion.TryParse(p.Version, out _)) throw new CatalogBundleException("latest.json without a valid version.");
        // Asset names are derived from the version; anything else is ignored rather than followed.
        p.Tag = CatalogBundle.TagFor(p.Version);
        p.Zip = CatalogBundle.ZipNameFor(p.Version);
        p.Sig = p.Zip + CatalogBundle.SignatureExtension;
        return p;
    }
}

public sealed class CatalogBundleException(string message) : Exception(message);
