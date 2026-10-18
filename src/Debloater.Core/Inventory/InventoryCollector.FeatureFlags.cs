using System.Globalization;
using Debloater.Core.Platform.FeatureFlags;

namespace Debloater.Core.Inventory;

public sealed partial class InventoryCollector
{
    /// <summary>
    /// Feature configurations (feature flags) in the runtime store, one item per feature id (read-only; works
    /// without administrator rights). Properties: the winning layer's state and priority, every layer when there
    /// is more than one, variant/payload when set, and the boot store's winner when it differs (a runtime change
    /// not yet persisted, or a change that only applies after a restart). Features that nothing configures use
    /// their built-in default and are not listed.
    /// </summary>
    internal static InventorySection CollectFeatureFlags(IFeatureStore store)
    {
        var section = new InventorySection
        {
            Kind = InventoryKinds.FeatureFlags,
            Note = "only features with a configuration are listed; the others use their built-in default",
        };
        var runtime = store.QueryAll(FeatureConfigurationType.Runtime).GroupBy(c => c.FeatureId).ToDictionary(g => g.Key, g => g.ToList());
        var boot = store.QueryAll(FeatureConfigurationType.Boot).GroupBy(c => c.FeatureId).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var id in runtime.Keys.Union(boot.Keys))
        {
            var layers = runtime.GetValueOrDefault(id) ?? [];
            var bootLayers = boot.GetValueOrDefault(id) ?? [];
            var winner = Winner(layers);
            var bootWinner = Winner(bootLayers);
            var known = KnownFeatures.Get(id);
            var item = new InventoryItem
            {
                Kind = InventoryKinds.FeatureFlags,
                Id = id.ToString(CultureInfo.InvariantCulture),
                DisplayName = known?.Name ?? known?.Description,
            };
            if (winner is null)
            {
                item.Properties["state"] = "absent at runtime";
            }
            else
            {
                item.Properties["state"] = FeatureFlagText.State(winner.State);
                item.Properties["priority"] = FeatureFlagText.Priority(winner.Priority);
                if (winner.Variant != 0) item.Properties["variant"] = winner.Variant.ToString(CultureInfo.InvariantCulture);
                if (winner.VariantPayload != 0 || winner.VariantPayloadKind != 0)
                    item.Properties["payload"] = $"{winner.VariantPayload.ToString(CultureInfo.InvariantCulture)} (kind {winner.VariantPayloadKind.ToString(CultureInfo.InvariantCulture)})";
                if (winner.IsWexpConfiguration) item.Properties["wexp"] = "true";
            }
            if (layers.Count > 1) item.Properties["layers"] = DescribeLayers(layers);
            if (!SameState(winner, bootWinner))
                item.Properties["boot"] = bootWinner is null ? "absent" : $"{FeatureFlagText.State(bootWinner.State)} ({FeatureFlagText.Priority(bootWinner.Priority)})";
            if (known?.BuiltInDefault is { } d)
            {
                item.Info = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["builtInDefault"] = FeatureFlagText.State(d) };
            }
            section.Items.Add(item);
        }
        return section;
    }

    private static FeatureConfiguration? Winner(List<FeatureConfiguration> layers) =>
        layers.Count == 0 ? null : layers.MaxBy(l => (int)l.Priority);

    private static bool SameState(FeatureConfiguration? a, FeatureConfiguration? b) =>
        a is null ? b is null : b is not null && a.State == b.State && a.Priority == b.Priority && a.Variant == b.Variant && a.VariantPayload == b.VariantPayload;

    /// <summary>"security: disabled, service: enabled" (highest priority first).</summary>
    internal static string DescribeLayers(IEnumerable<FeatureConfiguration> layers) =>
        string.Join(", ", layers.OrderByDescending(l => (int)l.Priority).Select(l => $"{FeatureFlagText.Priority(l.Priority)}: {FeatureFlagText.State(l.State)}"));
}
