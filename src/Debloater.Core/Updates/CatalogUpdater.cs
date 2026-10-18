using System.Net;
using System.Net.Http.Headers;

namespace Debloater.Core.Updates;

public enum CatalogUpdateOutcome
{
    /// <summary>The catalog in use is the newest published one (or newer).</summary>
    UpToDate,

    /// <summary>A newer catalog exists (check only).</summary>
    UpdateAvailable,

    /// <summary>A newer catalog was downloaded, verified and activated.</summary>
    Installed,

    /// <summary>The newest catalog needs a newer program (schema or minAppVersion).</summary>
    Incompatible,

    /// <summary>Automatic check: the newest catalog is one the user rolled back from.</summary>
    Skipped,

    /// <summary>Network error, missing release, bad signature... the current catalog stays.</summary>
    Failed,
}

public sealed record CatalogUpdateResult(
    CatalogUpdateOutcome Outcome,
    CatalogVersion Current,
    CatalogVersion? Latest,
    string? Message = null,
    InstallResult? Installed = null);

public sealed class CatalogUpdaterOptions
{
    /// <summary>Where releases are downloaded from: &lt;base&gt;/&lt;tag&gt;/&lt;asset&gt; (GitHub release download URLs).</summary>
    public Uri BaseUri { get; init; } = DefaultBaseUri;

    public static Uri DefaultBaseUri { get; } = new("https://github.com/0-programming-skill/windows-debloater/releases/download/");

    /// <summary>Release whose assets point to the newest bundle (replaced on every publish; never marked "latest").</summary>
    public const string PointerTag = "catalog-latest";

    public const string PointerAsset = "latest.json";

    /// <summary>Pointer for the newest bundle of one schema (lets older programs keep receiving compatible catalogs).</summary>
    public static string PointerAssetForSchema(int schema) => $"latest-schema{schema}.json";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>For tests: replaces the network.</summary>
    public HttpMessageHandler? Handler { get; init; }

    /// <summary>Minimum interval between automatic checks.</summary>
    public static TimeSpan AutoCheckInterval { get; } = TimeSpan.FromHours(24);
}

/// <summary>
/// Checks for and installs catalog bundles. Downloads latest.json, then catalog-&lt;v&gt;.zip and its .sig
/// from the versioned release, verifies the signature against the trusted keys and the manifest hashes,
/// checks compatibility and hands the bundle to <see cref="CatalogStore.Install"/>. Any failure keeps the
/// current catalog. Uses only anonymous downloads of release assets (no GitHub API, no rate limit).
/// </summary>
public sealed class CatalogUpdater
{
    private const int MaxPointerSize = 64 * 1024;
    private readonly CatalogStore _store;
    private readonly CatalogUpdaterOptions _options;

    public CatalogUpdater(CatalogStore store, CatalogUpdaterOptions? options = null)
    {
        _store = store;
        _options = options ?? new CatalogUpdaterOptions();
        if (_options.BaseUri.Scheme != Uri.UriSchemeHttps && _options.Handler is null)
            throw new ArgumentException("catalog updates must use HTTPS.");
    }

    /// <summary>Whether an automatic check is due (never checked, or the last one was 24 h ago or more).</summary>
    public static bool IsAutoCheckDue(CatalogUpdateState state, DateTimeOffset now) =>
        state.LastCheckUtc is not { } last || now - last >= CatalogUpdaterOptions.AutoCheckInterval || last > now;

    /// <summary>Only checks (downloads latest.json).</summary>
    public Task<CatalogUpdateResult> CheckAsync(CancellationToken ct = default) => RunAsync(install: false, automatic: false, ct);

    /// <summary>
    /// Checks and installs a newer compatible catalog. <paramref name="automatic"/> = a background check: a
    /// version the user rolled back from (or an older one) is skipped.
    /// </summary>
    public Task<CatalogUpdateResult> UpdateAsync(bool automatic = false, CancellationToken ct = default) => RunAsync(install: true, automatic, ct);

