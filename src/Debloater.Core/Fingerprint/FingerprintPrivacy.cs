using Debloater.Core.Inventory;

namespace Debloater.Core.Fingerprint;

/// <summary>What the privacy filter removed from the diff (counts per section).</summary>
public sealed class FingerprintPrivacyInfo
{
    public const string Binaries = "binaries";
    public const string RemovedStrings = "removedStrings";
    public const string Policies = "policies";
    public const string OldBinaryReferences = "oldBinaryReferences";
    public static string Inventory(string kind) => $"inventory:{kind}";

    /// <summary>Omitted items per section (keys: <see cref="Binaries"/>, <see cref="Inventory"/>...).</summary>
    public SortedDictionary<string, int> Omitted { get; set; } = new(StringComparer.Ordinal);

    public int Get(string key) => Omitted.GetValueOrDefault(key);
    public int Total => Omitted.Values.Sum();

    internal void Add(string key, int n = 1)
    {
        if (n > 0) Omitted[key] = Get(key) + n;
    }
}

/// <summary>
/// Privacy filter for the diff (on by default in <c>fingerprint diff</c>): reports are no longer
/// committed, but they may still be shared, and one of the sides may be someone's personal PC. Removes
/// what reveals a machine's software/hardware and keeps what belongs to Windows:
/// <list type="bullet">
/// <item>inventory items flagged as environment noise (third party, hardware, frameworks, languages,
/// autoruns, SID/GUID), on both sides; only the elevation and catalog-target noise is kept;</item>
/// <item>items that only exist on the old side (removed) with no evidence of being part of Windows: a
/// service/driver whose binary does not exist in the new build, a task outside <c>\Microsoft\Windows\</c>,
/// an appx that is not a system package, capabilities (installed separately);</item>
/// <item>removed binaries outside <c>Windows\SystemApps</c>; strings that disappeared and references to old
/// binaries that do not exist in the new build; policies from ADMX files that only exist on the old side
/// (separately installed templates);</item>
/// <item>the machine names.</item>
/// </list>
/// The omitted counts are stored in <see cref="FingerprintDiffResult.Privacy"/> and shown in the report.
/// </summary>
public static class FingerprintPrivacy
{
    /// <summary>Noise that says nothing about the machine (it comes from Windows or the catalog): kept.</summary>
    private static readonly HashSet<string> KeptNoise = new(StringComparer.Ordinal)
    {
        FingerprintDiff.ElevationNoise, FingerprintDiff.CatalogTargetNoise, FingerprintDiff.CatalogAppNoise,
    };

    private const string SystemApps = @"Windows\SystemApps\";

    public static FingerprintDiffResult Apply(FingerprintDiffResult d, WindowsFingerprint oldFp, WindowsFingerprint newFp)
    {
        var info = new FingerprintPrivacyInfo();
        var newPaths = new HashSet<string>(newFp.Binaries.Files.Select(b => b.Path), StringComparer.OrdinalIgnoreCase);
        var oldPaths = oldFp.Binaries.Files.Select(b => b.Path).ToList();
        var oldItems = new Dictionary<string, InventoryItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in oldFp.Inventory?.Sections ?? [])
            foreach (var i in s.Items) oldItems.TryAdd($"{s.Kind}|{i.Id}", i);

        bool InBoxOldBinary(string path) =>
            path.StartsWith(SystemApps, StringComparison.OrdinalIgnoreCase) || newPaths.Contains(path);

        // ---- inventory ----
        var inventory = new List<InventoryChange>();
        foreach (var i in d.Inventory)
        {
            bool keep = i.Noise is null ? i.Change != DiffChange.Removed || OldItemIsWindows(i) : KeptNoise.Contains(i.Noise);
            if (keep) inventory.Add(i);
            else info.Add(FingerprintPrivacyInfo.Inventory(i.Kind));
        }
        d.Inventory = inventory;

        // ---- removed binaries: only those from system packages ----
        var binaries = new List<BinaryChange>();
        foreach (var b in d.Binaries)
        {
            if (b.Change != DiffChange.Removed || b.Path.StartsWith(SystemApps, StringComparison.OrdinalIgnoreCase)) binaries.Add(b);
            else info.Add(FingerprintPrivacyInfo.Binaries);
        }
        d.Binaries = binaries;

