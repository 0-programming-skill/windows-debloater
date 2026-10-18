using System.Globalization;
using System.Security;
using System.ServiceProcess;
using Debloater.Core.Engine;
using Debloater.Core.Model;
using Microsoft.Win32;

namespace Debloater.Core.Actions;

/// <summary>
/// "service" action: sets a service's startup type via
/// HKLM\SYSTEM\CurrentControlSet\Services\&lt;name&gt;\Start (+ DelayedAutostart).
/// When disabling, tries to stop the service (best-effort). Missing service = NotApplicable.
/// </summary>
public sealed class ServiceActionHandler : IActionHandler
{
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";

    public string Type => ActionSpec.Service;
    public bool CanRestore => true;

    public void Validate(ActionSpec spec, IList<string> errors)
    {
        ActionHandlers.RequireField(spec.Name, "name", Type, errors);
        ActionHandlers.RequireOneOf(spec.Startup, "startup", Type, errors, "disabled", "manual", "automatic", "automaticDelayed");
    }

    /// <summary>Converts the YAML "startup" into (Start, DelayedAutostart).</summary>
    internal static (int Start, int Delayed) ToStartValues(string startup) => startup.ToLowerInvariant() switch
    {
        "disabled" => (4, 0),
        "manual" => (3, 0),
        "automatic" => (2, 0),
        "automaticdelayed" => (2, 1),
        _ => throw new FormatException($"invalid startup '{startup}'."),
    };

    internal static string Describe(int start, int delayed) => start switch
    {
        0 => "boot",
        1 => "system",
        2 => delayed == 1 ? "automaticDelayed" : "automatic",
        3 => "manual",
        4 => "disabled",
        _ => $"Start={start}",
    };

    private static RegistryKey OpenServicesRoot() =>
        RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    private static (bool Exists, int Start, int Delayed) Read(string name)
    {
        using var root = OpenServicesRoot();
        using var key = root.OpenSubKey($@"{ServicesKey}\{name}");
        if (key is null) return (false, 0, 0);
        int start = key.GetValue("Start") is int s ? s : -1;
        int delayed = key.GetValue("DelayedAutostart") is int d ? d : 0;
        return (true, start, delayed);
    }

    public Task<Detection> DetectAsync(ActionSpec spec, ScanContext ctx, CancellationToken ct = default)
    {
        try
        {
            var (exists, start, delayed) = Read(spec.Name!);
            if (!exists) return Task.FromResult(Detection.NotApplicable("service does not exist"));
            var (wantStart, wantDelayed) = ToStartValues(spec.Startup!);
            bool match = start == wantStart && (wantStart != 2 || delayed == wantDelayed);
            var detail = $"current: {Describe(start, delayed)}";
            return Task.FromResult(match ? Detection.Desired(detail) : Detection.NotDesired(detail));
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
        {
            return Task.FromResult(Detection.Error($"no access: {ex.Message}"));
        }
    }

    public Task<ActionBackup?> CaptureBackupAsync(ActionSpec spec, ScanContext ctx, CancellationToken ct = default)
    {
        var (exists, start, delayed) = Read(spec.Name!);
        return Task.FromResult<ActionBackup?>(new ActionBackup
        {
            Type = Type,
            Target = spec.Target,
            Existed = exists,
            Data = exists ? start.ToString(CultureInfo.InvariantCulture) : null,
            Extra = exists ? delayed.ToString(CultureInfo.InvariantCulture) : null,
        });
    }

    public async Task ApplyAsync(ActionSpec spec, ScanContext ctx, CancellationToken ct = default)
    {
        var (exists, _, _) = Read(spec.Name!);
        if (!exists) return; // NotApplicable: nothing to do.
        var (start, delayed) = ToStartValues(spec.Startup!);
        Write(spec.Name!, start, delayed);
        if (start == 4) await TryStopAsync(spec.Name!, ctx, ct).ConfigureAwait(false);
    }

    public Task RestoreAsync(ActionSpec spec, ActionBackup? backup, ScanContext ctx, CancellationToken ct = default)
    {
        if (backup is null) throw new InvalidOperationException($"No backup for {spec}.");
        if (!backup.Existed || backup.Data is null) return Task.CompletedTask;
        var (exists, _, _) = Read(spec.Name!);
        if (!exists) return Task.CompletedTask;
        int start = int.Parse(backup.Data, CultureInfo.InvariantCulture);
        int delayed = int.TryParse(backup.Extra, CultureInfo.InvariantCulture, out var d) ? d : 0;
        Write(spec.Name!, start, delayed);
        return Task.CompletedTask;
    }

    private static void Write(string name, int start, int delayed)
    {
        using var root = OpenServicesRoot();
        using var key = root.OpenSubKey($@"{ServicesKey}\{name}", writable: true)
            ?? throw new InvalidOperationException($"Service '{name}' not found.");
        key.SetValue("Start", start, RegistryValueKind.DWord);
        if (start == 2) key.SetValue("DelayedAutostart", delayed, RegistryValueKind.DWord);
        else if (key.GetValue("DelayedAutostart") is not null) key.SetValue("DelayedAutostart", 0, RegistryValueKind.DWord);
    }

    /// <summary>How long to wait for the service to stop when disabling it. What matters is the startup type.</summary>
    public static TimeSpan StopTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Tries to stop the service, waiting at most <see cref="StopTimeout"/> in total. ServiceController.Stop()
    /// can block (the SCM waits for the service to respond to the control, and dependents are stopped one by one),
    /// so the whole call runs in the background and the wait is bounded. If it does not stop in time,
    /// a note is recorded: the startup type was already changed and the service will not start again after a restart.
    /// </summary>
    private static async Task TryStopAsync(string name, ScanContext ctx, CancellationToken ct)
    {
        var stop = Task.Run(() =>
        {
            using var sc = new ServiceController(name);
            if (sc.Status is ServiceControllerStatus.Stopped) return true;
            if (sc.Status is not ServiceControllerStatus.StopPending)
            {
                if (!sc.CanStop) return false;
                sc.Stop();
            }
            sc.WaitForStatus(ServiceControllerStatus.Stopped, StopTimeout);
            return true;
        }, CancellationToken.None);

        string? problem;
        try
        {
            var finished = await Task.WhenAny(stop, Task.Delay(StopTimeout, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            problem = finished != stop
                ? $"did not stop within {StopTimeout.TotalSeconds:0} s"
                : await stop.ConfigureAwait(false) ? null : "does not accept the stop command right now";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException or System.ComponentModel.Win32Exception)
        {
            problem = ex is System.ServiceProcess.TimeoutException ? $"did not stop within {StopTimeout.TotalSeconds:0} s" : ex.Message;
        }
        // Do not leave an unobserved exception on the abandoned task.
        _ = stop.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

        if (problem is not null)
            ctx.AddNote($"service {name} disabled, but still running ({problem}); it stops when Windows restarts.");
    }
}
