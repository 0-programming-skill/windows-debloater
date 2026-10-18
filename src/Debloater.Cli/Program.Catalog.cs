using System.Globalization;
using System.Security.Cryptography;
using Debloater.Core.Catalog;
using Debloater.Core.Updates;

namespace Debloater.Cli;

internal static partial class Program
{
    /// <summary>
    /// Default catalog folder of the other commands: the active catalog (newest verified update, or the
    /// built-in one). A failed verification is reported once on stderr.
    /// </summary>
    internal static string ActiveCatalogDirectory()
    {
        var active = new CatalogStore().Resolve();
        if (active.Warning is { } warning) Console.Error.WriteLine(L.F("CatalogCmd_Warning", warning));
        return active.CatalogDirectory;
    }

    /// <summary>catalog info | update [--check-only] | rollback [--previous] | verify zip sig | pack --out DIR ...</summary>
    private static async Task<int> CatalogCommandAsync(string[] args)
    {
        var options = ParseCatalogOptions(args, out var positional);
        if (positional.Count == 0 || positional[0] is "help" or "-h" or "--help")
        {
            Console.WriteLine(L.T("CatalogUsage"));
            return positional.Count == 0 ? 1 : 0;
        }
        var store = new CatalogStore();
        return positional[0].ToLowerInvariant() switch
        {
            "info" => CatalogInfo(store, options),
            "update" => await CatalogUpdateAsync(store, options.ContainsKey("--check-only")),
            "rollback" => CatalogRollback(store, options.ContainsKey("--previous")),
            "verify" when positional.Count == 3 => CatalogVerify(positional[1], positional[2]),
            "pack" => CatalogPack(options),
            _ => Fail(L.T("CatalogUsage")),
        };
    }

    private static readonly HashSet<string> CatalogSwitches = ["--check-only", "--previous"];
    private static readonly HashSet<string> CatalogValueOptions =
        ["--out", "--catalog", "--reference-dir", "--bundle-version", "--git-commit", "--min-app-version", "--key-env", "--key"];

