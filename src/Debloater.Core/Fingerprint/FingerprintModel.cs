using System.Text.Json.Serialization;
using Debloater.Core.Inventory;

namespace Debloater.Core.Fingerprint;

/// <summary>Binary scan mode.</summary>
public static class BinaryScanModes
{
    /// <summary>Curated list of binaries relevant to debloating (shell, taskbar, Start, search, AI...).</summary>
    public const string Default = "default";

    /// <summary>Every PE under the roots.</summary>
    public const string All = "all";

    /// <summary>No binary scan.</summary>
    public const string None = "none";

    public static bool IsKnown(string mode) => mode is Default or All or None;
}

/// <summary>
/// "Fingerprint" of a Windows installation: what exists (inventory, ADMX policies) and, for each
/// relevant binary, the registry/policy/CSP names it contains. Saved as compressed JSON (gzip) by
/// 'fingerprint create' and compared by 'fingerprint diff'.
/// Contains only names, paths, versions and hashes; never binary content beyond the strings that
/// match the <see cref="StringClassifier"/> patterns.
/// </summary>
public sealed class WindowsFingerprint
{
    public int SchemaVersion { get; set; } = 1;
    public DateTime CreatedUtc { get; set; }

    /// <summary>Root that was read (e.g. C:\ or the folder of a mounted image).</summary>
    public string Root { get; set; } = "";

    /// <summary>true when the root is the running Windows (inventory and registry available).</summary>
    public bool Live { get; set; }

    public bool Elevated { get; set; }
    public string? MachineName { get; set; }

    public FingerprintOs Os { get; set; } = new();

    /// <summary>Inventory (services, tasks, appx...). Offline: only what can be read from files.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InventorySnapshot? Inventory { get; set; }

    public List<FingerprintPolicy> Policies { get; set; } = [];

    public BinaryScanInfo Binaries { get; set; } = new();

    /// <summary>Collection notes (what could not be read and why).</summary>
    public List<string> Notes { get; set; } = [];

    /// <summary>Duration of each phase, in milliseconds.</summary>
    public SortedDictionary<string, long> TimingsMs { get; set; } = new(StringComparer.Ordinal);

    [JsonIgnore]
    public string Label => $"{Os.FullBuild} ({Os.Edition ?? "?"}{(MachineName is null ? "" : $", {MachineName}")})";
}

public sealed class FingerprintOs
{
    public int Build { get; set; }
    public int Ubr { get; set; }
    public string? Edition { get; set; }
    public string? DisplayVersion { get; set; }
    public string? ProductName { get; set; }

    /// <summary>Where it came from (registry of the running system or the ntoskrnl.exe version).</summary>
    public string Source { get; set; } = "";

    [JsonIgnore]
    public string FullBuild => $"{Build}.{Ubr}";
}

/// <summary>Summarized ADMX policy: what matters for comparing builds.</summary>
public sealed class FingerprintPolicy
{
    public string File { get; set; } = "";
    public string Name { get; set; } = "";
    public string Class { get; set; } = "";
    public string Key { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ValueName { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SupportedOn { get; set; }

    /// <summary>Raw supportedOn reference (e.g. windows:SUPPORTED_Windows_10_0_2004), independent of the ADML language.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SupportedOnRef { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Category { get; set; }

    /// <summary>All written values (key\value), including those of elements and lists.</summary>
    public List<string> Values { get; set; } = [];

    [JsonIgnore]
    public string QualifiedName => $"{File}:{Name}";
}

public sealed class BinaryScanInfo
{
    public string Mode { get; set; } = BinaryScanModes.None;

    /// <summary>Scanned folders, relative to the root.</summary>
    public List<string> Roots { get; set; } = [];

    public int FilesConsidered { get; set; }
    public int Errors { get; set; }
    public long TotalBytes { get; set; }

    /// <summary>Limits used (strings per file).</summary>
    public int MaxStringsPerFile { get; set; }

    public List<BinaryEntry> Files { get; set; } = [];

    /// <summary>First read errors (file: reason).</summary>
    public List<string> ErrorSamples { get; set; } = [];
}

/// <summary>A PE binary and the "interesting" strings found in it.</summary>
public sealed class BinaryEntry
{
    /// <summary>Path relative to the root (e.g. Windows\System32\shell32.dll).</summary>
    public string Path { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }

    public long Size { get; set; }
    public string Sha256 { get; set; } = "";

    /// <summary>Is it in the curated list (relevant to debloating)?</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Curated { get; set; }

    /// <summary>Strings that match the patterns (registry paths, value names, CSP, WNF...).</summary>
    public List<string> Strings { get; set; } = [];

    /// <summary>
    /// Identifiers that do not match the patterns but are near (in the file) a registry path:
    /// likely names of values read from that key.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? NearNames { get; set; }

    /// <summary>Value name → nearest registry path in the file (context for layer 2).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SortedDictionary<string, string>? Context { get; set; }

    /// <summary>true when the per-file string limit cut the result short.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Truncated { get; set; }

    public override string ToString() => $"{Path} {Version}";
}
