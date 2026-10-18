using System.Management;

namespace Debloater.Core.Platform;

/// <summary>Result of trying to create a restore point.</summary>
/// <param name="Created">true if a new point was created.</param>
/// <param name="Message">Explanation (includes the reason when nothing was created).</param>
public sealed record RestorePointResult(bool Created, string Message);

/// <summary>
/// Creates system restore points via WMI (root\default:SystemRestore.CreateRestorePoint).
/// Requires administrator and System Protection enabled on the Windows drive. Windows creates at
/// most one point every 24 h (SystemRestorePointCreationFrequency): within that interval the method
/// returns success without creating anything. The program does NOT change that setting; it only warns.
/// </summary>
public static class RestorePoint
{
    private const string Scope = @"\\.\root\default";

    // RestorePointType 12 = MODIFY_SETTINGS; EventType 100 = BEGIN_SYSTEM_CHANGE.
    private const int ModifySettings = 12;
    private const int BeginSystemChange = 100;

    /// <summary>Date of the most recent restore point (UTC), or null if there is none / it cannot be read.</summary>
    public static DateTime? LatestUtc()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope, "SELECT CreationTime FROM SystemRestore");
            DateTime? latest = null;
            foreach (var obj in searcher.Get())
            {
                using (obj)
                {
                    if (obj["CreationTime"] is string s)
                    {
                        var dt = ManagementDateTimeConverter.ToDateTime(s).ToUniversalTime();
                        if (latest is null || dt > latest) latest = dt;
                    }
                }
            }
            return latest;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    public static Task<RestorePointResult> CreateAsync(string description, CancellationToken ct = default) =>
        Task.Run(() => Create(description), ct);

    public static RestorePointResult Create(string description)
    {
        if (!SystemInfo.IsProcessElevated())
            return new RestorePointResult(false, "Creating a restore point requires administrator.");

        var before = LatestUtc();
        uint code;
        try
        {
            using var cls = new ManagementClass(Scope, "SystemRestore", null);
            using var input = cls.GetMethodParameters("CreateRestorePoint");
            input["Description"] = description;
            input["RestorePointType"] = ModifySettings;
            input["EventType"] = BeginSystemChange;
            using var output = cls.InvokeMethod("CreateRestorePoint", input, null);
            code = Convert.ToUInt32(output?["ReturnValue"] ?? 0u);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // With System Protection off, Windows 11 throws COMException 0x80070422 (ERROR_SERVICE_DISABLED)
            // with an empty message instead of returning 1058.
            if (ex.HResult == unchecked((int)0x80070422)) return new RestorePointResult(false, Describe(1058));
            var detail = string.IsNullOrWhiteSpace(ex.Message) ? $"0x{ex.HResult:X8}" : ex.Message.Trim();
            return new RestorePointResult(false, $"Failed to create the restore point: {detail}");
        }

        if (code != 0) return new RestorePointResult(false, Describe(code));

        // The new point can take a few seconds to show up in the SystemRestore list after the call returns.
        var deadline = DateTime.UtcNow + ListingWait;
        while (true)
        {
            var after = LatestUtc();
            if (after is not null && (before is null || after > before))
                return new RestorePointResult(true, $"Restore point created ({after.Value.ToLocalTime():g}).");
            if (DateTime.UtcNow >= deadline) break;
            Thread.Sleep(3000);
        }

        // Success without a new point. Windows' frequency limit (one every 24 h by default) only explains it when
        // a recent point exists; otherwise say what is known instead of guessing.
        var frequency = CreationFrequency();
        if (before is not null && DateTime.UtcNow - before.Value < frequency)
        {
            return new RestorePointResult(false,
                $"Windows did not create a new point: it allows at most one every {FormatInterval(frequency)}. " +
                $"The most recent one is from {before.Value.ToLocalTime():g} and is still available for restoring.");
        }
        return new RestorePointResult(false,
            "Windows accepted the restore point request, but the new point is not listed yet. " +
            "Check System Restore before relying on it.");
    }

    /// <summary>How long to wait for the new point to appear in the list.</summary>
    private static readonly TimeSpan ListingWait = TimeSpan.FromSeconds(60);

    /// <summary>SystemRestorePointCreationFrequency (minutes; default 1440 = 24 h). Only read, never changed.</summary>
    private static TimeSpan CreationFrequency()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore");
            if (key?.GetValue("SystemRestorePointCreationFrequency") is int minutes && minutes >= 0)
                return TimeSpan.FromMinutes(minutes);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return TimeSpan.FromHours(24);
    }

    private static string FormatInterval(TimeSpan t) =>
        t.TotalHours >= 1 && t.TotalHours % 1 == 0 ? $"{t.TotalHours:0} hours" : $"{t.TotalMinutes:0} minutes";

    private static string Describe(uint code)
    {
        var hint = code == 1058
            ? " System Protection seems to be turned off (turn it on in System Properties > System Protection)."
            : "";
        return $"Windows refused the restore point (code {code}).{hint}";
    }
}
