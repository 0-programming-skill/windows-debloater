using YamlDotNet.Serialization;
using Debloater.Core.Model;
using Debloater.Core.Policies;

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
/// Uma política ADMX configurada pelo perfil. Formato YAML:
/// <code>
/// - { policy: "DataCollection.admx:AllowTelemetry", state: enabled, values: { AllowTelemetry: "0" } }
/// - { policy: "Explorer.admx:NoX", state: disabled }
/// - policy: Sample.admx:Hosts
///   state: enabled
///   scope: machine             # opcional (políticas de Computador e Usuário)
///   values: { Hosts: [a, b] }  # listas e multiText: sequência; list com explicitValue: mapa nome→valor
/// </code>
/// </summary>
public sealed class ProfilePolicyEntry
{
    /// <summary>"Arquivo.admx:Nome".</summary>
    public string Policy { get; set; } = "";

    public PolicyTargetState State { get; set; } = PolicyTargetState.Enabled;

    public PolicyScope? Scope { get; set; }

    /// <summary>Id do elemento → texto, sequência de textos ou mapa nome→valor.</summary>
    public Dictionary<string, object?>? Values { get; set; }

    /// <summary>Converte para <see cref="PolicySetting"/> (mapas viram itens "nome=valor").</summary>
    public PolicySetting ToSetting()
    {
        var setting = new PolicySetting { Policy = Policy.Trim(), State = State, Scope = Scope };
        foreach (var (id, raw) in Values ?? [])
        {
            setting.Values[id] = raw switch
            {
                null => [],
                string s => [s],
                IDictionary<object, object?> map => map.Select(kv => $"{kv.Key}={kv.Value}").ToList(),
                IDictionary<string, object?> smap => smap.Select(kv => $"{kv.Key}={kv.Value}").ToList(),
                IEnumerable<object?> seq => seq.Select(o => Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture) ?? "").ToList(),
                _ => [Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture) ?? ""],
            };
        }
        return setting;
    }

    /// <summary>Converte de <see cref="PolicySetting"/>: um valor vira texto; vários, sequência.</summary>
    public static ProfilePolicyEntry FromSetting(PolicySetting s) => new()
    {
        Policy = s.Policy,
        State = s.State,
        Scope = s.Scope,
        Values = s.State != PolicyTargetState.Enabled || s.Values.Count == 0
            ? null
            : s.Values.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Count == 1 ? (object?)kv.Value[0] : kv.Value.ToList()),
    };
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
/// policies:                                             # opcional; políticas ADMX (ver ProfilePolicyEntry)
///   - { policy: "DataCollection.admx:AllowTelemetry", state: enabled, values: { AllowTelemetry: "0" } }
/// </code>
/// </summary>
public sealed class Profile
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public ProfileRules? Rules { get; set; }
    public List<ProfileEntry> Tweaks { get; set; } = [];

    /// <summary>Políticas ADMX configuradas pelo perfil (opcional; null não é gravado no YAML).</summary>
    public List<ProfilePolicyEntry>? Policies { get; set; }

    /// <summary>Arquivo de origem (preenchido pelo carregador).</summary>
    [YamlIgnore]
    public string? SourceFile { get; set; }

    /// <summary>Perfil embutido (pasta catalog/profiles); somente leitura na interface.</summary>
    [YamlIgnore]
    public bool IsBuiltIn { get; set; }

    public override string ToString() => Name;
}

/// <summary>Resultado de resolver um perfil contra o catálogo.</summary>
public sealed record ProfileResolution(IReadOnlyDictionary<string, DesiredState> Desired, IReadOnlyList<string> UnknownIds)
{
    /// <summary>Configurações de políticas do perfil (sem conferir contra o ADMX; ver <see cref="ProfileStore.ResolvePolicies"/>).</summary>
    public IReadOnlyList<PolicySetting> Policies { get; init; } = [];
}
