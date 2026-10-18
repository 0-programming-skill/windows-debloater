using System.Text.Json.Serialization;

namespace Debloater.Core.Admx;

/// <summary>Classe da política: onde ela é gravada (HKLM, HKCU ou ambos).</summary>
public enum AdmxClass
{
    Machine,
    User,
    Both,
}

public enum AdmxValueKind
{
    /// <summary>REG_DWORD.</summary>
    Decimal,
    /// <summary>REG_QWORD.</summary>
    LongDecimal,
    /// <summary>REG_SZ.</summary>
    String,
    /// <summary>Apaga o valor.</summary>
    Delete,
}

/// <summary>Um valor de registro definido no ADMX (&lt;decimal value="1"/&gt;, &lt;string&gt;x&lt;/string&gt;, &lt;delete/&gt;).</summary>
public sealed record AdmxValue(AdmxValueKind Kind, string? Data)
{
    public override string ToString() => Kind switch
    {
        AdmxValueKind.Delete => "(apagar)",
        AdmxValueKind.String => $"\"{Data}\"",
        _ => Data ?? "",
    };
}

/// <summary>Item de &lt;enabledList&gt;/&lt;disabledList&gt;/valueList: grava um valor numa chave (que pode ser outra).</summary>
public sealed record AdmxListItem(string Key, string ValueName, AdmxValue Value);

/// <summary>Opção de um elemento &lt;enum&gt;.</summary>
public sealed class AdmxEnumItem
{
    public string DisplayName { get; set; } = "";
    public AdmxValue Value { get; set; } = new(AdmxValueKind.Delete, null);

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AdmxListItem>? ValueList { get; set; }
}

/// <summary>Elemento de uma política (decimal, longDecimal, boolean, enum, text, multiText, list).</summary>
public sealed class AdmxElement
{
    public string Type { get; set; } = "";
    public string Id { get; set; } = "";

