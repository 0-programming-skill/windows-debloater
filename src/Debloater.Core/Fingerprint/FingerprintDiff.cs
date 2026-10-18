using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Debloater.Core.Inventory;
using Debloater.Core.Model;

namespace Debloater.Core.Fingerprint;

/// <summary>Summary of one side of the diff.</summary>
public sealed class FingerprintSide
{
    public string Build { get; set; } = "";
    public string? Edition { get; set; }
    public string? DisplayVersion { get; set; }
    public string Root { get; set; } = "";
    public bool Live { get; set; }
    public bool Elevated { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MachineName { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string BinaryMode { get; set; } = "";
    public int Binaries { get; set; }
    public int Strings { get; set; }
    public int Policies { get; set; }
    public int InventoryItems { get; set; }

    internal static FingerprintSide From(WindowsFingerprint fp) => new()
    {
        Build = fp.Os.FullBuild,
        Edition = fp.Os.Edition,
        DisplayVersion = fp.Os.DisplayVersion,
        Root = fp.Root,
        Live = fp.Live,
        Elevated = fp.Elevated,
        MachineName = fp.MachineName,
        CreatedUtc = fp.CreatedUtc,
        BinaryMode = fp.Binaries.Mode,
        Binaries = fp.Binaries.Files.Count,
        Strings = fp.Binaries.Files.Sum(f => f.Strings.Count + (f.NearNames?.Count ?? 0)),
        Policies = fp.Policies.Count,
        InventoryItems = fp.Inventory?.Sections.Sum(s => s.Items.Count) ?? 0,
    };
}

public sealed record DiffCount(string Label, int Value);

/// <summary>Inventory change, flagged when it is likely environment noise (hardware, third-party apps...).</summary>
public sealed class InventoryChange
{
    public string Kind { get; set; } = "";
    public string Id { get; set; } = "";
    public DiffChange Change { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    public List<string> Details { get; set; } = [];
    public List<string> TweakIds { get; set; } = [];

    /// <summary>Why the change likely comes from the environment, not the build (null = from the system).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Noise { get; set; }
}

public sealed class PolicyChange
{
    public DiffChange Change { get; set; }
    public FingerprintPolicy Policy { get; set; } = new();
    public List<string> Details { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; set; }
}

public sealed class BinaryChange
{
    public DiffChange Change { get; set; }
    public string Path { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OldVersion { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NewVersion { get; set; }

    public bool Curated { get; set; }
}

/// <summary>Binary where a candidate appears (what layer 2 opens in Ghidra).</summary>
public sealed class CandidateBinary
{
    /// <summary>Path relative to the root of the new fingerprint (e.g. Windows\System32\shell32.dll).</summary>
    public string Path { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sha256 { get; set; }

    public long Size { get; set; }
    public bool Curated { get; set; }

    internal static CandidateBinary From(BinaryEntry b) =>
        new() { Path = b.Path, Version = b.Version, Sha256 = b.Sha256, Size = b.Size, Curated = b.Curated };
}

/// <summary>Something new to investigate (layer 2 input).</summary>
public sealed class Candidate
{
    public int Rank { get; set; }
    public string Value { get; set; } = "";

    /// <summary>See <see cref="CandidateKinds"/>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>new = did not exist in the old build; moved = existed, but in other binaries.</summary>
    public string Status { get; set; } = "new";

    public int Score { get; set; }
    public List<string> Reasons { get; set; } = [];

    /// <summary>Associated registry key (the path itself, the nearest key in the binary, or the policy key).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RegistryPath { get; set; }

    /// <summary>Associated value name (ADMX policies).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ValueName { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    /// <summary>Binaries (up to 5, curated first) that contain the string in the new build.</summary>
    public List<CandidateBinary> Binaries { get; set; } = [];

    /// <summary>Total number of binaries that contain the string (may be larger than <see cref="Binaries"/>).</summary>
    public int BinaryCount { get; set; }

    /// <summary>moved: where it was in the old build.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? OldBinaries { get; set; }

    public List<string> CoveredBy { get; set; } = [];
}

/// <summary>Catalog tweak whose target disappeared in the new build.</summary>
public sealed class ObsoleteTweak
{
    public string TweakId { get; set; } = "";
    public List<string> Targets { get; set; } = [];
    public List<string> Reasons { get; set; } = [];
    public List<string> OldBinaries { get; set; } = [];
}

public sealed class FingerprintDiffResult
{
    public int SchemaVersion { get; set; } = 1;
    public DateTime CreatedUtc { get; set; }
    public FingerprintSide Old { get; set; } = new();
    public FingerprintSide New { get; set; } = new();
    public List<string> Warnings { get; set; } = [];
    public List<DiffCount> Counts { get; set; } = [];

    /// <summary>Candidates ordered by score (at most <see cref="FingerprintDiff.MaxCandidates"/>).</summary>
    public List<Candidate> Candidates { get; set; } = [];

    /// <summary>Relevant strings that disappeared (scored as if they were candidates), highest first.</summary>
    public List<Candidate> RemovedStrings { get; set; } = [];

    public List<ObsoleteTweak> PossiblyObsolete { get; set; } = [];
    public List<PolicyChange> Policies { get; set; } = [];
    public List<InventoryChange> Inventory { get; set; } = [];
    public List<BinaryChange> Binaries { get; set; } = [];

    /// <summary>Set when the privacy filter was applied (<see cref="FingerprintPrivacy"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FingerprintPrivacyInfo? Privacy { get; set; }
}

/// <summary>
/// Compares two fingerprints: inventory, ADMX policies, binaries and strings. Strings that appear
/// in the new build and in no binary of the old one are strong candidates; those that moved to
/// another binary are weak ones. Cross-references the catalog: marks what is already covered and
/// points out tweaks whose names disappeared from every binary ("possibly obsolete").
/// </summary>
public static partial class FingerprintDiff
{
    public const int MaxCandidates = 5000;
    public const int MaxRemovedStrings = 300;
    public const int MaxBinariesPerCandidate = 5;

    public static JsonSerializerOptions JsonOptions => InventoryDiff.JsonOptions;

    private sealed class IndexEntry
    {
        public required string Value { get; init; }
        public bool Pattern { get; set; }
        public List<BinaryEntry> Binaries { get; } = [];
        public string? Context { get; set; }
    }

    public static FingerprintDiffResult Compare(WindowsFingerprint oldFp, WindowsFingerprint newFp, IReadOnlyList<Tweak>? catalog = null)
    {
        catalog ??= [];
        var result = new FingerprintDiffResult
        {
            CreatedUtc = DateTime.UtcNow,
            Old = FingerprintSide.From(oldFp),
            New = FingerprintSide.From(newFp),
        };
        var catalogIndex = CatalogRegistryIndex.Build(catalog);

        CompareInventory(oldFp, newFp, catalog, result);
        ComparePolicies(oldFp, newFp, result);
        CompareBinaries(oldFp, newFp, result);

        // ---- strings ----
        bool comparable = oldFp.Binaries.Mode != BinaryScanModes.None && newFp.Binaries.Mode != BinaryScanModes.None
            && oldFp.Binaries.Files.Count > 0 && newFp.Binaries.Files.Count > 0;
        Dictionary<string, IndexEntry> oldIndex = new(StringComparer.OrdinalIgnoreCase), newIndex = new(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<Candidate>();
        if (!comparable)
        {
            result.Warnings.Add("strings not compared: one of the fingerprints has no binary scan.");
        }
        else
        {
            Func<BinaryEntry, bool> include = _ => true;
            if (oldFp.Binaries.Mode != newFp.Binaries.Mode)
            {
                include = b => b.Curated;
                result.Warnings.Add($"different scan modes ({oldFp.Binaries.Mode} vs {newFp.Binaries.Mode}): strings compared only in the curated-list binaries.");
            }
            oldIndex = BuildIndex(oldFp, include);
            newIndex = BuildIndex(newFp, include);
            candidates.AddRange(StringCandidates(oldFp, newFp, oldIndex, newIndex, catalogIndex, include, out var removed));
            result.RemovedStrings = removed;
            result.PossiblyObsolete = FindObsolete(oldFp, newFp, oldIndex, newIndex, catalogIndex);
        }

        candidates.AddRange(PolicyCandidates(result, newIndex, catalogIndex));
        candidates.AddRange(InventoryCandidates(result, newFp, catalogIndex));

        var ordered = candidates
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Status == "new" ? 0 : 1)
            .ThenBy(c => c.Kind, StringComparer.Ordinal)
            .ThenBy(c => c.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        for (int i = 0; i < ordered.Count; i++) ordered[i].Rank = i + 1;
        result.Candidates = [.. ordered.Take(MaxCandidates)];

        if (oldFp.Os.Build > newFp.Os.Build)
            result.Warnings.Add($"the 'old' build ({oldFp.Os.FullBuild}) is newer than the 'new' one ({newFp.Os.FullBuild}): are the arguments swapped?");
        if (oldFp.Elevated != newFp.Elevated)
            result.Warnings.Add($"different elevation (old: {(oldFp.Elevated ? "elevated" : "not elevated")}; new: {(newFp.Elevated ? "elevated" : "not elevated")}): appx compared by presence only (no allUsers/provisioned) and capabilities only the installed ones.");
        if (oldFp.Live && newFp.Live && oldFp.MachineName is not null && !string.Equals(oldFp.MachineName, newFp.MachineName, StringComparison.OrdinalIgnoreCase))
            result.Warnings.Add("different machines: third-party/hardware services, drivers, apps and tasks show up as noise (flagged).");

        FillCounts(result, oldIndex, newIndex, candidates.Count);
        return result;
    }

    // ---------------- string index ----------------

    private static Dictionary<string, IndexEntry> BuildIndex(WindowsFingerprint fp, Func<BinaryEntry, bool> include)
    {
        var index = new Dictionary<string, IndexEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in fp.Binaries.Files)
        {
            if (!include(b)) continue;
            foreach (var s in b.Strings) Add(s, true, b);
            foreach (var s in b.NearNames ?? []) Add(s, false, b);
        }
        return index;

        void Add(string s, bool pattern, BinaryEntry b)
        {
            if (!index.TryGetValue(s, out var e)) index[s] = e = new IndexEntry { Value = s };
            e.Pattern |= pattern;
            e.Binaries.Add(b);
            if (e.Context is null && b.Context is not null && b.Context.TryGetValue(s, out var ctx)) e.Context = ctx;
        }
    }

    private static string KindOf(IndexEntry e)
    {
        var k = StringClassifier.Classify(e.Value);
        if (k == StringKind.None) return CandidateKinds.NearName;
        if (k == StringKind.ValueName && !e.Pattern) return CandidateKinds.NearName;
        return CandidateKinds.FromStringKind(k);
    }

    private static List<BinaryEntry> PickBinaries(IEnumerable<BinaryEntry> bins) =>
        [.. bins.OrderByDescending(b => b.Curated).ThenBy(b => b.Path, StringComparer.OrdinalIgnoreCase)];

    private static IEnumerable<Candidate> StringCandidates(WindowsFingerprint oldFp, WindowsFingerprint newFp,
        Dictionary<string, IndexEntry> oldIndex, Dictionary<string, IndexEntry> newIndex, CatalogRegistryIndex catalog,
        Func<BinaryEntry, bool> include, out List<Candidate> removed)
    {
        var oldPaths = new HashSet<string>(oldFp.Binaries.Files.Where(include).Select(b => b.Path), StringComparer.OrdinalIgnoreCase);
        var catalogKeyByBinary = CatalogKeysByBinary(newFp, catalog);
        var list = new List<Candidate>();

        foreach (var e in newIndex.Values)
        {
            bool moved = false;
            List<string>? oldBins = null;
            if (oldIndex.TryGetValue(e.Value, out var o))
            {
                var oldNames = new HashSet<string>(o.Binaries.Select(b => Path.GetFileName(b.Path)), StringComparer.OrdinalIgnoreCase);
                if (e.Binaries.Any(b => oldNames.Contains(Path.GetFileName(b.Path)))) continue;
                moved = true;
                oldBins = [.. o.Binaries.Select(b => b.Path).Order(StringComparer.OrdinalIgnoreCase).Take(MaxBinariesPerCandidate)];
            }
            else if (CatalogRegistryIndex.SplitValueSuffix(e.Value) is (_, { Length: > 0 } suffixName) && oldIndex.TryGetValue(suffixName, out var byName))
            {
                // New "Key[Value]", but the value name already existed: only the form changed (weak).
                moved = true;
                oldBins = [.. byName.Binaries.Select(b => b.Path).Order(StringComparer.OrdinalIgnoreCase).Take(MaxBinariesPerCandidate)];
            }
            list.Add(MakeStringCandidate(e, moved, oldBins, catalog, catalogKeyByBinary, oldPaths));
        }

        // Strings that disappeared (scored the same way; only the highest go into the report).
        var oldCatalogKeys = CatalogKeysByBinary(oldFp, catalog);
        removed = [.. oldIndex.Values
            .Where(e => !newIndex.ContainsKey(e.Value))
            .Select(e => MakeStringCandidate(e, false, null, catalog, oldCatalogKeys, null))
            .Select(c => { c.Status = "removed"; return c; })
            .OrderByDescending(c => c.Score).ThenBy(c => c.Value, StringComparer.OrdinalIgnoreCase)
            .Take(MaxRemovedStrings)];
        return list;
    }

    private static Candidate MakeStringCandidate(IndexEntry e, bool moved, List<string>? oldBins, CatalogRegistryIndex catalog,
        Dictionary<string, string> catalogKeyByBinary, HashSet<string>? oldPaths)
    {
        var kind = KindOf(e);
        var bins = PickBinaries(e.Binaries);
        bool isPath = kind is CandidateKinds.PolicyPath or CandidateKinds.RegistryPath;
        string? context = isPath ? null : e.Context;
        string? registryPath = isPath ? e.Value : context;
        string? valueName = null;
        if (isPath && CatalogRegistryIndex.SplitValueSuffix(e.Value) is (var keyPart, { Length: > 0 } namePart))
        {
            registryPath = keyPart;
            valueName = namePart;
        }
        string? sameKey = null;
        if (context is not null && catalog.Paths.TryGetValue(CatalogRegistryIndex.NormalizeRegistryPath(context), out var ids))
            sameKey = ids[0];
        string? keyInBinary = bins.Select(b => catalogKeyByBinary.GetValueOrDefault(b.Path)).FirstOrDefault(k => k is not null);
        var covered = valueName is null
            ? catalog.CoveredBy(kind, e.Value, context)
            : catalog.CoveredBy(CandidateKinds.ValueName, valueName, registryPath);
        var (score, reasons) = CandidateScorer.Score(new CandidateScorer.Input(
            e.Value, kind, context,
            CuratedBinary: bins.Any(b => b.Curated),
            NewBinary: oldPaths is not null && bins.All(b => !oldPaths.Contains(b.Path)),
            Moved: moved,
            CatalogKeyInSameBinary: keyInBinary,
            CatalogKeySameKeyTweak: sameKey,
            CoveredBy: covered,
            BinaryPath: bins.Count > 0 && bins.All(b => b.Path.Contains("Microsoft-Edge-WebView", StringComparison.OrdinalIgnoreCase)) ? bins[0].Path : null));
        return new Candidate
        {
            Value = e.Value,
            Kind = kind,
            Status = moved ? "moved" : "new",
            Score = score,
            Reasons = reasons,
            RegistryPath = registryPath,
            ValueName = valueName,
            Binaries = [.. bins.Take(MaxBinariesPerCandidate).Select(CandidateBinary.From)],
            BinaryCount = bins.Count,
            OldBinaries = oldBins,
            CoveredBy = covered,
        };
    }

    /// <summary>For each binary, the first catalog key it contains (if any).</summary>
    private static Dictionary<string, string> CatalogKeysByBinary(WindowsFingerprint fp, CatalogRegistryIndex catalog)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (catalog.Paths.Count == 0) return map;
        foreach (var b in fp.Binaries.Files)
        {
            foreach (var s in b.Strings)
            {
                if (!s.Contains('\\')) continue;
                var norm = CatalogRegistryIndex.NormalizeRegistryPath(s);
                if (catalog.Paths.ContainsKey(norm)) { map[b.Path] = norm; break; }
            }
        }
        return map;
    }

    // ---------------- possibly obsolete tweaks ----------------

    private static List<ObsoleteTweak> FindObsolete(WindowsFingerprint oldFp, WindowsFingerprint newFp,
        Dictionary<string, IndexEntry> oldIndex, Dictionary<string, IndexEntry> newIndex, CatalogRegistryIndex catalog)
    {
        var oldPaths = NormalizedPaths(oldIndex);
        var newPaths = NormalizedPaths(newIndex);
        var oldAdmx = new HashSet<string>(oldFp.Policies.SelectMany(p => p.Values), StringComparer.OrdinalIgnoreCase);
        var newAdmx = new HashSet<string>(newFp.Policies.SelectMany(p => p.Values), StringComparer.OrdinalIgnoreCase);
        var byTweak = new Dictionary<string, ObsoleteTweak>(StringComparer.Ordinal);

        foreach (var (path, name, tweak) in catalog.Actions)
        {
            if (tweak.MaxBuild is int max && max < newFp.Os.Build) continue;   // already limited to older builds
            if (tweak.MinBuild is int min && min > newFp.Os.Build) continue;
            var target = name.Length > 0 ? $@"{path}\{name}" : path;
            var reasons = new List<string>();
            var bins = new List<string>();

            if (name.Length > 0 && oldIndex.TryGetValue(name, out var o) && !newIndex.ContainsKey(name))
            {
                reasons.Add($"the name '{name}' was in {o.Binaries.Count} binary(ies) of the old build and appears in none of the new one");
                bins.AddRange(o.Binaries.Select(b => $"{b.Path} {b.Version}"));
            }
            if (oldPaths.ContainsKey(path) && !newPaths.ContainsKey(path))
            {
                reasons.Add($"the key '{path}' disappeared from every binary");
                bins.AddRange(oldPaths[path]);
            }
            if (name.Length > 0 && oldAdmx.Contains(target) && !newAdmx.Contains(target))
                reasons.Add("the value was removed from the ADMX policies");
            if (reasons.Count == 0) continue;

            if (!byTweak.TryGetValue(tweak.Id, out var ot)) byTweak[tweak.Id] = ot = new ObsoleteTweak { TweakId = tweak.Id };
            if (!ot.Targets.Contains(target, StringComparer.OrdinalIgnoreCase)) ot.Targets.Add(target);
            foreach (var r in reasons) if (!ot.Reasons.Contains(r)) ot.Reasons.Add(r);
            foreach (var b in bins.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxBinariesPerCandidate))
                if (!ot.OldBinaries.Contains(b)) ot.OldBinaries.Add(b);
        }
        return [.. byTweak.Values.OrderBy(t => t.TweakId, StringComparer.Ordinal)];
    }

    private static Dictionary<string, List<string>> NormalizedPaths(Dictionary<string, IndexEntry> index)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in index.Values)
        {
            if (!e.Value.Contains('\\')) continue;
            var norm = CatalogRegistryIndex.NormalizeRegistryPath(e.Value);
            if (!map.TryGetValue(norm, out var list)) map[norm] = list = [];
            list.AddRange(e.Binaries.Select(b => $"{b.Path} {b.Version}"));
        }
        return map;
    }

    // ---------------- ADMX policies ----------------

    private static void ComparePolicies(WindowsFingerprint oldFp, WindowsFingerprint newFp, FingerprintDiffResult result)
    {
        if (oldFp.Policies.Count == 0 || newFp.Policies.Count == 0)
        {
            result.Warnings.Add("ADMX policies not compared: one of the fingerprints has no ADMX.");
            return;
        }
        var oldMap = ToMap(oldFp.Policies);
        var newMap = ToMap(newFp.Policies);
        var newFiles = new HashSet<string>(newFp.Policies.Select(p => p.File), StringComparer.OrdinalIgnoreCase);
        var oldFiles = new HashSet<string>(oldFp.Policies.Select(p => p.File), StringComparer.OrdinalIgnoreCase);

        foreach (var (name, p) in newMap)
        {
            if (!oldMap.TryGetValue(name, out var before))
            {
                result.Policies.Add(new PolicyChange
                {
                    Change = DiffChange.Added, Policy = p, Details = [.. p.Values],
                    Note = oldFiles.Contains(p.File) ? null : "new ADMX file",
                });
                continue;
            }
            var details = new List<string>();
            void Cmp(string label, string? a, string? b)
            {
                if (!string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase)) details.Add($"{label}: {a ?? "(none)"} → {b ?? "(none)"}");
            }
            Cmp("class", before.Class, p.Class);
            Cmp("key", before.Key, p.Key);
            Cmp("value", before.ValueName, p.ValueName);
            // The supportedOn text depends on the ADML language; the raw reference does not.
            if (before.SupportedOnRef is not null && p.SupportedOnRef is not null)
            {
                if (!string.Equals(before.SupportedOnRef, p.SupportedOnRef, StringComparison.OrdinalIgnoreCase))
                    details.Add($"supported on: {before.SupportedOn} ({before.SupportedOnRef}) → {p.SupportedOn} ({p.SupportedOnRef})");
            }
            else Cmp("supported on", before.SupportedOn, p.SupportedOn);
            var added = p.Values.Except(before.Values, StringComparer.OrdinalIgnoreCase).ToList();
            var gone = before.Values.Except(p.Values, StringComparer.OrdinalIgnoreCase).ToList();
            if (added.Count > 0) details.Add($"new values: {string.Join(", ", added)}");
            if (gone.Count > 0) details.Add($"removed values: {string.Join(", ", gone)}");
            if (details.Count > 0) result.Policies.Add(new PolicyChange { Change = DiffChange.Changed, Policy = p, Details = details });
        }
        foreach (var (name, p) in oldMap)
        {
            if (newMap.ContainsKey(name)) continue;
            result.Policies.Add(new PolicyChange
            {
                Change = DiffChange.Removed, Policy = p, Details = [.. p.Values],
                Note = newFiles.Contains(p.File) ? null : "ADMX file missing in the new one (separately installed template?)",
            });
        }
        result.Policies = [.. result.Policies.OrderBy(c => c.Change).ThenBy(c => c.Policy.QualifiedName, StringComparer.OrdinalIgnoreCase)];
    }

    private static Dictionary<string, FingerprintPolicy> ToMap(IEnumerable<FingerprintPolicy> policies)
    {
        var map = new Dictionary<string, FingerprintPolicy>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in policies) map.TryAdd(p.QualifiedName, p);
        return map;
    }

