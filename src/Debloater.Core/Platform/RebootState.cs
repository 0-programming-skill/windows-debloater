using Microsoft.Win32;

namespace Debloater.Core.Platform;

/// <summary>
/// Tells whether Windows has a pending restart (read-only). Used by selftest to
/// record when an optional feature (dism /norestart) only takes full effect after a restart.
/// </summary>
public static class RebootState
{
    /// <summary>Reasons found (empty = no pending restart detected).</summary>
    public static IReadOnlyList<string> PendingReasons()
    {
        var reasons = new List<string>();
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using (var k = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
                if (k is not null) reasons.Add("CBS RebootPending");
            using (var k = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
                if (k is not null) reasons.Add("Windows Update RebootRequired");
            using (var k = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager"))
                if (k?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 }) reasons.Add("PendingFileRenameOperations");
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            reasons.Add($"could not check: {ex.Message}");
        }
        return reasons;
    }

    public static bool IsPending() => PendingReasons().Count > 0;
}
