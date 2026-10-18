using System.Net;
using System.Security.Cryptography;
using System.Text;
using Debloater.Core.Updates;

namespace Debloater.Core.Tests;

/// <summary>Temporary folders with a tiny valid catalog, and a test signing key (never the real one).</summary>
internal sealed class UpdateSandbox : IDisposable
{
    public const string TweakYaml = """
        category: Test/Updates
        tweaks:
          - id: test.updates.sample
            name: Sample
            description: Sample tweak for update tests.
            risk: low
            actions:
              - type: registry
                path: HKCU\Software\DebloaterUpdateTests
                name: Sample
                kind: dword
                value: 1
        """;

    public UpdateSandbox()
    {
        Root = Path.Combine(Path.GetTempPath(), "debloater-update-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Trusted = new TrustedKey("test", Key.ExportSubjectPublicKeyInfoPem());
        BuiltInRoot = Path.Combine(Root, "app");
        WriteCatalog(Path.Combine(BuiltInRoot, "catalog"), "built-in");
        Directory.CreateDirectory(Path.Combine(BuiltInRoot, "reference"));
        File.WriteAllText(Path.Combine(BuiltInRoot, "reference", "msdocs-policy-csp.json"), "{}");
        StoreRoot = Path.Combine(Root, "store");
    }

    public string Root { get; }
    public string BuiltInRoot { get; }
    public string StoreRoot { get; }
    public ECDsa Key { get; }
    public TrustedKey Trusted { get; }

    public static readonly CatalogVersion BuiltInVersion = CatalogVersion.Parse("2026.01.01.000000");

    public CatalogStore Store(Version? engine = null) =>
        new(StoreRoot, BuiltInRoot, BuiltInVersion, [Trusted], engine);

    public static void WriteCatalog(string dir, string marker)
    {
        Directory.CreateDirectory(Path.Combine(dir, "profiles"));
        File.WriteAllText(Path.Combine(dir, "test.yaml"), TweakYaml + $"\n# {marker}\n");
        File.WriteAllText(Path.Combine(dir, "profiles", "safe.yaml"), "name: Safe\ndescription: Test profile.\nrules:\n  maxRisk: low\n");
    }

    /// <summary>Packs and signs a bundle of version <paramref name="version"/>.</summary>
    public (byte[] Zip, byte[] Sig, CatalogManifest Manifest) Bundle(string version, int schema = CatalogSchema.CurrentVersion,
        string? minApp = "1.0.0", ECDsa? signer = null)
    {
        var src = Path.Combine(Root, "src-" + version);
        if (!Directory.Exists(src))
        {
            WriteCatalog(Path.Combine(src, "catalog"), version);
            Directory.CreateDirectory(Path.Combine(src, "reference"));
            File.WriteAllText(Path.Combine(src, "reference", "msdocs-policy-csp.json"), "{\"v\":\"" + version + "\"}");
        }
        var (zip, manifest) = CatalogBundle.Pack(new CatalogPackOptions
        {
            CatalogDirectory = Path.Combine(src, "catalog"),
            ReferenceDirectory = Path.Combine(src, "reference"),
            Version = CatalogVersion.Parse(version),
            GitCommit = new string('a', 40),
            MinAppVersion = minApp,
            CreatedUtc = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero),
            SchemaVersion = schema,
        });
        return (zip, CatalogSignature.Sign(zip, signer ?? Key), manifest);
    }

    public InstallResult Install(CatalogStore store, string version)
    {
        var (zip, sig, _) = Bundle(version);
        return store.Install(CatalogBundle.Verify(zip, sig, [Trusted]), zip, sig);
    }