    /// <summary>Chave do elemento (a da política, se o elemento não definir outra).</summary>
    public string Key { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ValueName { get; set; }

    /// <summary>Rótulo da apresentação (ADML), quando houver.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; set; }

    public bool Required { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? MinValue { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? MaxValue { get; set; }

    /// <summary>decimal/longDecimal: grava como REG_SZ.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool StoreAsText { get; set; }

    /// <summary>text/list: grava como REG_EXPAND_SZ.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Expandable { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxLength { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AdmxEnumItem>? Items { get; set; }

    /// <summary>boolean: valor gravado quando marcado (padrão: decimal 1).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AdmxValue? TrueValue { get; set; }

    /// <summary>boolean: valor gravado quando desmarcado (padrão: decimal 0).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AdmxValue? FalseValue { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AdmxListItem>? TrueList { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AdmxListItem>? FalseList { get; set; }

    /// <summary>list: prefixo dos nomes dos valores (ex.: "1", "2"...).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ValuePrefix { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Additive { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ExplicitValue { get; set; }

    /// <summary>
    /// list: nomes de valores da chave da lista que alguma política do catálogo grava como valor fixo
    /// (valueName, enabledList, elementos...). Não são itens da lista: ex. DefaultConsent, de
    /// WerDefaultConsent, mora na mesma chave que a lista explicitValue de WerConsentCustomize.
    /// Preenchido pelo <see cref="AdmxParser"/> depois de ler todas as políticas.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ReservedValueNames { get; set; }

    /// <summary>
    /// list: o valor pertence à lista? Com explicitValue ou sem valuePrefix, todo valor nomeado da chave
    /// pertence; com valuePrefix "P", só "P1", "P2"... (prefixo vazio: "1", "2"...). Nunca os nomes de
    /// <see cref="ReservedValueNames"/>.
    /// </summary>
    public bool OwnsListValue(string valueName)
    {
        if (valueName.Length == 0) return false;
        if (ReservedValueNames is { Count: > 0 } reserved && reserved.Contains(valueName, StringComparer.OrdinalIgnoreCase)) return false;
        if (ExplicitValue || ValuePrefix is null) return true;
        return ListIndex(ValuePrefix, valueName) is not null;
    }

    /// <summary>Índice do item numa lista com prefixo ("P12" → 12), ou null.</summary>
    public static int? ListIndex(string prefix, string valueName)
    {
        if (!valueName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = valueName[prefix.Length..];
        return rest.Length > 0 && rest.All(char.IsAsciiDigit)
            && int.TryParse(rest, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
    }

    /// <summary>multiText: número máximo de linhas (0 ou null = sem limite).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxStrings { get; set; }

    /// <summary>
    /// Controle da apresentação (ADML) que edita o elemento: decimalTextBox, longDecimalTextBox, textBox,
    /// comboBox, checkBox, dropdownList, listBox, multiTextBox. Null quando a política não tem apresentação.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PresentationType { get; set; }

    /// <summary>
    /// Valor padrão da apresentação, em texto: número (decimalTextBox), texto (textBox/comboBox),
    /// "true"/"false" (checkBox) ou "#índice" (dropdownList defaultItem).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DefaultValue { get; set; }

    /// <summary>comboBox: sugestões da apresentação.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Suggestions { get; set; }
}

/// <summary>Um controle da apresentação (ADML), na ordem em que o gpedit o mostra.</summary>
/// <param name="Type">Nome do controle (decimalTextBox, checkBox, text...).</param>
/// <param name="RefId">Elemento editado (null para &lt;text&gt;, que é só um texto).</param>
/// <param name="Text">Rótulo ou texto.</param>
public sealed record AdmxPresentationControl(string Type, string? RefId, string? Text);

/// <summary>Uma política de Grupo definida num arquivo ADMX, com os textos do ADML resolvidos.</summary>
public sealed class AdmxPolicy
{
    /// <summary>Nome do arquivo (ex.: DataCollection.admx).</summary>
    public string File { get; set; } = "";

    /// <summary>Namespace alvo do ADMX (ex.: Microsoft.Policies.DataCollection).</summary>
    public string Namespace { get; set; } = "";

    public string Name { get; set; } = "";
    public AdmxClass Class { get; set; }
    public string DisplayName { get; set; } = "";
    public string ExplainText { get; set; } = "";

    /// <summary>Caminho de categorias (nomes exibidos), da raiz até a categoria da política.</summary>
    public List<string> CategoryPath { get; set; } = [];

    public string Key { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ValueName { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AdmxValue? EnabledValue { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AdmxValue? DisabledValue { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AdmxListItem>? EnabledList { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AdmxListItem>? DisabledList { get; set; }

    public List<AdmxElement> Elements { get; set; } = [];

    /// <summary>Controles da apresentação na ordem do ADML (inclui os &lt;text&gt; descritivos).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AdmxPresentationControl>? Presentation { get; set; }

    /// <summary>Texto "Com suporte em" resolvido (ex.: "At least Windows 10").</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SupportedOn { get; set; }

    /// <summary>Referência crua do supportedOn (ex.: windows:SUPPORTED_Windows_10_0).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SupportedOnRef { get; set; }

    /// <summary>Estado atual no PC (preenchido só quando pedido, ex.: 'admx export --with-state').</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<AdmxScopeState>? LiveState { get; set; }

    /// <summary>Identificador único "Arquivo.admx:Nome".</summary>
    [JsonIgnore]
    public string QualifiedName => $"{File}:{Name}";

    /// <summary>
    /// Valores fixos (chave, nome) que a política grava, sem os itens das listas: valueName, enabledList,
    /// disabledList e, de cada elemento, valueName, trueList/falseList e valueList dos itens de enum.
    /// </summary>
    public IEnumerable<(string Key, string Name, AdmxElement? Element)> StaticValues()
    {
        if (ValueName is { } vn) yield return (Key, vn, null);
        foreach (var i in EnabledList ?? []) yield return (i.Key, i.ValueName, null);
        foreach (var i in DisabledList ?? []) yield return (i.Key, i.ValueName, null);
        foreach (var el in Elements)
        {
            if (el.Type == "list") continue;
            if (el.ValueName is { } evn) yield return (el.Key, evn, el);
            foreach (var i in (el.TrueList ?? []).Concat(el.FalseList ?? [])) yield return (i.Key, i.ValueName, el);
            foreach (var item in el.Items ?? [])
                foreach (var i in item.ValueList ?? []) yield return (i.Key, i.ValueName, el);
        }
    }

    public override string ToString() => QualifiedName;
}

/// <summary>Resultado da leitura de uma pasta PolicyDefinitions.</summary>
public sealed class AdmxCatalog
{
    public string Directory { get; set; } = "";

    /// <summary>Idioma do ADML efetivamente usado em cada arquivo é registrado em <see cref="Languages"/>.</summary>
    public List<string> Languages { get; set; } = [];

    public int FileCount { get; set; }
    public List<AdmxPolicy> Policies { get; set; } = [];
    public List<string> Errors { get; set; } = [];
}
