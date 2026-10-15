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

/// <summary>
/// Quão bem documentado está o mecanismo do tweak (opcional no YAML).
/// official: documentado pela Microsoft (Policy CSP em learn.microsoft.com);
/// admx: corresponde a uma política de um modelo administrativo (ADMX) oficial;
/// community: conhecido pela comunidade (ferramentas consolidadas), sem documentação oficial;
/// questionable: há indícios de que não funciona como descrito (valor fora do previsto, edição...).
/// </summary>
public enum Confidence
{
    Official,
    Admx,
    Community,
    Questionable,
}

/// <summary>Fonte que sustenta um tweak (ex.: { kind: msdocs, ref: "Policy CSP - System/AllowTelemetry" }).</summary>
public sealed class SourceRef
{
    public const string MsDocs = "msdocs";
    public const string AdmxKind = "admx";
    public const string Url = "url";
    public const string CommunityKind = "community";

    public static IReadOnlyList<string> KnownKinds { get; } = [MsDocs, AdmxKind, Url, CommunityKind];

    /// <summary>msdocs | admx | url | community.</summary>
    public string Kind { get; set; } = "";

    /// <summary>msdocs: "Policy CSP - Área/Nome"; admx: "Arquivo.admx:Política"; url/community: endereço ou descrição.</summary>
    public string Ref { get; set; } = "";

    public override string ToString() => $"{Kind}:{Ref}";
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

    /// <summary>Nível de confiança (opcional). Ver <see cref="Model.Confidence"/>.</summary>
    public Confidence? Confidence { get; set; }

    /// <summary>Fontes que sustentam o tweak (opcional).</summary>
    public List<SourceRef> Sources { get; set; } = [];

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