    private static IEnumerable<Candidate> PolicyCandidates(FingerprintDiffResult result, Dictionary<string, IndexEntry> newIndex, CatalogRegistryIndex catalog)
    {
        foreach (var change in result.Policies.Where(c => c.Change == DiffChange.Added))
        {
            var p = change.Policy;
            var names = p.Values.Select(v => v[(v.LastIndexOf('\\') + 1)..]).Where(n => n.Length > 0 && n != "*").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var bins = PickBinaries(names.SelectMany(n => newIndex.TryGetValue(n, out var e) ? e.Binaries : []).DistinctBy(b => b.Path));
            var covered = names.SelectMany(n => catalog.CoveredBy(CandidateKinds.ValueName, n, p.Key)).Distinct().ToList();
            var (score, reasons) = CandidateScorer.Score(new CandidateScorer.Input(
                p.Name, CandidateKinds.AdmxPolicy, p.Key, CoveredBy: covered,
                ExtraText: $"{p.Key} {p.ValueName} {p.Category}"));
            if (bins.Count == 0) reasons.Add("no scanned binary contains the value name");
            yield return new Candidate
            {
                Value = p.QualifiedName,
                Kind = CandidateKinds.AdmxPolicy,
                Score = score,
                Reasons = reasons,
                RegistryPath = $"{(p.Class == "User" ? "HKCU" : "HKLM")}\\{p.Key}",
                ValueName = p.ValueName ?? names.FirstOrDefault(),
                Description = p.DisplayName,
                Binaries = [.. bins.Take(MaxBinariesPerCandidate).Select(CandidateBinary.From)],
                BinaryCount = bins.Count,
                CoveredBy = covered,
            };
        }
    }