    public void Dispose()
    {
        Key.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public class CatalogVersionTests
{
    [Theory]
    [InlineData("1", "2", -1)]
    [InlineData("2026.10.18.093512", "2026.10.18.93512", 0)]
    [InlineData("2026.10.18.1", "2026.10.18", 1)]
    [InlineData("1.2", "1.2.0.0", 0)]
    [InlineData("2026.9.30.235959", "2026.10.1.000000", -1)]
    [InlineData("10", "9", 1)]
    public void Compares_numerically(string a, string b, int expected)
    {
        Assert.Equal(expected, Math.Sign(CatalogVersion.Parse(a).CompareTo(CatalogVersion.Parse(b))));
        if (expected == 0) Assert.Equal(CatalogVersion.Parse(a).GetHashCode(), CatalogVersion.Parse(b).GetHashCode());
    }

    [Theory]
    [InlineData("")]
    [InlineData("a.b")]
    [InlineData("1..2")]
    [InlineData("1.2.3.4.5")]
    [InlineData("..\\x")]
    [InlineData("1.-2")]
    [InlineData("1 .2")]
    [InlineData("1234567890")]
    public void Rejects_invalid_text(string text) => Assert.False(CatalogVersion.TryParse(text, out _));

    [Fact]
    public void Timestamp_version_is_utc_and_sortable()
    {
        var v = CatalogVersion.FromTimestamp(new DateTimeOffset(2026, 10, 18, 6, 31, 4, TimeSpan.FromHours(-3)));
        Assert.Equal("2026.10.18.093104", v.Text);
        Assert.True(v < CatalogVersion.FromTimestamp(new DateTimeOffset(2026, 10, 18, 9, 31, 5, TimeSpan.Zero)));
        Assert.True(CatalogVersion.Zero < v);
    }

    [Fact]
    public void Built_in_version_comes_from_git_metadata()
    {
        // The test build runs inside the repository, so the catalog commit time is known.
        Assert.True(CatalogVersion.BuiltIn > CatalogVersion.Parse("2026"));
        Assert.NotNull(CatalogVersion.BuiltInCommit);
    }
}

public class CatalogCompatibilityTests
{
    private static readonly Version Engine = new(1, 2, 0);

    [Theory]
    [InlineData(1, "1.0.0", null)]
    [InlineData(1, null, null)]
    [InlineData(1, "1.2", null)]
    [InlineData(2, "1.0.0", "newer")]
    [InlineData(0, "1.0.0", "older")]
    [InlineData(1, "1.2.1", "requires program version")]
    [InlineData(1, "2.0", "requires program version")]
    [InlineData(1, "x", "invalid minAppVersion")]
    public void Schema_and_min_app_version(int schema, string? minApp, string? expectedError)
    {
        var error = CatalogSchema.CheckCompatibility(schema, minApp, Engine, minSchema: 1, maxSchema: 1);
        if (expectedError is null) Assert.Null(error);
        else Assert.Contains(expectedError, error);
    }
}

public class CatalogBundleTests
{
    [Fact]
    public void Valid_bundle_verifies()
    {
        using var sb = new UpdateSandbox();
        var (zip, sig, manifest) = sb.Bundle("2026.10.18.1");
        var bundle = CatalogBundle.Verify(zip, sig, [sb.Trusted]);
        Assert.Equal("2026.10.18.1", bundle.Manifest.Version);
        Assert.Equal(CatalogSchema.CurrentVersion, bundle.Manifest.SchemaVersion);
        Assert.Contains(bundle.Files.Keys, k => k == "catalog/test.yaml");
        Assert.Contains(bundle.Files.Keys, k => k == "catalog/profiles/safe.yaml");
        Assert.Contains(bundle.Files.Keys, k => k == "reference/msdocs-policy-csp.json");
        Assert.Equal(manifest.Files.Count, bundle.Files.Count);
        Assert.Equal(sb.Trusted.Id, bundle.Key.Id);
    }

    [Fact]
    public void Tampered_zip_fails()
    {
        using var sb = new UpdateSandbox();
        var (zip, sig, _) = sb.Bundle("2026.10.18.1");
        zip[zip.Length / 2] ^= 0x01;
        var ex = Assert.Throws<CatalogBundleException>(() => CatalogBundle.Verify(zip, sig, [sb.Trusted]));
        Assert.Contains("signature", ex.Message);
    }

    [Fact]
    public void Wrong_key_fails_and_any_trusted_key_works()
    {
        using var sb = new UpdateSandbox();
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (zip, sig, _) = sb.Bundle("2026.10.18.1", signer: other);
        Assert.Throws<CatalogBundleException>(() => CatalogBundle.Verify(zip, sig, [sb.Trusted]));

        // Rotation: with both keys trusted, the bundle signed by the new key verifies.
        var rotated = new TrustedKey("next", other.ExportSubjectPublicKeyInfoPem());
        Assert.Equal(rotated.Id, CatalogBundle.Verify(zip, sig, [sb.Trusted, rotated]).Key.Id);
    }

    [Fact]
    public void Garbage_signatures_fail()
    {
        using var sb = new UpdateSandbox();
        var (zip, _, _) = sb.Bundle("2026.10.18.1");
        Assert.Null(CatalogSignature.Verify(zip, [], [sb.Trusted]));
        Assert.Null(CatalogSignature.Verify(zip, new byte[64], [sb.Trusted]));
        Assert.Null(CatalogSignature.Verify(zip, new byte[4096], [sb.Trusted]));
    }

    [Fact]
    public void Real_trusted_keys_are_valid_p256()
    {
        Assert.NotEmpty(CatalogSignature.TrustedKeys);
        foreach (var key in CatalogSignature.TrustedKeys)
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(key.PublicKeyPem);
            Assert.Equal(256, ecdsa.KeySize);
            Assert.Throws<CryptographicException>(() => ecdsa.ExportParameters(includePrivateParameters: true));
            Assert.NotEqual("invalid", key.Id);
        }
    }

    /// <summary>Signed by the trusted key but with a manifest that does not match the content.</summary>
    private static (byte[] Zip, byte[] Sig) Forge(UpdateSandbox sb, Action<CatalogManifest, List<(string Path, byte[] Data)>> mutate)
    {
        var data = Encoding.UTF8.GetBytes(UpdateSandbox.TweakYaml);
        var manifest = new CatalogManifest
        {
            Version = "2026.10.18.5",
            SchemaVersion = 1,
            CreatedUtc = DateTimeOffset.UtcNow,
            Files = [new CatalogFileEntry("catalog/test.yaml", Convert.ToHexStringLower(SHA256.HashData(data)), data.Length)],
        };
        var files = new List<(string, byte[])> { ("catalog/test.yaml", data) };
        mutate(manifest, files);
        var zip = CatalogBundle.Write(manifest.ToJsonBytes(), files, DateTimeOffset.UtcNow);
        return (zip, CatalogSignature.Sign(zip, sb.Key));
    }

    [Fact]
    public void Tampered_manifest_hash_fails()
    {
        using var sb = new UpdateSandbox();
        var (zip, sig) = Forge(sb, (m, _) => m.Files[0] = m.Files[0] with { Sha256 = new string('0', 64) });
        Assert.Contains("hash mismatch", Assert.Throws<CatalogBundleException>(() => CatalogBundle.Verify(zip, sig, [sb.Trusted])).Message);
    }

    [Fact]
    public void Size_mismatch_fails()
    {
        using var sb = new UpdateSandbox();
        var (zip, sig) = Forge(sb, (m, _) => m.Files[0] = m.Files[0] with { Size = m.Files[0].Size - 1 });
        Assert.Throws<CatalogBundleException>(() => CatalogBundle.Verify(zip, sig, [sb.Trusted]));
    }

    [Fact]
    public void Unlisted_entry_fails()
    {
        using var sb = new UpdateSandbox();
        var (zip, sig) = Forge(sb, (_, files) => files.Add(("catalog/extra.yaml", Encoding.UTF8.GetBytes(UpdateSandbox.TweakYaml))));
        Assert.Contains("not listed", Assert.Throws<CatalogBundleException>(() => CatalogBundle.Verify(zip, sig, [sb.Trusted])).Message);
    }

    [Fact]
    public void Missing_file_fails()
    {
        using var sb = new UpdateSandbox();
        var (zip, sig) = Forge(sb, (_, files) => files.Clear());
        Assert.Contains("missing", Assert.Throws<CatalogBundleException>(() => CatalogBundle.Verify(zip, sig, [sb.Trusted])).Message);
    }

    [Fact]
    public void Path_traversal_entry_fails()
    {
        using var sb = new UpdateSandbox();
        var (zip, sig) = Forge(sb, (_, files) => files.Add(("catalog/../evil.yaml", [1])));
        Assert.Contains("not allowed", Assert.Throws<CatalogBundleException>(() => CatalogBundle.Verify(zip, sig, [sb.Trusted])).Message);
    }

    [Theory]
    [InlineData("catalog/a.yaml", true)]
    [InlineData("catalog/profiles/safe.yaml", true)]
    [InlineData("catalog/i18n/categories.yml", true)]
    [InlineData("reference/msdocs-policy-csp.json", true)]
    [InlineData("reference/sub/x.json", false)]
    [InlineData("catalog/a.json", false)]
    [InlineData("catalog/../a.yaml", false)]
    [InlineData("/catalog/a.yaml", false)]
    [InlineData("catalog\\a.yaml", false)]
    [InlineData("catalog/a b.yaml", false)]
    [InlineData("C:/catalog/a.yaml", false)]
    [InlineData("other/a.yaml", false)]
    [InlineData("manifest.json", false)]
    public void Allowed_paths(string path, bool allowed) => Assert.Equal(allowed, CatalogBundle.IsAllowedPath(path));

    [Fact]
    public void Pack_refuses_an_invalid_catalog()
    {
        using var sb = new UpdateSandbox();
        var dir = Path.Combine(sb.Root, "bad");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "bad.yaml"), "tweaks:\n  - id: Not Valid\n");
        Assert.Throws<CatalogBundleException>(() => CatalogBundle.Pack(new CatalogPackOptions { CatalogDirectory = dir, Version = CatalogVersion.Parse("1") }));
    }

    [Fact]
    public void Pointer_derives_asset_names_from_the_version()
    {
        var json = """{"version":"2026.10.18.1","schemaVersion":1,"tag":"evil","zip":"../../x.zip","sig":"y"}"""u8;
        var p = CatalogPointer.Parse(json);
        Assert.Equal("catalog-v2026.10.18.1", p.Tag);
        Assert.Equal("catalog-2026.10.18.1.zip", p.Zip);
        Assert.Equal("catalog-2026.10.18.1.zip.sig", p.Sig);
        Assert.Throws<CatalogBundleException>(() => CatalogPointer.Parse("""{"version":"../1"}"""u8));
    }
}

