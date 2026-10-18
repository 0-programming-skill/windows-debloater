using Microsoft.Win32;

namespace Debloater.Core.Tests;

/// <summary>
/// Temporary key at HKCU\Software\DebloaterTests\&lt;guid&gt;. Tests only touch this key;
/// everything is deleted on Dispose (and the DebloaterTests root too, if it is left empty).
/// </summary>
public sealed class RegistrySandbox : IDisposable
{
    public const string RootSubKey = @"Software\DebloaterTests";

    public RegistrySandbox()
    {
        SubKey = $@"{RootSubKey}\{Guid.NewGuid():N}";
    }

    /// <summary>Subkey relative to HKCU.</summary>
    public string SubKey { get; }

    /// <summary>Path in catalog format (HKCU\...).</summary>
    public string Path => $@"HKCU\{SubKey}";

    public RegistryKey Open(bool writable = false) =>
        Registry.CurrentUser.OpenSubKey(SubKey, writable) ?? throw new InvalidOperationException("sandbox does not exist");

    public RegistryKey? TryOpen(string relative = "") =>
        Registry.CurrentUser.OpenSubKey(relative.Length == 0 ? SubKey : $@"{SubKey}\{relative}");

    public RegistryKey Create(string relative = "") =>
        Registry.CurrentUser.CreateSubKey(relative.Length == 0 ? SubKey : $@"{SubKey}\{relative}", writable: true);

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(SubKey, throwOnMissingSubKey: false);
        using var root = Registry.CurrentUser.OpenSubKey(RootSubKey);
        if (root is { SubKeyCount: 0, ValueCount: 0 })
        {
            root.Dispose();
            Registry.CurrentUser.DeleteSubKey(RootSubKey, throwOnMissingSubKey: false);
        }
    }
}
