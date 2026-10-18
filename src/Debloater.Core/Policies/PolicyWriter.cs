using System.Globalization;
using Debloater.Core.Admx;
using RegValue = Debloater.Core.Admx.AdmxPolicyStateReader.RegValue;

namespace Debloater.Core.Policies;

/// <summary>
/// Calcula, a partir da definição ADMX, exatamente quais valores do registro uma configuração de
/// política grava e apaga — a mesma semântica do Editor de Política de Grupo (gpedit) depois que o
/// cliente de GP processa o Registry.pol:
/// <list type="bullet">
/// <item><b>Habilitada</b>: valueName ← enabledValue (padrão REG_DWORD 1); itens de enabledList; cada
/// elemento grava o seu valor (decimal → REG_DWORD ou REG_SZ com storeAsText; longDecimal → REG_QWORD;
/// text → REG_SZ/REG_EXPAND_SZ; multiText → REG_MULTI_SZ; boolean → trueValue/falseValue (padrão 1/0)
/// + trueList/falseList; enum → valor do item + valueList do item; list → um valor por item).</item>
/// <item><b>Desabilitada</b>: valueName ← disabledValue (sem disabledValue: o valor é apagado); itens
/// de disabledList; valores dos elementos são apagados (gpedit grava "**del." / "**delvals.").</item>
/// <item><b>Não configurada</b>: apaga todos os valores que a política possui (valueName, itens das
/// listas enabled/disabled, valores dos elementos, valueList dos enums, trueList/falseList, valores
/// das chaves de list) e apaga a chave de cada list que ficar vazia. Nada mais é tocado.</item>
/// </list>
/// Valores que a política possui mas a configuração não grava são apagados (o cliente de GP remove o
/// que não está mais na política). Listas: sem additive, a chave da lista fica exatamente com os itens
/// pedidos; com additive, os itens existentes são mantidos e os novos são acrescentados (com
/// valuePrefix, a numeração continua depois do maior índice existente, sem sobrescrever).
/// </summary>
public static class PolicyWriter
{
    /// <summary>maxLength padrão de text/multiText no esquema ADMX.</summary>
    public const int DefaultTextMaxLength = 1023;

    // ---------------- plano ----------------

    public static PolicyWritePlan Plan(AdmxPolicy policy, PolicySetting setting, IPolicyRegistry registry)
    {
        var scope = setting.EffectiveScope(policy.Class);
        var plan = new PolicyWritePlan { Policy = policy, Setting = setting, Scope = scope };
        if (!PolicySetting.ScopeAllowed(policy.Class, scope))
        {
            plan.Errors.Add($"a política é da classe {policy.Class}: não pode ser gravada em {PolicySetting.ScopeText(scope)}.");
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
                        targets.Set(policy.Key, vn, PolicyValues.FromAdmx(policy.EnabledValue ?? new AdmxValue(AdmxValueKind.Decimal, "1")), "valor da política (habilitada)");
                    foreach (var item in policy.EnabledList ?? [])
                        targets.Set(item.Key, item.ValueName, PolicyValues.FromAdmx(item.Value), "enabledList");
                    CheckUnknownElements(policy, setting, plan.Errors);
                    foreach (var el in policy.Elements)
                        EncodeElement(policy, el, setting, scope, registry, targets, additiveKeep, plan);
                    break;

                case PolicyTargetState.Disabled:
                    if (policy.ValueName is { } dvn)
                        targets.Set(policy.Key, dvn, policy.DisabledValue is { } off ? PolicyValues.FromAdmx(off) : null, "valor da política (desabilitada)");
                    foreach (var item in policy.DisabledList ?? [])
                        targets.Set(item.Key, item.ValueName, PolicyValues.FromAdmx(item.Value), "disabledList");
                    if (setting.Values.Count > 0) plan.Notes.Add("valores dos elementos são ignorados: a política fica desabilitada.");
                    if (policy.ValueName is null && policy.DisabledList is not { Count: > 0 })
                        plan.Notes.Add("esta política não grava nada quando desabilitada; no registro, Desabilitada fica igual a Não configurada.");
                    break;

                default:
                    if (setting.Values.Count > 0) plan.Notes.Add("valores dos elementos são ignorados: a política fica não configurada.");
                    break;
            }
        }
        catch (FormatException ex)
        {
            plan.Errors.Add(ex.Message);
        }
        if (!plan.IsValid) return plan;

