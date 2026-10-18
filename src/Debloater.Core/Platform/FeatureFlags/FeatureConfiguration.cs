using System.Buffers.Binary;
using System.Numerics;

namespace Debloater.Core.Platform.FeatureFlags;

/// <summary>Store queried by the feature configuration API (RTL_FEATURE_CONFIGURATION_TYPE).</summary>
public enum FeatureConfigurationType
{
    /// <summary>Configuration loaded at boot; read-only afterwards.</summary>
    Boot = 0,
    /// <summary>What processes see now; changeable at runtime (lost on reboot).</summary>
    Runtime = 1,
}

/// <summary>RTL_FEATURE_ENABLED_STATE.</summary>
public enum FeatureEnabledState
{
    /// <summary>No explicit state: the binary's built-in default applies.</summary>
    Default = 0,
    Disabled = 1,
    Enabled = 2,
}

/// <summary>
/// RTL_FEATURE_CONFIGURATION_PRIORITY: when several layers configure the same feature, the highest number
/// wins. Names follow the public headers used by the community (phnt); the meaning of the reserved slots is unknown.
/// </summary>
public enum FeaturePriority
{
    ImageDefault = 0,
    Ekb = 1,
    Safeguard = 2,
    Reserved3 = 3,
    Service = 4,
    Reserved5 = 5,
    Dynamic = 6,
    Reserved7 = 7,
    User = 8,
    Security = 9,
    UserPolicy = 10,
    Reserved11 = 11,
    Test = 12,
    Reserved13 = 13,
    Reserved14 = 14,
    ImageOverride = 15,
}

/// <summary>One configuration layer of a feature (one RTL_FEATURE_CONFIGURATION).</summary>
public sealed record FeatureConfiguration(
    uint FeatureId,
    FeaturePriority Priority,
    FeatureEnabledState State,
    int Variant = 0,
    int VariantPayloadKind = 0,
    uint VariantPayload = 0,
    bool IsWexpConfiguration = false,
    bool HasSubscriptions = false)
{
    internal static FeatureConfiguration From(in FeatureConfigurationNative.RtlFeatureConfiguration c) => new(
        c.FeatureId,
        FeatureConfigurationNative.Priority(c.Flags),
        FeatureConfigurationNative.EnabledState(c.Flags),
        FeatureConfigurationNative.Variant(c.Flags),
        FeatureConfigurationNative.VariantPayloadKind(c.Flags),
        c.VariantPayload,
        FeatureConfigurationNative.IsWexpConfiguration(c.Flags),
        FeatureConfigurationNative.HasSubscriptions(c.Flags));
}

/// <summary>A runtime change (RTL_FEATURE_CONFIGURATION_UPDATE). <see cref="Reset"/> removes the layer at <see cref="Priority"/>.</summary>
public sealed record FeatureConfigurationUpdate(uint FeatureId, FeaturePriority Priority, FeatureEnabledState State, bool Reset = false);

/// <summary>
/// Persistent override in HKLM\SYSTEM\CurrentControlSet\Control\FeatureManagement\Overrides\&lt;priority&gt;\&lt;key&gt;,
/// which Windows loads into both stores at boot. <see cref="Values"/> holds the key's DWORD values exactly as
/// found (EnabledState, EnabledStateOptions, Variant, VariantPayload, VariantPayloadKind; Windows adds
/// TelemetryFlags after loading it).
/// </summary>
public sealed record FeatureOverride(uint FeatureId, FeaturePriority Priority, IReadOnlyDictionary<string, uint> Values)
{
    public const string EnabledStateValue = "EnabledState";
    public const string EnabledStateOptionsValue = "EnabledStateOptions";

    public FeatureEnabledState State =>
        Values.TryGetValue(EnabledStateValue, out var s) && s <= 2 ? (FeatureEnabledState)s : FeatureEnabledState.Default;
}

/// <summary>
/// Registry key names under FeatureManagement\Overrides are not the feature ids: Windows stores an obfuscated
/// form and decodes it when loading. Determined on 26300 by writing known key names, rebooting and reading back
/// the ids through the API; the same transform maps every override on 26200 to the id the API reports.
/// </summary>
public static class FeatureOverrideKey
{
    private const uint Mask = 0x447F3700;

    /// <summary>Registry key name for a feature id.</summary>
    public static uint Encode(uint featureId) => BitOperations.RotateLeft(BinaryPrimitives.ReverseEndianness(featureId ^ Mask), 1);

    /// <summary>Feature id for a registry key name.</summary>
    public static uint Decode(uint keyName) => BinaryPrimitives.ReverseEndianness(BitOperations.RotateRight(keyName, 1)) ^ Mask;
}

public static class FeatureFlagText
{
    public static string State(FeatureEnabledState s) => s switch
    {
        FeatureEnabledState.Enabled => "enabled",
        FeatureEnabledState.Disabled => "disabled",
        _ => "default",
    };

    /// <summary>Lower camel case name of a priority (user, userPolicy, imageOverride, ...).</summary>
    public static string Priority(FeaturePriority p) =>
        Enum.IsDefined(p) ? char.ToLowerInvariant(p.ToString()[0]) + p.ToString()[1..] : ((int)p).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static bool TryParsePriority(string? text, out FeaturePriority priority)
    {
        priority = FeaturePriority.User;
        if (string.IsNullOrWhiteSpace(text)) return true;
        foreach (var p in Enum.GetValues<FeaturePriority>())
        {
            if (string.Equals(Priority(p), text, StringComparison.OrdinalIgnoreCase)) { priority = p; return true; }
        }
        return false;
    }
}
