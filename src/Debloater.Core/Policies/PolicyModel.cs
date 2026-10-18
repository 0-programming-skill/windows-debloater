using System.Globalization;
using Debloater.Core.Admx;
using Microsoft.Win32;
using RegValue = Debloater.Core.Admx.AdmxPolicyStateReader.RegValue;

namespace Debloater.Core.Policies;

/// <summary>Desired state of a policy, as in gpedit.</summary>
public enum PolicyTargetState
{
    NotConfigured,
    Enabled,
    Disabled,
}

/// <summary>Where the policy is written: Computer (HKLM) or User (HKCU).</summary>
public enum PolicyScope
{
    Machine,
    User,
}

/// <summary>
/// Desired setting of an ADMX policy: state + element values (only used when
/// Enabled). Each value is a list of strings: scalar elements (decimal, text, boolean, enum)
/// use the first item; multiText and list use all of them. In lists with explicitValue, each item is
/// "name=value". Booleans accept true/false, 1/0, yes/no (and sim/não). Enums accept the stored value
/// (e.g. "1"), the display name or "#index" (0-based).
/// </summary>
public sealed class PolicySetting
{
    /// <summary>"File.admx:Name".</summary>
    public string Policy { get; set; } = "";

    public PolicyTargetState State { get; set; }

    /// <summary>Null = class default (Machine for Machine/Both, User for User).</summary>
    public PolicyScope? Scope { get; set; }

    /// <summary>Element id → values. Ids without an entry use the presentation default.</summary>
    public Dictionary<string, List<string>> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public PolicySetting Set(string elementId, params string[] values)
    {
        Values[elementId] = [.. values];
        return this;
    }

    public PolicySetting Clone() => new()
    {
        Policy = Policy,
        State = State,
        Scope = Scope,
        Values = Values.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.OrdinalIgnoreCase),
    };

    /// <summary>Same setting (state, effective scope and values)?</summary>
    public bool SameAs(PolicySetting other, AdmxClass cls)
    {
        if (State != other.State || EffectiveScope(cls) != other.EffectiveScope(cls)) return false;
        if (State != PolicyTargetState.Enabled) return true;
        if (Values.Count != other.Values.Count) return false;
        foreach (var (k, v) in Values)
            if (!other.Values.TryGetValue(k, out var o) || !v.SequenceEqual(o, StringComparer.Ordinal)) return false;
        return true;
    }

    public PolicyScope EffectiveScope(AdmxClass cls) => Scope ?? DefaultScope(cls);

    public static PolicyScope DefaultScope(AdmxClass cls) => cls == AdmxClass.User ? PolicyScope.User : PolicyScope.Machine;

    /// <summary>Does the policy class allow this scope?</summary>
    public static bool ScopeAllowed(AdmxClass cls, PolicyScope scope) => cls switch
    {
        AdmxClass.Machine => scope == PolicyScope.Machine,
        AdmxClass.User => scope == PolicyScope.User,
        _ => true,
    };

    public override string ToString()
    {
        var text = $"{Policy} = {StateText(State)}";
        if (Scope is { } s) text += $" ({ScopeText(s)})";
        if (State == PolicyTargetState.Enabled && Values.Count > 0)
            text += " [" + string.Join("; ", Values.Select(kv => $"{kv.Key}={string.Join("|", kv.Value)}")) + "]";
        return text;
    }

    public static string StateText(PolicyTargetState s) => s switch
    {
        PolicyTargetState.Enabled => "Enabled",
        PolicyTargetState.Disabled => "Disabled",
        _ => "Not configured",
    };

    public static string ScopeText(PolicyScope s) => s == PolicyScope.Machine ? "HKLM" : "HKCU";

    /// <summary>Accepts enabled/disabled/notConfigured (and Portuguese variants).</summary>
    // The Portuguese aliases are user input accepted for compatibility (CLI arguments, saved settings).
    public static bool TryParseState(string text, out PolicyTargetState state)
    {
        switch (text.Trim().ToLowerInvariant().Replace("-", "").Replace("_", "").Replace(" ", ""))
        {
            case "enabled": case "enable": case "habilitada": case "habilitar": case "on":
                state = PolicyTargetState.Enabled; return true;
            case "disabled": case "disable": case "desabilitada": case "desabilitar": case "off":
                state = PolicyTargetState.Disabled; return true;
            case "notconfigured": case "naoconfigurada": case "nãoconfigurada": case "unset": case "none":
                state = PolicyTargetState.NotConfigured; return true;
            default:
                state = default; return false;
        }
    }
}

