using System.Globalization;
using System.Security;
using Debloater.Core.Engine;
using Debloater.Core.Model;
using Microsoft.Win32;

namespace Debloater.Core.Actions;

/// <summary>
/// "registry" action: ensures a value exists with a specific type/data, or (delete: true) that it does not exist.
/// </summary>
public sealed class RegistryActionHandler : IActionHandler
{
    public string Type => ActionSpec.Registry;
    public bool CanRestore => true;

    private static readonly string[] s_kinds = ["dword", "qword", "string", "expandString", "multiString", "binary"];

    public void Validate(ActionSpec spec, IList<string> errors)
    {
        ActionHandlers.RequireField(spec.Path, "path", Type, errors);
        if (spec.Path is not null && !TryParsePath(spec.Path, out _, out _))
            errors.Add($"action 'registry': invalid path '{spec.Path}' (use HKLM\\..., HKCU\\..., HKCR\\... or HKU\\...).");
        if (spec.Name is null)
            errors.Add("action 'registry' requires the field 'name' (use \"\" for the key's default value).");
        if (spec.Delete)
        {
            if (spec.Value is not null || spec.Kind is not null)
                errors.Add("action 'registry': 'delete: true' cannot have 'kind' or 'value'.");
            return;
        }
        ActionHandlers.RequireOneOf(spec.Kind, "kind", Type, errors, s_kinds);
        if (spec.Value is null)
        {
            errors.Add("action 'registry' requires 'value' (or 'delete: true').");
            return;
        }
        if (spec.Kind is not null && s_kinds.Contains(spec.Kind, StringComparer.OrdinalIgnoreCase))
        {
            try { _ = ToRegistryValue(ParseKind(spec.Kind), spec.Value); }
            catch (FormatException ex) { errors.Add($"action 'registry' ({spec.Path}\\{spec.Name}): {ex.Message}"); }
        }
    }

    public Task<Detection> DetectAsync(ActionSpec spec, ScanContext ctx, CancellationToken ct = default)
    {
        try
        {
            var (hive, subKey) = ParsePathOrThrow(spec.Path);
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(subKey, writable: false);
            var current = ReadValue(key, spec.Name!);

            if (spec.Delete)
            {
                return Task.FromResult(current is null
                    ? Detection.Desired("absent")
                    : Detection.NotDesired($"present: {Describe(current.Value.Kind, current.Value.Data)}"));
            }

            if (current is null) return Task.FromResult(Detection.NotDesired("absent"));

            var kind = ParseKind(spec.Kind!);
            var desired = ToRegistryValue(kind, spec.Value!);
            var (curKind, curData) = current.Value;
            bool match = curKind == kind && ValuesEqual(curData, desired);
            var detail = $"current: {Describe(curKind, curData)}";
            return Task.FromResult(match ? Detection.Desired(detail) : Detection.NotDesired(detail));
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return Task.FromResult(Detection.Error($"no registry access: {ex.Message}"));
        }
    }

    public Task<ActionBackup?> CaptureBackupAsync(ActionSpec spec, ScanContext ctx, CancellationToken ct = default)
    {
        var (hive, subKey) = ParsePathOrThrow(spec.Path);
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(subKey, writable: false);
        var backup = new ActionBackup { Type = Type, Target = spec.Target };

        if (key is null)
        {
            backup.Extra = FirstMissingKey(baseKey, subKey);
            return Task.FromResult<ActionBackup?>(backup);
        }

        var current = ReadValue(key, spec.Name!);
        if (current is null) return Task.FromResult<ActionBackup?>(backup);

        var (kind, data) = current.Value;
        backup.Existed = true;
        backup.Kind = KindToString(kind);
        if (kind == RegistryValueKind.MultiString) backup.MultiData = [.. (string[])data];
        else backup.Data = DataToString(kind, data);
        return Task.FromResult<ActionBackup?>(backup);
    }

    public Task ApplyAsync(ActionSpec spec, ScanContext ctx, CancellationToken ct = default)
    {
        try
        {
            Write(spec);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            throw new ProtectedRegistryException(spec, ex);
        }
        return Task.CompletedTask;
    }

    private static void Write(ActionSpec spec)
    {
        var (hive, subKey) = ParsePathOrThrow(spec.Path);
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        if (spec.Delete)
        {
            using var existing = baseKey.OpenSubKey(subKey, writable: true);
            existing?.DeleteValue(spec.Name!, throwOnMissingValue: false);
            return;
        }

        var kind = ParseKind(spec.Kind!);
        using var key = baseKey.CreateSubKey(subKey, writable: true)
            ?? throw new InvalidOperationException($"Could not create key {spec.Path}.");
        key.SetValue(spec.Name!, ToRegistryValue(kind, spec.Value!), kind);
    }

