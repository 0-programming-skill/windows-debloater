using System.Globalization;
using Debloater.Core.Admx;
using RegValue = Debloater.Core.Admx.AdmxPolicyStateReader.RegValue;

namespace Debloater.Core.Policies;

/// <summary>
/// Computes, from the ADMX definition, exactly which registry values a policy setting writes and
/// deletes — the same semantics as the Group Policy Editor (gpedit) after the GP client processes
/// Registry.pol:
/// <list type="bullet">
/// <item><b>Enabled</b>: valueName ← enabledValue (default REG_DWORD 1); enabledList items; each
/// element writes its value (decimal → REG_DWORD, or REG_SZ with storeAsText; longDecimal → REG_QWORD;
/// text → REG_SZ/REG_EXPAND_SZ; multiText → REG_MULTI_SZ; boolean → trueValue/falseValue (default 1/0)
/// + trueList/falseList; enum → item value + item valueList; list → one value per item).</item>
/// <item><b>Disabled</b>: valueName ← disabledValue (without disabledValue: the value is deleted);
/// disabledList items; element values are deleted (gpedit writes "**del." / "**delvals.").</item>
/// <item><b>Not configured</b>: deletes every value the policy owns (valueName, enabled/disabled list
/// items, element values, enum valueLists, trueList/falseList, values of list keys) and deletes the
/// key of each list left empty. Nothing else is touched.</item>
/// </list>
/// Values the policy owns that the setting does not write are deleted (the GP client removes what is
/// no longer in the policy). Lists: without additive, the list key ends up with exactly the requested
/// items; with additive, existing items are kept and the new ones are appended (with valuePrefix,
/// numbering continues after the highest existing index, without overwriting).
/// </summary>
public static class PolicyWriter
{
    /// <summary>Default maxLength of text/multiText in the ADMX schema.</summary>
    public const int DefaultTextMaxLength = 1023;

    // ---------------- plan ----------------

