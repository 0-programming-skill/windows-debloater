using System.ComponentModel;
using System.Diagnostics;
using Debloater.App.Helpers;

namespace Debloater.App.Services;

/// <summary>
/// The app starts without elevation (asInvoker manifest), in read-only mode. To apply/revert, the user
/// asks to restart as administrator, which triggers the UAC prompt (verb "runas").
/// </summary>
public static class Elevation
{
    /// <summary>Restarts the app elevated. Returns false if the user cancelled UAC or it failed.</summary>
    public static bool TryRelaunchElevated(out string? error)
    {
        error = null;
        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            error = Loc.Get("Error_ExecutableNotFound");
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
            error = Loc.Get("Error_ElevationCancelled");
            return false;
        }
        catch (Win32Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Starts a new instance with the same arguments (minus the one-run "--lang" override) and the same
    /// elevation as this one (the child inherits the token). The caller exits the app on success.
    /// </summary>
    public static bool TryRestart(out string? error)
    {
        error = null;
        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            error = Loc.Get("Error_ExecutableNotFound");
            return false;
        }
        var args = new List<string>();
        var argv = Environment.GetCommandLineArgs();
        for (int i = 1; i < argv.Length; i++)
        {
            if (argv[i].Equals("--lang", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                continue;
            }
            args.Add(Quote(argv[i]));
        }
        try
        {
            Process.Start(new ProcessStartInfo(exe, string.Join(' ', args))
            {
                UseShellExecute = false,
                WorkingDirectory = Environment.CurrentDirectory,
            });
            return true;
        }
        catch (Win32Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string Quote(string a) => a.Contains(' ') || a.Contains('"') ? "\"" + a.Replace("\"", "\\\"") + "\"" : a;

    /// <summary>Opens a folder in File Explorer (creating it if possible).</summary>
    public static void OpenFolder(string path)
    {
        try { Directory.CreateDirectory(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
    }

    public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
