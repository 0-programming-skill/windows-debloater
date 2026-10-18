using System.Text.RegularExpressions;

namespace Debloater.Coverage;

/// <summary>Tipo de alvo que uma ferramenta altera.</summary>
public enum TargetKind
{
    Registry,     // chave + valor
    RegistryKey,  // chave inteira (apagar/criar a chave, sem valor específico)
    Service,
    Task,
    Appx,
    Feature,
    Capability,
}

/// <summary>
/// Um alvo normalizado (ex.: HKLM\SOFTWARE\POLICIES\...\VALOR, serviço DIAGTRACK). <see cref="Key"/> é a forma
/// canônica usada para deduplicar e casar com o catálogo; <see cref="Display"/> é a forma legível.
/// Só nomes e caminhos: nenhum texto das ferramentas comparadas é guardado.
/// </summary>
public sealed record Target(TargetKind Kind, string Key, string Display)
{
    public override string ToString() => $"{Kind}:{Display}";
}

/// <summary>Normalização de alvos vindos de formatos diferentes (.reg, PowerShell, JSON, C#, YAML).</summary>
public static partial class Targets
{
    private static readonly (string Long, string Short)[] s_hives =
    [
        ("HKEY_LOCAL_MACHINE", "HKLM"), ("HKEY_CURRENT_USER", "HKCU"), ("HKEY_CLASSES_ROOT", "HKCR"),
        ("HKEY_USERS", "HKU"), ("HKEY_CURRENT_CONFIG", "HKCC"),
        ("REGISTRY::HKLM", "HKLM"), ("REGISTRY::HKCU", "HKCU"),
        ("HKLM:", "HKLM"), ("HKCU:", "HKCU"), ("HKCR:", "HKCR"), ("HKU:", "HKU"),
    ];

    [GeneratedRegex(@"\\ControlSet00\d\\", RegexOptions.IgnoreCase)]
    private static partial Regex ControlSetN();