/// <summary>A registry value change computed by <see cref="PolicyWriter"/>.</summary>
/// <param name="Key">Subkey relative to the scope root (e.g. Software\Policies\Microsoft\...).</param>
/// <param name="Before">Current value (null = absent).</param>
/// <param name="After">Desired value (null = delete).</param>
/// <param name="Origin">Where it comes from (e.g. "policy value", "element 'Count'").</param>
public sealed record PolicyRegistryChange(string Key, string Name, RegValue? Before, RegValue? After, string Origin)
{
    public bool IsNoOp => PolicyValues.Equal(Before, After);
    public bool IsDelete => After is null;
}

/// <summary>Write plan of a policy: what will be written/deleted, with validation errors.</summary>
public sealed class PolicyWritePlan
{
    public required AdmxPolicy Policy { get; init; }
    public required PolicySetting Setting { get; init; }
    public required PolicyScope Scope { get; init; }

    /// <summary>All values the policy owns that this setting touches (includes the ones already correct).</summary>
    public List<PolicyRegistryChange> Changes { get; } = [];

    /// <summary>List keys left empty that are deleted (only on "Not configured").</summary>
    public List<string> KeysToDelete { get; } = [];

    public List<string> Errors { get; } = [];
    public List<string> Notes { get; } = [];

    public bool IsValid => Errors.Count == 0;
    public IEnumerable<PolicyRegistryChange> Effective => Changes.Where(c => !c.IsNoOp);
    public bool HasEffect => Effective.Any() || KeysToDelete.Count > 0;

    public string Root => PolicySetting.ScopeText(Scope);

    /// <summary>Readable preview: "+" writes, "-" deletes, "=" already set.</summary>
    public IEnumerable<string> Describe(bool includeNoOps = false)
    {
        foreach (var c in Changes)
        {
            if (c.IsNoOp && !includeNoOps) continue;
            var path = $@"{Root}\{c.Key}\{(c.Name.Length == 0 ? "(Default)" : c.Name)}";
            if (c.IsNoOp) yield return $"= {path} = {PolicyValues.Describe(c.After)} (no change)";
            else if (c.After is null) yield return $"- {path} (delete; current: {PolicyValues.Describe(c.Before)})";
            else yield return $"+ {path} = {PolicyValues.Describe(c.After)}" + (c.Before is null ? " (new)" : $" (current: {PolicyValues.Describe(c.Before)})");
        }
        foreach (var k in KeysToDelete) yield return $@"- {Root}\{k}\ (empty list key is deleted)";
    }
}

/// <summary>Conversions and comparisons of registry values used by policies.</summary>
public static class PolicyValues
{
    public static RegValue Dword(uint v) => new(RegistryValueKind.DWord, unchecked((int)v));
    public static RegValue Qword(ulong v) => new(RegistryValueKind.QWord, unchecked((long)v));
    public static RegValue Sz(string v) => new(RegistryValueKind.String, v);
    public static RegValue ExpandSz(string v) => new(RegistryValueKind.ExpandString, v);
    public static RegValue MultiSz(IEnumerable<string> v) => new(RegistryValueKind.MultiString, v.ToArray());

    /// <summary>Converts an ADMX value (decimal/longDecimal/string/delete) to a registry value. Delete → null.</summary>
    public static RegValue? FromAdmx(AdmxValue value)
    {
        switch (value.Kind)
        {
            case AdmxValueKind.Delete:
                return null;
            case AdmxValueKind.String:
                return Sz(value.Data ?? "");
            case AdmxValueKind.Decimal:
                if (!ulong.TryParse(value.Data, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) || d > uint.MaxValue)
                    throw new FormatException($"invalid decimal in ADMX: '{value.Data}'.");
                return Dword((uint)d);
            case AdmxValueKind.LongDecimal:
                if (!ulong.TryParse(value.Data, NumberStyles.Integer, CultureInfo.InvariantCulture, out var q))
                    throw new FormatException($"invalid longDecimal in ADMX: '{value.Data}'.");
                return Qword(q);
            default:
                throw new FormatException($"unknown value type: {value.Kind}.");
        }
    }

