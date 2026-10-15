using System.Runtime.InteropServices;
using Debloater.Core.Engine;
using Debloater.Core.Model;

namespace Debloater.Core.Actions;

/// <summary>
/// Ação "scheduledTask": habilita/desabilita uma tarefa do Agendador de Tarefas usando a API COM
/// "Schedule.Service" (sem pacotes extras). Tarefa inexistente = NotApplicable.
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
            errors.Add($"ação 'scheduledTask': path deve ser o caminho completo da tarefa, ex.: \\Microsoft\\Windows\\...\\Nome (recebido '{p}').");
        ActionHandlers.RequireOneOf(spec.State, "state", Type, errors, "enabled", "disabled");
    }

    private static Task<object> GetServiceAsync(ScanContext ctx) =>
        ctx.GetOrAddAsync<object>(CacheKey, () => Task.Run(() =>
        {
            var type = System.Type.GetTypeFromProgID("Schedule.Service")
                ?? throw new InvalidOperationException("Agendador de Tarefas (Schedule.Service) indisponível.");
            dynamic service = Activator.CreateInstance(type)!;
            service.Connect();
            return (object)service;
        }));

    /// <summary>Retorna a tarefa registrada (IRegisteredTask) ou null se não existir.</summary>
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
            // Via 'dynamic', o HRESULT pode chegar como COMException ou FileNotFoundException/DirectoryNotFoundException.
            return null;
        }
    }

    private static bool WantEnabled(ActionSpec spec) => string.Equals(spec.State, "enabled", StringComparison.OrdinalIgnoreCase);

    public async Task<Detection> DetectAsync(ActionSpec spec, ScanContext ctx, CancellationToken ct = default)
    {
        try
        {
            var task = await FindTaskAsync(spec.Path!, ctx).ConfigureAwait(false);
            if (task is null) return Detection.NotApplicable("tarefa não existe");
            bool enabled = (bool)task.Enabled;
            var detail = $"atual: {(enabled ? "enabled" : "disabled")}";
            return enabled == WantEnabled(spec) ? Detection.Desired(detail) : Detection.NotDesired(detail);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException or IOException)
        {
            return Detection.Error($"Agendador de Tarefas: {ex.Message}");
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
        if (backup is null) throw new InvalidOperationException($"Sem backup para {spec}.");
        if (!backup.Existed || backup.Data is null) return;
        var task = await FindTaskAsync(spec.Path!, ctx).ConfigureAwait(false);
        if (task is null) return;
        task.Enabled = backup.Data == "enabled";
    }
}