    [GeneratedRegex(@"^HKLM\\SYSTEM\\CurrentControlSet\\Services\\([^\\]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ServiceKey();

    /// <summary>Normaliza uma chave: hive curto, sem "Registry::", sem barras duplicadas, ControlSet001 = CurrentControlSet.
    /// Devolve null se não for uma chave de registro utilizável (variável, modelo, hive desconhecido).</summary>
    public static string? NormalizeKey(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim().Trim('"', '\'', '[', ']').Trim();
        if (p.StartsWith("-", StringComparison.Ordinal)) p = p[1..]; // [-HKEY...] em .reg = apagar a chave
        if (p.StartsWith("Registry::", StringComparison.OrdinalIgnoreCase)) p = p["Registry::".Length..];
        if (p.StartsWith("Microsoft.PowerShell.Core\\Registry::", StringComparison.OrdinalIgnoreCase)) p = p["Microsoft.PowerShell.Core\\Registry::".Length..];
        p = p.Replace('/', '\\');
        while (p.Contains("\\\\")) p = p.Replace("\\\\", "\\");
        p = p.TrimEnd('\\');

        string? hive = null;
        foreach (var (l, s) in s_hives)
        {
            if (p.StartsWith(l, StringComparison.OrdinalIgnoreCase))
            {
                hive = s;
                p = p[l.Length..].TrimStart('\\');
                break;
            }
        }
        if (hive is null)
        {
            foreach (var s in new[] { "HKLM", "HKCU", "HKCR", "HKU", "HKCC" })
            {
                if (p.StartsWith(s + "\\", StringComparison.OrdinalIgnoreCase) || p.Equals(s, StringComparison.OrdinalIgnoreCase))
                {
                    hive = s;
                    p = p[s.Length..].TrimStart('\\');
                    break;
                }
            }
        }
        if (hive is null || p.Length == 0) return null;
        // Variáveis e modelos ($env, %VAR%, {{ }}, `) não são caminhos concretos. Chaves com GUID ({...}) são.
        if (p.IndexOfAny(['$', '%', '`', '*', '<', '>', '"', '\n', '\r']) >= 0 || p.Contains("{{")) return null;

        // HKU\.DEFAULT, HKU\S-1-5-..., HKU\<SID> → tratados como HKCU (perfil de usuário).
        if (hive == "HKU")
        {
            int i = p.IndexOf('\\');
            if (i <= 0) return null;
            p = p[(i + 1)..];
            hive = "HKCU";
        }
        // HKLM\SOFTWARE\Classes = HKCR.
        if (hive == "HKLM" && p.StartsWith("SOFTWARE\\Classes\\", StringComparison.OrdinalIgnoreCase))
        {
            hive = "HKCR";
            p = p["SOFTWARE\\Classes\\".Length..];
        }
        if (hive == "HKCU" && p.StartsWith("SOFTWARE\\Classes\\", StringComparison.OrdinalIgnoreCase))
        {
            hive = "HKCR";
            p = p["SOFTWARE\\Classes\\".Length..];
        }
        p = ControlSetN().Replace("\\" + p + "\\", "\\CurrentControlSet\\").Trim('\\');
        if (p.StartsWith("CurrentControlSet\\", StringComparison.OrdinalIgnoreCase)) p = "SYSTEM\\" + p;
        return hive + "\\" + p;
    }

    /// <summary>Alvo de registro. Escrever "Start" em Services\X vira o alvo de serviço X.</summary>
    public static Target? Registry(string? path, string? valueName)
    {
        var key = NormalizeKey(path);
        if (key is null) return null;
        var m = ServiceKey().Match(key);
        if (m.Success && (valueName is null || valueName.Equals("Start", StringComparison.OrdinalIgnoreCase)))
            return valueName is null ? null : Service(m.Groups[1].Value);
        if (valueName is null)
            return new Target(TargetKind.RegistryKey, key.ToUpperInvariant(), key);
        var name = valueName.Trim().Trim('"', '\'');
        if (name.Length == 0 || name == "@") name = "(padrão)";
        if (name.Length > 120 || name.Contains('$') || name.Contains('\n')) return null;
        return new Target(TargetKind.Registry, (key + "\\" + name).ToUpperInvariant(), key + "\\" + name);
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{2,80}$")]
    private static partial Regex SimpleName();

    public static Target? Service(string? name)
    {
        var n = name?.Trim().Trim('"', '\'');
        if (n is null || !SimpleName().IsMatch(n)) return null;
        // serviços por usuário (OneSyncSvc_1a2b3) → nome base
        n = Regex.Replace(n, @"_[0-9a-f]{4,8}$", "", RegexOptions.IgnoreCase);
        return new Target(TargetKind.Service, n.ToUpperInvariant(), n);
    }

    /// <summary>Tarefa agendada. Casada pelo nome da tarefa (último segmento), porque muitas ferramentas só dão o nome.</summary>
    public static Target? Task(string? path, string? name = null)
    {
        string full;
        if (!string.IsNullOrWhiteSpace(name))
        {
            var dir = (path ?? "").Trim().Trim('"', '\'').TrimEnd('\\');
            full = dir.Length == 0 ? name.Trim() : dir + "\\" + name.Trim().Trim('"', '\'');
        }
        else full = (path ?? "").Trim().Trim('"', '\'');
        full = full.Replace('/', '\\');
        while (full.Contains("\\\\")) full = full.Replace("\\\\", "\\");
        full = full.TrimEnd('\\');
        if (full.Length == 0 || full.IndexOfAny(['$', '*', '{', '%']) >= 0) return null;
        if (!full.StartsWith('\\') && full.Contains('\\')) full = "\\" + full;
        var leaf = full[(full.LastIndexOf('\\') + 1)..];
        if (leaf.Length < 2) return null;
        return new Target(TargetKind.Task, leaf.ToUpperInvariant(), full);
    }

    public static Target? Appx(string? name)
    {
        var n = name?.Trim().Trim('"', '\'', '*').Trim();
        if (n is null || n.Length < 3) return null;
        int u = n.IndexOf('_'); // nome completo / família → nome
        if (u > 0) n = n[..u];
        if (!Regex.IsMatch(n, @"^[A-Za-z0-9][A-Za-z0-9.\-]+$") || !n.Contains('.')) return null;
        return new Target(TargetKind.Appx, n.ToUpperInvariant(), n);
    }

    public static Target? Feature(string? name)
    {
        var n = name?.Trim().Trim('"', '\'');
        if (n is null || !SimpleName().IsMatch(n)) return null;
        return new Target(TargetKind.Feature, n.ToUpperInvariant(), n);
    }

    public static Target? Capability(string? name)
    {
        var n = name?.Trim().Trim('"', '\'');
        if (n is null) return null;
        int t = n.IndexOf('~');
        if (t > 0) n = n[..t];
        if (!SimpleName().IsMatch(n)) return null;
        return new Target(TargetKind.Capability, n.ToUpperInvariant(), n);
    }
}