    private static Dictionary<string, string> ParseCatalogOptions(string[] args, out List<string> positional)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        positional = [];
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (CatalogSwitches.Contains(arg)) options[arg] = "";
            else if (CatalogValueOptions.Contains(arg))
            {
                if (i + 1 >= args.Length) throw new ArgumentException(L.F("Args_NeedsValue", arg));
                options[arg] = args[++i];
            }
            else if (arg.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException(L.F("Args_UnknownOption", arg));
            else positional.Add(arg);
        }
        return options;
    }

    private static string SourceText(CatalogSource source) => source switch
    {
        CatalogSource.Updated => L.T("CatalogCmd_Source_Updated"),
        CatalogSource.Custom => L.T("CatalogCmd_Source_Custom"),
        _ => L.T("CatalogCmd_Source_BuiltIn"),
    };

    private static int CatalogInfo(CatalogStore store, Dictionary<string, string> options)
    {
        var active = options.TryGetValue("--catalog", out var custom) ? ActiveCatalog.Custom(custom) : store.Resolve();
        Console.WriteLine(L.F("CatalogCmd_InfoActive", active.Version?.Text ?? "-", SourceText(active.Source)));
        Console.WriteLine(L.F("CatalogCmd_InfoFolder", active.CatalogDirectory));
        if (active.GitCommit is { } commit) Console.WriteLine(L.F("CatalogCmd_InfoCommit", commit));
        if (active.CreatedUtc is { } created) Console.WriteLine(L.F("CatalogCmd_InfoCreated", created.UtcDateTime));
        if (active.KeyId is { } key) Console.WriteLine(L.F("CatalogCmd_InfoKey", key));
        if (active.Warning is { } warning) Console.WriteLine(L.F("CatalogCmd_Warning", warning));
        Console.WriteLine(L.F("CatalogCmd_InfoBuiltIn", store.BuiltInVersion));
        Console.WriteLine(L.F("CatalogCmd_InfoSchema", CatalogSchema.CurrentVersion, CatalogSchema.MinSupportedVersion, CatalogSchema.CurrentVersion, CatalogSchema.EngineVersion));
        Console.WriteLine(L.F("CatalogCmd_InfoStore", store.Root));
        var installed = store.InstalledVersions();
        Console.WriteLine(L.F("CatalogCmd_InfoInstalled", installed.Count == 0 ? L.T("CatalogCmd_None") : string.Join(", ", installed)));
        var pointer = store.ReadActive();
        if (pointer.RolledBackFrom is { } rb) Console.WriteLine(L.F("CatalogCmd_InfoRolledBack", rb));
        var state = store.ReadState();
        if (state.LastCheckUtc is { } last)
        {
            var outcome = state.LastOutcome + (state.LatestVersion is { } lv ? $" (latest {lv})" : "") + (state.LastMessage is { } m ? $": {m}" : "");
            Console.WriteLine(L.F("CatalogCmd_InfoLastCheck", last.UtcDateTime, outcome));
        }
        else Console.WriteLine(L.T("CatalogCmd_InfoNeverChecked"));
        return 0;
    }

    private static async Task<int> CatalogUpdateAsync(CatalogStore store, bool checkOnly)
    {
        Console.WriteLine(L.F("CatalogCmd_Checking", store.EffectiveVersion()));
        var updater = new CatalogUpdater(store);
        var r = checkOnly ? await updater.CheckAsync() : await updater.UpdateAsync();
        switch (r.Outcome)
        {
            case CatalogUpdateOutcome.UpToDate:
                Console.WriteLine(L.F("CatalogCmd_UpToDate", r.Current, r.Latest));
                return 0;
            case CatalogUpdateOutcome.UpdateAvailable:
                Console.WriteLine(L.F("CatalogCmd_Available", r.Latest, r.Current));
                return 0;
            case CatalogUpdateOutcome.Installed:
                Console.WriteLine(L.F("CatalogCmd_Installed", r.Latest, r.Current));
                return 0;
            case CatalogUpdateOutcome.Skipped:
                Console.WriteLine(L.F("CatalogCmd_Skipped", r.Latest, r.Message));
                return 0;
            case CatalogUpdateOutcome.Incompatible:
                Console.Error.WriteLine(L.F("CatalogCmd_Incompatible", r.Latest, r.Message));
                return 1;
            default:
                Console.Error.WriteLine(L.F("CatalogCmd_Failed", r.Message));
                return 1;
        }
    }

    private static int CatalogRollback(CatalogStore store, bool previous)
    {
        try
        {
            if (previous)
            {
                Console.WriteLine(L.F("CatalogCmd_RolledBackPrevious", store.RollbackToPrevious()));
            }
            else
            {
                store.RollbackToBuiltIn();
                Console.WriteLine(L.F("CatalogCmd_RolledBackBuiltIn", store.BuiltInVersion));
            }
            return 0;
        }
        catch (CatalogBundleException ex)
        {
            return Fail(ex.Message);
        }
    }

    private static int CatalogVerify(string zipPath, string sigPath)
    {
        try
        {
            var bundle = CatalogBundle.Verify(File.ReadAllBytes(zipPath), File.ReadAllBytes(sigPath));
            var m = bundle.Manifest;
            Console.WriteLine(L.F("CatalogCmd_VerifyOk", m.Version, m.SchemaVersion, m.Files.Count, bundle.Key.Id, bundle.Key.Name));
            if (m.GitCommit is { } commit) Console.WriteLine(L.F("CatalogCmd_InfoCommit", commit));
            Console.WriteLine(L.F("CatalogCmd_InfoCreated", m.CreatedUtc.UtcDateTime));
            if (CatalogSchema.CheckCompatibility(m.SchemaVersion, m.MinAppVersion) is { } incompatible)
            {
                Console.WriteLine(L.F("CatalogCmd_VerifyIncompatible", incompatible));
                return 1;
            }
            Console.WriteLine(L.T("CatalogCmd_VerifyCompatible"));
            return 0;
        }
        catch (Exception ex) when (ex is CatalogBundleException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(L.F("CatalogCmd_VerifyFailed", ex.Message));
            return 1;
        }
    }

    /// <summary>
    /// Builds catalog-&lt;v&gt;.zip, manifest.json, latest.json and latest-schema&lt;N&gt;.json in --out. With
    /// --key-env NAME (PEM in an environment variable, as in CI) or --key FILE also writes the .sig and
    /// checks it against the trusted keys embedded in this build. The private key is never printed.
    /// </summary>
    private static int CatalogPack(Dictionary<string, string> options)
    {
        if (!options.TryGetValue("--out", out var outDir)) return Fail(L.T("CatalogUsage"));
        outDir = Path.GetFullPath(outDir);
        var catalogDir = Path.GetFullPath(options.GetValueOrDefault("--catalog") ?? Path.Combine(AppContext.BaseDirectory, CatalogBundle.CatalogFolder));
        var referenceDir = Path.GetFullPath(options.GetValueOrDefault("--reference-dir")
            ?? Path.Combine(Path.GetDirectoryName(catalogDir.TrimEnd(Path.DirectorySeparatorChar))!, CatalogBundle.ReferenceFolder));

        CatalogVersion version;
        if (options.TryGetValue("--bundle-version", out var v)) version = CatalogVersion.Parse(v);
        else if (CatalogVersion.BuiltIn.Equals(CatalogVersion.Zero)) return Fail(L.T("CatalogCmd_PackNoVersion"));
        else version = CatalogVersion.BuiltIn;
        var commit = options.GetValueOrDefault("--git-commit") ?? (options.ContainsKey("--bundle-version") ? null : CatalogVersion.BuiltInCommit);
        var minApp = options.GetValueOrDefault("--min-app-version") ?? "1.0.0";
        if (!Version.TryParse(minApp, out _)) return Fail($"invalid --min-app-version '{minApp}'.");

        ECDsa? key = null;
        try
        {
            if (options.TryGetValue("--key-env", out var envName))
            {
                var pem = Environment.GetEnvironmentVariable(envName);
                if (string.IsNullOrWhiteSpace(pem)) return Fail(L.F("CatalogCmd_PackNoKey", envName));
                key = ECDsa.Create();
                key.ImportFromPem(pem);
            }
            else if (options.TryGetValue("--key", out var keyFile))
            {
                key = ECDsa.Create();
                key.ImportFromPem(File.ReadAllText(keyFile));
            }

            var (zip, manifest) = CatalogBundle.Pack(new CatalogPackOptions
            {
                CatalogDirectory = catalogDir,
                ReferenceDirectory = referenceDir,
                Version = version,
                GitCommit = commit,
                MinAppVersion = minApp,
                CreatedUtc = DateTimeOffset.UtcNow,
            });

            Directory.CreateDirectory(outDir);
            var zipName = CatalogBundle.ZipNameFor(manifest.Version);
            File.WriteAllBytes(Path.Combine(outDir, zipName), zip);
            File.WriteAllBytes(Path.Combine(outDir, CatalogManifest.FileName), manifest.ToJsonBytes());
            var pointer = CatalogPointer.For(manifest, zip).ToJsonBytes();
            File.WriteAllBytes(Path.Combine(outDir, CatalogUpdaterOptions.PointerAsset), pointer);
            File.WriteAllBytes(Path.Combine(outDir, CatalogUpdaterOptions.PointerAssetForSchema(manifest.SchemaVersion)), pointer);
            Console.WriteLine(L.F("CatalogCmd_PackDone", manifest.Version, manifest.Files.Count, zip.LongLength, Path.Combine(outDir, zipName)));

            if (key is null)
            {
                Console.WriteLine(L.T("CatalogCmd_PackUnsigned"));
                return 0;
            }
            var signature = CatalogSignature.Sign(zip, key);
            var trusted = CatalogSignature.Verify(zip, signature);
            if (trusted is null)
                return Fail(L.F("CatalogCmd_PackKeyNotTrusted", CatalogSignature.KeyId(CatalogSignature.PublicPem(key))));
            CatalogBundle.Verify(zip, signature); // full check, as the updater will do
            File.WriteAllBytes(Path.Combine(outDir, zipName + CatalogBundle.SignatureExtension), signature);
            Console.WriteLine(L.F("CatalogCmd_PackSigned", $"{trusted.Id} ({trusted.Name})"));
            return 0;
        }
        catch (Exception ex) when (ex is CatalogBundleException or IOException or CryptographicException or FormatException or ArgumentException)
        {
            // Key import errors carry no key material.
            return Fail(ex.Message);
        }
        finally
        {
            key?.Dispose();
        }
    }
}