public class CatalogStoreTests
{
    [Fact]
    public void Without_updates_the_built_in_catalog_is_used()
    {
        using var sb = new UpdateSandbox();
        var active = sb.Store().Resolve();
        Assert.Equal(CatalogSource.BuiltIn, active.Source);
        Assert.Equal(Path.Combine(sb.BuiltInRoot, "catalog"), active.CatalogDirectory);
        Assert.Null(active.Warning);
    }

    [Fact]
    public void Install_switches_the_active_pointer_and_keeps_the_previous_version()
    {
        using var sb = new UpdateSandbox();
        var store = sb.Store();

        var first = sb.Install(store, "2026.02.01.000000");
        Assert.Null(first.Previous);
        var active = store.Resolve();
        Assert.Equal(CatalogSource.Updated, active.Source);
        Assert.Equal("2026.02.01.000000", active.Version!.Text);
        Assert.True(File.Exists(Path.Combine(active.CatalogDirectory, "test.yaml")));
        Assert.True(File.Exists(Path.Combine(active.ReferenceDirectory, "msdocs-policy-csp.json")));
        Assert.Equal(sb.Trusted.Id, active.KeyId);

        sb.Install(store, "2026.03.01.000000");
        var third = sb.Install(store, "2026.04.01.000000");
        Assert.Equal("2026.03.01.000000", third.Previous!.Text);
        Assert.Equal("2026.04.01.000000", store.Resolve().Version!.Text);
        // Only the active and the previous version stay on disk.
        Assert.Equal(["2026.04.01.000000", "2026.03.01.000000"], store.InstalledVersions().Select(v => v.Text));
        Assert.Empty(Directory.EnumerateDirectories(sb.StoreRoot, ".staging-*"));
    }