        // Tudo o que a política possui e não foi gravado acima é apagado.
        var owned = OwnedValues(policy);
        var ownedSet = new HashSet<(string, string)>(owned.Select(o => Targets.Norm(o.Key, o.Name)));
        foreach (var el in policy.Elements.Where(e => e.Type == "list"))
        {
            if (additiveKeep.Contains(el)) continue;
            foreach (var name in OwnedListValues(el, scope, registry, ownedSet))
                if (!targets.Has(el.Key, name)) targets.Set(el.Key, name, null, $"lista '{ElementName(el)}'");
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
            var known = policy.Elements.Count == 0 ? "a política não tem elementos" : "elementos: " + string.Join(", ", policy.Elements.Select(e => e.Id));
            errors.Add($"elemento '{id}' não existe nesta política ({known}).");
        }
    }

    private static List<string>? Input(PolicySetting setting, AdmxElement el)
    {
        if (setting.Values.TryGetValue(el.Id, out var v)) return v;
        foreach (var (k, list) in setting.Values)
            if (k.Equals(el.Id, StringComparison.OrdinalIgnoreCase)) return list;
        return null;
    }

    /// <summary>Valor escalar: o informado, senão o padrão da apresentação, senão null.</summary>
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
        var origin = $"elemento '{name}'";
        switch (el.Type)
        {
            case "decimal":
            case "longDecimal":
            {
                var text = Scalar(setting, el)?.Trim();
                if (string.IsNullOrEmpty(text))
                {
                    if (el.Required) errors.Add($"{origin}: valor obrigatório.");
                    return;
                }
                if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                {
                    errors.Add($"{origin}: '{text}' não é um número inteiro não negativo.");
                    return;
                }
                ulong min = el.MinValue ?? 0, max = el.MaxValue ?? (el.Type == "decimal" ? uint.MaxValue : ulong.MaxValue);
                if (n < min || n > max)
                {
                    errors.Add($"{origin}: {n} fora do intervalo permitido ({min}..{max}).");
                    return;
                }
                if (el.Type == "decimal" && n > uint.MaxValue)
                {
                    errors.Add($"{origin}: {n} não cabe num REG_DWORD.");
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
                    errors.Add($"{origin}: '{text}' não é um booleano (use true/false).");
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
                    if (el.Required) errors.Add($"{origin}: escolha uma opção.");
                    return;
                }
                var index = ResolveEnum(el, text);
                if (index is not { } i)
                {
                    var options = string.Join(", ", (el.Items ?? []).Select((it, k) => $"{EnumToken(el, k)} ({it.DisplayName})"));
                    errors.Add($"{origin}: opção '{text}' não existe (opções: {options}).");
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
                    if (el.Required) errors.Add($"{origin}: texto obrigatório.");
                    return;
                }
                int maxLength = el.MaxLength ?? DefaultTextMaxLength;
                if (text.Length > maxLength)
                {
                    errors.Add($"{origin}: texto com {text.Length} caracteres; o máximo é {maxLength}.");
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
                    if (el.Required) errors.Add($"{origin}: informe ao menos uma linha.");
                    return;
                }
                int maxLength = el.MaxLength ?? DefaultTextMaxLength;
                if (lines.FirstOrDefault(l => l.Length > maxLength) is { } tooLong)
                {
                    errors.Add($"{origin}: a linha '{Short(tooLong)}' tem {tooLong.Length} caracteres; o máximo é {maxLength}.");
                    return;
                }
                if (el.MaxStrings is > 0 and var maxStrings && lines.Count > maxStrings)
                {
                    errors.Add($"{origin}: {lines.Count} linhas; o máximo é {maxStrings}.");
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
                plan.Notes.Add($"elemento '{el.Id}' de tipo desconhecido '{el.Type}' foi ignorado.");
                return;
        }
    }

    private static void EncodeList(AdmxElement el, List<string> input, PolicyScope scope, IPolicyRegistry registry,
        Targets targets, HashSet<AdmxElement> additiveKeep, PolicyWritePlan plan, string origin)
    {
        var items = input.Select(i => i.Trim()).Where(i => i.Length > 0).ToList();
        if (items.Count == 0 && el.Required)
        {
            plan.Errors.Add($"{origin}: informe ao menos um item.");
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
                    plan.Errors.Add($"{origin}: item '{Short(item)}' deve ser 'nome=valor'.");
                    return;
                }
                var n = item[..eq].Trim();
                if (entries.Any(e => e.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
                {
                    plan.Errors.Add($"{origin}: nome '{n}' repetido.");
                    return;
                }
                entries.Add((n, item[(eq + 1)..].Trim()));
            }
        }
        else
        {
            var distinct = items.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (distinct.Count < items.Count) plan.Notes.Add($"{origin}: itens repetidos foram ignorados.");
            if (el.ValuePrefix is { } prefix)
            {
                int start = 1;
                if (el.Additive)
                {
                    // Aditiva: não sobrescreve itens existentes; numera depois do maior índice e não repete dados.
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

    // ---------------- posse de valores ----------------

    /// <summary>Valores (chave, nome) que a política possui, exceto os das chaves de list.</summary>
    public static List<(string Key, string Name, string Origin)> OwnedValues(AdmxPolicy policy)
    {
        var result = new List<(string, string, string)>();
        var seen = new HashSet<(string, string)>();
        foreach (var (key, name, el) in policy.StaticValues())
        {
            var origin = el is null ? "valor da política" : $"elemento '{ElementName(el)}'";
            if (seen.Add(Targets.Norm(key, name))) result.Add((key, name, origin));
        }
        return result;
    }

    /// <summary>
    /// O nome de valor pertence à list? Com explicitValue ou sem valuePrefix, todo valor nomeado da
    /// chave pertence; com valuePrefix "P", só "P1", "P2"... (prefixo vazio: "1", "2"...). Valores fixos
    /// de qualquer política na mesma chave (<see cref="AdmxElement.ReservedValueNames"/>) nunca pertencem.
    /// </summary>
    public static bool ListOwns(AdmxElement el, string valueName) => el.OwnsListValue(valueName);

    private static int? ListIndex(string prefix, string valueName) => AdmxElement.ListIndex(prefix, valueName);

    private static IEnumerable<string> OwnedListValues(AdmxElement el, PolicyScope scope, IPolicyRegistry registry, HashSet<(string, string)> staticOwned) =>
        registry.ValueNames(scope, el.Key).Where(n => ListOwns(el, n) && !staticOwned.Contains(Targets.Norm(el.Key, n)));

    /// <summary>Itens atuais de uma list no registro (nome, dado), em ordem.</summary>
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

    // ---------------- leitura da configuração atual ----------------

    /// <summary>
    /// Lê do registro o estado e os valores atuais de cada elemento (para preencher o editor e
    /// salvar perfis). Elementos sem valor no registro ficam de fora.
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
                    // Item com <delete/> casa com "ausente": só vale se a política está habilitada.
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

    // ---------------- enum e booleano ----------------

    /// <summary>Identificador estável de um item de enum: o valor gravado (se único entre os itens), senão "#índice".</summary>
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

    /// <summary>Índice do item: aceita o token, "#índice", o valor gravado ou o nome exibido.</summary>
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

    // ---------------- alvos ----------------

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