    public static bool Equal(RegValue? a, RegValue? b)
    {
        if (a is null || b is null) return a is null && b is null;
        var (x, y) = (a.Value, b.Value);
        if (x.Kind != y.Kind) return false;
        return (x.Data, y.Data) switch
        {
            (int i, int j) => i == j,
            (long i, long j) => i == j,
            (string s, string t) => string.Equals(s, t, StringComparison.Ordinal),
            (string[] s, string[] t) => s.SequenceEqual(t, StringComparer.Ordinal),
            (byte[] s, byte[] t) => s.AsSpan().SequenceEqual(t),
            _ => false,
        };
    }

    public static string KindName(RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => "REG_DWORD",
        RegistryValueKind.QWord => "REG_QWORD",
        RegistryValueKind.String => "REG_SZ",
        RegistryValueKind.ExpandString => "REG_EXPAND_SZ",
        RegistryValueKind.MultiString => "REG_MULTI_SZ",
        RegistryValueKind.Binary => "REG_BINARY",
        _ => kind.ToString(),
    };

    public static string Describe(RegValue? v)
    {
        if (v is not { } r) return "(absent)";
        var text = r.Data switch
        {
            string s => $"\"{s}\"",
            string[] lines => "[" + string.Join(", ", lines.Select(l => $"\"{l}\"")) + "]",
            long l => unchecked((ulong)l).ToString(CultureInfo.InvariantCulture),
            _ => r.ToString(),
        };
        return $"{text} ({KindName(r.Kind)})";
    }

    /// <summary>Serializes for the backup (same format as the tweak backup).</summary>
    public static (string Kind, string? Data, List<string>? Multi) ToBackup(RegValue v) => v.Data switch
    {
        string[] lines => (KindToString(v.Kind), null, [.. lines]),
        int i => (KindToString(v.Kind), unchecked((uint)i).ToString(CultureInfo.InvariantCulture), null),
        long l => (KindToString(v.Kind), l.ToString(CultureInfo.InvariantCulture), null),
        byte[] b => (KindToString(v.Kind), Convert.ToHexString(b), null),
        _ => (KindToString(v.Kind), Convert.ToString(v.Data, CultureInfo.InvariantCulture), null),
    };

    public static RegValue FromBackup(string kind, string? data, List<string>? multi)
    {
        var k = ParseKind(kind);
        return k switch
        {
            RegistryValueKind.DWord => new RegValue(k, unchecked((int)uint.Parse(data ?? "0", CultureInfo.InvariantCulture))),
            RegistryValueKind.QWord => new RegValue(k, long.Parse(data ?? "0", CultureInfo.InvariantCulture)),
            RegistryValueKind.MultiString => new RegValue(k, (multi ?? []).ToArray()),
            RegistryValueKind.Binary or RegistryValueKind.None => new RegValue(k, Convert.FromHexString(data ?? "")),
            _ => new RegValue(k, data ?? ""),
        };
    }

    public static string KindToString(RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => "dword",
        RegistryValueKind.QWord => "qword",
        RegistryValueKind.String => "string",
        RegistryValueKind.ExpandString => "expandString",
        RegistryValueKind.MultiString => "multiString",
        RegistryValueKind.Binary => "binary",
        RegistryValueKind.None => "none",
        _ => "unknown",
    };

    public static RegistryValueKind ParseKind(string kind) => kind.ToLowerInvariant() switch
    {
        "dword" => RegistryValueKind.DWord,
        "qword" => RegistryValueKind.QWord,
        "string" => RegistryValueKind.String,
        "expandstring" => RegistryValueKind.ExpandString,
        "multistring" => RegistryValueKind.MultiString,
        "binary" => RegistryValueKind.Binary,
        "none" => RegistryValueKind.None,
        _ => throw new FormatException($"unknown value type in backup: '{kind}'."),
    };
}