    [Fact]
    public void Older_or_same_versions_are_refused()
    {
        using var sb = new UpdateSandbox();
        var store = sb.Store();
        sb.Install(store, "2026.03.01.000000");
        Assert.Throws<CatalogBundleException>(() => sb.Install(store, "2026.03.01.000000"));
        Assert.Throws<CatalogBundleException>(() => sb.Install(store, "2026.02.01.000000"));
        // Not newer than the built-in catalog either.
        using var sb2 = new UpdateSandbox();
        Assert.Throws<CatalogBundleException>(() => sb2.Install(sb2.Store(), "2025.12.31.000000"));
    }

    [Fact]
    public void Incompatible_bundles_are_refused()
    {
        using var sb = new UpdateSandbox();
        var store = sb.Store(engine: new Version(1, 0));
        var (zip, sig, _) = sb.Bundle("2026.05.01.000000", minApp: "9.0");
        Assert.Throws<CatalogBundleException>(() => store.Install(CatalogBundle.Verify(zip, sig, [sb.Trusted]), zip, sig));
        (zip, sig, _) = sb.Bundle("2026.05.02.000000", schema: CatalogSchema.CurrentVersion + 1);
        Assert.Throws<CatalogBundleException>(() => store.Install(CatalogBundle.Verify(zip, sig, [sb.Trusted]), zip, sig));
        Assert.Equal(CatalogSource.BuiltIn, store.Resolve().Source);
    }

