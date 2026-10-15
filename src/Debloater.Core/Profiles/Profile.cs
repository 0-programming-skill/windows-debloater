using YamlDotNet.Serialization;
using Debloater.Core.Model;

namespace Debloater.Core.Profiles;

/// <summary>Estado desejado de um tweak: aplicado ou não aplicado (revertido).</summary>
public enum DesiredState
{
    Applied,
    NotApplied,
}

/// <summary>Um tweak citado explicitamente no perfil.</summary>
public sealed class ProfileEntry
{
    public string Id { get; set; } = "";

    /// <summary>applied (padrão) ou notApplied.</summary>
    public DesiredState Desired { get; set; } = DesiredState.Applied;
}

/// <summary>
/// Regras que selecionam tweaks do catálogo (todos os selecionados ficam "aplicado"). Permitem
/// perfis que acompanham o catálogo sem listar cada id.
/// </summary>
public sealed class ProfileRules
{
    /// <summary>Risco máximo incluído (inclusive).</summary>
    public RiskLevel MaxRisk { get; set; } = RiskLevel.Low;

    /// <summary>Prefixos de categoria (ex.: "Privacidade", "IA/Copilot e Recall"). Vazio = todas.</summary>
    public List<string> Categories { get; set; } = [];

    /// <summary>Ids excluídos mesmo que casem com as regras.</summary>
    public List<string> ExcludeIds { get; set; } = [];

    /// <summary>Incluir tweaks não reversíveis (ex.: remoção de apps). Padrão: sim.</summary>
    public bool IncludeIrreversible { get; set; } = true;
}

/// <summary>
/// Perfil: uma seleção de estados desejados. Formato YAML:
/// <code>
/// name: Seguro
/// description: Só ajustes de risco baixo.
/// rules: { maxRisk: low, categories: [Privacidade] }   # opcional
/// tweaks:                                               # opcional; prevalece sobre as regras
///   - { id: privacy.telemetry.feedback-requests, desired: applied }
///   - { id: ui.explorer.hide-file-extensions, desired: notApplied }
/// </code>
/// </summary>
public sealed class Profile
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public ProfileRules? Rules { get; set; }
    public List<ProfileEntry> Tweaks { get; set; } = [];

    /// <summary>Arquivo de origem (preenchido pelo carregador).</summary>
    [YamlIgnore]
    public string? SourceFile { get; set; }

    /// <summary>Perfil embutido (pasta catalog/profiles); somente leitura na interface.</summary>
    [YamlIgnore]
    public bool IsBuiltIn { get; set; }

    public override string ToString() => Name;
}

/// <summary>Resultado de resolver um perfil contra o catálogo.</summary>
public sealed record ProfileResolution(IReadOnlyDictionary<string, DesiredState> Desired, IReadOnlyList<string> UnknownIds);