    private async Task<CatalogUpdateResult> RunAsync(bool install, bool automatic, CancellationToken ct)
    {
        var current = _store.EffectiveVersion();
        CatalogUpdateResult result;
        try
        {
            result = await RunCoreAsync(current, install, automatic, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Network, HTTP, signature, manifest, disk... any failure keeps the current catalog.
            var message = ex is TaskCanceledException ? "timed out." : ex.Message;
            result = new CatalogUpdateResult(CatalogUpdateOutcome.Failed, current, null, message);
        }
        Record(result);
        return result;
    }

    private async Task<CatalogUpdateResult> RunCoreAsync(CatalogVersion current, bool install, bool automatic, CancellationToken ct)
    {
        using var http = CreateClient();
        var pointer = await FetchPointerAsync(http, ct).ConfigureAwait(false);
        var latest = CatalogVersion.Parse(pointer.Version);

        if (latest <= current)
            return new CatalogUpdateResult(CatalogUpdateOutcome.UpToDate, current, latest);
        if (CatalogSchema.CheckCompatibility(pointer.SchemaVersion, pointer.MinAppVersion, _store.EngineVersion) is { } incompatible)
            return new CatalogUpdateResult(CatalogUpdateOutcome.Incompatible, current, latest, incompatible);
        if (automatic && _store.ReadActive().RolledBackFrom is { } rb && CatalogVersion.TryParse(rb, out var rolledBack) && latest <= rolledBack)
            return new CatalogUpdateResult(CatalogUpdateOutcome.Skipped, current, latest, $"catalog {latest} was rolled back by the user.");
        if (!install)
            return new CatalogUpdateResult(CatalogUpdateOutcome.UpdateAvailable, current, latest);

        var zip = await DownloadAsync(http, pointer.Tag, pointer.Zip, CatalogBundle.MaxZipSize, ct).ConfigureAwait(false);
        var sig = await DownloadAsync(http, pointer.Tag, pointer.Sig, CatalogSignature.MaxSignatureSize, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(pointer.Sha256)
            && !Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(zip)).Equals(pointer.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new CatalogBundleException("the downloaded bundle does not match latest.json (corrupted or replaced download).");

        var bundle = CatalogBundle.Verify(zip, sig, _store.TrustedKeys);
        if (!bundle.Version.Equals(latest))
            throw new CatalogBundleException($"the bundle is version {bundle.Manifest.Version}, latest.json says {latest}.");
        if (CatalogSchema.CheckCompatibility(bundle.Manifest.SchemaVersion, bundle.Manifest.MinAppVersion, _store.EngineVersion) is { } bundleIncompatible)
            return new CatalogUpdateResult(CatalogUpdateOutcome.Incompatible, current, latest, bundleIncompatible);

        var installed = _store.Install(bundle, zip, sig);
        return new CatalogUpdateResult(CatalogUpdateOutcome.Installed, current, latest, null, installed);
    }

    private HttpClient CreateClient()
    {
        var http = _options.Handler is { } h ? new HttpClient(h, disposeHandler: false) : new HttpClient();
        http.Timeout = _options.Timeout;
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WindowsDebloater", CatalogSchema.EngineVersion.ToString()));
        return http;
    }

    /// <summary>latest-schema&lt;N&gt;.json for this engine's schema, else latest.json.</summary>
    private async Task<CatalogPointer> FetchPointerAsync(HttpClient http, CancellationToken ct)
    {
        var schemaAsset = CatalogUpdaterOptions.PointerAssetForSchema(CatalogSchema.CurrentVersion);
        var bytes = await TryDownloadAsync(http, CatalogUpdaterOptions.PointerTag, schemaAsset, MaxPointerSize, ct).ConfigureAwait(false)
            ?? await DownloadAsync(http, CatalogUpdaterOptions.PointerTag, CatalogUpdaterOptions.PointerAsset, MaxPointerSize, ct).ConfigureAwait(false);
        return CatalogPointer.Parse(bytes);
    }

    private async Task<byte[]> DownloadAsync(HttpClient http, string tag, string asset, long limit, CancellationToken ct) =>
        await TryDownloadAsync(http, tag, asset, limit, ct).ConfigureAwait(false)
        ?? throw new CatalogBundleException($"not found: {tag}/{asset}.");

    /// <summary>Downloads a release asset (null on 404), refusing more than <paramref name="limit"/> bytes.</summary>
    private async Task<byte[]?> TryDownloadAsync(HttpClient http, string tag, string asset, long limit, CancellationToken ct)
    {
        var uri = new Uri(_options.BaseUri, $"{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(asset)}");
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.RequestMessage?.RequestUri is { } final && final.Scheme != Uri.UriSchemeHttps && _options.Handler is null)
            throw new CatalogBundleException("download redirected to a non-HTTPS address.");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{uri}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        if (response.Content.Headers.ContentLength is { } len && len > limit)
            throw new CatalogBundleException($"{asset} is too large ({len} bytes).");

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + n > limit) throw new CatalogBundleException($"{asset} is too large.");
            ms.Write(buffer, 0, n);
        }
        return ms.ToArray();
    }

    private void Record(CatalogUpdateResult result)
    {
        try
        {
            var state = _store.ReadState();
            state.LastCheckUtc = DateTimeOffset.UtcNow;
            state.LastOutcome = result.Outcome.ToString();
            state.LastMessage = result.Message;
            if (result.Latest is not null) state.LatestVersion = result.Latest.Text;
            _store.WriteState(state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The check result is still returned.
        }
    }
}
