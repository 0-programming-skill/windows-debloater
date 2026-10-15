using YamlDotNet.Serialization;

namespace Debloater.Core.Model;

/// <summary>Nível de risco de um tweak. A ordem importa: é usada no controle de risco (risk gating).</summary>
public enum RiskLevel
{
    Low = 0,
    Medium = 1,
    High = 2,
    Critical = 3,
}

/// <summary>Um tweak do catálogo. É apenas DADO, carregado de YAML, sem lógica.</summary>
public sealed class Tweak
{
    /// <summary>Identificador estável, em inglês, separado por pontos (ex.: privacy.telemetry.allow-telemetry).</summary>
    public string Id { get; set; } = "";

    /// <summary>Nome curto exibido na interface (pt-BR).</summary>
    public string Name { get; set; } = "";

    /// <summary>Explicação do que o tweak faz (pt-BR).</summary>
    public string Description { get; set; } = "";

    /// <summary>Caminho de categoria separado por "/" (ex.: Privacidade/Telemetria). Se omitido, herda o do arquivo.</summary>
    public string Category { get; set; } = "";

    public RiskLevel Risk { get; set; } = RiskLevel.Low;

    /// <summary>Build mínimo do Windows (inclusive).</summary>
    public int? MinBuild { get; set; }

    /// <summary>Build máximo do Windows (inclusive).</summary>
    public int? MaxBuild { get; set; }

    public List<string> Tags { get; set; } = [];

    /// <summary>Aviso do que pode deixar de funcionar.</summary>
    public string? Breaks { get; set; }

    public List<string> References { get; set; } = [];

    /// <summary>Se o engine consegue desfazer o tweak a partir do backup.</summary>
    public bool Reversible { get; set; } = true;

    public List<ActionSpec> Actions { get; set; } = [];

    /// <summary>Arquivo YAML de origem (preenchido pelo loader).</summary>
    [YamlIgnore]
    public string? SourceFile { get; set; }

    public bool IsBuildInRange(int build) =>
        (MinBuild is null || build >= MinBuild) && (MaxBuild is null || build <= MaxBuild);

    public override string ToString() => $"{Id} ({Name})";
}

/// <summary>Formato de um arquivo de catálogo: categoria padrão + lista de tweaks.</summary>
public sealed class CatalogFile
{
    /// <summary>Categoria padrão para os tweaks do arquivo que não definem a sua.</summary>
    public string? Category { get; set; }

    public List<Tweak> Tweaks { get; set; } = [];
}
