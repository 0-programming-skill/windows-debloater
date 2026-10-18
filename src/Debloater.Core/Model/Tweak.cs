using Debloater.Core.Localization;
using YamlDotNet.Serialization;

namespace Debloater.Core.Model;

/// <summary>Risk level of a tweak. The order matters: it is used by risk gating.</summary>
public enum RiskLevel
{
    Low = 0,
    Medium = 1,
    High = 2,
    Critical = 3,
}

/// <summary>
/// How well documented the tweak's mechanism is (optional in the YAML).
/// official: documented by Microsoft (Policy CSP on learn.microsoft.com);
/// admx: matches a policy from an official administrative template (ADMX);
/// community: known in the community (established tools), without official documentation;
/// questionable: there are signs it does not work as described (unexpected value, edition...).
/// </summary>
public enum Confidence
{
    Official,
    Admx,
    Community,
    Questionable,
}

/// <summary>Source backing a tweak (e.g. { kind: msdocs, ref: "Policy CSP - System/AllowTelemetry" }).</summary>
public sealed class SourceRef
{
    public const string MsDocs = "msdocs";
    public const string AdmxKind = "admx";
    public const string Url = "url";
    public const string CommunityKind = "community";

    /// <summary>
    /// Reverse engineering: ref = "&lt;build&gt;/&lt;slug&gt;" identifying a finding (e.g. "26300/start-show-suggested-files").
    /// The write-ups are kept outside the repository; the id is what the catalog records.
    /// </summary>
    public const string ReKind = "re";

    /// <summary>
    /// Another debloat tool that makes the same change: ref = "&lt;id&gt;:&lt;item&gt;", with the id from
    /// tools/coverage/sources.json (e.g. "winutil:WPFTweaksTelemetry", "sophia:ScheduledTasks"). It only records where
    /// the idea came from; the tweak's description is always our own.
    /// </summary>
    public const string ToolKind = "tool";

    public static IReadOnlyList<string> KnownKinds { get; } = [MsDocs, AdmxKind, Url, CommunityKind, ReKind, ToolKind];

    /// <summary>msdocs | admx | url | community | re | tool.</summary>
    public string Kind { get; set; } = "";

    /// <summary>msdocs: "Policy CSP - Area/Name"; admx: "File.admx:Policy"; url/community: address or description;
    /// re: "&lt;build&gt;/&lt;slug&gt;" of a reverse-engineering finding; tool: "&lt;tool&gt;:&lt;item&gt;".</summary>
    public string Ref { get; set; } = "";

    public override string ToString() => $"{Kind}:{Ref}";
}

/// <summary>Translated texts of a tweak for one language (any field may be omitted: falls back to English).</summary>
public sealed class TweakText
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Breaks { get; set; }

    /// <summary>Translation of <see cref="Precondition.Reason"/>.</summary>
    public string? Reason { get; set; }
}

/// <summary>A catalog tweak. It is only DATA, loaded from YAML, with no logic.</summary>
public sealed class Tweak
{
    /// <summary>Stable identifier, in English, dot-separated (e.g. privacy.telemetry.allow-telemetry).</summary>
    public string Id { get; set; } = "";

    /// <summary>Short name shown in the interface (English; see <see cref="DisplayName"/>).</summary>
    public string Name { get; set; } = "";

    /// <summary>Explanation of what the tweak does (English; see <see cref="DisplayDescription"/>).</summary>
    public string Description { get; set; } = "";

    /// <summary>
    /// Category path separated by "/" (e.g. Privacy/Telemetry), always in English: it is also an identifier
    /// (profile rules, --category). Display names come from catalog/i18n/categories.yaml. If omitted, inherits the file's.
    /// </summary>
    public string Category { get; set; } = "";

    public RiskLevel Risk { get; set; } = RiskLevel.Low;

    /// <summary>Confidence level (optional). See <see cref="Model.Confidence"/>.</summary>
    public Confidence? Confidence { get; set; }

    /// <summary>Sources backing the tweak (optional).</summary>
    public List<SourceRef> Sources { get; set; } = [];

    /// <summary>Minimum Windows build (inclusive).</summary>
    public int? MinBuild { get; set; }

    /// <summary>Maximum Windows build (inclusive).</summary>
    public int? MaxBuild { get; set; }

    public List<string> Tags { get; set; } = [];

    /// <summary>Warning about what may stop working (English; see <see cref="DisplayBreaks"/>).</summary>
    public string? Breaks { get; set; }

    public List<string> References { get; set; } = [];

    /// <summary>Whether the engine can undo the tweak from the backup.</summary>
    public bool Reversible { get; set; } = true;

    /// <summary>
    /// Optional condition for the tweak to make sense on this system (e.g. the hardware supports hibernation).
    /// If it is false, the tweak is NotApplicable and is not applied.
    /// </summary>
    public Precondition? Precondition { get; set; }

    /// <summary>
    /// Translations keyed by language (e.g. "pt-BR"). Format:
    /// <code>
    /// i18n:
    ///   pt-BR:
    ///     name: ...
    ///     description: ...
    ///     breaks: ...      # when the tweak has 'breaks'
    ///     reason: ...      # when the tweak has a 'precondition'
    /// </code>
    /// </summary>
    public Dictionary<string, TweakText>? I18n { get; set; }

    public List<ActionSpec> Actions { get; set; } = [];

    /// <summary>Source YAML file (filled in by the loader).</summary>
    [YamlIgnore]
    public string? SourceFile { get; set; }

    /// <summary>Name in the current UI language (English fallback).</summary>
    [YamlIgnore]
    public string DisplayName => UiLanguage.Pick(Name, I18n, t => t.Name) ?? Name;

    /// <summary>Description in the current UI language (English fallback).</summary>
    [YamlIgnore]
    public string DisplayDescription => UiLanguage.Pick(Description, I18n, t => t.Description) ?? Description;

    /// <summary><see cref="Breaks"/> in the current UI language (English fallback); null when the tweak has none.</summary>
    [YamlIgnore]
    public string? DisplayBreaks => Breaks is null ? null : UiLanguage.Pick(Breaks, I18n, t => t.Breaks);

    /// <summary><see cref="Precondition.Reason"/> in the current UI language (English fallback).</summary>
    [YamlIgnore]
    public string? DisplayPreconditionReason =>
        Precondition?.Reason is null ? null : UiLanguage.Pick(Precondition.Reason, I18n, t => t.Reason);

    public bool IsBuildInRange(int build) =>
        (MinBuild is null || build >= MinBuild) && (MaxBuild is null || build <= MaxBuild);

    public override string ToString() => $"{Id} ({Name})";
}

/// <summary>
/// Precondition of a tweak: a PowerShell script that prints True (the tweak applies to this system)
/// or False (it does not) on its last line. Read-only: it must not change the system.
/// </summary>
public sealed class Precondition
{
    /// <summary>Script that prints True or False.</summary>
    public string? Powershell { get; set; }

    /// <summary>Why the tweak does not apply when the script returns False (English, shown to the user; translated in i18n.*.reason).</summary>
    public string? Reason { get; set; }
}

/// <summary>Format of a catalog file: default category + list of tweaks.</summary>
public sealed class CatalogFile
{
    /// <summary>Default category for the file's tweaks that do not define their own.</summary>
    public string? Category { get; set; }

    public List<Tweak> Tweaks { get; set; } = [];
}
