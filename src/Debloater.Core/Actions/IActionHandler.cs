using Debloater.Core.Engine;
using Debloater.Core.Model;

namespace Debloater.Core.Actions;

/// <summary>Resultado da detecção de uma ação.</summary>
public sealed record Detection(ActionState State, string Detail)
{
    public static Detection Desired(string detail) => new(ActionState.Desired, detail);
    public static Detection NotDesired(string detail) => new(ActionState.NotDesired, detail);
    public static Detection NotApplicable(string detail) => new(ActionState.NotApplicable, detail);
    public static Detection Error(string detail) => new(ActionState.Error, detail);
}

/// <summary>Implementa um tipo de ação do catálogo (registry, service, ...).</summary>
public interface IActionHandler
{
    /// <summary>Valor do campo "type" no YAML.</summary>
    string Type { get; }

    /// <summary>Se o handler consegue restaurar o estado original a partir de um backup.</summary>
    bool CanRestore { get; }

    /// <summary>Valida os campos da ação; adiciona mensagens em <paramref name="errors"/>.</summary>
    void Validate(ActionSpec spec, IList<string> errors);

    /// <summary>A ação já está no estado desejado?</summary>
    Task<Detection> DetectAsync(ActionSpec spec, ScanContext ctx, CancellationToken ct = default);

    /// <summary>Captura o valor atual (original), ou null se não há o que guardar.</summary>
    Task<ActionBackup?> CaptureBackupAsync(ActionSpec spec, ScanContext ctx, CancellationToken ct = default);

    /// <summary>Leva o sistema ao estado desejado.</summary>
    Task ApplyAsync(ActionSpec spec, ScanContext ctx, CancellationToken ct = default);

    /// <summary>Restaura o estado original. <paramref name="backup"/> pode ser null (ex.: powershell usa o script revert).</summary>
    Task RestoreAsync(ActionSpec spec, ActionBackup? backup, ScanContext ctx, CancellationToken ct = default);
}

public static class ActionHandlers
{
    private static readonly Dictionary<string, IActionHandler> s_handlers = new IActionHandler[]
    {
        new RegistryActionHandler(),
        new ServiceActionHandler(),
        new ScheduledTaskActionHandler(),
        new AppxActionHandler(),
        new OptionalFeatureActionHandler(),
        new PowerShellActionHandler(),
    }.ToDictionary(h => h.Type, StringComparer.Ordinal);

    public static IReadOnlyCollection<string> KnownTypes => s_handlers.Keys;

    public static bool TryGet(string type, out IActionHandler handler) => s_handlers.TryGetValue(type, out handler!);

    public static IActionHandler Get(string type) =>
        s_handlers.TryGetValue(type, out var h) ? h : throw new InvalidOperationException($"Tipo de ação desconhecido: '{type}'.");

    internal static void RequireField(string? value, string field, string type, IList<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) errors.Add($"ação '{type}' exige o campo '{field}'.");
    }

    internal static void RequireOneOf(string? value, string field, string type, IList<string> errors, params string[] allowed)
    {
        if (string.IsNullOrWhiteSpace(value))
            errors.Add($"ação '{type}' exige o campo '{field}' ({string.Join(" | ", allowed)}).");
        else if (!allowed.Contains(value, StringComparer.OrdinalIgnoreCase))
            errors.Add($"ação '{type}': '{field}' inválido '{value}' (esperado: {string.Join(" | ", allowed)}).");
    }
}
