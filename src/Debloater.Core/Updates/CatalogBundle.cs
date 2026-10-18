using System.IO.Compression;
using System.Security.Cryptography;
using Debloater.Core.Catalog;

namespace Debloater.Core.Updates;

/// <summary>What goes into a bundle.</summary>
public sealed class CatalogPackOptions
{
    /// <summary>Catalog folder (YAML files, profiles/ and i18n/ included).</summary>
    public required string CatalogDirectory { get; init; }

    /// <summary>Reference folder (reference/*.json); null or missing = no reference files.</summary>
    public string? ReferenceDirectory { get; init; }

    public required CatalogVersion Version { get; init; }
    public string? GitCommit { get; init; }
    public string? MinAppVersion { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public int SchemaVersion { get; init; } = CatalogSchema.CurrentVersion;
}

/// <summary>A bundle whose signature and manifest hashes were checked; file contents are held in memory.</summary>
public sealed class VerifiedBundle
{
    public required CatalogManifest Manifest { get; init; }
    public required byte[] ManifestBytes { get; init; }
    public required TrustedKey Key { get; init; }
    public required IReadOnlyDictionary<string, byte[]> Files { get; init; }
    public CatalogVersion Version => Manifest.ParsedVersion;
}

/// <summary>
/// Catalog bundle: <c>catalog-&lt;version&gt;.zip</c> with the catalog tree (<c>catalog/**</c>), the Policy CSP
/// reference (<c>reference/*.json</c>) and <c>manifest.json</c> (version, schema, hashes), plus a detached
/// signature over the zip bytes (<see cref="CatalogSignature"/>).
/// </summary>
public static class CatalogBundle
{
    public const string SignatureExtension = ".sig";
    public const string TagPrefix = "catalog-v";
    public const string CatalogFolder = "catalog";
    public const string ReferenceFolder = "reference";

    /// <summary>Limits applied before trusting anything (zip size, total extracted size, entry count).</summary>
    public const long MaxZipSize = 64L * 1024 * 1024;
    public const long MaxTotalSize = 256L * 1024 * 1024;
    public const int MaxEntries = 5000;
    private const int MaxManifestSize = 4 * 1024 * 1024;

    public static string TagFor(string version) => TagPrefix + version;
    public static string ZipNameFor(string version) => $"catalog-{version}.zip";

    /// <summary>
    /// A path a bundle may contain: "catalog/..." (.yaml/.yml) or "reference/..." (.json), forward slashes,
    /// only [A-Za-z0-9._-] in each segment, no "." or ".." segments.
    /// </summary>
    public static bool IsAllowedPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 200) return false;
        var segments = path.Split('/');
        if (segments.Length < 2) return false;
        foreach (var s in segments)
        {
            if (s.Length == 0 || s == "." || s == "..") return false;
            if (!s.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')) return false;
        }
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return segments[0] switch
        {
            CatalogFolder => ext is ".yaml" or ".yml",
            ReferenceFolder => ext == ".json" && segments.Length == 2,
            _ => false,
        };
    }

    // ---------------- pack ----------------

