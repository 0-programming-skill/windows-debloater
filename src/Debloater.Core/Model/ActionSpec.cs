using YamlDotNet.Serialization;

namespace Debloater.Core.Model;

/// <summary>
/// Especificação "plana" de uma ação. O campo <see cref="Type"/> escolhe o handler; cada handler
/// usa só os campos que lhe dizem respeito e os valida. Assim o YAML fica fácil de escrever à mão,
/// sem tags nem discriminadores especiais.
///
/// Campos por tipo:
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

    /// <summary>registry: chave (HKLM\..., HKCU\...). scheduledTask: caminho completo da tarefa.</summary>
    public string? Path { get; set; }

    /// <summary>registry: nome do valor ("" = valor padrão). service/appx/optionalFeature: nome do alvo.</summary>
    public string? Name { get; set; }

    /// <summary>registry: dword | qword | string | expandString | multiString | binary.</summary>
    public string? Kind { get; set; }

    /// <summary>registry: valor desejado. Escalar, ou lista para multiString. binary = hex ("01 0A FF").</summary>
    public object? Value { get; set; }

    /// <summary>registry: exige que o valor NÃO exista.</summary>
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitDefaults)]
    public bool Delete { get; set; }

    /// <summary>service: disabled | manual | automatic | automaticDelayed.</summary>
    public string? Startup { get; set; }

    /// <summary>scheduledTask: enabled | disabled. appx: removed. optionalFeature: enabled | disabled.</summary>
    public string? State { get; set; }

    /// <summary>appx: ID da Microsoft Store para reinstalar via winget (opcional).</summary>
    public string? StoreId { get; set; }

    /// <summary>powershell: script que imprime True (estado desejado) ou False.</summary>
    public string? Detect { get; set; }

    /// <summary>powershell: script que aplica.</summary>
    public string? Apply { get; set; }

    /// <summary>powershell: script que desfaz.</summary>
    public string? Revert { get; set; }

    /// <summary>Descrição estável do alvo, usada para casar backups com ações.</summary>
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
