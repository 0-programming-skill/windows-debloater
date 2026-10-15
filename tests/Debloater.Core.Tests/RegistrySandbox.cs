using Microsoft.Win32;

namespace Debloater.Core.Tests;

/// <summary>
/// Chave temporária em HKCU\Software\DebloaterTests\&lt;guid&gt;. Os testes só tocam aqui;
/// tudo é apagado no Dispose (e a raiz DebloaterTests também, se ficar vazia).
/// </summary>
public sealed class RegistrySandbox : IDisposable
{
    public const string RootSubKey = @"Software\DebloaterTests";

    public RegistrySandbox()
    {
        SubKey = $@"{RootSubKey}\{Guid.NewGuid():N}";
    }

    /// <summary>Subchave relativa a HKCU.</summary>
    public string SubKey { get; }

    /// <summary>Caminho no formato do catálogo (HKCU\...).</summary>
    public string Path => $@"HKCU\{SubKey}";

    public RegistryKey Open(bool writable = false) =>
        Registry.CurrentUser.OpenSubKey(SubKey, writable) ?? throw new InvalidOperationException("sandbox não existe");

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
