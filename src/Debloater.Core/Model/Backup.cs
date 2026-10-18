namespace Debloater.Core.Model;

/// <summary>Original value of an action, captured before applying.</summary>
public sealed class ActionBackup
{
    public string Type { get; set; } = "";

    /// <summary>Target (see <see cref="ActionSpec.Target"/>); used to match the action on restore.</summary>
    public string Target { get; set; } = "";

    /// <summary>Whether the target/value existed before.</summary>
    public bool Existed { get; set; }

    /// <summary>registry: type of the original value.</summary>
    public string? Kind { get; set; }

    /// <summary>Original data as text (registry: decimal/hex/text; service: Start; task/feature: state).</summary>
    public string? Data { get; set; }

    /// <summary>registry multiString: original lines.</summary>
    public List<string>? MultiData { get; set; }

    /// <summary>Extra data (service: DelayedAutostart; registry: first key that did not exist).</summary>
    public string? Extra { get; set; }
}

/// <summary>Backup file of a tweak (%ProgramData%\Debloater\backups\&lt;id&gt;.json).</summary>
public sealed class TweakBackup
{
    public string TweakId { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string? WindowsBuild { get; set; }
    public List<ActionBackup> Actions { get; set; } = [];
}
