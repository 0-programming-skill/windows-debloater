using Microsoft.Win32;
using RegValue = Debloater.Core.Admx.AdmxPolicyStateReader.RegValue;

namespace Debloater.Core.Policies;

/// <summary>
/// Acesso ao registro usado pelo gravador de políticas. Chaves são relativas à raiz do escopo
/// (HKLM ou HKCU). A implementação pode redirecionar as raízes (testes gravam numa sandbox).
/// </summary>
public interface IPolicyRegistry
{
    RegValue? Read(PolicyScope scope, string key, string name);

    /// <summary>Nomes dos valores da chave (vazio se ela não existir).</summary>
    IReadOnlyList<string> ValueNames(PolicyScope scope, string key);

    bool KeyExists(PolicyScope scope, string key);

    void Write(PolicyScope scope, string key, string name, RegValue value);

    void Delete(PolicyScope scope, string key, string name);

    /// <summary>Apaga a chave se ela existir e não tiver valores nem subchaves. Devolve se apagou.</summary>
    bool DeleteKeyIfEmpty(PolicyScope scope, string key);

    void CreateKey(PolicyScope scope, string key);

    /// <summary>Texto da raiz para mensagens (ex.: HKLM, ou o caminho da sandbox).</summary>
    string Describe(PolicyScope scope);
}

/// <summary>
/// Registro real do Windows (visão de 64 bits). Por padrão Machine → HKLM e User → HKCU; com
/// <see cref="Redirected"/> as duas raízes viram subchaves de HKCU (para testes sem elevação).
/// </summary>
public sealed class WindowsPolicyRegistry : IPolicyRegistry
{
    private readonly (RegistryHive Hive, string Prefix) _machine;
    private readonly (RegistryHive Hive, string Prefix) _user;

    public WindowsPolicyRegistry() : this((RegistryHive.LocalMachine, ""), (RegistryHive.CurrentUser, "")) { }

    private WindowsPolicyRegistry((RegistryHive, string) machine, (RegistryHive, string) user)
    {
        _machine = machine;
        _user = user;
    }

    /// <summary>Raízes redirecionadas para HKCU\&lt;base&gt;\HKLM e HKCU\&lt;base&gt;\HKCU.</summary>
    public static WindowsPolicyRegistry Redirected(string hkcuBaseSubKey) => new(
        (RegistryHive.CurrentUser, hkcuBaseSubKey.TrimEnd('\\') + @"\HKLM"),
        (RegistryHive.CurrentUser, hkcuBaseSubKey.TrimEnd('\\') + @"\HKCU"));

    public static WindowsPolicyRegistry Default { get; } = new();

    public bool IsRedirected => _machine.Prefix.Length > 0 || _user.Prefix.Length > 0;

    private (RegistryHive Hive, string Path) Map(PolicyScope scope, string key)
    {
        var (hive, prefix) = scope == PolicyScope.Machine ? _machine : _user;
        key = key.Trim('\\');
        return (hive, prefix.Length == 0 ? key : key.Length == 0 ? prefix : $@"{prefix}\{key}");
    }

    private static RegistryKey Base(RegistryHive hive) => RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);

    public RegValue? Read(PolicyScope scope, string key, string name)
    {
        var (hive, path) = Map(scope, key);
        using var b = Base(hive);
        using var k = b.OpenSubKey(path);
        if (k is null) return null;
        var data = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return data is null ? null : new RegValue(k.GetValueKind(name), data);
    }

    public IReadOnlyList<string> ValueNames(PolicyScope scope, string key)
    {
        var (hive, path) = Map(scope, key);
        using var b = Base(hive);
        using var k = b.OpenSubKey(path);
        return k is null ? [] : k.GetValueNames();
    }

    public bool KeyExists(PolicyScope scope, string key)
    {
        var (hive, path) = Map(scope, key);
        using var b = Base(hive);
        using var k = b.OpenSubKey(path);
        return k is not null;
    }

    public void Write(PolicyScope scope, string key, string name, RegValue value)
    {
        var (hive, path) = Map(scope, key);
        using var b = Base(hive);
        using var k = b.CreateSubKey(path, writable: true)
            ?? throw new InvalidOperationException($@"Não foi possível criar a chave {Describe(scope)}\{key}.");
        k.SetValue(name, value.Data, value.Kind);
    }

    public void Delete(PolicyScope scope, string key, string name)
    {
        var (hive, path) = Map(scope, key);
        using var b = Base(hive);
        using var k = b.OpenSubKey(path, writable: true);
        k?.DeleteValue(name, throwOnMissingValue: false);
    }

    public bool DeleteKeyIfEmpty(PolicyScope scope, string key)
    {
        var (hive, path) = Map(scope, key);
        using var b = Base(hive);
        using (var k = b.OpenSubKey(path))
        {
            if (k is null || k.ValueCount > 0 || k.SubKeyCount > 0) return false;
        }
        b.DeleteSubKey(path, throwOnMissingSubKey: false);
        return true;
    }

    public void CreateKey(PolicyScope scope, string key)
    {
        var (hive, path) = Map(scope, key);
        using var b = Base(hive);
        using var _ = b.CreateSubKey(path, writable: true);
    }

    public string Describe(PolicyScope scope)
    {
        var (hive, prefix) = scope == PolicyScope.Machine ? _machine : _user;
        var root = hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU";
        return prefix.Length == 0 ? root : $@"{root}\{prefix}";
    }
}
