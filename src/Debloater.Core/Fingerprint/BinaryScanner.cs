using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Debloater.Core.Fingerprint;

/// <summary>
/// Scans PE binaries of a Windows installation (live or mounted in a folder): version, size,
/// SHA-256 and the interesting strings (<see cref="StringExtractor"/>). Runs in parallel, one whole
/// file at a time in memory (reused buffer).
/// </summary>
public static partial class BinaryScanner
{
    /// <summary>A scan root, relative to the installation root.</summary>
    public sealed record ScanRoot(string RelativePath, bool Recursive, bool CuratedFolder);

    /// <summary>
    /// Roots: the Windows folder (top level only: explorer.exe), System32 (recursive, without
    /// DriverStore), SystemApps, ImmersiveControlPanel and the Shell* folders (ShellComponents,
    /// ShellExperiences). SysWOW64 and WinSxS are left out (duplicates); Program Files\WindowsApps
    /// is not readable without privileges.
    /// </summary>
    public static IReadOnlyList<ScanRoot> DefaultRoots(string root)
    {
        var list = new List<ScanRoot>
        {
            new("Windows", false, false),
            new(@"Windows\System32", true, false),
            new(@"Windows\SystemApps", true, true),
            new(@"Windows\ImmersiveControlPanel", true, true),
        };
        var windows = Path.Combine(root, "Windows");
        if (Directory.Exists(windows))
        {
            foreach (var dir in Directory.EnumerateDirectories(windows, "Shell*"))
                list.Add(new ScanRoot(Path.Combine("Windows", Path.GetFileName(dir)), true, true));
        }
        return list;
    }

