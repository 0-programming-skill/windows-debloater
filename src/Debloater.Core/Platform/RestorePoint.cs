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
            return new RestorePointResult(false, $"Failed to create the restore point: {ex.Message}");
        }

        if (code != 0)
        {
            var hint = code == 1058
                ? " System Protection seems to be turned off (turn it on in System Properties > System Protection)."
                : "";
            return new RestorePointResult(false, $"Windows refused the restore point (code {code}).{hint}");
        }

        var after = LatestUtc();
        if (after is not null && (before is null || after > before))
            return new RestorePointResult(true, $"Restore point created ({after.Value.ToLocalTime():g}).");

        // Success without a new point: Windows frequency limit (one every 24 h).
        var when = before is null ? "" : $" The most recent one is from {before.Value.ToLocalTime():g}.";
        return new RestorePointResult(false,
            "Windows did not create a new point: it allows at most one every 24 hours." + when +
            " That existing point is still available for restoring.");
    }
}
