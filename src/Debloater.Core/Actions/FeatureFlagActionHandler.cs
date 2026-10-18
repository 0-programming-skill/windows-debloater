using System.Globalization;
using System.Security;
using Debloater.Core.Engine;
using Debloater.Core.Model;
using Debloater.Core.Platform.FeatureFlags;

namespace Debloater.Core.Actions;

/// <summary>
/// "featureFlag" action: sets a Windows feature configuration override (the switches ViVeTool changes).
///
/// The override is written in two places, because neither is enough alone:
/// <list type="bullet">
/// <item>the registry, <c>HKLM\SYSTEM\CurrentControlSet\Control\FeatureManagement\Overrides\&lt;priority&gt;\&lt;encoded id&gt;</c>,
///   which Windows loads into the boot and runtime stores at every boot (persistence);</item>
/// <item>the runtime store through <c>RtlSetFeatureConfigurations</c>, so that processes that query the feature
///   from now on see the new state without waiting for a restart.</item>
/// </list>
/// Components that already read the feature (and boot-time consumers) only notice after a restart, so every
/// change adds a "restart required" note.
///
/// Detection looks at the persistent override at the action's priority; the detail also shows the effective
/// state and warns when a higher priority layer wins over the override.
/// <c>state: default</c> means "no override at this priority".
/// </summary>
public sealed class FeatureFlagActionHandler : IActionHandler
{
    public string Type => ActionSpec.FeatureFlag;
    public bool CanRestore => true;

    /// <summary>Priorities a catalog action may use. Lower layers (service, image defaults) belong to Windows.</summary>
    public static IReadOnlyList<FeaturePriority> AllowedPriorities { get; } = [FeaturePriority.User, FeaturePriority.UserPolicy, FeaturePriority.Test];

    private static readonly string[] s_states = ["enabled", "disabled", "default"];

    public void Validate(ActionSpec spec, IList<string> errors)
    {
        if (spec.FeatureId is null) errors.Add("action 'featureFlag' requires the field 'id' (numeric feature id).");
        else if (spec.FeatureId == 0) errors.Add("action 'featureFlag': 'id' must be a positive number.");
        ActionHandlers.RequireOneOf(spec.State, "state", Type, errors, s_states);
        if (!FeatureFlagText.TryParsePriority(spec.Priority, out var p) || !AllowedPriorities.Contains(p))
            errors.Add($"action 'featureFlag': invalid 'priority' '{spec.Priority}' (expected: {string.Join(" | ", AllowedPriorities.Select(FeatureFlagText.Priority))}).");
    }

    internal static (uint Id, FeaturePriority Priority, FeatureEnabledState Want) Parse(ActionSpec spec)
    {
        var id = spec.FeatureId ?? throw new InvalidOperationException($"{spec}: missing 'id'.");
        if (!FeatureFlagText.TryParsePriority(spec.Priority, out var priority) || !AllowedPriorities.Contains(priority))
            throw new InvalidOperationException($"{spec}: invalid priority '{spec.Priority}'.");
        var want = spec.State?.ToLowerInvariant() switch
        {
            "enabled" => FeatureEnabledState.Enabled,
            "disabled" => FeatureEnabledState.Disabled,
            "default" => FeatureEnabledState.Default,
            _ => throw new InvalidOperationException($"{spec}: invalid state '{spec.State}'."),
        };
        return (id, priority, want);
    }

    public Task<Detection> DetectAsync(ActionSpec spec, ScanContext ctx, CancellationToken ct = default)
    {
        var (id, priority, want) = Parse(spec);
        try
        {
            var store = ctx.FeatureStore;
            var current = store.ReadOverride(priority, id);
            var effective = FeatureFlagStatus.Read(store, id);
            bool desired = want == FeatureEnabledState.Default ? current is null : current is not null && current.State == want;
            var detail = Describe(priority, current, effective, want, desired);
            return Task.FromResult(desired ? Detection.Desired(detail) : Detection.NotDesired(detail));
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            return Task.FromResult(Detection.Error($"could not read the feature configuration: {ex.Message}"));
        }
    }

