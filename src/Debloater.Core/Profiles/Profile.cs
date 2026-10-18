using Debloater.Core.Localization;
using YamlDotNet.Serialization;
using Debloater.Core.Model;
using Debloater.Core.Policies;

namespace Debloater.Core.Profiles;

/// <summary>Desired state of a tweak: applied or not applied (reverted).</summary>
public enum DesiredState
{
    Applied,
    NotApplied,
}

/// <summary>A tweak listed explicitly in the profile.</summary>
public sealed class ProfileEntry
{
    public string Id { get; set; } = "";

    /// <summary>applied (default) or notApplied.</summary>
    public DesiredState Desired { get; set; } = DesiredState.Applied;
}

/// <summary>
/// Rules that select catalog tweaks (every selected tweak becomes "applied"). They allow
/// profiles that follow the catalog without listing every id.
/// </summary>
public sealed class ProfileRules
{
    /// <summary>Highest risk included (inclusive).</summary>
    public RiskLevel MaxRisk { get; set; } = RiskLevel.Low;

    /// <summary>Category prefixes (English paths, e.g. "Privacy", "AI/Copilot and Recall"). Empty = all.</summary>
    public List<string> Categories { get; set; } = [];

    /// <summary>Ids excluded even if they match the rules.</summary>
    public List<string> ExcludeIds { get; set; } = [];

    /// <summary>Include irreversible tweaks (e.g. app removal). Default: yes.</summary>
    public bool IncludeIrreversible { get; set; } = true;
}

/// <summary>
/// An ADMX policy configured by the profile. YAML format:
/// <code>
/// - { policy: "DataCollection.admx:AllowTelemetry", state: enabled, values: { AllowTelemetry: "0" } }
/// - { policy: "Explorer.admx:NoX", state: disabled }
/// - policy: Sample.admx:Hosts
///   state: enabled
///   scope: machine             # optional (Computer and User policies)
///   values: { Hosts: [a, b] }  # lists and multiText: sequence; list with explicitValue: name→value map
/// </code>
/// </summary>
public sealed class ProfilePolicyEntry
{
    /// <summary>"File.admx:Name".</summary>
    public string Policy { get; set; } = "";

    public PolicyTargetState State { get; set; } = PolicyTargetState.Enabled;

    public PolicyScope? Scope { get; set; }

    /// <summary>Element id → text, sequence of texts or name→value map.</summary>
    public Dictionary<string, object?>? Values { get; set; }

    /// <summary>Converts to <see cref="PolicySetting"/> (maps become "name=value" items).</summary>
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

    /// <summary>Converts from <see cref="PolicySetting"/>: one value becomes a text; several, a sequence.</summary>
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
/// Profile: a selection of desired states. YAML format:
/// <code>
/// name: Safe
/// description: Only low-risk tweaks.
/// i18n: { pt-BR: { name: Seguro, description: ... } }   # optional translations
/// rules: { maxRisk: low, categories: [Privacy] }        # optional
/// tweaks:                                               # optional; takes precedence over the rules
///   - { id: privacy.telemetry.feedback-requests, desired: applied }
///   - { id: ui.explorer.hide-file-extensions, desired: notApplied }
/// policies:                                             # optional; ADMX policies (see ProfilePolicyEntry)
///   - { policy: "DataCollection.admx:AllowTelemetry", state: enabled, values: { AllowTelemetry: "0" } }
/// </code>
/// </summary>
public sealed class Profile
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>Translations keyed by language (e.g. "pt-BR": { name, description }); optional.</summary>
    public Dictionary<string, ProfileText>? I18n { get; set; }

    public ProfileRules? Rules { get; set; }
    public List<ProfileEntry> Tweaks { get; set; } = [];

    /// <summary>ADMX policies configured by the profile (optional; null is not written to the YAML).</summary>
    public List<ProfilePolicyEntry>? Policies { get; set; }

    /// <summary>Source file (filled in by the loader).</summary>
    [YamlIgnore]
    public string? SourceFile { get; set; }

    /// <summary>Built-in profile (catalog/profiles folder); read-only in the interface.</summary>
    [YamlIgnore]
    public bool IsBuiltIn { get; set; }

    /// <summary>Name in the current UI language (English fallback).</summary>
    [YamlIgnore]
    public string DisplayName => UiLanguage.Pick(Name, I18n, t => t.Name) ?? Name;

    /// <summary>Description in the current UI language (English fallback).</summary>
    [YamlIgnore]
    public string? DisplayDescription => Description is null ? null : UiLanguage.Pick(Description, I18n, t => t.Description);

    public override string ToString() => Name;
}

/// <summary>Translated texts of a profile for one language.</summary>
public sealed class ProfileText
{
    public string? Name { get; set; }
    public string? Description { get; set; }
}

/// <summary>Result of resolving a profile against the catalog.</summary>
public sealed record ProfileResolution(IReadOnlyDictionary<string, DesiredState> Desired, IReadOnlyList<string> UnknownIds)
{
    /// <summary>Policy settings of the profile (not checked against the ADMX; see <see cref="ProfileStore.ResolvePolicies"/>).</summary>

    public IReadOnlyList<PolicySetting> Policies { get; init; } = [];
}
