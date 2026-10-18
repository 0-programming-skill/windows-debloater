using Debloater.Core.Engine;
using Debloater.Core.Model;
using Debloater.Core.Platform;

namespace Debloater.Core.Actions;

/// <summary>
/// "powershell" action: escape hatch for what does not fit the other types.
/// "detect" must print True (desired state) or False on its last line;
/// NotApplicable is also accepted. "apply" applies and "revert" undoes.
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
                _ => Detection.Error($"detect returned '{last}' (expected True/False). {result.StdErr.Trim()}".Trim()),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Detection.Error($"failed to run detect: {ex.Message}");
        }
    }

    public Task<ActionBackup?> CaptureBackupAsync(ActionSpec spec, ScanContext ctx, CancellationToken ct = default) =>
        // The "backup" of a script is its revert script.
        Task.FromResult<ActionBackup?>(new ActionBackup { Type = Type, Target = spec.Target, Existed = true });

    public Task ApplyAsync(ActionSpec spec, ScanContext ctx, CancellationToken ct = default) =>
        RunAsync(spec.Apply!, "apply", ct);

    public Task RestoreAsync(ActionSpec spec, ActionBackup? backup, ScanContext ctx, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(spec.Revert)
            ? throw new NotSupportedException("This powershell action does not define 'revert'.")
            : RunAsync(spec.Revert!, "revert", ct);

    private static async Task RunAsync(string script, string what, CancellationToken ct)
    {
        var wrapped = "$ErrorActionPreference = 'Stop'\n" + script;
        var result = await ProcessRunner.RunPowerShellAsync(wrapped, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"{what} script failed ({result.ExitCode}): {result.StdErr.Trim()}");
    }
}
