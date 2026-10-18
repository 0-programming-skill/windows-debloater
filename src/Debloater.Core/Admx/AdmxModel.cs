using System.Text.Json.Serialization;

namespace Debloater.Core.Admx;

/// <summary>Policy class: where it is written (HKLM, HKCU or both).</summary>
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
    /// <summary>Deletes the value.</summary>
    Delete,
}

/// <summary>A registry value defined in the ADMX (&lt;decimal value="1"/&gt;, &lt;string&gt;x&lt;/string&gt;, &lt;delete/&gt;).</summary>
public sealed record AdmxValue(AdmxValueKind Kind, string? Data)
{
    public override string ToString() => Kind switch
    {
        AdmxValueKind.Delete => "(delete)",
        AdmxValueKind.String => $"\"{Data}\"",
        _ => Data ?? "",
    };
}

/// <summary>Item of &lt;enabledList&gt;/&lt;disabledList&gt;/valueList: writes a value to a key (which may be a different one).</summary>
public sealed record AdmxListItem(string Key, string ValueName, AdmxValue Value);

/// <summary>Option of an &lt;enum&gt; element.</summary>
public sealed class AdmxEnumItem
{
    public string DisplayName { get; set; } = "";
    public AdmxValue Value { get; set; } = new(AdmxValueKind.Delete, null);

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AdmxListItem>? ValueList { get; set; }
}

/// <summary>Element of a policy (decimal, longDecimal, boolean, enum, text, multiText, list).</summary>
public sealed class AdmxElement
{
    public string Type { get; set; } = "";
    public string Id { get; set; } = "";

    /// <summary>Key of the element (the policy's, if the element does not define another).</summary>
    public string Key { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ValueName { get; set; }

    /// <summary>Presentation (ADML) label, if any.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; set; }

    public bool Required { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? MinValue { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? MaxValue { get; set; }

    /// <summary>decimal/longDecimal: written as REG_SZ.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool StoreAsText { get; set; }

    /// <summary>text/list: written as REG_EXPAND_SZ.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Expandable { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxLength { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AdmxEnumItem>? Items { get; set; }

    /// <summary>boolean: value written when checked (default: decimal 1).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AdmxValue? TrueValue { get; set; }

    /// <summary>boolean: value written when unchecked (default: decimal 0).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AdmxValue? FalseValue { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AdmxListItem>? TrueList { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AdmxListItem>? FalseList { get; set; }

    /// <summary>list: prefix of the value names (e.g. "1", "2"...).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ValuePrefix { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Additive { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ExplicitValue { get; set; }

    /// <summary>
    /// list: value names in the list's key that some policy in the catalog writes as a fixed value
    /// (valueName, enabledList, elements...). They are not list items: e.g. DefaultConsent, from
    /// WerDefaultConsent, lives in the same key as the explicitValue list of WerConsentCustomize.
    /// Filled in by <see cref="AdmxParser"/> after reading all policies.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ReservedValueNames { get; set; }

    /// <summary>
    /// list: does the value belong to the list? With explicitValue or without valuePrefix, every named value
    /// in the key belongs; with valuePrefix "P", only "P1", "P2"... (empty prefix: "1", "2"...). Never the names in
    /// <see cref="ReservedValueNames"/>.
    /// </summary>
    public bool OwnsListValue(string valueName)
    {
        if (valueName.Length == 0) return false;
        if (ReservedValueNames is { Count: > 0 } reserved && reserved.Contains(valueName, StringComparer.OrdinalIgnoreCase)) return false;
        if (ExplicitValue || ValuePrefix is null) return true;
        return ListIndex(ValuePrefix, valueName) is not null;
    }

    /// <summary>Index of the item in a prefixed list ("P12" → 12), or null.</summary>
    public static int? ListIndex(string prefix, string valueName)
    {
        if (!valueName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = valueName[prefix.Length..];
        return rest.Length > 0 && rest.All(char.IsAsciiDigit)
            && int.TryParse(rest, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
    }

    /// <summary>multiText: maximum number of lines (0 or null = no limit).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxStrings { get; set; }

    /// <summary>
    /// Presentation (ADML) control that edits the element: decimalTextBox, longDecimalTextBox, textBox,
    /// comboBox, checkBox, dropdownList, listBox, multiTextBox. Null when the policy has no presentation.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PresentationType { get; set; }

    /// <summary>
    /// Presentation default value, as text: number (decimalTextBox), text (textBox/comboBox),
    /// "true"/"false" (checkBox) or "#index" (dropdownList defaultItem).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DefaultValue { get; set; }

    /// <summary>comboBox: presentation suggestions.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Suggestions { get; set; }
}

/// <summary>A presentation (ADML) control, in the order gpedit shows it.</summary>
/// <param name="Type">Control name (decimalTextBox, checkBox, text...).</param>
/// <param name="RefId">Edited element (null for &lt;text&gt;, which is just text).</param>
/// <param name="Text">Label or text.</param>
public sealed record AdmxPresentationControl(string Type, string? RefId, string? Text);

/// <summary>A Group Policy defined in an ADMX file, with the ADML texts resolved.</summary>
public sealed class AdmxPolicy
{
    /// <summary>File name (e.g. DataCollection.admx).</summary>
    public string File { get; set; } = "";

    /// <summary>Target namespace of the ADMX (e.g. Microsoft.Policies.DataCollection).</summary>
    public string Namespace { get; set; } = "";

    public string Name { get; set; } = "";
    public AdmxClass Class { get; set; }
    public string DisplayName { get; set; } = "";
    public string ExplainText { get; set; } = "";

    /// <summary>Category path (display names), from the root to the policy's category.</summary>
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

    /// <summary>Presentation controls in ADML order (includes the descriptive &lt;text&gt; items).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AdmxPresentationControl>? Presentation { get; set; }

    /// <summary>Resolved "Supported on" text (e.g. "At least Windows 10").</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SupportedOn { get; set; }

    /// <summary>Raw supportedOn reference (e.g. windows:SUPPORTED_Windows_10_0).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SupportedOnRef { get; set; }

    /// <summary>Current state on the PC (filled in only on request, e.g. 'admx export --with-state').</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<AdmxScopeState>? LiveState { get; set; }

    /// <summary>Unique identifier "File.admx:Name".</summary>
    [JsonIgnore]
    public string QualifiedName => $"{File}:{Name}";

    /// <summary>
    /// Fixed values (key, name) the policy writes, excluding list items: valueName, enabledList,
    /// disabledList and, for each element, valueName, trueList/falseList and the valueList of enum items.
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

/// <summary>Result of reading a PolicyDefinitions folder.</summary>
public sealed class AdmxCatalog
{
    public string Directory { get; set; } = "";

    /// <summary>The ADML languages actually used across the files.</summary>
    public List<string> Languages { get; set; } = [];

    public int FileCount { get; set; }
    public List<AdmxPolicy> Policies { get; set; } = [];
    public List<string> Errors { get; set; } = [];
}