    public static PolicyWritePlan Plan(AdmxPolicy policy, PolicySetting setting, IPolicyRegistry registry)
    {
        var scope = setting.EffectiveScope(policy.Class);
        var plan = new PolicyWritePlan { Policy = policy, Setting = setting, Scope = scope };
        if (!PolicySetting.ScopeAllowed(policy.Class, scope))
        {
            plan.Errors.Add($"the policy is of class {policy.Class}: it cannot be written to {PolicySetting.ScopeText(scope)}.");
            return plan;
        }

        var targets = new Targets();
        var additiveKeep = new HashSet<AdmxElement>();
        try
        {
            switch (setting.State)
            {
                case PolicyTargetState.Enabled:
                    if (policy.ValueName is { } vn)
                        targets.Set(policy.Key, vn, PolicyValues.FromAdmx(policy.EnabledValue ?? new AdmxValue(AdmxValueKind.Decimal, "1")), "policy value (enabled)");
                    foreach (var item in policy.EnabledList ?? [])
                        targets.Set(item.Key, item.ValueName, PolicyValues.FromAdmx(item.Value), "enabledList");
                    CheckUnknownElements(policy, setting, plan.Errors);
                    foreach (var el in policy.Elements)
                        EncodeElement(policy, el, setting, scope, registry, targets, additiveKeep, plan);
                    break;

                case PolicyTargetState.Disabled:
                    if (policy.ValueName is { } dvn)
                        targets.Set(policy.Key, dvn, policy.DisabledValue is { } off ? PolicyValues.FromAdmx(off) : null, "policy value (disabled)");
                    foreach (var item in policy.DisabledList ?? [])
                        targets.Set(item.Key, item.ValueName, PolicyValues.FromAdmx(item.Value), "disabledList");
                    if (setting.Values.Count > 0) plan.Notes.Add("element values are ignored: the policy is disabled.");
                    if (policy.ValueName is null && policy.DisabledList is not { Count: > 0 })
                        plan.Notes.Add("this policy writes nothing when disabled; in the registry, Disabled is the same as Not configured.");
                    break;

                default:
                    if (setting.Values.Count > 0) plan.Notes.Add("element values are ignored: the policy is not configured.");
                    break;
            }
        }
        catch (FormatException ex)
        {
            plan.Errors.Add(ex.Message);
        }
        if (!plan.IsValid) return plan;

        // Everything the policy owns that was not written above is deleted.
        var owned = OwnedValues(policy);
        var ownedSet = new HashSet<(string, string)>(owned.Select(o => Targets.Norm(o.Key, o.Name)));
        foreach (var el in policy.Elements.Where(e => e.Type == "list"))
        {
            if (additiveKeep.Contains(el)) continue;
            foreach (var name in OwnedListValues(el, scope, registry, ownedSet))
                if (!targets.Has(el.Key, name)) targets.Set(el.Key, name, null, $"list '{ElementName(el)}'");
        }
        foreach (var (key, name, origin) in owned)
            if (!targets.Has(key, name)) targets.Set(key, name, null, origin);

        foreach (var t in targets.Items)
            plan.Changes.Add(new PolicyRegistryChange(t.Key, t.Name, registry.Read(scope, t.Key, t.Name), t.Value, t.Origin));

        if (setting.State == PolicyTargetState.NotConfigured)
        {
            var otherKeys = new HashSet<string>(owned.Select(o => Norm(o.Key)), StringComparer.Ordinal) { Norm(policy.Key) };
            foreach (var el in policy.Elements.Where(e => e.Type == "list"))
            {
                if (otherKeys.Contains(Norm(el.Key)) || plan.KeysToDelete.Any(k => Norm(k) == Norm(el.Key))) continue;
                if (!registry.KeyExists(scope, el.Key)) continue;
                var remaining = registry.ValueNames(scope, el.Key)
                    .Where(n => !plan.Changes.Any(c => c.IsDelete && Norm(c.Key) == Norm(el.Key) && string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase)));
                if (!remaining.Any()) plan.KeysToDelete.Add(el.Key);
            }
        }
        return plan;
    }

    private static string Norm(string key) => key.Trim('\\').ToUpperInvariant();

    private static string ElementName(AdmxElement el) => el.Label is { Length: > 0 } l ? $"{el.Id}: {l.TrimEnd(':')}" : el.Id;

    private static void CheckUnknownElements(AdmxPolicy policy, PolicySetting setting, List<string> errors)
    {
        foreach (var id in setting.Values.Keys)
        {
            if (policy.Elements.Any(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) continue;
            var known = policy.Elements.Count == 0 ? "the policy has no elements" : "elements: " + string.Join(", ", policy.Elements.Select(e => e.Id));
            errors.Add($"element '{id}' does not exist in this policy ({known}).");
        }
    }

    private static List<string>? Input(PolicySetting setting, AdmxElement el)
    {
        if (setting.Values.TryGetValue(el.Id, out var v)) return v;
        foreach (var (k, list) in setting.Values)
            if (k.Equals(el.Id, StringComparison.OrdinalIgnoreCase)) return list;
        return null;
    }

    /// <summary>Scalar value: the given one, else the presentation default, else null.</summary>
    private static string? Scalar(PolicySetting setting, AdmxElement el, bool useDefault = true)
    {
        var input = Input(setting, el);
        if (input is not null) return input.Count == 0 ? null : input[0];
        return useDefault ? el.DefaultValue : null;
    }

    private static void EncodeElement(AdmxPolicy policy, AdmxElement el, PolicySetting setting, PolicyScope scope,
        IPolicyRegistry registry, Targets targets, HashSet<AdmxElement> additiveKeep, PolicyWritePlan plan)
    {
        var errors = plan.Errors;
        var name = ElementName(el);
        var origin = $"element '{name}'";
        switch (el.Type)
        {
            case "decimal":
            case "longDecimal":
            {
                var text = Scalar(setting, el)?.Trim();
                if (string.IsNullOrEmpty(text))
                {
                    if (el.Required) errors.Add($"{origin}: value required.");
                    return;
                }
                if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                {
                    errors.Add($"{origin}: '{text}' is not a non-negative integer.");
                    return;
                }
                ulong min = el.MinValue ?? 0, max = el.MaxValue ?? (el.Type == "decimal" ? uint.MaxValue : ulong.MaxValue);
                if (n < min || n > max)
                {
                    errors.Add($"{origin}: {n} is outside the allowed range ({min}..{max}).");
                    return;
                }
                if (el.Type == "decimal" && n > uint.MaxValue)
                {
                    errors.Add($"{origin}: {n} does not fit in a REG_DWORD.");
                    return;
                }
                if (el.ValueName is null) return;
                var value = el.StoreAsText ? PolicyValues.Sz(n.ToString(CultureInfo.InvariantCulture))
                    : el.Type == "decimal" ? PolicyValues.Dword((uint)n) : PolicyValues.Qword(n);
                targets.Set(el.Key, el.ValueName, value, origin);
                return;
            }

            case "boolean":
            {
                var text = Scalar(setting, el);
                bool on = false;
                if (text is not null && !TryParseBool(text, out on))
                {
                    errors.Add($"{origin}: '{text}' is not a boolean (use true/false).");
                    return;
                }
                if (el.ValueName is { } vn)
                {
                    var v = on ? el.TrueValue ?? new AdmxValue(AdmxValueKind.Decimal, "1") : el.FalseValue ?? new AdmxValue(AdmxValueKind.Decimal, "0");
                    targets.Set(el.Key, vn, PolicyValues.FromAdmx(v), origin);
                }
                foreach (var item in (on ? el.TrueList : el.FalseList) ?? [])
                    targets.Set(item.Key, item.ValueName, PolicyValues.FromAdmx(item.Value), origin + (on ? " (trueList)" : " (falseList)"));
                return;
            }

            case "enum":
            {
                var text = Scalar(setting, el);
                if (string.IsNullOrWhiteSpace(text))
                {
                    if (el.Required) errors.Add($"{origin}: choose an option.");
                    return;
                }
                var index = ResolveEnum(el, text);
                if (index is not { } i)
                {
                    var options = string.Join(", ", (el.Items ?? []).Select((it, k) => $"{EnumToken(el, k)} ({it.DisplayName})"));
                    errors.Add($"{origin}: option '{text}' does not exist (options: {options}).");
                    return;
                }
                var chosen = el.Items![i];
                if (el.ValueName is { } vn) targets.Set(el.Key, vn, PolicyValues.FromAdmx(chosen.Value), origin);
                foreach (var item in chosen.ValueList ?? [])
                    targets.Set(item.Key, item.ValueName, PolicyValues.FromAdmx(item.Value), origin + $" ({chosen.DisplayName})");
                return;
            }

            case "text":
            {
                var text = Scalar(setting, el);
                if (string.IsNullOrEmpty(text))
                {
                    if (el.Required) errors.Add($"{origin}: text required.");
                    return;
                }
                int maxLength = el.MaxLength ?? DefaultTextMaxLength;
                if (text.Length > maxLength)
                {
                    errors.Add($"{origin}: text has {text.Length} characters; the maximum is {maxLength}.");
                    return;
                }
                if (el.ValueName is null) return;
                targets.Set(el.Key, el.ValueName, el.Expandable ? PolicyValues.ExpandSz(text) : PolicyValues.Sz(text), origin);
                return;
            }

            case "multiText":
            {
                var lines = (Input(setting, el) ?? [])
                    .SelectMany(l => l.Replace("\r\n", "\n").Split('\n'))
                    .Where(l => l.Length > 0)
                    .ToList();
                if (lines.Count == 0)
                {
                    if (el.Required) errors.Add($"{origin}: enter at least one line.");
                    return;
                }
                int maxLength = el.MaxLength ?? DefaultTextMaxLength;
                if (lines.FirstOrDefault(l => l.Length > maxLength) is { } tooLong)
                {
                    errors.Add($"{origin}: line '{Short(tooLong)}' has {tooLong.Length} characters; the maximum is {maxLength}.");
                    return;
                }
                if (el.MaxStrings is > 0 and var maxStrings && lines.Count > maxStrings)
                {
                    errors.Add($"{origin}: {lines.Count} lines; the maximum is {maxStrings}.");
                    return;
                }
                if (el.ValueName is null) return;
                targets.Set(el.Key, el.ValueName, PolicyValues.MultiSz(lines), origin);
                return;
            }

            case "list":
                EncodeList(el, Input(setting, el) ?? [], scope, registry, targets, additiveKeep, plan, origin);
                return;

            default:
                plan.Notes.Add($"element '{el.Id}' of unknown type '{el.Type}' was ignored.");
                return;
        }
    }

    private static void EncodeList(AdmxElement el, List<string> input, PolicyScope scope, IPolicyRegistry registry,
        Targets targets, HashSet<AdmxElement> additiveKeep, PolicyWritePlan plan, string origin)
    {
        var items = input.Select(i => i.Trim()).Where(i => i.Length > 0).ToList();
        if (items.Count == 0 && el.Required)
        {
            plan.Errors.Add($"{origin}: enter at least one item.");
            return;
        }

        RegValue Make(string data) => el.Expandable ? PolicyValues.ExpandSz(data) : PolicyValues.Sz(data);
        var entries = new List<(string Name, string Data)>();
        if (el.ExplicitValue)
        {
            foreach (var item in items)
            {
                int eq = item.IndexOf('=');
                if (eq <= 0)
                {
                    plan.Errors.Add($"{origin}: item '{Short(item)}' must be 'name=value'.");
                    return;
                }
                var n = item[..eq].Trim();
                if (entries.Any(e => e.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
                {
                    plan.Errors.Add($"{origin}: duplicate name '{n}'.");
                    return;
                }
                entries.Add((n, item[(eq + 1)..].Trim()));
            }
        }
        else
        {
            var distinct = items.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (distinct.Count < items.Count) plan.Notes.Add($"{origin}: duplicate items were ignored.");
            if (el.ValuePrefix is { } prefix)
            {
                int start = 1;
                if (el.Additive)
                {
                    // Additive: does not overwrite existing items; numbers after the highest index and does not repeat data.
                    var existing = ListEntries(el, scope, registry);
                    start = existing.Count == 0 ? 1 : existing.Max(e => ListIndex(prefix, e.Name) ?? 0) + 1;
                    distinct = distinct.Where(d => !existing.Any(e => string.Equals(e.Data, d, StringComparison.OrdinalIgnoreCase))).ToList();
                }
                for (int i = 0; i < distinct.Count; i++)
                    entries.Add((prefix + (start + i).ToString(CultureInfo.InvariantCulture), distinct[i]));
            }
            else
            {
                entries.AddRange(distinct.Select(d => (d, d)));
            }
        }

        if (el.Additive) additiveKeep.Add(el);
        foreach (var (n, data) in entries) targets.Set(el.Key, n, Make(data), origin);
    }

    private static string Short(string s) => s.Length > 40 ? s[..37] + "..." : s;

    // ---------------- value ownership ----------------

    /// <summary>(key, name) values the policy owns, except those of list keys.</summary>
    public static List<(string Key, string Name, string Origin)> OwnedValues(AdmxPolicy policy)
    {
        var result = new List<(string, string, string)>();
        var seen = new HashSet<(string, string)>();
        foreach (var (key, name, el) in policy.StaticValues())
        {
            var origin = el is null ? "policy value" : $"element '{ElementName(el)}'";
            if (seen.Add(Targets.Norm(key, name))) result.Add((key, name, origin));
        }
        return result;
    }

    /// <summary>
    /// Does the value name belong to the list? With explicitValue or without valuePrefix, every named
    /// value of the key belongs; with valuePrefix "P", only "P1", "P2"... (empty prefix: "1", "2"...).
    /// Fixed values of any policy on the same key (<see cref="AdmxElement.ReservedValueNames"/>) never belong.
    /// </summary>
    public static bool ListOwns(AdmxElement el, string valueName) => el.OwnsListValue(valueName);

    private static int? ListIndex(string prefix, string valueName) => AdmxElement.ListIndex(prefix, valueName);

    private static IEnumerable<string> OwnedListValues(AdmxElement el, PolicyScope scope, IPolicyRegistry registry, HashSet<(string, string)> staticOwned) =>
        registry.ValueNames(scope, el.Key).Where(n => ListOwns(el, n) && !staticOwned.Contains(Targets.Norm(el.Key, n)));

    /// <summary>Current items of a list in the registry (name, data), in order.</summary>
    public static List<(string Name, string Data)> ListEntries(AdmxElement el, PolicyScope scope, IPolicyRegistry registry)
    {
        var entries = new List<(string Name, string Data)>();
        foreach (var n in registry.ValueNames(scope, el.Key).Where(n => ListOwns(el, n)))
        {
            var v = registry.Read(scope, el.Key, n);
            if (v is null) continue;
            entries.Add((n, v.Value.Data as string ?? v.Value.ToString()));
        }
        if (el.ValuePrefix is { } prefix && !el.ExplicitValue)
            entries = entries.OrderBy(e => ListIndex(prefix, e.Name) ?? int.MaxValue).ToList();
        return entries;
    }

    // ---------------- reading the current setting ----------------

    /// <summary>
    /// Reads the current state and each element's current values from the registry (to fill the
    /// editor and save profiles). Elements without a value in the registry are left out.
    /// </summary>
    public static (AdmxPolicyState State, PolicySetting Setting) ReadCurrent(AdmxPolicy policy, PolicyScope scope, IPolicyRegistry registry)
    {
        var state = AdmxPolicyStateReader.Evaluate(policy, PolicySetting.ScopeText(scope),
            (k, n) => registry.Read(scope, k, n),
            k => registry.ValueNames(scope, k).Select(n => (n, registry.Read(scope, k, n))).Where(x => x.Item2 is not null)
                .Select(x => (x.n, x.Item2!.Value)).ToList()).State;

        var setting = new PolicySetting
        {
            Policy = policy.QualifiedName,
            State = state switch
            {
                AdmxPolicyState.Enabled or AdmxPolicyState.Unknown => PolicyTargetState.Enabled,
                AdmxPolicyState.Disabled => PolicyTargetState.Disabled,
                _ => PolicyTargetState.NotConfigured,
            },
            Scope = policy.Class == AdmxClass.Both ? scope : null,
        };

        foreach (var el in policy.Elements)
        {
            RegValue? Read(string key, string name) => registry.Read(scope, key, name);
            var v = el.ValueName is { } vn ? Read(el.Key, vn) : null;
            switch (el.Type)
            {
                case "decimal":
                case "longDecimal":
                    if (v is { Data: int i }) setting.Set(el.Id, unchecked((uint)i).ToString(CultureInfo.InvariantCulture));
                    else if (v is { Data: long l }) setting.Set(el.Id, unchecked((ulong)l).ToString(CultureInfo.InvariantCulture));
                    else if (v is { Data: string s }) setting.Set(el.Id, s);
                    break;
                case "boolean":
                {
                    bool? on = null;
                    if (el.ValueName is not null)
                    {
                        if (AdmxPolicyStateReader.Matches(el.TrueValue ?? new AdmxValue(AdmxValueKind.Decimal, "1"), v)) on = true;
                        else if (AdmxPolicyStateReader.Matches(el.FalseValue ?? new AdmxValue(AdmxValueKind.Decimal, "0"), v)) on = false;
                    }
                    else if (el.TrueList is { Count: > 0 } tl && tl.All(x => AdmxPolicyStateReader.Matches(x.Value, Read(x.Key, x.ValueName)))) on = true;
                    else if (el.FalseList is { Count: > 0 } fl && fl.All(x => AdmxPolicyStateReader.Matches(x.Value, Read(x.Key, x.ValueName)))) on = false;
                    if (on is { } b) setting.Set(el.Id, b ? "true" : "false");
                    break;
                }
                case "enum":
                {
                    var items = el.Items ?? [];
                    bool ListOk(AdmxEnumItem it) => (it.ValueList ?? []).All(x => AdmxPolicyStateReader.Matches(x.Value, Read(x.Key, x.ValueName)));
                    int found = -1;
                    for (int k = 0; k < items.Count && found < 0; k++)
                        if ((el.ValueName is null || AdmxPolicyStateReader.Matches(items[k].Value, v)) && ListOk(items[k])
                            && (el.ValueName is not null || items[k].ValueList is { Count: > 0 })) found = k;
                    if (found < 0 && el.ValueName is not null && v is not null)
                        for (int k = 0; k < items.Count && found < 0; k++)
                            if (AdmxPolicyStateReader.Matches(items[k].Value, v)) found = k;
                    // An item with <delete/> matches "absent": only counts if the policy is enabled.
                    if (found >= 0 && (v is not null || state is AdmxPolicyState.Enabled)) setting.Set(el.Id, EnumToken(el, found));
                    break;
                }
                case "text":
                    if (v is { Data: string t }) setting.Set(el.Id, t);
                    break;
                case "multiText":
                    if (v is { Data: string[] lines }) setting.Set(el.Id, lines);
                    else if (v is { Data: string single }) setting.Set(el.Id, single);
                    break;
                case "list":
                {
                    var entries = ListEntries(el, scope, registry);
                    if (entries.Count == 0) break;
                    setting.Set(el.Id, [.. entries.Select(e => el.ExplicitValue ? $"{e.Name}={e.Data}" : e.Data)]);
                    break;
                }
            }
        }
        return (state, setting);
    }

    // ---------------- enum and boolean ----------------

    /// <summary>Stable identifier of an enum item: the stored value (if unique among the items), else "#index".</summary>
    public static string EnumToken(AdmxElement el, int index)
    {
        var items = el.Items ?? [];
        var item = items[index];
        if (item.Value.Kind != AdmxValueKind.Delete && item.Value.Data is { } data
            && items.Count(i => i.Value.Kind != AdmxValueKind.Delete && string.Equals(i.Value.Data, data, StringComparison.OrdinalIgnoreCase)) == 1
            && !data.StartsWith('#'))
            return data;
        return "#" + index.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Item index: accepts the token, "#index", the stored value or the display name.</summary>
    public static int? ResolveEnum(AdmxElement el, string input)
    {
        var items = el.Items ?? [];
        input = input.Trim();
        if (input.StartsWith('#') && int.TryParse(input[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var idx))
            return idx < items.Count ? idx : null;
        for (int i = 0; i < items.Count; i++)
            if (string.Equals(EnumToken(el, i), input, StringComparison.OrdinalIgnoreCase)) return i;
        for (int i = 0; i < items.Count; i++)
            if (items[i].Value.Kind != AdmxValueKind.Delete && string.Equals(items[i].Value.Data, input, StringComparison.OrdinalIgnoreCase)) return i;
        for (int i = 0; i < items.Count; i++)
            if (string.Equals(items[i].DisplayName, input, StringComparison.OrdinalIgnoreCase)) return i;
        return null;
    }

    public static bool TryParseBool(string text, out bool value)
    {
        // The Portuguese words are user input accepted for compatibility (CLI arguments, saved settings).
        switch (text.Trim().ToLowerInvariant())
        {
            case "true": case "1": case "sim": case "yes": case "on": case "marcado": case "verdadeiro":
                value = true; return true;
            case "false": case "0": case "não": case "nao": case "no": case "off": case "desmarcado": case "falso":
                value = false; return true;
            default:
                value = false; return false;
        }
    }

    // ---------------- targets ----------------

    private sealed class Targets
    {
        private readonly Dictionary<(string, string), int> _index = [];
        public List<(string Key, string Name, RegValue? Value, string Origin)> Items { get; } = [];

        public static (string, string) Norm(string key, string name) => (key.Trim('\\').ToUpperInvariant(), name.ToUpperInvariant());

        public bool Has(string key, string name) => _index.ContainsKey(Norm(key, name));

        public void Set(string key, string name, RegValue? value, string origin)
        {
            var k = Norm(key, name);
            if (_index.TryGetValue(k, out var i)) Items[i] = (Items[i].Key, Items[i].Name, value, origin);
            else
            {
                _index[k] = Items.Count;
                Items.Add((key.Trim('\\'), name, value, origin));
            }
        }
    }
}
