using System.Security.Principal;
using Microsoft.Win32;

namespace Debloater.Core.Platform;

/// <summary>Information about the running Windows.</summary>
public sealed record SystemInfo(int Build, int Ubr, string Edition, string DisplayVersion, string ProductName, bool IsElevated)
{
    public string FullBuild => $"{Build}.{Ubr}";

    public static SystemInfo Current { get; } = Read();

    public static SystemInfo Read()
    {
        using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
            .OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");

        int build = Environment.OSVersion.Version.Build;
        if (key?.GetValue("CurrentBuildNumber") is string b && int.TryParse(b, out var parsed)) build = parsed;
        int ubr = key?.GetValue("UBR") is int u ? u : 0;
        string edition = key?.GetValue("EditionID") as string ?? "?";
        string display = key?.GetValue("DisplayVersion") as string ?? "";
        string product = key?.GetValue("ProductName") as string ?? "Windows";
        // ProductName still says "Windows 10" on Windows 11; we fix it by build.
        if (build >= 22000) product = product.Replace("Windows 10", "Windows 11");

        return new SystemInfo(build, ubr, edition, display, product, IsProcessElevated());
    }

    public static bool IsProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public override string ToString() =>
        $"{ProductName} {DisplayVersion} (build {FullBuild}, edition {Edition}){(IsElevated ? " [administrator]" : "")}";
}
