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
/// </summary>
public sealed class ActionSpec
{
    public const string Registry = "registry";
    public const string Service = "service";
    public const string ScheduledTask = "scheduledTask";
    public const string Appx = "appx";
    public const string OptionalFeature = "optionalFeature";
    public const string PowerShell = "powershell";

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

    /// <summary>scheduledTask: enabled | disabled. appx: removed. optionalFeature: enabled | disabled.</summary>
    public string? State { get; set; }

    /// <summary>appx: Microsoft Store ID to reinstall via winget (optional).</summary>
    public string? StoreId { get; set; }

    /// <summary>powershell: script that prints True (desired state) or False.</summary>
    public string? Detect { get; set; }

    /// <summary>powershell: script that applies.</summary>
    public string? Apply { get; set; }

    /// <summary>powershell: script that undoes.</summary>
    public string? Revert { get; set; }

    /// <summary>Stable description of the target, used to match backups with actions.</summary>
    [YamlIgnore]
    public string Target => Type switch
    {
        Registry => $"{Path}\\{Name}",
        ScheduledTask => Path ?? "",
        PowerShell => "powershell",
        _ => Name ?? "",
    };

    public override string ToString() => $"{Type}:{Target}";
}
