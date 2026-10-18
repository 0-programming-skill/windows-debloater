using Debloater.Core.Policies;
using RegValue = Debloater.Core.Admx.AdmxPolicyStateReader.RegValue;

namespace Debloater.Core.Tests;

/// <summary>Registro em memória para testar o gravador de políticas sem tocar no registro real.</summary>
public sealed class MemoryPolicyRegistry : IPolicyRegistry
{
    private readonly Dictionary<(PolicyScope, string), Dictionary<string, (string Name, RegValue Value)>> _keys = [];

    private static string Norm(string key) => key.Trim('\\').ToUpperInvariant();

    private Dictionary<string, (string, RegValue)>? Get(PolicyScope scope, string key) =>
        _keys.TryGetValue((scope, Norm(key)), out var k) ? k : null;

    public RegValue? Read(PolicyScope scope, string key, string name) =>
        Get(scope, key) is { } k && k.TryGetValue(name.ToUpperInvariant(), out var v) ? v.Item2 : null;

    public IReadOnlyList<string> ValueNames(PolicyScope scope, string key) =>
        Get(scope, key) is { } k ? k.Values.Select(v => v.Item1).ToList() : [];

    public bool KeyExists(PolicyScope scope, string key) => Get(scope, key) is not null;

    public void Write(PolicyScope scope, string key, string name, RegValue value)
    {
        CreateKey(scope, key);
        Get(scope, key)![name.ToUpperInvariant()] = (name, value);
    }

    public void Delete(PolicyScope scope, string key, string name) => Get(scope, key)?.Remove(name.ToUpperInvariant());

    public bool DeleteKeyIfEmpty(PolicyScope scope, string key)
    {
        var k = Get(scope, key);
        if (k is null || k.Count > 0) return false;
        var prefix = Norm(key) + "\\";
        if (_keys.Keys.Any(x => x.Item1 == scope && x.Item2.StartsWith(prefix, StringComparison.Ordinal))) return false;
        _keys.Remove((scope, Norm(key)));
        return true;
    }

    public void CreateKey(PolicyScope scope, string key)
    {
        var parts = key.Trim('\\').Split('\\');
        for (int i = 1; i <= parts.Length; i++)
            _keys.TryAdd((scope, Norm(string.Join('\\', parts.Take(i)))), []);
    }

    public string Describe(PolicyScope scope) => scope == PolicyScope.Machine ? "HKLM" : "HKCU";

    /// <summary>Executa o plano (como o engine faz, sem backup).</summary>
    public void Execute(PolicyWritePlan plan)
    {
        foreach (var c in plan.Effective)
        {
            if (c.After is { } v) Write(plan.Scope, c.Key, c.Name, v);
            else Delete(plan.Scope, c.Key, c.Name);
        }
        foreach (var k in plan.KeysToDelete) DeleteKeyIfEmpty(plan.Scope, k);
    }

    /// <summary>Instantâneo de todos os valores: "ESCOPO\chave\nome" → descrição.</summary>
    public SortedDictionary<string, string> Snapshot(PolicyScope? only = null)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var ((scope, key), values) in _keys)
        {
            if (only is { } o && o != scope) continue;
            foreach (var (_, (name, value)) in values)
                result[$@"{PolicySetting.ScopeText(scope)}\{key}\{name.ToUpperInvariant()}"] = PolicyValues.Describe(value);
        }
        return result;
    }
}