    public Task RestoreAsync(ActionSpec spec, ActionBackup? backup, ScanContext ctx, CancellationToken ct = default)
    {
        if (backup is null) throw new InvalidOperationException($"No backup for {spec}.");
        try
        {
            Restore(spec, backup, ctx);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            throw new ProtectedRegistryException(spec, ex);
        }
        return Task.CompletedTask;
    }

    private static void Restore(ActionSpec spec, ActionBackup backup, ScanContext ctx)
    {
        var (hive, subKey) = ParsePathOrThrow(spec.Path);
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);

        if (backup.Existed)
        {
            var kind = ParseKind(backup.Kind ?? throw new InvalidOperationException("Backup has no 'kind'."));
            object data = kind == RegistryValueKind.MultiString
                ? (backup.MultiData ?? []).ToArray()
                : StringToData(kind, backup.Data ?? "");
            using var key = baseKey.CreateSubKey(subKey, writable: true);
            key.SetValue(spec.Name!, data, kind);
            return;
        }

        // The value did not exist: remove it and delete the keys that apply created, if they are now empty.
        using (var key = baseKey.OpenSubKey(subKey, writable: true))
            key?.DeleteValue(spec.Name!, throwOnMissingValue: false);

        if (backup.Extra is { Length: > 0 } firstCreated)
        {
            // Cleanup of the keys that apply created: best-effort. A leftover empty key changes nothing, and
            // Windows sometimes refuses to delete it (e.g. Policies\Microsoft\Dsh, protected by UCPD on 26300).
            try
            {
                DeleteEmptyKeysUpTo(baseKey, subKey, firstCreated);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
            {
                ctx.AddNote($"the empty key {RootName(hive)}\\{subKey} was kept: Windows did not allow deleting it ({ex.Message}).");
            }
        }
    }

    // ---------- helpers ----------

    public static bool TryParsePath(string path, out RegistryHive hive, out string subKey)
    {
        hive = default;
        subKey = "";
        int idx = path.IndexOf('\\');
        string root = idx < 0 ? path : path[..idx];
        subKey = idx < 0 ? "" : path[(idx + 1)..].Trim('\\');
        switch (root.ToUpperInvariant())
        {
            case "HKLM": case "HKEY_LOCAL_MACHINE": hive = RegistryHive.LocalMachine; break;
            case "HKCU": case "HKEY_CURRENT_USER": hive = RegistryHive.CurrentUser; break;
            case "HKCR": case "HKEY_CLASSES_ROOT": hive = RegistryHive.ClassesRoot; break;
            case "HKU": case "HKEY_USERS": hive = RegistryHive.Users; break;
            default: return false;
        }
        return subKey.Length > 0;
    }

    private static string RootName(RegistryHive hive) => hive switch
    {
        RegistryHive.LocalMachine => "HKLM",
        RegistryHive.CurrentUser => "HKCU",
        RegistryHive.ClassesRoot => "HKCR",
        RegistryHive.Users => "HKU",
        _ => hive.ToString(),
    };

    private static (RegistryHive, string) ParsePathOrThrow(string? path) =>
        path is not null && TryParsePath(path, out var hive, out var sub)
            ? (hive, sub)
            : throw new InvalidOperationException($"Invalid registry path: '{path}'.");

    private static (RegistryValueKind Kind, object Data)? ReadValue(RegistryKey? key, string name)
    {
        if (key is null) return null;
        var data = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (data is null) return null;
        return (key.GetValueKind(name), data);
    }

    public static RegistryValueKind ParseKind(string kind) => kind.ToLowerInvariant() switch
    {
        "dword" => RegistryValueKind.DWord,
        "qword" => RegistryValueKind.QWord,
        "string" => RegistryValueKind.String,
        "expandstring" => RegistryValueKind.ExpandString,
        "multistring" => RegistryValueKind.MultiString,
        "binary" => RegistryValueKind.Binary,
        _ => throw new FormatException($"unknown kind '{kind}'."),
    };

    public static string KindToString(RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => "dword",
        RegistryValueKind.QWord => "qword",
        RegistryValueKind.String => "string",
        RegistryValueKind.ExpandString => "expandString",
        RegistryValueKind.MultiString => "multiString",
        RegistryValueKind.Binary => "binary",
        _ => kind.ToString(),
    };

