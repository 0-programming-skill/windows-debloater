using System.Text.Json;
using Debloater.Core.Catalog;

namespace Debloater.Core.Updates;

public enum CatalogSource
{
    /// <summary>The catalog shipped next to the executable.</summary>
    BuiltIn,

    /// <summary>A verified downloaded bundle from the catalog store.</summary>
    Updated,

    /// <summary>A folder chosen by the user (settings or --catalog); not managed by the updater.</summary>
    Custom,
}

/// <summary>The catalog in use: where its files are and where they came from.</summary>
public sealed class ActiveCatalog
{
    public required CatalogSource Source { get; init; }

    /// <summary>Folder that holds catalog/ and reference/ (for Custom: the catalog folder's parent).</summary>
    public required string RootDirectory { get; init; }

    public required string CatalogDirectory { get; init; }

    public string ReferenceDirectory => Path.Combine(RootDirectory, CatalogBundle.ReferenceFolder);

    /// <summary>Version (Custom: null).</summary>
    public CatalogVersion? Version { get; init; }

    public string? GitCommit { get; init; }
    public DateTimeOffset? CreatedUtc { get; init; }

    /// <summary>Id of the key that signed the bundle (Updated only).</summary>
    public string? KeyId { get; init; }

    /// <summary>Why a newer installed catalog was not used (failed verification...); null when all is well.</summary>
    public string? Warning { get; init; }

    public static ActiveCatalog Custom(string catalogDirectory)
    {
        var full = Path.GetFullPath(catalogDirectory);
        return new ActiveCatalog
        {
            Source = CatalogSource.Custom,
            CatalogDirectory = full,
            RootDirectory = Path.GetDirectoryName(full.TrimEnd(Path.DirectorySeparatorChar)) ?? full,
        };
    }
}

/// <summary>active.json: which installed version is active (null = the built-in catalog).</summary>
public sealed class ActivePointer
{
    public string? Version { get; set; }

    /// <summary>Version active before this one (kept on disk for rollback).</summary>
    public string? Previous { get; set; }

    public DateTimeOffset? ActivatedUtc { get; set; }

    /// <summary>
    /// Set by "roll back to built-in": automatic checks do not reinstall this version or older ones (a manual
    /// update does).
    /// </summary>
    public string? RolledBackFrom { get; set; }
}

/// <summary>state.json: result of the last update check (shared by the app and the CLI).</summary>
public sealed class CatalogUpdateState
{
    public DateTimeOffset? LastCheckUtc { get; set; }
    public string? LastOutcome { get; set; }
    public string? LastMessage { get; set; }
    public string? LatestVersion { get; set; }
}

public sealed record InstallResult(CatalogVersion Version, string Directory, CatalogVersion? Previous);

/// <summary>
/// Installed catalog bundles, in %LocalAppData%\Debloater\catalog (the app normally runs without elevation):
/// <code>
/// active.json             pointer to the active version (atomic replace)
/// state.json              last update check
/// &lt;version&gt;\             catalog\, reference\, manifest.json, bundle.zip, bundle.zip.sig
/// </code>
/// Only the active and the previous version are kept. Every resolve re-verifies the active version (signature
/// of the stored zip, then the extracted files against its manifest) and falls back to the previous version
/// or the built-in catalog, never to anything older than the built-in one.
/// </summary>
public sealed class CatalogStore
{
    public const string ActiveFile = "active.json";
    public const string StateFile = "state.json";
    public const string BundleFile = "bundle.zip";
    public const string SignatureFile = "bundle.zip.sig";
    private const string LockFile = ".lock";
    private const string StagingPrefix = ".staging-";

    private readonly IReadOnlyList<TrustedKey>? _keys;

    public CatalogStore(string? root = null, string? builtInRoot = null, CatalogVersion? builtInVersion = null,
        IEnumerable<TrustedKey>? trustedKeys = null, Version? engineVersion = null)
    {
        Root = Path.GetFullPath(root ?? DefaultRoot);
        BuiltInRoot = Path.GetFullPath(builtInRoot ?? AppContext.BaseDirectory);
        BuiltInVersion = builtInVersion ?? CatalogVersion.BuiltIn;
        _keys = trustedKeys?.ToList();
        EngineVersion = engineVersion;
    }

    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Debloater", "catalog");

    public string Root { get; }
    public string BuiltInRoot { get; }
    public CatalogVersion BuiltInVersion { get; }
    public IEnumerable<TrustedKey> TrustedKeys => _keys ?? CatalogSignature.TrustedKeys;

    /// <summary>Engine version for minAppVersion checks (null = the real one).</summary>
    public Version? EngineVersion { get; }

    public string VersionDirectory(CatalogVersion v) => Path.Combine(Root, v.Text);

    public ActiveCatalog BuiltIn(string? warning = null) => new()
    {
        Source = CatalogSource.BuiltIn,
        RootDirectory = BuiltInRoot,
        CatalogDirectory = Path.Combine(BuiltInRoot, CatalogBundle.CatalogFolder),
        Version = BuiltInVersion,
        GitCommit = CatalogVersion.BuiltInCommit,
        Warning = warning,
    };