    // System32 subfolders that are not of interest (hardware drivers, logs, databases...).
    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "DriverStore", "catroot", "catroot2", "config", "LogFiles", "spool", "Tasks", "winevt", "sru",
        "WDI", "SleepStudy", "DriverState", "MsDtc", "CodeIntegrity", "WinMetadata", "Microsoft",
    };

    private static readonly HashSet<string> PeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".sys", ".cpl", ".ocx", ".scr", ".drv", ".ax", ".efi",
    };

    /// <summary>
    /// File names relevant to debloating (default mode): shell/Explorer/taskbar/Start, search,
    /// widgets, Settings, telemetry/diagnostics, Edge integration, Copilot/Recall/AI,
    /// sponsored content, notifications, OOBE, Microsoft account, Windows Update, policies/CSP.
    /// </summary>
    [GeneratedRegex("""
        ^(explorer\.exe|shell32\.dll|ExplorerFrame\.dll|windows\.storage\.dll|windows\.immersiveshell\.serviceprovider\.dll
        |sihost\.exe|StartTileData\.dll|policymanager\.dll|gpsvc\.dll|ubpm\.dll|wuaueng\.dll|usosvc\.dll|WaaSMedic.*
        |dmenrollengine\.dll|wlidsvc\.dll|wlidprov\.dll|lockappbroker\.dll|LockScreenData\.dll|wpnapps?\.dll|wpncore\.dll
        |ActionCenter.*|CompatTelRunner\.exe|DeviceCensus\.exe|appraiser\.dll|aepic\.dll|aitstatic\.exe|invagent\.dll
        |generaltel\.dll|devinv\.dll|wermgr\.exe|wer.*\.dll|SettingSync.*|SettingsHandlers_.*|SystemSettings.*)$
        |shell|twinui|taskbar|start(menu|ui|experience)|search|cortana|widget|webexperience|newsandinterests
        |^windows\.ui\.|^windows\.internal\.|composableshell|copilot|recall|(^|[._-])ai([._-]|x|host|mgr|core|platform)
        |^windowsai|semantic|workload|diagtrack|diag(track|nostic|svc)|telemetry|utcutil|census|feedback|siuf
        |edge|contentdelivery|cloudexperience|cloudstore|cloudnotification|oobe|consent|advert|spotlight|tips
        |notification|gamebar|xbox|onedrive|smartscreen|securityhealth|textinput|inputservice|speech|people|phone
        |^usermgr|account|^dsreg|^windows\.cloudstore
        """, RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex CuratedNameRegex();

    public static bool IsCuratedName(string fileName) => CuratedNameRegex().IsMatch(fileName);

    public sealed record Candidate(string FullPath, string RelativePath, bool Curated);

    /// <summary>Lists the PE files under the roots (default mode: curated only; all: everything).</summary>
    public static List<Candidate> Enumerate(string root, string mode, IReadOnlyList<ScanRoot>? roots = null)
    {
        var result = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in roots ?? DefaultRoots(root))
        {
            var dir = Path.Combine(root, r.RelativePath);
            if (!Directory.Exists(dir)) continue;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = r.Recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Offline,
                ReturnSpecialDirectories = false,
            };
            var enumerable = new FileSystemEnumerable<string>(dir, (ref FileSystemEntry e) => e.ToFullPath(), options)
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory && PeExtensions.Contains(Path.GetExtension(e.FileName).ToString()),
                ShouldRecursePredicate = (ref FileSystemEntry e) => !ExcludedDirs.Contains(e.FileName.ToString()),
            };
            foreach (var full in enumerable)
            {
                var rel = Path.GetRelativePath(root, full);
                bool curated = r.CuratedFolder || IsCuratedName(Path.GetFileName(full));
                if (mode == BinaryScanModes.Default && !curated) continue;
                result.TryAdd(rel, new Candidate(full, rel, curated));
            }
        }
        return [.. result.Values.OrderBy(c => c.RelativePath, StringComparer.OrdinalIgnoreCase)];
    }

    public sealed class ScanResult
    {
        public required List<BinaryEntry> Files { get; init; }
        public required List<string> Errors { get; init; }
        public long TotalBytes { get; init; }
    }

    /// <summary>Files larger than this get only hash and version (no string extraction).</summary>
    public const long MaxExtractBytes = 512L * 1024 * 1024;

    public static ScanResult Scan(IReadOnlyList<Candidate> candidates, StringExtractorOptions? options = null,
        int? maxParallelism = null, IProgress<int>? progress = null)
    {
        var files = new ConcurrentBag<BinaryEntry>();
        var errors = new ConcurrentBag<string>();
        long total = 0;
        int done = 0;
        Parallel.ForEach(candidates, new ParallelOptions { MaxDegreeOfParallelism = maxParallelism ?? Environment.ProcessorCount }, c =>
        {
            try
            {
                var entry = ScanFile(c, options);
                if (entry is not null)
                {
                    files.Add(entry);
                    Interlocked.Add(ref total, entry.Size);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{c.RelativePath}: {ex.Message}");
            }
            int n = Interlocked.Increment(ref done);
            if (n % 500 == 0) progress?.Report(n);
        });
        return new ScanResult
        {
            Files = [.. files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)],
            Errors = [.. errors.Order(StringComparer.OrdinalIgnoreCase)],
            TotalBytes = total,
        };
    }

    /// <summary>Reads a file; null if it is not a PE (no "MZ").</summary>
    public static BinaryEntry? ScanFile(Candidate c, StringExtractorOptions? options = null)
    {
        using var handle = File.OpenHandle(c.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
        long length = RandomAccess.GetLength(handle);
        if (length < 64) return null;

        var entry = new BinaryEntry { Path = c.RelativePath, Size = length, Curated = c.Curated };
        if (length > MaxExtractBytes)
        {
            using var stream = new FileStream(handle, FileAccess.Read);
            Span<byte> mz = stackalloc byte[2];
            stream.ReadExactly(mz);
            if (mz[0] != 'M' || mz[1] != 'Z') return null;
            stream.Position = 0;
            entry.Sha256 = Convert.ToHexStringLower(SHA256.HashData(stream));
            entry.Truncated = true;
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent((int)length);
            try
            {
                int read = 0;
                while (read < length)
                {
                    int n = RandomAccess.Read(handle, buffer.AsSpan(read, (int)length - read), read);
                    if (n == 0) break;
                    read += n;
                }
                var data = buffer.AsSpan(0, read);
                if (data[0] != 'M' || data[1] != 'Z') return null;
                entry.Sha256 = Convert.ToHexStringLower(SHA256.HashData(data));
                var extracted = StringExtractor.Extract(data, options);
                entry.Strings = extracted.Strings;
                entry.NearNames = extracted.NearNames.Count > 0 ? extracted.NearNames : null;
                entry.Context = extracted.Context.Count > 0 ? extracted.Context : null;
                entry.Truncated = extracted.Truncated;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        entry.Version = ReadVersion(c.FullPath);
        return entry;
    }

    private static string? ReadVersion(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            if (v.FileMajorPart == 0 && v.FileMinorPart == 0 && v.FileBuildPart == 0 && v.FilePrivatePart == 0) return null;
            return $"{v.FileMajorPart}.{v.FileMinorPart}.{v.FileBuildPart}.{v.FilePrivatePart}";
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }
}