    [Fact]
    public void Rollback_to_built_in_and_to_previous()
    {
        using var sb = new UpdateSandbox();
        var store = sb.Store();
        sb.Install(store, "2026.02.01.000000");
        sb.Install(store, "2026.03.01.000000");

        store.RollbackToBuiltIn();
        Assert.Equal(CatalogSource.BuiltIn, store.Resolve().Source);
        var pointer = store.ReadActive();
        Assert.Null(pointer.Version);
        Assert.Equal("2026.03.01.000000", pointer.RolledBackFrom);
        // Installed versions stay on disk.
        Assert.Equal(2, store.InstalledVersions().Count);

        // From the built-in catalog, "previous" is the version rolled back from.
        Assert.Equal("2026.03.01.000000", store.RollbackToPrevious().Text);
        Assert.Equal("2026.03.01.000000", store.Resolve().Version!.Text);

        // Between updates: 3 -> 2 -> 3.
        using var sb2 = new UpdateSandbox();
        var store2 = sb2.Store();
        sb2.Install(store2, "2026.02.01.000000");
        sb2.Install(store2, "2026.03.01.000000");
        Assert.Equal("2026.02.01.000000", store2.RollbackToPrevious().Text);
        Assert.Equal("2026.02.01.000000", store2.Resolve().Version!.Text);
        Assert.Equal("2026.03.01.000000", store2.RollbackToPrevious().Text);
        Assert.Equal("2026.03.01.000000", store2.Resolve().Version!.Text);
    }

    [Fact]
    public void Modified_files_fall_back_to_the_previous_version_then_built_in()
    {
        using var sb = new UpdateSandbox();
        var store = sb.Store();
        sb.Install(store, "2026.02.01.000000");
        var latest = sb.Install(store, "2026.03.01.000000");

        File.AppendAllText(Path.Combine(latest.Directory, "catalog", "test.yaml"), "\n# changed\n");
        var active = store.Resolve();
        Assert.Equal("2026.02.01.000000", active.Version!.Text);
        Assert.Contains("modified", active.Warning);

        File.WriteAllText(Path.Combine(store.VersionDirectory(CatalogVersion.Parse("2026.02.01.000000")), "catalog", "extra.yaml"), UpdateSandbox.TweakYaml);
        active = store.Resolve();
        Assert.Equal(CatalogSource.BuiltIn, active.Source);
        Assert.NotNull(active.Warning);
    }

    [Fact]
    public void Untrusted_store_signature_is_rejected_at_resolve()
    {
        using var sb = new UpdateSandbox();
        sb.Install(sb.Store(), "2026.02.01.000000");
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var otherStore = new CatalogStore(sb.StoreRoot, sb.BuiltInRoot, UpdateSandbox.BuiltInVersion, [new TrustedKey("other", other.ExportSubjectPublicKeyInfoPem())]);
        var active = otherStore.Resolve();
        Assert.Equal(CatalogSource.BuiltIn, active.Source);
        Assert.Contains("signature", active.Warning);
    }