    /// <summary>Files that would be packed: (bundle path, absolute path), sorted by bundle path.</summary>
    public static List<(string Path, string Source)> CollectFiles(string catalogDirectory, string? referenceDirectory)
    {
        var files = new List<(string, string)>();
        void Add(string folder, string root, IEnumerable<string> sources)
        {
            foreach (var f in sources)
            {
                var rel = folder + "/" + Path.GetRelativePath(root, f).Replace('\\', '/');
                if (!IsAllowedPath(rel)) throw new CatalogBundleException($"file name not allowed in a bundle: '{rel}'.");
                files.Add((rel, Path.GetFullPath(f)));
            }
        }
        Add(CatalogFolder, catalogDirectory, Directory.EnumerateFiles(catalogDirectory, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)));
        if (referenceDirectory is not null && Directory.Exists(referenceDirectory))
            Add(ReferenceFolder, referenceDirectory, Directory.EnumerateFiles(referenceDirectory, "*.json", SearchOption.TopDirectoryOnly));
        return [.. files.OrderBy(f => f.Item1, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Builds the zip in memory. The catalog must load without errors (the same check the updater runs
    /// before activating a bundle).
    /// </summary>
    public static (byte[] Zip, CatalogManifest Manifest) Pack(CatalogPackOptions options)
    {
        if (!Directory.Exists(options.CatalogDirectory))
            throw new CatalogBundleException($"catalog directory not found: {options.CatalogDirectory}");
        var load = CatalogLoader.LoadDirectory(options.CatalogDirectory);
        if (!load.IsValid)
            throw new CatalogBundleException($"the catalog has {load.Errors.Count} error(s); first: {load.Errors[0]}");

        var files = CollectFiles(options.CatalogDirectory, options.ReferenceDirectory);
        var manifest = new CatalogManifest
        {
            Version = options.Version.Text,
            SchemaVersion = options.SchemaVersion,
            MinAppVersion = options.MinAppVersion,
            CreatedUtc = options.CreatedUtc.ToUniversalTime(),
            GitCommit = options.GitCommit,
        };
        var contents = new List<(string Path, byte[] Data)>();
        foreach (var (path, source) in files)
        {
            var data = File.ReadAllBytes(source);
            contents.Add((path, data));
            manifest.Files.Add(new CatalogFileEntry(path, Convert.ToHexStringLower(SHA256.HashData(data)), data.LongLength));
        }
        return (Write(manifest.ToJsonBytes(), contents, manifest.CreatedUtc), manifest);
    }

    /// <summary>Writes a zip: manifest.json first, then the files (fixed order and timestamps).</summary>
    internal static byte[] Write(byte[] manifestJson, IEnumerable<(string Path, byte[] Data)> files, DateTimeOffset timestamp)
    {
        var stamp = timestamp.Year < 1981 ? new DateTimeOffset(1981, 1, 1, 0, 0, 0, TimeSpan.Zero) : timestamp;
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, byte[] data)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                entry.LastWriteTime = stamp;
                using var s = entry.Open();
                s.Write(data);
            }
            Add(CatalogManifest.FileName, manifestJson);
            foreach (var (path, data) in files) Add(path, data);
        }
        return ms.ToArray();
    }

    // ---------------- verify ----------------

    /// <summary>
    /// Checks the signature over the zip bytes against the trusted keys, then the manifest: every listed file
    /// present with the right size and SHA-256, no unlisted or disallowed entries. Throws
    /// <see cref="CatalogBundleException"/> on any problem. Does not check version or compatibility.
    /// </summary>
    public static VerifiedBundle Verify(byte[] zip, byte[] signature, IEnumerable<TrustedKey>? keys = null)
    {
        if (zip.LongLength > MaxZipSize) throw new CatalogBundleException("bundle too large.");
        var key = CatalogSignature.Verify(zip, signature, keys)
            ?? throw new CatalogBundleException("invalid signature: the bundle is not signed by a trusted key.");

        // From here on the bytes come from a trusted publisher; the checks below still guard against
        // packaging mistakes and keep extraction safe.
        using var archive = OpenZip(zip);
        // Case-insensitive: two entries differing only in case would collide on disk.
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in archive.Entries)
        {
            if (e.FullName.EndsWith('/')) continue; // directory entry
            if (e.FullName != CatalogManifest.FileName && !IsAllowedPath(e.FullName))
                throw new CatalogBundleException($"entry not allowed: '{e.FullName}'.");
            if (!entries.TryAdd(e.FullName, e))
                throw new CatalogBundleException($"duplicate entry '{e.FullName}'.");
        }
        if (entries.Count > MaxEntries) throw new CatalogBundleException("too many entries.");
        if (!entries.TryGetValue(CatalogManifest.FileName, out var manifestEntry))
            throw new CatalogBundleException("bundle without manifest.json.");