    // ---------------- binaries ----------------

    private static void CompareBinaries(WindowsFingerprint oldFp, WindowsFingerprint newFp, FingerprintDiffResult result)
    {
        if (oldFp.Binaries.Files.Count == 0 || newFp.Binaries.Files.Count == 0) return;
        bool sameMode = oldFp.Binaries.Mode == newFp.Binaries.Mode;
        var oldMap = oldFp.Binaries.Files.Where(b => sameMode || b.Curated).ToDictionary(b => b.Path, StringComparer.OrdinalIgnoreCase);
        var newMap = newFp.Binaries.Files.Where(b => sameMode || b.Curated).ToDictionary(b => b.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var (path, b) in newMap)
        {
            if (!oldMap.TryGetValue(path, out var o))
                result.Binaries.Add(new BinaryChange { Change = DiffChange.Added, Path = b.Path, NewVersion = b.Version, Curated = b.Curated });
            else if (!string.Equals(o.Version, b.Version, StringComparison.Ordinal))
                result.Binaries.Add(new BinaryChange { Change = DiffChange.Changed, Path = b.Path, OldVersion = o.Version, NewVersion = b.Version, Curated = b.Curated });
        }
        foreach (var (path, o) in oldMap)
            if (!newMap.ContainsKey(path))
                result.Binaries.Add(new BinaryChange { Change = DiffChange.Removed, Path = o.Path, OldVersion = o.Version, Curated = o.Curated });
        result.Binaries = [.. result.Binaries.OrderBy(b => b.Change).ThenByDescending(b => b.Curated).ThenBy(b => b.Path, StringComparer.OrdinalIgnoreCase)];
    }

