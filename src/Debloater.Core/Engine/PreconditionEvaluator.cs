using Debloater.Core.Model;
using Debloater.Core.Platform;

namespace Debloater.Core.Engine;

/// <summary>Result of a precondition: <see cref="Met"/> true/false, or null if the script failed.</summary>
public sealed record PreconditionResult(bool? Met, string Detail)
{
    public static PreconditionResult True(string detail = "precondition met") => new(true, detail);
    public static PreconditionResult False(string detail) => new(false, detail);
    public static PreconditionResult Failed(string detail) => new(null, detail);
}

/// <summary>Evaluates a tweak's <see cref="Precondition"/> by running the PowerShell script.</summary>
public static class PreconditionEvaluator
{
    public static async Task<PreconditionResult> EvaluateAsync(Precondition precondition, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(precondition.Powershell)) return PreconditionResult.True();
        try
        {
            var result = await ProcessRunner.RunPowerShellAsync(precondition.Powershell, ct).ConfigureAwait(false);
            return Interpret(result.StdOut, result.StdErr, precondition.Reason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return PreconditionResult.Failed($"failed to run the precondition: {ex.Message}");
        }
    }

    /// <summary>Interprets the last line of the output: True / False (case-insensitive).</summary>
    public static PreconditionResult Interpret(string stdout, string stderr, string? reason)
    {
        var last = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        return last.ToLowerInvariant() switch
        {
            "true" => PreconditionResult.True(),
            "false" => PreconditionResult.False(string.IsNullOrWhiteSpace(reason) ? "precondition not met" : reason.Trim()),
            _ => PreconditionResult.Failed($"precondition returned '{last}' (expected True/False). {stderr.Trim()}".Trim()),
        };
    }
}
