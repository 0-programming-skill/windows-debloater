namespace Debloater.Core.Model;

/// <summary>Valor original de uma ação, capturado antes de aplicar.</summary>
public sealed class ActionBackup
{
    public string Type { get; set; } = "";

    /// <summary>Alvo (ver <see cref="ActionSpec.Target"/>); usado para casar com a ação na restauração.</summary>
    public string Target { get; set; } = "";

    /// <summary>Se o alvo/valor existia antes.</summary>
    public bool Existed { get; set; }

    /// <summary>registry: tipo do valor original.</summary>
    public string? Kind { get; set; }

    /// <summary>Dado original em texto (registry: decimal/hex/texto; service: Start; task/feature: estado).</summary>
    public string? Data { get; set; }

    /// <summary>registry multiString: linhas originais.</summary>
    public List<string>? MultiData { get; set; }

    /// <summary>Dado extra (service: DelayedAutostart; registry: primeira chave que não existia).</summary>
    public string? Extra { get; set; }
}

/// <summary>Arquivo de backup de um tweak (%ProgramData%\Debloater\backups\&lt;id&gt;.json).</summary>
public sealed class TweakBackup
{
    public string TweakId { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string? WindowsBuild { get; set; }
    public List<ActionBackup> Actions { get; set; } = [];
}