    // ---------------- inventory ----------------

    private static void CompareInventory(WindowsFingerprint oldFp, WindowsFingerprint newFp, IReadOnlyList<Tweak> catalog, FingerprintDiffResult result)
    {
        if (oldFp.Inventory is null || newFp.Inventory is null)
        {
            result.Warnings.Add("inventory not compared: missing from one of the fingerprints.");
            return;
        }
        bool strip = oldFp.Elevated != newFp.Elevated;
        var oldSnap = NormalizeInventory(oldFp.Inventory, strip);
        var newSnap = NormalizeInventory(newFp.Inventory, strip);
        if (catalog.Count > 0)
        {
            CatalogCrossReference.Annotate(oldSnap, catalog);
            CatalogCrossReference.Annotate(newSnap, catalog);
        }
        var diff = InventoryDiff.Compare(oldSnap, newSnap);
        result.Warnings.AddRange(diff.Warnings.Select(w => $"inventory: {w}"));
        var oldItems = Lookup(oldSnap);
        var newItems = Lookup(newSnap);
        // Different machines: a driver whose .sys exists on only one side is hardware/VM, not a build change.
        bool hardwareCheck = oldFp.Live && newFp.Live && !string.Equals(oldFp.MachineName, newFp.MachineName, StringComparison.OrdinalIgnoreCase)
            && oldFp.Binaries.Mode == BinaryScanModes.All && newFp.Binaries.Mode == BinaryScanModes.All;
        var oldBins = new HashSet<string>(oldFp.Binaries.Files.Select(b => b.Path), StringComparer.OrdinalIgnoreCase);
        var newBins = new HashSet<string>(newFp.Binaries.Files.Select(b => b.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var e in diff.Entries)
        {
            var key = $"{e.Kind}|{e.Id}";
            var item = e.Change == DiffChange.Removed ? oldItems.GetValueOrDefault(key) : newItems.GetValueOrDefault(key);
            string? noise = item is null ? null : NoiseReason(item);
            if (noise is null && hardwareCheck && item is not null && e.Kind == InventoryKinds.Drivers && e.Change != DiffChange.Changed
                && ResolveImagePath(item.Properties.GetValueOrDefault("imagePath") ?? $@"\SystemRoot\System32\drivers\{item.Id}.sys") is { } sys)
            {
                if (e.Change == DiffChange.Removed && !newBins.Contains(sys)) noise = "driver whose .sys exists only on the old PC (likely hardware)";
                if (e.Change == DiffChange.Added && !oldBins.Contains(sys)) noise = "driver whose .sys exists only on the new PC (likely hardware/VM)";
            }
            // Without elevation, tasks with a restricted ACL silently drop out of the enumeration: those that only show up on the elevated side are uncertain.
            if (noise is null && e.Kind == InventoryKinds.Tasks && oldFp.Elevated != newFp.Elevated
                && (e.Change == DiffChange.Added && newFp.Elevated || e.Change == DiffChange.Removed && oldFp.Elevated))
                noise = ElevationNoise;
            // The compared PC may already have tweaks applied: a change in a catalog target is not necessarily from the build.
            if (noise is null && e.TweakIds.Count > 0 && (e.Change == DiffChange.Changed || e.Kind == InventoryKinds.Appx && e.Change == DiffChange.Added))
                noise = e.Change == DiffChange.Changed ? CatalogTargetNoise : CatalogAppNoise;
            result.Inventory.Add(new InventoryChange
            {
                Kind = e.Kind, Id = e.Id, Change = e.Change, DisplayName = e.DisplayName,
                Details = [.. e.Details], TweakIds = [.. e.TweakIds],
                Noise = noise,
            });
        }
    }

    private static Dictionary<string, InventoryItem> Lookup(InventorySnapshot snap)
    {
        var map = new Dictionary<string, InventoryItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in snap.Sections) foreach (var i in s.Items) map.TryAdd($"{s.Kind}|{i.Id}", i);
        return map;
    }

