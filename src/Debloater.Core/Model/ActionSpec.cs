using YamlDotNet.Serialization;

namespace Debloater.Core.Model;

/// <summary>
/// "Flat" specification of an action. The <see cref="Type"/> field selects the handler; each handler
/// uses only the fields that concern it and validates them. This keeps the YAML easy to write by hand,
/// with no special tags or discriminators.
///
/// Fields per type:
///   registry        path, name, kind, value | delete: true
///   service         name, startup
///   scheduledTask   path, state
///   appx            name, state (removed), storeId
///   optionalFeature name, state (enabled | disabled)
///   powershell      detect, apply, revert
///   featureFlag     id, state (enabled | disabled | default), priority (user | userPolicy | test; default user)
///
/// <see cref="AllowedFields"/> lists the fields of each type; the loader rejects the others.
/// </summary>
public sealed class ActionSpec
{
    public const string Registry = "registry";
    public const string Service = "service";
    public const string ScheduledTask = "scheduledTask";
    public const string Appx = "appx";
    public const string OptionalFeature = "optionalFeature";
    public const string PowerShell = "powershell";
    public const string FeatureFlag = "featureFlag";

    /// <summary>YAML fields (besides "type") that each action type accepts.</summary>
    public static IReadOnlyDictionary<string, string[]> AllowedFields { get; } = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [Registry] = ["path", "name", "kind", "value", "delete"],
        [Service] = ["name", "startup"],
        [ScheduledTask] = ["path", "state"],
        [Appx] = ["name", "state", "storeId"],
        [OptionalFeature] = ["name", "state"],
        [PowerShell] = ["detect", "apply", "revert"],
        [FeatureFlag] = ["id", "state", "priority"],
    };

    /// <summary>YAML names of the fields that are set (besides "type").</summary>
    public IEnumerable<string> SetFields()
    {
        if (Path is not null) yield return "path";
        if (Name is not null) yield return "name";
        if (Kind is not null) yield return "kind";
        if (Value is not null) yield return "value";
        if (Delete) yield return "delete";
        if (Startup is not null) yield return "startup";
        if (State is not null) yield return "state";
        if (StoreId is not null) yield return "storeId";
        if (Detect is not null) yield return "detect";
        if (Apply is not null) yield return "apply";
        if (Revert is not null) yield return "revert";
        if (FeatureId is not null) yield return "id";
        if (Priority is not null) yield return "priority";
    }

    public string Type { get; set; } = "";

    /// <summary>registry: key (HKLM\..., HKCU\...). scheduledTask: full task path.</summary>
    public string? Path { get; set; }

    /// <summary>registry: value name ("" = default value). service/appx/optionalFeature: target name.</summary>
    public string? Name { get; set; }

    /// <summary>registry: dword | qword | string | expandString | multiString | binary.</summary>
    public string? Kind { get; set; }

    /// <summary>registry: desired value. Scalar, or a list for multiString. binary = hex ("01 0A FF").</summary>
    public object? Value { get; set; }

    /// <summary>registry: requires the value to NOT exist.</summary>
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitDefaults)]
    public bool Delete { get; set; }

    /// <summary>service: disabled | manual | automatic | automaticDelayed.</summary>
    public string? Startup { get; set; }

    /// <summary>scheduledTask: enabled | disabled. appx: removed. optionalFeature: enabled | disabled. featureFlag: enabled | disabled | default.</summary>
    public string? State { get; set; }

    /// <summary>appx: Microsoft Store ID to reinstall via winget (optional).</summary>
    public string? StoreId { get; set; }

    /// <summary>powershell: script that prints True (desired state) or False.</summary>
    public string? Detect { get; set; }

    /// <summary>powershell: script that applies.</summary>
    public string? Apply { get; set; }

    /// <summary>powershell: script that undoes.</summary>
    public string? Revert { get; set; }

    /// <summary>featureFlag: numeric feature id (as shown by ViVeTool and in WIL symbols).</summary>
    [YamlMember(Alias = "id")]
    public uint? FeatureId { get; set; }

    /// <summary>featureFlag: priority of the override (user | userPolicy | test). Default: user.</summary>
    public string? Priority { get; set; }

    /// <summary>Stable description of the target, used to match backups with actions.</summary>
    [YamlIgnore]
    public string Target => Type switch
    {
        Registry => $"{Path}\\{Name}",
        ScheduledTask => Path ?? "",
        PowerShell => "powershell",
        FeatureFlag => $"{FeatureId}@{(string.IsNullOrWhiteSpace(Priority) ? "user" : Priority)}",
        _ => Name ?? "",
    };

    public override string ToString() => $"{Type}:{Target}";
}
