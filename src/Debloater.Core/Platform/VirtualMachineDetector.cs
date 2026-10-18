using System.Management;
using Microsoft.Win32;

namespace Debloater.Core.Platform;

/// <summary>Result of the "am I in a Hyper-V guest?" check.</summary>
/// <param name="IsHyperVGuest">true only when there is positive evidence of a Hyper-V guest.</param>
/// <param name="Manufacturer">Win32_ComputerSystem.Manufacturer (empty if it could not be read).</param>
/// <param name="Model">Win32_ComputerSystem.Model (empty if it could not be read).</param>
/// <param name="HostName">HKLM\...\Virtual Machine\Guest\Parameters\HostName, when present.</param>
/// <param name="Evidence">Explanation of what was found.</param>
public sealed record VmDetectionResult(bool IsHyperVGuest, string Manufacturer, string Model, string? HostName, string Evidence);

/// <summary>
/// Detects whether the process runs inside a Hyper-V virtual machine. Used by <c>selftest</c>, which
/// applies and reverts ALL tweaks and therefore must never run on the real PC by accident.
///
/// Criteria (any one is enough):
/// 1. WMI Win32_ComputerSystem: Manufacturer "Microsoft Corporation" and Model "Virtual Machine";
/// 2. HKLM\SOFTWARE\Microsoft\Virtual Machine\Guest\Parameters with HostName, VirtualMachineName or
///    VirtualMachineId set (data from the data exchange/KVP service, which only exists in the guest).
///
/// Note: on a HOST with Hyper-V enabled the key "...\Virtual Machine\Guest" exists (without "Parameters") and
/// Win32_ComputerSystem.HypervisorPresent is true; neither counts as evidence.
/// </summary>
public static class VirtualMachineDetector
{
    public const string GuestParametersKey = @"SOFTWARE\Microsoft\Virtual Machine\Guest\Parameters";

    private static readonly string[] s_identityValues = ["HostName", "VirtualMachineName", "VirtualMachineId"];

    public static VmDetectionResult Detect()
    {
        string manufacturer = "", model = "";
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT Manufacturer, Model FROM Win32_ComputerSystem");
            using var items = searcher.Get();
            foreach (var item in items)
            {
                using (item)
                {
                    manufacturer = item["Manufacturer"] as string ?? "";
                    model = item["Model"] as string ?? "";
                }
                break;
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // No WMI: decide by the registry only.
        }

        Dictionary<string, string>? parameters = null;
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(GuestParametersKey);
            if (key is not null)
            {
                parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var name in key.GetValueNames())
                    if (key.GetValue(name) is string s) parameters[name] = s;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        return Evaluate(manufacturer, model, parameters);
    }

    /// <summary>Pure decision (testable): see the criteria in <see cref="VirtualMachineDetector"/>.</summary>
    /// <param name="guestParameters">Text values of Guest\Parameters, or null if the key does not exist.</param>
    public static VmDetectionResult Evaluate(string? manufacturer, string? model, IReadOnlyDictionary<string, string>? guestParameters)
    {
        manufacturer = (manufacturer ?? "").Trim();
        model = (model ?? "").Trim();
        bool wmi = manufacturer.Equals("Microsoft Corporation", StringComparison.OrdinalIgnoreCase)
                   && model.Equals("Virtual Machine", StringComparison.OrdinalIgnoreCase);

        string? hostName = null;
        bool registry = false;
        if (guestParameters is not null)
        {
            foreach (var name in s_identityValues)
            {
                if (guestParameters.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v))
                {
                    registry = true;
                    break;
                }
            }
            if (guestParameters.TryGetValue("HostName", out var h) && !string.IsNullOrWhiteSpace(h)) hostName = h.Trim();
        }

        var evidence = new List<string>();
        evidence.Add(wmi
            ? $"WMI: \"{manufacturer}\" / \"{model}\" (Hyper-V virtual machine)"
            : $"WMI: \"{manufacturer}\" / \"{model}\" (not the Hyper-V VM model)");
        evidence.Add(guestParameters is null
            ? "registry: Guest\\Parameters absent"
            : registry
                ? $"registry: Guest\\Parameters present{(hostName is null ? "" : $" (host: {hostName})")}"
                : "registry: Guest\\Parameters present, but without host/VM identification");

        return new VmDetectionResult(wmi || registry, manufacturer, model, hostName, string.Join("; ", evidence));
    }
}
