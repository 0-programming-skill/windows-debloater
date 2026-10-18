using System.Globalization;
using System.Security;
using Microsoft.Win32;

namespace Debloater.Core.Admx;

/// <summary>Estado de uma política ADMX no registro deste PC.</summary>
public enum AdmxPolicyState
{
    /// <summary>Nenhum valor da política presente.</summary>
    NotConfigured,
    Enabled,
    Disabled,
    /// <summary>Há valores, mas não correspondem exatamente a Habilitada nem a Desabilitada.</summary>
    Unknown,
}

/// <summary>Estado de uma política num escopo (HKLM ou HKCU), com os valores encontrados.</summary>
public sealed record AdmxScopeState(string Scope, AdmxPolicyState State, IReadOnlyList<string> Values);

/// <summary>
/// Lê, somente leitura, o estado atual de uma política ADMX (Não configurada / Habilitada /
/// Desabilitada / valores). Segue a semântica do ADMX: sem &lt;enabledValue&gt;, "habilitada" grava
/// REG_DWORD 1; sem &lt;disabledValue&gt;, "desabilitada" apaga o valor (o que no registro é
/// indistinguível de "não configurada").
/// </summary>
public static class AdmxPolicyStateReader
{
    public static IReadOnlyList<AdmxScopeState> Read(AdmxPolicy policy)
    {
        var result = new List<AdmxScopeState>();
        if (policy.Class is AdmxClass.Machine or AdmxClass.Both) result.Add(Read(policy, RegistryHive.LocalMachine));
        if (policy.Class is AdmxClass.User or AdmxClass.Both) result.Add(Read(policy, RegistryHive.CurrentUser));
        return result;
    }

    /// <summary>Estado combinado: o de HKLM prevalece sobre o de HKCU, como no Windows.</summary>
    public static AdmxPolicyState Combined(IReadOnlyList<AdmxScopeState> scopes) =>
        scopes.FirstOrDefault(s => s.State != AdmxPolicyState.NotConfigured)?.State ?? AdmxPolicyState.NotConfigured;

    public static AdmxScopeState Read(AdmxPolicy policy, RegistryHive hive)
    {
        var scope = hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU";
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            return Evaluate(policy, scope, (key, name) => ReadValue(baseKey, key, name), key => ListValues(baseKey, key));
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return new AdmxScopeState(scope, AdmxPolicyState.Unknown, [$"sem acesso: {ex.Message}"]);
        }
    }

    /// <summary>Valor lido do registro: tipo + dado.</summary>
    public readonly record struct RegValue(RegistryValueKind Kind, object Data)
    {
        public override string ToString() => Data switch
        {
            string[] lines => string.Join("|", lines),
            byte[] bytes => Convert.ToHexString(bytes),
            int i => unchecked((uint)i).ToString(CultureInfo.InvariantCulture),
            _ => Convert.ToString(Data, CultureInfo.InvariantCulture) ?? "",
        };
    }

    /// <summary>Avalia o estado com leitores injetados (testável sem tocar no registro real).</summary>
    public static AdmxScopeState Evaluate(AdmxPolicy policy, string scope,
        Func<string, string, RegValue?> read, Func<string, IReadOnlyList<(string Name, RegValue Value)>> listValues)
    {
        int enabledEvidence = 0, disabledEvidence = 0;
        bool anyPresent = false;
        var found = new List<string>();

        void Note(string key, string name, RegValue? v)
        {
            if (v is null) return;
            anyPresent = true;
            var label = key.Equals(policy.Key, StringComparison.OrdinalIgnoreCase) ? name : $@"{key}\{name}";
            var entry = $"{label}={v}";
            if (!found.Contains(entry)) found.Add(entry);
        }

        if (policy.ValueName is { } valueName)
        {
            var v = read(policy.Key, valueName);
            Note(policy.Key, valueName, v);
            var on = policy.EnabledValue ?? new AdmxValue(AdmxValueKind.Decimal, "1");
            if (Matches(on, v)) enabledEvidence++;
            if (policy.DisabledValue is { Kind: not AdmxValueKind.Delete } off && Matches(off, v)) disabledEvidence++;
        }

        if (policy.EnabledList is { Count: > 0 } enabledList && ListMatches(enabledList, read, Note)) enabledEvidence++;
        if (policy.DisabledList is { Count: > 0 } disabledList && disabledList.Any(i => i.Value.Kind != AdmxValueKind.Delete)
            && ListMatches(disabledList, read, Note)) disabledEvidence++;

        foreach (var el in policy.Elements)
        {
            if (el.Type == "list")
            {
                var values = listValues(el.Key)
                    .Where(x => el.OwnsListValue(x.Name))
                    .ToList();
                foreach (var (name, value) in values) Note(el.Key, name, value);
                if (values.Count > 0) enabledEvidence++;
                continue;
            }
            if (el.ValueName is null) continue;
            var v = read(el.Key, el.ValueName);
            Note(el.Key, el.ValueName, v);
            if (v is not null) enabledEvidence++;
        }

        var state = enabledEvidence > disabledEvidence ? AdmxPolicyState.Enabled
            : disabledEvidence > enabledEvidence ? AdmxPolicyState.Disabled
            : anyPresent ? AdmxPolicyState.Unknown
            : AdmxPolicyState.NotConfigured;
        return new AdmxScopeState(scope, state, found);
    }

    private static bool ListMatches(List<AdmxListItem> list, Func<string, string, RegValue?> read, Action<string, string, RegValue?> note)
    {
        bool all = true;
        foreach (var item in list)
        {
            var v = read(item.Key, item.ValueName);
            note(item.Key, item.ValueName, v);
            if (!Matches(item.Value, v)) all = false;
        }
        return all;
    }

    /// <summary>O valor do registro corresponde ao valor ADMX esperado?</summary>
    public static bool Matches(AdmxValue expected, RegValue? actual)
    {
        if (expected.Kind == AdmxValueKind.Delete) return actual is null;
        if (actual is not { } v) return false;
        return expected.Kind switch
        {
            AdmxValueKind.Decimal or AdmxValueKind.LongDecimal =>
                TryNumber(v, out var n) && ulong.TryParse(expected.Data, NumberStyles.Integer, CultureInfo.InvariantCulture, out var e) && n == e,
            AdmxValueKind.String => v.Data is string s && string.Equals(s, expected.Data, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static bool TryNumber(RegValue v, out ulong n)
    {
        switch (v.Data)
        {
            case int i: n = unchecked((uint)i); return true;
            case long l: n = unchecked((ulong)l); return true;
            case string s when ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n): return true;
            default: n = 0; return false;
        }
    }

    private static RegValue? ReadValue(RegistryKey baseKey, string subKey, string name)
    {
        using var key = baseKey.OpenSubKey(subKey);
        if (key is null) return null;
        var data = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return data is null ? null : new RegValue(key.GetValueKind(name), data);
    }

    private static IReadOnlyList<(string, RegValue)> ListValues(RegistryKey baseKey, string subKey)
    {
        using var key = baseKey.OpenSubKey(subKey);
        if (key is null) return [];
        var list = new List<(string, RegValue)>();
        foreach (var name in key.GetValueNames())
        {
            var data = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (data is not null) list.Add((name, new RegValue(key.GetValueKind(name), data)));
        }
        return list;
    }
}