    /// <summary>Comparable copy: without the properties that only exist with elevation (if the elevations differ).</summary>
    internal static InventorySnapshot NormalizeInventory(InventorySnapshot s, bool stripElevatedOnly)
    {
        var copy = new InventorySnapshot { CreatedUtc = s.CreatedUtc, WindowsBuild = s.WindowsBuild, Edition = s.Edition, Elevated = s.Elevated };
        foreach (var section in s.Sections)
        {
            var sec = new InventorySection { Kind = section.Kind, Available = section.Available, Note = section.Note };
            foreach (var item in section.Items)
            {
                var clone = new InventoryItem
                {
                    Kind = item.Kind,
                    // Capabilities: the CBS index (no elevation) gives only the name; DISM gives Name~~~~version. Compare by name.
                    Id = section.Kind == InventoryKinds.Capabilities && item.Id.Contains('~') ? item.Id[..item.Id.IndexOf('~')] : item.Id, DisplayName = item.DisplayName, Info = item.Info,
                    Properties = new SortedDictionary<string, string>(item.Properties, StringComparer.Ordinal),
                };
                if (stripElevatedOnly)
                {
                    if (section.Kind == InventoryKinds.Appx) { clone.Properties.Remove("allUsers"); clone.Properties.Remove("provisioned"); }
                    if (section.Kind == InventoryKinds.Capabilities && clone.Properties.GetValueOrDefault("state") != "installed") continue;
                }
                if (sec.Items.Any(i => i.Id.Equals(clone.Id, StringComparison.OrdinalIgnoreCase))) continue;
                sec.Items.Add(clone);
            }
            if (stripElevatedOnly && section.Kind is InventoryKinds.Appx or InventoryKinds.Capabilities or InventoryKinds.Tasks) sec.Note = null;
            copy.Sections.Add(sec);
        }
        return copy;
    }