        var manifestBytes = ReadEntry(manifestEntry, MaxManifestSize);
        var manifest = CatalogManifest.Parse(manifestBytes);
        if (manifest.Files.Sum(f => f.Size) > MaxTotalSize) throw new CatalogBundleException("bundle content too large.");

        var listed = manifest.Files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var unlisted = entries.Values.Select(e => e.FullName).FirstOrDefault(k => k != CatalogManifest.FileName && !listed.Contains(k));
        if (unlisted is not null) throw new CatalogBundleException($"file not listed in the manifest: '{unlisted}'.");

        var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var f in manifest.Files)
        {
            if (!entries.TryGetValue(f.Path, out var entry)) throw new CatalogBundleException($"missing file '{f.Path}'.");
            var data = ReadEntry(entry, f.Size);
            if (data.LongLength != f.Size) throw new CatalogBundleException($"size mismatch for '{f.Path}'.");
            if (!Convert.ToHexStringLower(SHA256.HashData(data)).Equals(f.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new CatalogBundleException($"hash mismatch for '{f.Path}'.");
            contents[f.Path] = data;
        }
        return new VerifiedBundle { Manifest = manifest, ManifestBytes = manifestBytes, Key = key, Files = contents };
    }

    private static ZipArchive OpenZip(byte[] zip)
    {
        try
        {
            return new ZipArchive(new MemoryStream(zip, writable: false), ZipArchiveMode.Read);
        }
        catch (InvalidDataException ex)
        {
            throw new CatalogBundleException($"invalid zip: {ex.Message}");
        }
    }

    /// <summary>Reads an entry, refusing more than <paramref name="limit"/> bytes (zip bombs, lying headers).</summary>
    private static byte[] ReadEntry(ZipArchiveEntry entry, long limit)
    {
        try
        {
            using var s = entry.Open();
            using var ms = new MemoryStream();
            var buffer = new byte[81920];
            int n;
            while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (ms.Length + n > limit) throw new CatalogBundleException($"entry '{entry.FullName}' is larger than declared.");
                ms.Write(buffer, 0, n);
            }
            return ms.ToArray();
        }
        catch (InvalidDataException ex)
        {
            throw new CatalogBundleException($"corrupted entry '{entry.FullName}': {ex.Message}");
        }
    }

    // ---------------- extract ----------------

    /// <summary>Writes the verified files and manifest.json under <paramref name="directory"/> (must be empty or absent).</summary>
    public static void Extract(VerifiedBundle bundle, string directory)
    {
        var root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);
        if (Directory.EnumerateFileSystemEntries(root).Any()) throw new IOException($"target folder is not empty: {root}");
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var (path, data) in bundle.Files)
        {
            var target = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new CatalogBundleException($"path escapes the target folder: '{path}'.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, data);
        }
        File.WriteAllBytes(Path.Combine(root, CatalogManifest.FileName), bundle.ManifestBytes);
    }

    /// <summary>
    /// Checks an extracted folder against its manifest: every listed file with the right hash and no extra
    /// files under catalog/ or reference/ (an extra YAML would be loaded as catalog).
    /// </summary>
    public static string? CheckExtracted(string directory, CatalogManifest manifest)
    {
        var listed = new HashSet<string>(manifest.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var folder in new[] { CatalogFolder, ReferenceFolder })
        {
            var dir = Path.Combine(directory, folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(directory, file).Replace('\\', '/');
                if (!listed.Contains(rel)) return $"unexpected file '{rel}'.";
            }
        }
        foreach (var f in manifest.Files)
        {
            var path = Path.Combine(directory, f.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) return $"missing file '{f.Path}'.";
            byte[] hash;
            using (var s = File.OpenRead(path))
            {
                if (s.Length != f.Size) return $"size mismatch for '{f.Path}'.";
                hash = SHA256.HashData(s);
            }
            if (!Convert.ToHexStringLower(hash).Equals(f.Sha256, StringComparison.OrdinalIgnoreCase))
                return $"hash mismatch for '{f.Path}'.";
        }
        return null;
    }
}