    /// <summary>Converts the YAML value into the .NET object expected by <see cref="RegistryKey.SetValue(string?, object, RegistryValueKind)"/>.</summary>
    public static object ToRegistryValue(RegistryValueKind kind, object value)
    {
        switch (kind)
        {
            case RegistryValueKind.DWord:
            {
                long n = ParseInteger(Scalar(value));
                if (n is < int.MinValue or > uint.MaxValue) throw new FormatException($"dword value out of range: {n}.");
                return unchecked((int)(uint)(n & 0xFFFFFFFF));
            }
            case RegistryValueKind.QWord:
                return ParseInteger(Scalar(value));
            case RegistryValueKind.String:
            case RegistryValueKind.ExpandString:
                return Scalar(value);
            case RegistryValueKind.MultiString:
                return value is IEnumerable<object> list
                    ? list.Select(o => o?.ToString() ?? "").ToArray()
                    : [Scalar(value)];
            case RegistryValueKind.Binary:
                return ParseHex(Scalar(value));
            default:
                throw new FormatException($"unsupported kind: {kind}.");
        }
    }

    private static string Scalar(object value) => value switch
    {
        string s => s,
        IEnumerable<object> => throw new FormatException("expected a scalar value, not a list."),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    private static long ParseInteger(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (ulong.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)) return unchecked((long)hex);
        }
        else if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec)) return dec;
        else if (ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var udec)) return unchecked((long)udec);
        throw new FormatException($"invalid number '{s}'.");
    }

    private static byte[] ParseHex(string s)
    {
        var clean = new string(s.Where(Uri.IsHexDigit).ToArray());
        if (clean.Length % 2 != 0 || clean.Length != s.Count(c => !char.IsWhiteSpace(c) && c != ',' && c != '-'))
            throw new FormatException($"invalid binary hex '{s}'.");
        return Convert.FromHexString(clean);
    }

    private static bool ValuesEqual(object current, object desired) => (current, desired) switch
    {
        (int a, int b) => a == b,
        (long a, long b) => a == b,
        (string a, string b) => string.Equals(a, b, StringComparison.Ordinal),
        (string[] a, string[] b) => a.SequenceEqual(b, StringComparer.Ordinal),
        (byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b),
        _ => false,
    };

    private static string DataToString(RegistryValueKind kind, object data) => kind switch
    {
        RegistryValueKind.DWord => unchecked((uint)(int)data).ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.QWord => ((long)data).ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.Binary => Convert.ToHexString((byte[])data),
        RegistryValueKind.MultiString => string.Join("\n", (string[])data),
        _ => data.ToString() ?? "",
    };

    private static object StringToData(RegistryValueKind kind, string s) => kind switch
    {
        RegistryValueKind.DWord => unchecked((int)uint.Parse(s, CultureInfo.InvariantCulture)),
        RegistryValueKind.QWord => long.Parse(s, CultureInfo.InvariantCulture),
        RegistryValueKind.Binary => Convert.FromHexString(s),
        _ => s,
    };

    private static string Describe(RegistryValueKind kind, object data)
    {
        var text = kind == RegistryValueKind.MultiString ? string.Join(" | ", (string[])data) : DataToString(kind, data);
        if (text.Length > 60) text = text[..57] + "...";
        return $"{(text.Length == 0 ? "\"\"" : text)} ({KindToString(kind)})";
    }

    /// <summary>Returns the subpath of the first key (top-down) that does not exist yet.</summary>
    private static string? FirstMissingKey(RegistryKey baseKey, string subKey)
    {
        var parts = subKey.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i <= parts.Length; i++)
        {
            var partial = string.Join('\\', parts.Take(i));
            using var k = baseKey.OpenSubKey(partial);
            if (k is null) return partial;
        }
        return null;
    }

    private static void DeleteEmptyKeysUpTo(RegistryKey baseKey, string subKey, string stopAt)
    {
        var current = subKey;
        while (current.Length >= stopAt.Length && current.StartsWith(stopAt, StringComparison.OrdinalIgnoreCase))
        {
            using (var k = baseKey.OpenSubKey(current))
            {
                if (k is null || k.SubKeyCount > 0 || k.ValueCount > 0) break;
            }
            baseKey.DeleteSubKey(current, throwOnMissingSubKey: false);
            if (current.Length == stopAt.Length) break;
            int idx = current.LastIndexOf('\\');
            if (idx < 0) break;
            current = current[..idx];
        }
    }
}

/// <summary>
/// Windows denied writing a registry value even for an administrator: key with a restricted ACL
/// (e.g. TrustedInstaller) or value protected by a kernel filter (e.g. the UCPD driver, which blocks
/// values such as HKCU\...\Explorer\Advanced\TaskbarDa and HKLM\SOFTWARE\Policies\Microsoft\Dsh\AllowNewsAndInterests
/// on recent builds). The program does NOT take ownership of system keys.
/// </summary>
public sealed class ProtectedRegistryException(ActionSpec spec, Exception inner) : InvalidOperationException(
    $"key protected by the system: Windows denied writing {spec.Target} even as administrator " +
    "(restricted ACL or system protection, such as the UCPD driver). The program does not take ownership of system keys; " +
    "this tweak cannot be applied by this mechanism on this build.", inner)
{
    public ActionSpec Spec { get; } = spec;
}