    [GeneratedRegex(@"S-1-5-21-[\d-]+|\{?[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}?")]
    private static partial Regex MachineSpecificRegex();

    internal const string ElevationNoise = "may be just elevation (without administrator, tasks with a restricted ACL do not show up)";
    internal const string CatalogTargetNoise = "catalog tweak target: the state may have been changed on the PC, not by the build";
    internal const string CatalogAppNoise = "app the catalog removes: probably removed on the old PC";

    private static readonly HashSet<string> MicrosoftPublishers = new(StringComparer.OrdinalIgnoreCase) { "8wekyb3d8bbwe", "cw5n1h2txyewy" };

    /// <summary>Why the change likely comes from the environment (hardware, installed apps) and not the build. null = from the system.</summary>
    internal static string? NoiseReason(InventoryItem item)
    {
        switch (item.Kind)
        {
            case InventoryKinds.Services:
            case InventoryKinds.Drivers:
            {
                var image = item.Properties.GetValueOrDefault("imagePath") ?? "";
                if (image.Contains(@"\DriverStore\", StringComparison.OrdinalIgnoreCase)) return "hardware driver (DriverStore)";
                if (image.Length > 0 && !IsWindowsPath(image)) return "outside the Windows folder (third party)";
                return null;
            }
            case InventoryKinds.Tasks:
                if (!item.Id.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)) return "third-party task";
                if (MachineSpecificRegex().IsMatch(item.Id)) return "user/PC-specific (SID/GUID in the name)";
                if (Regex.IsMatch(item.Id, @"^\\Microsoft\\(Office|VisualStudio|EdgeUpdate|OneDrive|Teams|XblGameSave)", RegexOptions.IgnoreCase))
                    return "separately installed app";
                return null;
            case InventoryKinds.Appx:
                if (item.Properties.GetValueOrDefault("framework") == "true") return "framework (VCLibs, .NET, WinAppSDK...)";
                var publisher = item.Properties.GetValueOrDefault("publisher");
                if (publisher is not null && !MicrosoftPublishers.Contains(publisher)) return "third-party app";
                return null;
            case InventoryKinds.Capabilities:
                if (item.Id.StartsWith("Language.", StringComparison.OrdinalIgnoreCase)) return "language pack";
                return null;
            case InventoryKinds.Autoruns:
                return "user/PC startup entry";
            default:
                return null;
        }
    }

    private static bool IsWindowsPath(string imagePath)
    {
        var p = imagePath.Trim().Trim('"');
        return p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("%SystemRoot%", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("%windir%", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(@"system32\", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(@"\??\C:\Windows\", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(p, @"^[A-Za-z]:\\Windows\\", RegexOptions.IgnoreCase);
    }

    /// <summary>ImagePath/ServiceDll → path relative to the root (Windows\System32\x.dll), or null.</summary>
    internal static string? ResolveImagePath(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return null;
        var p = imagePath.Trim();
        if (p.StartsWith('"')) p = p[1..(p.IndexOf('"', 1) is var q and > 0 ? q : p.Length)];
        var m = Regex.Match(p, @"^.*?\.(exe|dll|sys)", RegexOptions.IgnoreCase);
        if (m.Success) p = m.Value;
        p = Regex.Replace(p, @"^(\\SystemRoot\\|%SystemRoot%\\|%windir%\\)", @"Windows\", RegexOptions.IgnoreCase);
        p = Regex.Replace(p, @"^\\\?\?\\", "");
        p = Regex.Replace(p, @"^[A-Za-z]:\\", "");
        if (p.StartsWith(@"system32\", StringComparison.OrdinalIgnoreCase)) p = @"Windows\" + p;
        return p;
    }

    private static IEnumerable<Candidate> InventoryCandidates(FingerprintDiffResult result, WindowsFingerprint newFp, CatalogRegistryIndex catalog)
    {
        var bins = newFp.Binaries.Files.ToDictionary(b => b.Path, StringComparer.OrdinalIgnoreCase);
        var newItems = newFp.Inventory is null ? [] : Lookup(newFp.Inventory);
        foreach (var e in result.Inventory.Where(e => e.Change == DiffChange.Added && (e.Noise is null || e.Noise == ElevationNoise)))
        {
            string? kind = e.Kind switch
            {
                InventoryKinds.Services => CandidateKinds.Service,
                InventoryKinds.Tasks => CandidateKinds.Task,
                InventoryKinds.Appx => CandidateKinds.Appx,
                InventoryKinds.Features => CandidateKinds.OptionalFeature,
                InventoryKinds.Capabilities => CandidateKinds.Capability,
                _ => null,
            };
            if (kind is null) continue;
            var item = newItems.GetValueOrDefault($"{e.Kind}|{e.Id}");
            var candidateBins = new List<CandidateBinary>();
            if (kind == CandidateKinds.Service && item is not null)
            {
                foreach (var raw in new[] { item.Info?.GetValueOrDefault("serviceDll"), item.Properties.GetValueOrDefault("imagePath") })
                {
                    var rel = ResolveImagePath(raw);
                    if (rel is null || rel.EndsWith("svchost.exe", StringComparison.OrdinalIgnoreCase) && candidateBins.Count > 0) continue;
                    candidateBins.Add(bins.TryGetValue(rel, out var b) ? CandidateBinary.From(b) : new CandidateBinary { Path = rel });
                }
            }
            else if (kind == CandidateKinds.Appx)
            {
                var prefix = $@"Windows\SystemApps\{e.Id}_";
                candidateBins.AddRange(PickBinaries(newFp.Binaries.Files.Where(b => b.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    .Take(MaxBinariesPerCandidate).Select(CandidateBinary.From));
            }
            var (score, reasons) = CandidateScorer.Score(new CandidateScorer.Input(
                e.Id, kind, ExtraText: e.DisplayName, CoveredBy: e.TweakIds));
            if (e.Noise == ElevationNoise)
            {
                score = Math.Max(0, score - 20);
                reasons.Add("uncertain: may exist in the old one, invisible without elevation");
            }
            yield return new Candidate
            {
                Value = e.Id,
                Kind = kind,
                Score = score,
                Reasons = reasons,
                Description = e.DisplayName,
                Binaries = candidateBins,
                BinaryCount = candidateBins.Count,
                CoveredBy = [.. e.TweakIds],
            };
        }
    }

    // ---------------- counts ----------------

    private static void FillCounts(FingerprintDiffResult r, Dictionary<string, IndexEntry> oldIndex, Dictionary<string, IndexEntry> newIndex, int totalCandidates)
    {
        var c = r.Counts;
        void Add(string label, int value) => c.Add(new DiffCount(label, value));
        Add("candidates (total)", totalCandidates);
        Add("new candidates with score ≥ 50", r.Candidates.Count(x => x.Status == "new" && x.Score >= 50 && x.CoveredBy.Count == 0));
        Add("new strings", r.Candidates.Count(x => CandidateKinds.IsString(x.Kind) && x.Status == "new"));
        Add("strings that moved to another binary", r.Candidates.Count(x => CandidateKinds.IsString(x.Kind) && x.Status == "moved"));
        Add("strings that disappeared", oldIndex.Keys.Count(k => !newIndex.ContainsKey(k)));
        Add("new ADMX policies", r.Policies.Count(p => p.Change == DiffChange.Added));
        Add("changed ADMX policies", r.Policies.Count(p => p.Change == DiffChange.Changed));
        Add("removed ADMX policies", r.Policies.Count(p => p.Change == DiffChange.Removed));
        Add("possibly obsolete tweaks", r.PossiblyObsolete.Count);
        foreach (var kind in InventoryKinds.All)
        {
            var items = r.Inventory.Where(i => i.Kind == kind).ToList();
            if (items.Count == 0) continue;
            var system = items.Where(i => i.Noise is null).ToList();
            Add($"{kind}: +{system.Count(i => i.Change == DiffChange.Added)} -{system.Count(i => i.Change == DiffChange.Removed)} ~{system.Count(i => i.Change == DiffChange.Changed)} (from the system)", system.Count);
            Add($"{kind}: likely from the environment", items.Count(i => i.Noise is not null));
        }
        Add("binaries added", r.Binaries.Count(b => b.Change == DiffChange.Added));
        Add("binaries removed", r.Binaries.Count(b => b.Change == DiffChange.Removed));
        Add("binaries with a changed version", r.Binaries.Count(b => b.Change == DiffChange.Changed));
    }
}
