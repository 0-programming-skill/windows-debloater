using Debloater.Core.Model;
using Debloater.Core.Platform;

namespace Debloater.Core.Engine;

/// <summary>Resultado de uma pré-condição: <see cref="Met"/> true/false, ou null se o script falhou.</summary>
public sealed record PreconditionResult(bool? Met, string Detail)
{
    public static PreconditionResult True(string detail = "pré-condição atendida") => new(true, detail);
    public static PreconditionResult False(string detail) => new(false, detail);
    public static PreconditionResult Failed(string detail) => new(null, detail);
}

/// <summary>Avalia a <see cref="Precondition"/> de um tweak rodando o script PowerShell.</summary>
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
            return PreconditionResult.Failed($"falha ao executar a pré-condição: {ex.Message}");
        }
    }

    /// <summary>Interpreta a última linha da saída: True / False (sem diferenciar maiúsculas).</summary>
    public static PreconditionResult Interpret(string stdout, string stderr, string? reason)
    {
        var last = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        return last.ToLowerInvariant() switch
        {
            "true" => PreconditionResult.True(),
            "false" => PreconditionResult.False(string.IsNullOrWhiteSpace(reason) ? "pré-condição não atendida" : reason.Trim()),
            _ => PreconditionResult.Failed($"pré-condição retornou '{last}' (esperado True/False). {stderr.Trim()}".Trim()),
        };
    }
}