    [Fact]
    public void A_newer_built_in_catalog_wins()
    {
        using var sb = new UpdateSandbox();
        sb.Install(sb.Store(), "2026.02.01.000000");
        var newerApp = new CatalogStore(sb.StoreRoot, sb.BuiltInRoot, CatalogVersion.Parse("2026.06.01.000000"), [sb.Trusted]);
        var active = newerApp.Resolve();
        Assert.Equal(CatalogSource.BuiltIn, active.Source);
        Assert.Null(active.Warning);
    }

    [Fact]
    public void Corrupted_active_pointer_means_built_in()
    {
        using var sb = new UpdateSandbox();
        var store = sb.Store();
        sb.Install(store, "2026.02.01.000000");
        File.WriteAllText(Path.Combine(sb.StoreRoot, CatalogStore.ActiveFile), "{ not json");
        Assert.Equal(CatalogSource.BuiltIn, store.Resolve().Source);
        File.WriteAllText(Path.Combine(sb.StoreRoot, CatalogStore.ActiveFile), """{"version":"..\\..\\x"}""");
        Assert.Equal(CatalogSource.BuiltIn, store.Resolve().Source);
    }
}

public class CatalogUpdaterTests
{
    /// <summary>Serves release assets from a dictionary "tag/asset" → bytes (404 otherwise).</summary>
    private sealed class FakeReleases : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Assets { get; } = new(StringComparer.Ordinal);
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath.TrimStart('/'));
            path = path["releases/download/".Length..];
            Requests.Add(path);
            return Task.FromResult(Assets.TryGetValue(path, out var data)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        public void Publish(UpdateSandbox sb, string version, bool schemaPointer = false, Action<CatalogPointer>? editPointer = null)
        {
            var (zip, sig, manifest) = sb.Bundle(version);
            var tag = CatalogBundle.TagFor(version);
            Assets[$"{tag}/{CatalogBundle.ZipNameFor(version)}"] = zip;
            Assets[$"{tag}/{CatalogBundle.ZipNameFor(version)}.sig"] = sig;
            var pointer = CatalogPointer.For(manifest, zip);
            editPointer?.Invoke(pointer);
            var name = schemaPointer ? CatalogUpdaterOptions.PointerAssetForSchema(CatalogSchema.CurrentVersion) : CatalogUpdaterOptions.PointerAsset;
            Assets[$"{CatalogUpdaterOptions.PointerTag}/{name}"] = pointer.ToJsonBytes();
        }
    }

    private static CatalogUpdater Updater(CatalogStore store, FakeReleases releases) => new(store, new CatalogUpdaterOptions
    {
        BaseUri = new Uri("https://example.invalid/releases/download/"),
        Handler = releases,
    });

    [Fact]
    public async Task Check_install_and_up_to_date()
    {
        using var sb = new UpdateSandbox();
        var store = sb.Store();
        var releases = new FakeReleases();
        releases.Publish(sb, "2026.02.01.000000");
        var updater = Updater(store, releases);

        var check = await updater.CheckAsync();
        Assert.Equal(CatalogUpdateOutcome.UpdateAvailable, check.Outcome);
        Assert.Equal(CatalogSource.BuiltIn, store.Resolve().Source);
        // The per-schema pointer is tried first, then latest.json.
        Assert.Equal($"catalog-latest/latest-schema{CatalogSchema.CurrentVersion}.json", releases.Requests[0]);

        var update = await updater.UpdateAsync();
        Assert.Equal(CatalogUpdateOutcome.Installed, update.Outcome);
        Assert.Equal("2026.02.01.000000", store.Resolve().Version!.Text);

        Assert.Equal(CatalogUpdateOutcome.UpToDate, (await updater.UpdateAsync()).Outcome);
        var state = store.ReadState();
        Assert.Equal(nameof(CatalogUpdateOutcome.UpToDate), state.LastOutcome);
        Assert.NotNull(state.LastCheckUtc);
    }

    [Fact]
    public async Task Bad_signature_or_offline_keeps_the_current_catalog()
    {
        using var sb = new UpdateSandbox();
        var store = sb.Store();
        var releases = new FakeReleases();
        var updater = Updater(store, releases);

        // No release at all (404).
        Assert.Equal(CatalogUpdateOutcome.Failed, (await updater.UpdateAsync()).Outcome);

        releases.Publish(sb, "2026.02.01.000000");
        var sigKey = releases.Assets.Keys.Single(k => k.EndsWith(".sig", StringComparison.Ordinal));
        using (var other = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            releases.Assets[sigKey] = CatalogSignature.Sign(releases.Assets[sigKey.Replace(".sig", "")], other);
        var r = await updater.UpdateAsync();
        Assert.Equal(CatalogUpdateOutcome.Failed, r.Outcome);
        Assert.Contains("signature", r.Message);
        Assert.Equal(CatalogSource.BuiltIn, store.Resolve().Source);
        Assert.Empty(store.InstalledVersions());
    }

    [Fact]
    public async Task Replaced_zip_is_detected()
    {
        using var sb = new UpdateSandbox();
        var store = sb.Store();
        var releases = new FakeReleases();
        releases.Publish(sb, "2026.02.01.000000", editPointer: p => p.Sha256 = new string('1', 64));
        var r = await Updater(store, releases).UpdateAsync();
        Assert.Equal(CatalogUpdateOutcome.Failed, r.Outcome);
        Assert.Equal(CatalogSource.BuiltIn, store.Resolve().Source);
    }

    [Fact]
    public async Task Incompatible_latest_is_not_downloaded()
    {
        using var sb = new UpdateSandbox();
        var store = sb.Store();
        var releases = new FakeReleases();
        releases.Publish(sb, "2026.02.01.000000", editPointer: p => p.SchemaVersion = CatalogSchema.CurrentVersion + 1);
        var r = await Updater(store, releases).UpdateAsync();
        Assert.Equal(CatalogUpdateOutcome.Incompatible, r.Outcome);
        Assert.DoesNotContain(releases.Requests, q => q.EndsWith(".zip", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Automatic_check_skips_a_rolled_back_version_but_manual_update_installs_it()
    {
        using var sb = new UpdateSandbox();
        var store = sb.Store();
        var releases = new FakeReleases();
        releases.Publish(sb, "2026.02.01.000000", schemaPointer: true);
        var updater = Updater(store, releases);
        Assert.Equal(CatalogUpdateOutcome.Installed, (await updater.UpdateAsync(automatic: true)).Outcome);

        store.RollbackToBuiltIn();
        Assert.Equal(CatalogUpdateOutcome.Skipped, (await updater.UpdateAsync(automatic: true)).Outcome);
        Assert.Equal(CatalogSource.BuiltIn, store.Resolve().Source);

        // A newer version is installed automatically again.
        releases.Publish(sb, "2026.03.01.000000", schemaPointer: true);
        Assert.Equal(CatalogUpdateOutcome.Installed, (await updater.UpdateAsync(automatic: true)).Outcome);
        Assert.Equal("2026.03.01.000000", store.Resolve().Version!.Text);
    }

    [Fact]
    public async Task Manual_update_reinstalls_after_rollback()
    {
        using var sb = new UpdateSandbox();
        var store = sb.Store();
        var releases = new FakeReleases();
        releases.Publish(sb, "2026.02.01.000000");
        var updater = Updater(store, releases);
        await updater.UpdateAsync();
        store.RollbackToBuiltIn();
        Assert.Equal(CatalogUpdateOutcome.Installed, (await updater.UpdateAsync(automatic: false)).Outcome);
        Assert.Equal(CatalogSource.Updated, store.Resolve().Source);
    }

    [Fact]
    public void Auto_check_at_most_once_a_day()
    {
        var now = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        Assert.True(CatalogUpdater.IsAutoCheckDue(new CatalogUpdateState(), now));
        Assert.False(CatalogUpdater.IsAutoCheckDue(new CatalogUpdateState { LastCheckUtc = now.AddHours(-23) }, now));
        Assert.True(CatalogUpdater.IsAutoCheckDue(new CatalogUpdateState { LastCheckUtc = now.AddHours(-24) }, now));
        // A clock that went backwards does not block checks forever.
        Assert.True(CatalogUpdater.IsAutoCheckDue(new CatalogUpdateState { LastCheckUtc = now.AddDays(3) }, now));
    }
}
