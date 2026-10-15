using Microsoft.Win32;

namespace Debloater.Core.Platform;

/// <summary>
/// Indica se o Windows tem uma reinicialização pendente (somente leitura). Usado pelo selftest para
/// registrar quando um recurso opcional (dism /norestart) só terá efeito completo após reiniciar.
/// </summary>
public static class RebootState
{
    /// <summary>Motivos encontrados (vazio = nenhuma reinicialização pendente detectada).</summary>
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
            reasons.Add($"não foi possível verificar: {ex.Message}");
        }
        return reasons;
    }

    public static bool IsPending() => PendingReasons().Count > 0;
}