        // ---- strings that disappeared: only those from binaries still present in the new build ----
        var removed = new List<Candidate>();
        foreach (var c in d.RemovedStrings)
        {
            var kept = c.Binaries.Where(b => InBoxOldBinary(b.Path)).ToList();
            if (kept.Count == 0) { info.Add(FingerprintPrivacyInfo.RemovedStrings); continue; }
            if (kept.Count != c.Binaries.Count || c.BinaryCount > c.Binaries.Count) c.BinaryCount = kept.Count;
            c.Binaries = kept;
            removed.Add(c);
        }
        d.RemovedStrings = removed;

        // ---- references to old-side binaries ----
        foreach (var c in d.Candidates.Where(c => c.OldBinaries is { Count: > 0 }))
        {
            var kept = c.OldBinaries!.Where(InBoxOldBinary).ToList();
            info.Add(FingerprintPrivacyInfo.OldBinaryReferences, c.OldBinaries!.Count - kept.Count);
            c.OldBinaries = kept;
        }
        foreach (var t in d.PossiblyObsolete)
        {
            var kept = t.OldBinaries.Where(s => InBoxOldBinary(PathOf(s))).ToList();
            info.Add(FingerprintPrivacyInfo.OldBinaryReferences, t.OldBinaries.Count - kept.Count);
            t.OldBinaries = kept;
        }

        // ---- policies from ADMX templates that only exist on the old side ----
        var policies = new List<PolicyChange>();
        foreach (var p in d.Policies)
        {
            if (p.Change == DiffChange.Removed && p.Note is not null) info.Add(FingerprintPrivacyInfo.Policies);
            else policies.Add(p);
        }
        d.Policies = policies;

        d.Old.MachineName = null;
        d.New.MachineName = null;
        d.Privacy = info;
        return d;

        // "path version" (obsolete tweaks) → path: the path is the longest old binary that is a prefix.
        string PathOf(string s) =>
            oldPaths.Where(p => s.Equals(p, StringComparison.OrdinalIgnoreCase) || s.StartsWith(p + " ", StringComparison.OrdinalIgnoreCase))
                .MaxBy(p => p.Length) ?? s;

        bool OldItemIsWindows(InventoryChange i)
        {
            var item = oldItems.GetValueOrDefault($"{i.Kind}|{i.Id}");
            switch (i.Kind)
            {
                case InventoryKinds.Services:
                case InventoryKinds.Drivers:
                {
                    if (item is null) return false;
                    var raw = item.Info?.GetValueOrDefault("serviceDll") ?? item.Properties.GetValueOrDefault("imagePath")
                              ?? (i.Kind == InventoryKinds.Drivers ? $@"\SystemRoot\System32\drivers\{item.Id}.sys" : null);
                    var path = FingerprintDiff.ResolveImagePath(raw);
                    // svchost without a ServiceDll does not tell whose service it is.
                    return path is not null && !path.EndsWith(@"\svchost.exe", StringComparison.OrdinalIgnoreCase) && InBoxOldBinary(path);
                }
                case InventoryKinds.Tasks:
                    return i.Id.StartsWith(@"\Microsoft\Windows\", StringComparison.OrdinalIgnoreCase);
                case InventoryKinds.Appx:
                    return i.Id.StartsWith("MicrosoftWindows.", StringComparison.OrdinalIgnoreCase)
                        || i.Id.StartsWith("Microsoft.Windows.", StringComparison.OrdinalIgnoreCase)
                        || oldPaths.Any(p => p.StartsWith($@"{SystemApps}{i.Id}_", StringComparison.OrdinalIgnoreCase)
                                          || p.StartsWith($@"{SystemApps}SxS\{i.Id}_", StringComparison.OrdinalIgnoreCase));
                case InventoryKinds.Features:
                    return true;   // optional features belong to Windows by definition
                default:
                    return false;  // separately installed capabilities, autoruns...
            }
        }
    }
}