    /// <summary>"override (user): enabled; effective: enabled (configured, user priority)" plus a warning when it does not win.</summary>
    internal static string Describe(FeaturePriority priority, FeatureOverride? current, FeatureFlagStatus effective, FeatureEnabledState want, bool desired)
    {
        var text = $"override ({FeatureFlagText.Priority(priority)}): {(current is null ? "absent" : FeatureFlagText.State(current.State))}; effective: {effective}";
        if (desired && want != FeatureEnabledState.Default && effective.Error is null)
        {
            bool wantEnabled = want == FeatureEnabledState.Enabled;
            if (effective.Winning is { } w && w.Priority > priority && w.State != FeatureEnabledState.Default && (w.State == FeatureEnabledState.Enabled) != wantEnabled)
                text += $"; overridden by the {FeatureFlagText.Priority(w.Priority)} priority layer, which wins";
            else if (effective.IsEnabled != wantEnabled)
                text += "; takes effect after a restart";
        }
        return text;
    }

    public Task<ActionBackup?> CaptureBackupAsync(ActionSpec spec, ScanContext ctx, CancellationToken ct = default)
    {
        var (id, priority, _) = Parse(spec);
        var current = ctx.FeatureStore.ReadOverride(priority, id);
        return Task.FromResult<ActionBackup?>(new ActionBackup
        {
            Type = Type,
            Target = spec.Target,
            Existed = current is not null,
            Data = current is null ? null : FeatureFlagText.State(current.State),
            MultiData = current?.Values.Select(kv => $"{kv.Key}={kv.Value.ToString(CultureInfo.InvariantCulture)}").ToList(),
        });
    }

    public Task ApplyAsync(ActionSpec spec, ScanContext ctx, CancellationToken ct = default)
    {
        var (id, priority, want) = Parse(spec);
        var store = ctx.FeatureStore;
        if (want == FeatureEnabledState.Default)
        {
            store.DeleteOverride(priority, id);
            SetRuntime(ctx, new FeatureConfigurationUpdate(id, priority, FeatureEnabledState.Default, Reset: true));
        }
        else
        {
            var values = new SortedDictionary<string, uint>(StringComparer.Ordinal);
            if (store.ReadOverride(priority, id) is { } existing)
                foreach (var (k, v) in existing.Values) values[k] = v;
            values[FeatureOverride.EnabledStateValue] = (uint)want;
            values.TryAdd(FeatureOverride.EnabledStateOptionsValue, 0u);
            store.WriteOverride(new FeatureOverride(id, priority, values));
            SetRuntime(ctx, new FeatureConfigurationUpdate(id, priority, want));
        }
        AddRestartNote(ctx, id);
        return Task.CompletedTask;
    }

    public Task RestoreAsync(ActionSpec spec, ActionBackup? backup, ScanContext ctx, CancellationToken ct = default)
    {
        if (backup is null) throw new InvalidOperationException($"No backup for {spec}.");
        var (id, priority, _) = Parse(spec);
        var store = ctx.FeatureStore;
        if (backup.Existed)
        {
            var values = ParseValues(backup.MultiData);
            var original = new FeatureOverride(id, priority, values);
            store.WriteOverride(original);
            SetRuntime(ctx, original.State == FeatureEnabledState.Default
                ? new FeatureConfigurationUpdate(id, priority, FeatureEnabledState.Default, Reset: true)
                : new FeatureConfigurationUpdate(id, priority, original.State));
        }
        else
        {
            store.DeleteOverride(priority, id);
            SetRuntime(ctx, new FeatureConfigurationUpdate(id, priority, FeatureEnabledState.Default, Reset: true));
        }
        AddRestartNote(ctx, id);
        return Task.CompletedTask;
    }

    internal static SortedDictionary<string, uint> ParseValues(IEnumerable<string>? lines)
    {
        var values = new SortedDictionary<string, uint>(StringComparer.Ordinal);
        foreach (var line in lines ?? [])
        {
            int eq = line.IndexOf('=');
            if (eq <= 0 || !uint.TryParse(line[(eq + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var v))
                throw new InvalidOperationException($"invalid feature override backup line '{line}'.");
            values[line[..eq]] = v;
        }
        return values;
    }

    /// <summary>The registry is the source of truth; a runtime failure only delays the change until the next restart.</summary>
    private static void SetRuntime(ScanContext ctx, FeatureConfigurationUpdate update)
    {
        try
        {
            ctx.FeatureStore.SetRuntime(update);
        }
        catch (Exception ex) when (ex is InvalidOperationException or EntryPointNotFoundException)
        {
            ctx.AddNote($"feature {update.FeatureId}: the running system could not be updated ({ex.Message}); the change applies after a restart.");
        }
    }

    private static void AddRestartNote(ScanContext ctx, uint id) =>
        ctx.AddNote($"feature {KnownFeatures.Label(id)}: restart Windows so that every component picks up the change.");
}
