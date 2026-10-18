using System.Globalization;
using Debloater.Core.Admx;
using Microsoft.Win32;
using RegValue = Debloater.Core.Admx.AdmxPolicyStateReader.RegValue;

namespace Debloater.Core.Policies;

/// <summary>Estado desejado de uma política, como no gpedit.</summary>
public enum PolicyTargetState
{
    NotConfigured,
    Enabled,
    Disabled,
}

/// <summary>Onde a política é gravada: Computador (HKLM) ou Usuário (HKCU).</summary>
public enum PolicyScope
{
    Machine,
    User,
}

/// <summary>
/// Configuração desejada de uma política ADMX: estado + valores dos elementos (só usados quando
/// Habilitada). Cada valor é uma lista de textos: elementos escalares (decimal, text, boolean, enum)
/// usam o primeiro item; multiText e list usam todos. Em listas com explicitValue, cada item é
/// "nome=valor". Booleanos aceitam true/false, 1/0, sim/não. Enums aceitam o valor gravado
/// (ex.: "1"), o nome exibido ou "#índice" (a partir de 0).
/// </summary>
public sealed class PolicySetting
{
    /// <summary>"Arquivo.admx:Nome".</summary>
    public string Policy { get; set; } = "";

    public PolicyTargetState State { get; set; }

    /// <summary>Null = padrão da classe (Machine para Machine/Both, User para User).</summary>
    public PolicyScope? Scope { get; set; }

    /// <summary>Id do elemento → valores. Ids sem entrada usam o padrão da apresentação.</summary>
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

    /// <summary>Mesma configuração (estado, escopo efetivo e valores)?</summary>
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

    /// <summary>A classe da política permite este escopo?</summary>
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
        PolicyTargetState.Enabled => "Habilitada",
        PolicyTargetState.Disabled => "Desabilitada",
        _ => "Não configurada",
    };

    public static string ScopeText(PolicyScope s) => s == PolicyScope.Machine ? "HKLM" : "HKCU";

    /// <summary>Aceita enabled/disabled/notConfigured (e variações em português).</summary>
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

/// <summary>Uma alteração de valor do registro calculada pelo <see cref="PolicyWriter"/>.</summary>
/// <param name="Key">Subchave relativa à raiz do escopo (ex.: Software\Policies\Microsoft\...).</param>
/// <param name="Before">Valor atual (null = ausente).</param>
/// <param name="After">Valor desejado (null = apagar).</param>
/// <param name="Origin">De onde vem (ex.: "valor da política", "elemento 'Count'").</param>
public sealed record PolicyRegistryChange(string Key, string Name, RegValue? Before, RegValue? After, string Origin)
{
    public bool IsNoOp => PolicyValues.Equal(Before, After);
    public bool IsDelete => After is null;
}

/// <summary>Plano de gravação de uma política: o que será escrito/apagado, com erros de validação.</summary>
public sealed class PolicyWritePlan
{
    public required AdmxPolicy Policy { get; init; }
    public required PolicySetting Setting { get; init; }
    public required PolicyScope Scope { get; init; }

    /// <summary>Todos os valores que a política possui e que esta configuração toca (inclui os que já estão certos).</summary>
    public List<PolicyRegistryChange> Changes { get; } = [];

    /// <summary>Chaves de lista que ficam vazias e são apagadas (só em "Não configurada").</summary>
    public List<string> KeysToDelete { get; } = [];

    public List<string> Errors { get; } = [];
    public List<string> Notes { get; } = [];

    public bool IsValid => Errors.Count == 0;
    public IEnumerable<PolicyRegistryChange> Effective => Changes.Where(c => !c.IsNoOp);
    public bool HasEffect => Effective.Any() || KeysToDelete.Count > 0;

    public string Root => PolicySetting.ScopeText(Scope);

    /// <summary>Prévia legível: "+" grava, "-" apaga, "=" já está assim.</summary>
    public IEnumerable<string> Describe(bool includeNoOps = false)
    {
        foreach (var c in Changes)
        {
            if (c.IsNoOp && !includeNoOps) continue;
            var path = $@"{Root}\{c.Key}\{(c.Name.Length == 0 ? "(Padrão)" : c.Name)}";
            if (c.IsNoOp) yield return $"= {path} = {PolicyValues.Describe(c.After)} (sem mudança)";
            else if (c.After is null) yield return $"- {path} (apagar; atual: {PolicyValues.Describe(c.Before)})";
            else yield return $"+ {path} = {PolicyValues.Describe(c.After)}" + (c.Before is null ? " (novo)" : $" (atual: {PolicyValues.Describe(c.Before)})");
        }
        foreach (var k in KeysToDelete) yield return $@"- {Root}\{k}\ (chave vazia da lista é apagada)";
    }
}

/// <summary>Conversões e comparações de valores do registro usados pelas políticas.</summary>
public static class PolicyValues
{
    public static RegValue Dword(uint v) => new(RegistryValueKind.DWord, unchecked((int)v));
    public static RegValue Qword(ulong v) => new(RegistryValueKind.QWord, unchecked((long)v));
    public static RegValue Sz(string v) => new(RegistryValueKind.String, v);
    public static RegValue ExpandSz(string v) => new(RegistryValueKind.ExpandString, v);
    public static RegValue MultiSz(IEnumerable<string> v) => new(RegistryValueKind.MultiString, v.ToArray());

    /// <summary>Converte um valor do ADMX (decimal/longDecimal/string/delete) para o registro. Delete → null.</summary>
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
                    throw new FormatException($"decimal inválido no ADMX: '{value.Data}'.");
                return Dword((uint)d);
            case AdmxValueKind.LongDecimal:
                if (!ulong.TryParse(value.Data, NumberStyles.Integer, CultureInfo.InvariantCulture, out var q))
                    throw new FormatException($"longDecimal inválido no ADMX: '{value.Data}'.");
                return Qword(q);
            default:
                throw new FormatException($"tipo de valor desconhecido: {value.Kind}.");
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
        if (v is not { } r) return "(ausente)";
        var text = r.Data switch
        {
            string s => $"\"{s}\"",
            string[] lines => "[" + string.Join(", ", lines.Select(l => $"\"{l}\"")) + "]",
            long l => unchecked((ulong)l).ToString(CultureInfo.InvariantCulture),
            _ => r.ToString(),
        };
        return $"{text} ({KindName(r.Kind)})";
    }

    /// <summary>Serializa para o backup (mesmo formato do backup dos tweaks).</summary>
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
        _ => throw new FormatException($"tipo de valor desconhecido no backup: '{kind}'."),
    };
}
