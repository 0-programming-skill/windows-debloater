namespace Debloater.Core.Platform.FeatureFlags;

/// <summary>Current state of a feature as far as we can tell: the winning configuration, or the known built-in default.</summary>
public sealed record FeatureFlagStatus(uint Id, FeatureConfiguration? Winning, KnownFeature? Known, string? Error = null)
{
    /// <summary>true/false when known; null when nothing configures it and the built-in default is unknown (or on error).</summary>
    public bool? IsEnabled => Winning is { State: not FeatureEnabledState.Default } w
        ? w.State == FeatureEnabledState.Enabled
        : Known?.BuiltInDefault is { } d && d != FeatureEnabledState.Default ? d == FeatureEnabledState.Enabled : null;

    /// <summary>Whether the state comes from the binary's default (no explicit configuration).</summary>
    public bool IsBuiltInDefault => Winning is null or { State: FeatureEnabledState.Default };

    /// <summary>Reads the runtime state of a feature (never throws).</summary>
    public static FeatureFlagStatus Read(IFeatureStore store, uint id)
    {
        try
        {
            return new FeatureFlagStatus(id, store.Query(id), KnownFeatures.Get(id));
        }
        catch (Exception ex) when (ex is InvalidOperationException or EntryPointNotFoundException or DllNotFoundException)
        {
            return new FeatureFlagStatus(id, null, KnownFeatures.Get(id), ex.Message);
        }
    }

    /// <summary>English, compact: "enabled (configured: service priority)", "disabled (built-in default)", "unknown (not configured)".</summary>
    public override string ToString()
    {
        if (Error is not null) return $"unknown ({Error})";
        var state = IsEnabled switch { true => "enabled", false => "disabled", null => "unknown" };
        return IsBuiltInDefault
            ? $"{state} ({(Known?.BuiltInDefault is null ? "not configured, built-in default unknown" : "built-in default")})"
            : $"{state} (configured, {FeatureFlagText.Priority(Winning!.Priority)} priority)";
    }
}
