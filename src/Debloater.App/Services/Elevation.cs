using System.ComponentModel;
using System.Diagnostics;

namespace Debloater.App.Services;

/// <summary>
/// O app abre sem elevação (manifesto asInvoker), em modo somente leitura. Para aplicar/reverter,
/// o usuário pede para reiniciar como administrador, o que dispara o prompt do UAC (verbo "runas").
/// </summary>
public static class Elevation
{
    /// <summary>Reinicia o app elevado. Retorna false se o usuário cancelou o UAC ou falhou.</summary>
    public static bool TryRelaunchElevated(out string? error)
    {
        error = null;
        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            error = "Não foi possível localizar o executável.";
            return false;
        }
        var args = Environment.GetCommandLineArgs().Skip(1).Select(Quote);
        try
        {
            Process.Start(new ProcessStartInfo(exe, string.Join(' ', args))
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
            });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            error = "A elevação foi cancelada.";
            return false;
        }
        catch (Win32Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string Quote(string a) => a.Contains(' ') || a.Contains('"') ? "\"" + a.Replace("\"", "\\\"") + "\"" : a;

    /// <summary>Abre uma pasta no Explorador (criando-a se possível).</summary>
    public static void OpenFolder(string path)
    {
        try { Directory.CreateDirectory(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
    }

    public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
