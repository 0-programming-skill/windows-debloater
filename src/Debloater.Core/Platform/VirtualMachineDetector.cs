using System.Management;
using Microsoft.Win32;

namespace Debloater.Core.Platform;

/// <summary>Resultado da verificação "estou num convidado Hyper-V?".</summary>
/// <param name="IsHyperVGuest">true só quando há evidência positiva de convidado Hyper-V.</param>
/// <param name="Manufacturer">Win32_ComputerSystem.Manufacturer (vazio se não deu para ler).</param>
/// <param name="Model">Win32_ComputerSystem.Model (vazio se não deu para ler).</param>
/// <param name="HostName">HKLM\...\Virtual Machine\Guest\Parameters\HostName, quando existe.</param>
/// <param name="Evidence">Explicação em pt-BR do que foi encontrado.</param>
public sealed record VmDetectionResult(bool IsHyperVGuest, string Manufacturer, string Model, string? HostName, string Evidence);

/// <summary>
/// Detecta se o processo roda dentro de uma máquina virtual Hyper-V. Usado pelo <c>selftest</c>, que
/// aplica e reverte TODOS os tweaks e por isso nunca deve rodar no PC real por acidente.
///
/// Critérios (basta um):
/// 1. WMI Win32_ComputerSystem: Manufacturer "Microsoft Corporation" e Model "Virtual Machine";
/// 2. HKLM\SOFTWARE\Microsoft\Virtual Machine\Guest\Parameters com HostName, VirtualMachineName ou
///    VirtualMachineId preenchido (dados do serviço de troca de dados/KVP, só existem no convidado).
///
/// Atenção: no HOST com Hyper-V ativado existe a chave "...\Virtual Machine\Guest" (sem "Parameters") e
/// Win32_ComputerSystem.HypervisorPresent é true; nenhum dos dois serve como evidência.
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
            // Sem WMI: decide só pelo registro.
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

    /// <summary>Decisão pura (testável): ver critérios em <see cref="VirtualMachineDetector"/>.</summary>
    /// <param name="guestParameters">Valores de texto de Guest\Parameters, ou null se a chave não existe.</param>
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
            ? $"WMI: \"{manufacturer}\" / \"{model}\" (máquina virtual Hyper-V)"
            : $"WMI: \"{manufacturer}\" / \"{model}\" (não é o modelo de VM Hyper-V)");
        evidence.Add(guestParameters is null
            ? "registro: Guest\\Parameters ausente"
            : registry
                ? $"registro: Guest\\Parameters presente{(hostName is null ? "" : $" (host: {hostName})")}"
                : "registro: Guest\\Parameters presente, mas sem identificação do host/VM");

        return new VmDetectionResult(wmi || registry, manufacturer, model, hostName, string.Join("; ", evidence));
    }
}
