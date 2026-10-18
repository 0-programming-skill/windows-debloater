using Debloater.Core.Engine;
using Debloater.Core.Model;

namespace Debloater.Core.Actions;

/// <summary>Result of detecting an action.</summary>
public sealed record Detection(ActionState State, string Detail)
{
    public static Detection Desired(string detail) => new(ActionState.Desired, detail);
    public static Detection NotDesired(string detail) => new(ActionState.NotDesired, detail);
    public static Detection NotApplicable(string detail) => new(ActionState.NotApplicable, detail);
    public static Detection Error(string detail) => new(ActionState.Error, detail);
}

/// <summary>Implements one catalog action type (registry, service, ...).</summary>
public interface IActionHandler
{
    /// <summary>Value of the "type" field in the YAML.</summary>
    string Type { get; }

    /// <summary>Whether the handler can restore the original state from a backup.</summary>
    bool CanRestore { get; }

    /// <summary>Validates the action's fields; adds messages to <paramref name="errors"/>.</summary>
    void Validate(ActionSpec spec, IList<string> errors);

    /// <summary>Is the action already in the desired state?</summary>
    Task<Detection> DetectAsync(ActionSpec spec, ScanContext ctx, CancellationToken ct = default);

    /// <summary>Captures the current (original) value, or null if there is nothing to keep.</summary>
    Task<ActionBackup?> CaptureBackupAsync(ActionSpec spec, ScanContext ctx, CancellationToken ct = default);

    /// <summary>Brings the system to the desired state.</summary>
    Task ApplyAsync(ActionSpec spec, ScanContext ctx, CancellationToken ct = default);

    /// <summary>Restores the original state. <paramref name="backup"/> may be null (e.g. powershell uses the revert script).</summary>
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
        s_handlers.TryGetValue(type, out var h) ? h : throw new InvalidOperationException($"Unknown action type: '{type}'.");

    internal static void RequireField(string? value, string field, string type, IList<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) errors.Add($"action '{type}' requires the field '{field}'.");
    }

    internal static void RequireOneOf(string? value, string field, string type, IList<string> errors, params string[] allowed)
    {
        if (string.IsNullOrWhiteSpace(value))
            errors.Add($"action '{type}' requires the field '{field}' ({string.Join(" | ", allowed)}).");
        else if (!allowed.Contains(value, StringComparer.OrdinalIgnoreCase))
            errors.Add($"action '{type}': invalid '{field}' '{value}' (expected: {string.Join(" | ", allowed)}).");
    }
}
