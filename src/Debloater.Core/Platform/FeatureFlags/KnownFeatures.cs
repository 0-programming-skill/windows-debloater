namespace Debloater.Core.Platform.FeatureFlags;

/// <summary>What reverse engineering found out about a feature id.</summary>
/// <param name="Name">WIL feature name from Microsoft's public symbols, when the symbol has one (many only have a number).</param>
/// <param name="Description">What the feature gates (English, short).</param>
/// <param name="BuiltInDefault">State the binary assumes when nothing configures the feature (null = unknown).</param>
/// <param name="Source">Reverse-engineering finding, "&lt;build&gt;/&lt;slug&gt;".</param>
public sealed record KnownFeature(uint Id, string? Name, string Description, FeatureEnabledState? BuiltInDefault, string Source)
{
    /// <summary>"61825274 (Feature_CAM_Mcp_PolicyEnforcement)" or just the number.</summary>
    public string Label => Name is null ? Id.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"{Id} ({Name})";
}

/// <summary>
/// Small map of feature ids found in our reverse-engineering notes. Only ids whose meaning the notes justify
/// are listed; any other id is shown as a number. Built-in defaults come from the decompiled
/// <c>GetCurrentFeatureEnabledState</c> of each feature (build 26300.9711).
/// </summary>
public static class KnownFeatures
{
    private static readonly Dictionary<uint, KnownFeature> s_map = new KnownFeature[]
    {
        new(61825274, "Feature_CAM_Mcp_PolicyEnforcement",
            "Lets CapabilityAccessManager enforce the AI agent connector policies (ConfigureAgentConnectors, AgentConnectorAccessPolicy). Effective only while 58336780 is also enabled.",
            FeatureEnabledState.Enabled, "26300/cam-ai-configure-agent-connectors"),
        new(58336780, null,
            "Prerequisite of Feature_CAM_Mcp_PolicyEnforcement (61825274) in CapabilityAccessManager.dll.",
            FeatureEnabledState.Disabled, "26300/cam-ai-configure-agent-connectors"),
        new(61532758, null,
            "Gates the Store and web suggestion settings of Windows Search (windowsudk.shellcommon.dll); without it the settings are read-only.",
            FeatureEnabledState.Enabled, "26300/search-store-web-suggestions"),
        new(61754985, null,
            "Gates the separate \"Show suggested files\" setting of Start's Recommended section (windowsudk.shellcommon.dll); without it Start follows the recent items setting.",
            FeatureEnabledState.Disabled, "26300/start-show-suggested-files"),
        new(61218511, null,
            "Start menu: part of the Recommended section's \"More\" button wiring (StartMenu.dll).",
            null, "26300/start-show-more-suggestions"),
    }.ToDictionary(f => f.Id);

    public static IReadOnlyCollection<KnownFeature> All => s_map.Values;

    public static KnownFeature? Get(uint id) => s_map.GetValueOrDefault(id);

    /// <summary>Feature name or number.</summary>
    public static string Label(uint id) => Get(id)?.Label ?? id.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
