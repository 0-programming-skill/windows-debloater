using System.Runtime.InteropServices;
using Debloater.Core.Engine;
using Debloater.Core.Model;

namespace Debloater.Core.Actions;

/// <summary>
/// "scheduledTask" action: enables/disables a Task Scheduler task using the
/// "Schedule.Service" COM API (no extra packages). Missing task = NotApplicable.
/// </summary>
public sealed class ScheduledTaskActionHandler : IActionHandler
{
    private const string CacheKey = "taskscheduler.service";
    private const int HResultFileNotFound = unchecked((int)0x80070002);
    private const int HResultPathNotFound = unchecked((int)0x80070003);

    public string Type => ActionSpec.ScheduledTask;
    public bool CanRestore => true;

    public void Validate(ActionSpec spec, IList<string> errors)
    {
        ActionHandlers.RequireField(spec.Path, "path", Type, errors);
        if (spec.Path is { Length: > 0 } p && (!p.StartsWith('\\') || p.EndsWith('\\')))
            errors.Add($"action 'scheduledTask': path must be the full task path, e.g. \\Microsoft\\Windows\\...\\Name (got '{p}').");
        ActionHandlers.RequireOneOf(spec.State, "state", Type, errors, "enabled", "disabled");
    }

    private static Task<object> GetServiceAsync(ScanContext ctx) =>
        ctx.GetOrAddAsync<object>(CacheKey, () => Task.Run(() =>
        {
            var type = System.Type.GetTypeFromProgID("Schedule.Service")
                ?? throw new InvalidOperationException("Task Scheduler (Schedule.Service) unavailable.");
            dynamic service = Activator.CreateInstance(type)!;
            service.Connect();
            return (object)service;
        }));

    /// <summary>Returns the registered task (IRegisteredTask), or null if it does not exist.</summary>
    private static async Task<dynamic?> FindTaskAsync(string path, ScanContext ctx)
    {
        dynamic service = await GetServiceAsync(ctx).ConfigureAwait(false);
        int idx = path.LastIndexOf('\\');
        string folderPath = idx <= 0 ? "\\" : path[..idx];
        string name = path[(idx + 1)..];
        try
        {
            dynamic folder = service.GetFolder(folderPath);
            return folder.GetTask(name);
        }
        catch (Exception ex) when (ex.HResult is HResultFileNotFound or HResultPathNotFound)
        {
            // Through 'dynamic', the HRESULT may arrive as COMException or FileNotFoundException/DirectoryNotFoundException.
            return null;
        }
    }

    private static bool WantEnabled(ActionSpec spec) => string.Equals(spec.State, "enabled", StringComparison.OrdinalIgnoreCase);

    public async Task<Detection> DetectAsync(ActionSpec spec, ScanContext ctx, CancellationToken ct = default)
    {
        try
        {
            var task = await FindTaskAsync(spec.Path!, ctx).ConfigureAwait(false);
            if (task is null) return Detection.NotApplicable("task does not exist");
            bool enabled = (bool)task.Enabled;
            var detail = $"current: {(enabled ? "enabled" : "disabled")}";
            return enabled == WantEnabled(spec) ? Detection.Desired(detail) : Detection.NotDesired(detail);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException or IOException)
        {
            return Detection.Error($"Task Scheduler: {ex.Message}");
        }
    }

    public async Task<ActionBackup?> CaptureBackupAsync(ActionSpec spec, ScanContext ctx, CancellationToken ct = default)
    {
        var task = await FindTaskAsync(spec.Path!, ctx).ConfigureAwait(false);
        return new ActionBackup
        {
            Type = Type,
            Target = spec.Target,
            Existed = task is not null,
            Data = task is null ? null : ((bool)task.Enabled ? "enabled" : "disabled"),
        };
    }

    public async Task ApplyAsync(ActionSpec spec, ScanContext ctx, CancellationToken ct = default)
    {
        var task = await FindTaskAsync(spec.Path!, ctx).ConfigureAwait(false);
        if (task is null) return;
        task.Enabled = WantEnabled(spec);
    }

    public async Task RestoreAsync(ActionSpec spec, ActionBackup? backup, ScanContext ctx, CancellationToken ct = default)
    {
        if (backup is null) throw new InvalidOperationException($"No backup for {spec}.");
        if (!backup.Existed || backup.Data is null) return;
        var task = await FindTaskAsync(spec.Path!, ctx).ConfigureAwait(false);
        if (task is null) return;
        task.Enabled = backup.Data == "enabled";
    }
}