    // ---------------- pointer and state files ----------------

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public ActivePointer ReadActive() => ReadJson<ActivePointer>(ActiveFile) ?? new ActivePointer();

    public CatalogUpdateState ReadState() => ReadJson<CatalogUpdateState>(StateFile) ?? new CatalogUpdateState();

    public void WriteState(CatalogUpdateState state) => WriteJsonAtomic(StateFile, state);

    private void WriteActive(ActivePointer pointer) => WriteJsonAtomic(ActiveFile, pointer);

    private T? ReadJson<T>(string name) where T : class
    {
        try
        {
            var path = Path.Combine(Root, name);
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Write to a temporary file in the same folder, then replace (MoveFileEx REPLACE_EXISTING).</summary>
    private void WriteJsonAtomic<T>(string name, T value)
    {
        Directory.CreateDirectory(Root);
        var target = Path.Combine(Root, name);
        var tmp = Path.Combine(Root, $".{name}.{Guid.NewGuid():N}.tmp");
        using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            fs.Write(JsonSerializer.SerializeToUtf8Bytes(value, Json));
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, target, overwrite: true);
    }

    /// <summary>Exclusive lock over the store (app and CLI may run at the same time).</summary>
    public IDisposable Lock(TimeSpan? timeout = null)
    {
        Directory.CreateDirectory(Root);
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (true)
        {
            try
            {
                return new FileStream(Path.Combine(Root, LockFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(200);
            }
        }
    }

    // ---------------- installed versions ----------------

    public IReadOnlyList<CatalogVersion> InstalledVersions()
    {
        if (!Directory.Exists(Root)) return [];
        return Directory.EnumerateDirectories(Root)
            .Select(Path.GetFileName)
            .Select(n => CatalogVersion.TryParse(n, out var v) && v.Text == n ? v : null)
            .OfType<CatalogVersion>()
            .OrderDescending()
            .ToList();
    }

    /// <summary>
    /// Re-verifies an installed version: signature of the stored zip, manifest version, compatibility with this
    /// engine, then the extracted files against the manifest. Returns the bundle or the reason it is unusable.
    /// </summary>
    public (VerifiedBundle? Bundle, string? Error) VerifyInstalled(CatalogVersion version)
    {
        var dir = VersionDirectory(version);
        try
        {
            var zipPath = Path.Combine(dir, BundleFile);
            var sigPath = Path.Combine(dir, SignatureFile);
            if (!File.Exists(zipPath) || !File.Exists(sigPath)) return (null, $"catalog {version} is incomplete (bundle or signature missing).");
            if (new FileInfo(zipPath).Length > CatalogBundle.MaxZipSize || new FileInfo(sigPath).Length > CatalogSignature.MaxSignatureSize)
                return (null, $"catalog {version}: stored files too large.");
            var bundle = CatalogBundle.Verify(File.ReadAllBytes(zipPath), File.ReadAllBytes(sigPath), TrustedKeys);
            if (!bundle.Version.Equals(version)) return (null, $"catalog {version}: the manifest says {bundle.Manifest.Version}.");
            if (CatalogSchema.CheckCompatibility(bundle.Manifest.SchemaVersion, bundle.Manifest.MinAppVersion, EngineVersion) is { } incompatible)
                return (null, $"catalog {version}: {incompatible}");
            if (CatalogBundle.CheckExtracted(dir, bundle.Manifest) is { } mismatch)
                return (null, $"catalog {version} was modified on disk: {mismatch}");
            return (bundle, null);
        }
        catch (CatalogBundleException ex)
        {
            return (null, $"catalog {version}: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"catalog {version}: {ex.Message}");
        }
    }

    // ---------------- resolve ----------------

    /// <summary>
    /// The catalog to use: the active installed version when it is newer than the built-in one and verifies;
    /// otherwise the previous installed version under the same conditions; otherwise the built-in catalog.
    /// </summary>
    public ActiveCatalog Resolve()
    {
        var pointer = ReadActive();
        if (pointer.Version is null || !CatalogVersion.TryParse(pointer.Version, out var active)) return BuiltIn();
        if (active <= BuiltInVersion) return BuiltIn();

        var (bundle, error) = VerifyInstalled(active);
        if (bundle is not null) return Updated(bundle, null);

        if (pointer.Previous is not null && CatalogVersion.TryParse(pointer.Previous, out var previous) && previous > BuiltInVersion)
        {
            var (prevBundle, _) = VerifyInstalled(previous);
            if (prevBundle is not null) return Updated(prevBundle, error);
        }
        return BuiltIn(error);
    }

    private ActiveCatalog Updated(VerifiedBundle bundle, string? warning)
    {
        var dir = VersionDirectory(bundle.Version);
        return new ActiveCatalog
        {
            Source = CatalogSource.Updated,
            RootDirectory = dir,
            CatalogDirectory = Path.Combine(dir, CatalogBundle.CatalogFolder),
            Version = bundle.Version,
            GitCommit = bundle.Manifest.GitCommit,
            CreatedUtc = bundle.Manifest.CreatedUtc,
            KeyId = bundle.Key.Id,
            Warning = warning,
        };
    }

    /// <summary>Version currently in effect for update decisions: the active installed one or the built-in one.</summary>
    public CatalogVersion EffectiveVersion() => Resolve().Version ?? BuiltInVersion;

    // ---------------- install and rollback ----------------

    /// <summary>
    /// Installs a verified bundle and makes it active: compatibility and version checks, extraction to a
    /// staging folder, hash check of the written files, CatalogLoader validation, then a rename to
    /// &lt;version&gt; and an atomic switch of active.json. Keeps the previously active version for rollback
    /// and deletes the others.
    /// </summary>
    public InstallResult Install(VerifiedBundle bundle, byte[] zip, byte[] signature)
    {
        if (CatalogSchema.CheckCompatibility(bundle.Manifest.SchemaVersion, bundle.Manifest.MinAppVersion, EngineVersion) is { } incompatible)
            throw new CatalogBundleException(incompatible);

        using var _ = Lock();
        var version = bundle.Version;
        var current = Resolve();
        var currentVersion = current.Version ?? BuiltInVersion;
        if (version <= currentVersion)
            throw new CatalogBundleException($"catalog {version} is not newer than the one in use ({currentVersion}).");

        // The bytes on disk must be the verified ones.
        var check = CatalogBundle.Verify(zip, signature, TrustedKeys);
        if (!check.Version.Equals(version)) throw new CatalogBundleException("the bundle bytes do not match the verified bundle.");

        var staging = Path.Combine(Root, StagingPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            CatalogBundle.Extract(bundle, staging);
            File.WriteAllBytes(Path.Combine(staging, BundleFile), zip);
            File.WriteAllBytes(Path.Combine(staging, SignatureFile), signature);
            if (CatalogBundle.CheckExtracted(staging, bundle.Manifest) is { } mismatch)
                throw new CatalogBundleException($"extraction check failed: {mismatch}");

            var load = CatalogLoader.LoadDirectory(Path.Combine(staging, CatalogBundle.CatalogFolder));
            if (!load.IsValid)
                throw new CatalogBundleException($"the catalog does not load ({load.Errors.Count} error(s); first: {load.Errors[0]}).");
            if (load.Tweaks.Count == 0) throw new CatalogBundleException("the catalog has no tweaks.");

            var final = VersionDirectory(version);
            if (Directory.Exists(final)) Directory.Delete(final, recursive: true);
            Directory.Move(staging, final);

            var old = ReadActive();
            var previous = current.Source == CatalogSource.Updated ? current.Version : null;
            WriteActive(new ActivePointer
            {
                Version = version.Text,
                Previous = previous?.Text,
                ActivatedUtc = DateTimeOffset.UtcNow,
                RolledBackFrom = old.RolledBackFrom,
            });
            Prune(keep: [version, .. previous is null ? Array.Empty<CatalogVersion>() : [previous]]);
            return new InstallResult(version, final, previous);
        }
        finally
        {
            TryDelete(staging);
        }
    }

    /// <summary>Switches back to the built-in catalog. Installed versions stay on disk (the previous one for a later update).</summary>
    public void RollbackToBuiltIn()
    {
        using var _ = Lock();
        var old = ReadActive();
        WriteActive(new ActivePointer
        {
            Version = null,
            Previous = old.Version ?? old.Previous,
            ActivatedUtc = DateTimeOffset.UtcNow,
            RolledBackFrom = MaxText(old.Version, old.RolledBackFrom),
        });
    }

    /// <summary>Makes the previous installed version active again (it must still verify and be newer than the built-in one).</summary>
    public CatalogVersion RollbackToPrevious()
    {
        using var _ = Lock();
        var old = ReadActive();
        if (old.Previous is null || !CatalogVersion.TryParse(old.Previous, out var previous))
            throw new CatalogBundleException("there is no previous catalog version.");
        if (previous <= BuiltInVersion)
            throw new CatalogBundleException($"the previous catalog ({previous}) is not newer than the built-in one ({BuiltInVersion}).");
        var (bundle, error) = VerifyInstalled(previous);
        if (bundle is null) throw new CatalogBundleException(error ?? "the previous catalog does not verify.");
        WriteActive(new ActivePointer
        {
            Version = previous.Text,
            Previous = old.Version,
            ActivatedUtc = DateTimeOffset.UtcNow,
            RolledBackFrom = MaxText(old.Version, old.RolledBackFrom),
        });
        return previous;
    }

    private static string? MaxText(string? a, string? b)
    {
        CatalogVersion.TryParse(a, out var va);
        CatalogVersion.TryParse(b, out var vb);
        if (a is null) return b;
        if (b is null) return a;
        return va >= vb ? a : b;
    }

    private void Prune(IReadOnlyCollection<CatalogVersion> keep)
    {
        foreach (var v in InstalledVersions().Where(v => !keep.Contains(v))) TryDelete(VersionDirectory(v));
        foreach (var dir in Directory.EnumerateDirectories(Root, StagingPrefix + "*")) TryDelete(dir);
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next prune.
        }
    }
}
