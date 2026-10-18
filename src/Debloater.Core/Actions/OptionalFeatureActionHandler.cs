using System.Management;
using Debloater.Core.Engine;
using Debloater.Core.Model;
using Debloater.Core.Platform;

namespace Debloater.Core.Actions;

/// <summary>
/// "optionalFeature" action: enables/disables a Windows optional feature (DISM name).
/// Detection uses WMI (Win32_OptionalFeature), which works without administrator and is read once
/// per scan. Applying uses dism.exe with /norestart. Missing feature = NotApplicable.
/// </summary>
public sealed class OptionalFeatureActionHandler : IActionHandler
{
    private const string CacheKey = "optionalfeature.states";

    // Win32_OptionalFeature.InstallState
    private const uint Enabled = 1;
    private const uint Disabled = 2;
    private const uint Absent = 3;

    public string Type => ActionSpec.OptionalFeature;
    public bool CanRestore => true;

    public void Validate(ActionSpec spec, IList<string> errors)
    {
        ActionHandlers.RequireField(spec.Name, "name", Type, errors);
        ActionHandlers.RequireOneOf(spec.State, "state", Type, errors, "enabled", "disabled");
    }

    private static Task<Dictionary<string, uint>> GetStatesAsync(ScanContext ctx) =>
        ctx.GetOrAddAsync(CacheKey, () => Task.Run(() =>
        {
            var result = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            using var searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT Name, InstallState FROM Win32_OptionalFeature");
            using var items = searcher.Get();
            foreach (var item in items)
            {
                using (item)
                {
                    if (item["Name"] is string name)
                        result[name] = Convert.ToUInt32(item["InstallState"] ?? 0u);
                }
            }
            return result;
        }));

    private static bool WantEnabled(ActionSpec spec) => string.Equals(spec.State, "enabled", StringComparison.OrdinalIgnoreCase);

    private static string Describe(uint state) => state switch
    {
        Enabled => "enabled",
        Disabled => "disabled",
        Absent => "absent",
        _ => $"InstallState={state}",
    };

    public async Task<Detection> DetectAsync(ActionSpec spec, ScanContext ctx, CancellationToken ct = default)
    {
        try
        {
            var states = await GetStatesAsync(ctx).ConfigureAwait(false);
            if (!states.TryGetValue(spec.Name!, out var state))
                return Detection.NotApplicable("feature does not exist on this Windows");
            var detail = $"current: {Describe(state)}";
            if (state == Absent)
                // Payload removed: equivalent to disabled; enabling would require an installation source.
                return WantEnabled(spec) ? Detection.NotDesired(detail) : Detection.Desired(detail);
            bool isEnabled = state == Enabled;
            return isEnabled == WantEnabled(spec) ? Detection.Desired(detail) : Detection.NotDesired(detail);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return Detection.Error($"WMI: {ex.Message}");
        }
    }

    public async Task<ActionBackup?> CaptureBackupAsync(ActionSpec spec, ScanContext ctx, CancellationToken ct = default)
    {
        var states = await GetStatesAsync(ctx).ConfigureAwait(false);
        bool exists = states.TryGetValue(spec.Name!, out var state);
        return new ActionBackup
        {
            Type = Type,
            Target = spec.Target,
            Existed = exists,
            Data = exists ? (state == Enabled ? "enabled" : "disabled") : null,
        };
    }

    public async Task ApplyAsync(ActionSpec spec, ScanContext ctx, CancellationToken ct = default)
    {
        var states = await GetStatesAsync(ctx).ConfigureAwait(false);
        if (!states.ContainsKey(spec.Name!)) return;
        await RunDismAsync(spec.Name!, WantEnabled(spec), ct).ConfigureAwait(false);
        ctx.Invalidate(CacheKey);
    }

    public async Task RestoreAsync(ActionSpec spec, ActionBackup? backup, ScanContext ctx, CancellationToken ct = default)
    {
        if (backup is null) throw new InvalidOperationException($"No backup for {spec}.");
        if (!backup.Existed || backup.Data is null) return;
        await RunDismAsync(spec.Name!, backup.Data == "enabled", ct).ConfigureAwait(false);
        ctx.Invalidate(CacheKey);
    }

    private static async Task RunDismAsync(string feature, bool enable, CancellationToken ct)
    {
        var dism = Path.Combine(Environment.SystemDirectory, "dism.exe");
        var verb = enable ? "/Enable-Feature /All" : "/Disable-Feature";
        var result = await ProcessRunner.RunAsync(dism, $"/Online {verb} /FeatureName:{feature} /NoRestart /English", ct).ConfigureAwait(false);
        // 3010 = success, restart required.
        if (result.ExitCode is not (0 or 3010))
            throw new InvalidOperationException($"dism failed ({result.ExitCode}) for '{feature}': {result.StdOut.Trim()} {result.StdErr.Trim()}");
    }
}
