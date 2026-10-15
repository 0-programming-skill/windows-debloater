using Debloater.Core.Engine;
using Debloater.Core.Model;
using Debloater.Core.Platform;

namespace Debloater.Core.Actions;

/// <summary>
/// Ação "powershell": válvula de escape para o que não cabe nos outros tipos.
/// "detect" deve imprimir True (estado desejado) ou False na última linha;
/// também aceita NotApplicable. "apply" aplica e "revert" desfaz.
/// </summary>
public sealed class PowerShellActionHandler : IActionHandler
{
    public string Type => ActionSpec.PowerShell;
    public bool CanRestore => true;

    public void Validate(ActionSpec spec, IList<string> errors)
    {
        ActionHandlers.RequireField(spec.Detect, "detect", Type, errors);
        ActionHandlers.RequireField(spec.Apply, "apply", Type, errors);
    }

    public async Task<Detection> DetectAsync(ActionSpec spec, ScanContext ctx, CancellationToken ct = default)
    {
        try
        {
            var result = await ProcessRunner.RunPowerShellAsync(spec.Detect!, ct).ConfigureAwait(false);
            var last = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
            return last.ToLowerInvariant() switch
            {
                "true" => Detection.Desired("detect: True"),
                "false" => Detection.NotDesired("detect: False"),
                "notapplicable" => Detection.NotApplicable("detect: NotApplicable"),
                _ => Detection.Error($"detect retornou '{last}' (esperado True/False). {result.StdErr.Trim()}".Trim()),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Detection.Error($"falha ao executar detect: {ex.Message}");
        }
    }

    public Task<ActionBackup?> CaptureBackupAsync(ActionSpec spec, ScanContext ctx, CancellationToken ct = default) =>
        // O "backup" de um script é o próprio script de revert.
        Task.FromResult<ActionBackup?>(new ActionBackup { Type = Type, Target = spec.Target, Existed = true });

    public Task ApplyAsync(ActionSpec spec, ScanContext ctx, CancellationToken ct = default) =>
        RunAsync(spec.Apply!, "apply", ct);

    public Task RestoreAsync(ActionSpec spec, ActionBackup? backup, ScanContext ctx, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(spec.Revert)
            ? throw new NotSupportedException("Esta ação powershell não define 'revert'.")
            : RunAsync(spec.Revert!, "revert", ct);

    private static async Task RunAsync(string script, string what, CancellationToken ct)
    {
        var wrapped = "$ErrorActionPreference = 'Stop'\n" + script;
        var result = await ProcessRunner.RunPowerShellAsync(wrapped, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"script {what} falhou ({result.ExitCode}): {result.StdErr.Trim()}");
    }
}
