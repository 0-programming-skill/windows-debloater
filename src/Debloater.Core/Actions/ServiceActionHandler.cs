using System.Globalization;
using System.Security;
using System.ServiceProcess;
using Debloater.Core.Engine;
using Debloater.Core.Model;
using Microsoft.Win32;

namespace Debloater.Core.Actions;

/// <summary>
/// Ação "service": define o tipo de inicialização de um serviço via
/// HKLM\SYSTEM\CurrentControlSet\Services\&lt;name&gt;\Start (+ DelayedAutostart).
/// Ao desativar, tenta parar o serviço (best-effort). Serviço inexistente = NotApplicable.
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

    /// <summary>Converte o "startup" do YAML em (Start, DelayedAutostart).</summary>
    internal static (int Start, int Delayed) ToStartValues(string startup) => startup.ToLowerInvariant() switch
    {
        "disabled" => (4, 0),
        "manual" => (3, 0),
        "automatic" => (2, 0),
        "automaticdelayed" => (2, 1),
        _ => throw new FormatException($"startup inválido '{startup}'."),
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
            if (!exists) return Task.FromResult(Detection.NotApplicable("serviço não existe"));
            var (wantStart, wantDelayed) = ToStartValues(spec.Startup!);
            bool match = start == wantStart && (wantStart != 2 || delayed == wantDelayed);
            var detail = $"atual: {Describe(start, delayed)}";
            return Task.FromResult(match ? Detection.Desired(detail) : Detection.NotDesired(detail));
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
        {
            return Task.FromResult(Detection.Error($"sem acesso: {ex.Message}"));
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
        if (!exists) return; // NotApplicable: nada a fazer.
        var (start, delayed) = ToStartValues(spec.Startup!);
        Write(spec.Name!, start, delayed);
        if (start == 4) await TryStopAsync(spec.Name!, ctx, ct).ConfigureAwait(false);
    }

    public Task RestoreAsync(ActionSpec spec, ActionBackup? backup, ScanContext ctx, CancellationToken ct = default)
    {
        if (backup is null) throw new InvalidOperationException($"Sem backup para {spec}.");
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
            ?? throw new InvalidOperationException($"Serviço '{name}' não encontrado.");
        key.SetValue("Start", start, RegistryValueKind.DWord);
        if (start == 2) key.SetValue("DelayedAutostart", delayed, RegistryValueKind.DWord);
        else if (key.GetValue("DelayedAutostart") is not null) key.SetValue("DelayedAutostart", 0, RegistryValueKind.DWord);
    }

    /// <summary>Quanto esperar o serviço parar ao desativá-lo. O que importa é o tipo de inicialização.</summary>
    public static TimeSpan StopTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Tenta parar o serviço, esperando no máximo <see cref="StopTimeout"/> no total. ServiceController.Stop()
    /// pode bloquear (o SCM espera o serviço responder ao controle, e os dependentes são parados um a um),
    /// por isso a chamada inteira roda em segundo plano e a espera é limitada. Se não parar a tempo,
    /// registra um aviso: a inicialização já foi alterada e o serviço não volta a iniciar depois de reiniciar.
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
                ? $"não parou em {StopTimeout.TotalSeconds:0} s"
                : await stop.ConfigureAwait(false) ? null : "não aceita o comando de parar agora";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException or System.ComponentModel.Win32Exception)
        {
            problem = ex is System.ServiceProcess.TimeoutException ? $"não parou em {StopTimeout.TotalSeconds:0} s" : ex.Message;
        }
        // Não deixa exceção sem observação na tarefa abandonada.
        _ = stop.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

        if (problem is not null)
            ctx.AddNote($"serviço {name} desativado, mas ainda em execução ({problem}); ele para ao reiniciar o Windows.");
    }
}
