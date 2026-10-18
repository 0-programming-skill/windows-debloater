using System.Security;
using Debloater.Core.Admx;
using Debloater.Core.Engine;
using Debloater.Core.Platform;

namespace Debloater.Core.Policies;

public sealed class PolicyEngineOptions
{
    /// <summary>Tweak backup folder; policy backups go in the "policies" subfolder.</summary>
    public string BackupDirectory { get; init; } = BackupStore.DefaultDirectory;

    /// <summary>Require an elevated process to apply/revert. Only turn off in tests.</summary>
    public bool RequireElevation { get; init; } = true;

    public SystemInfo? System { get; init; }

    /// <summary>Target registry. Default: the real one (HKLM/HKCU). Tests use <see cref="WindowsPolicyRegistry.Redirected"/>.</summary>
    public IPolicyRegistry? Registry { get; init; }
}

/// <summary>Result of applying/reverting a policy.</summary>
public sealed record PolicyOperationResult(string Policy, bool Success, string Message, IReadOnlyList<string> Errors)
{
    /// <summary>Operations performed (preview lines: "+ ...", "- ...").</summary>
    public IReadOnlyList<string> Operations { get; init; } = [];

    /// <summary>State read after the operation (when the definition is known).</summary>
    public AdmxPolicyState? StateAfter { get; init; }
}

/// <summary>
/// Applies and reverts ADMX policy settings by writing directly to the registry (like WinUtil/O&amp;O),
/// backing up the original value of everything it touches. Does not change Registry.pol: gpedit.msc does
/// not show what was written, and a domain GPO may overwrite it.
/// </summary>
public sealed class PolicyEngine
{
    private readonly PolicyEngineOptions _options;

    public PolicyEngine(PolicyEngineOptions? options = null)
    {
        _options = options ?? new PolicyEngineOptions();
        System = _options.System ?? SystemInfo.Current;
        Registry = _options.Registry ?? WindowsPolicyRegistry.Default;
        Backups = new PolicyBackupStore(_options.BackupDirectory);
    }

    public SystemInfo System { get; }
    public IPolicyRegistry Registry { get; }
    public PolicyBackupStore Backups { get; }

    public PolicyWritePlan Plan(AdmxPolicy policy, PolicySetting setting) => PolicyWriter.Plan(policy, setting, Registry);

    public (AdmxPolicyState State, PolicySetting Setting) ReadCurrent(AdmxPolicy policy, PolicyScope? scope = null) =>
        PolicyWriter.ReadCurrent(policy, scope ?? PolicySetting.DefaultScope(policy.Class), Registry);

    // ---------------- apply ----------------

    public PolicyOperationResult Apply(AdmxPolicy policy, PolicySetting setting, OperationLog? log = null)
    {
        EnsureElevated();
        var name = policy.QualifiedName;
        var plan = Plan(policy, setting);
        if (!plan.IsValid)
        {
            log?.Write($"  {name}: invalid setting: {string.Join(" ", plan.Errors)}");
            return new PolicyOperationResult(name, false, "Invalid setting; nothing was changed.", plan.Errors);
        }
        if (!plan.HasEffect)
        {
            log?.Write($"  {name}: already set; nothing to change.");
            return new PolicyOperationResult(name, true, "Already set; nothing was changed.", [])
            {
                StateAfter = ReadCurrent(policy, plan.Scope).State,
            };
        }

        // 1) Backup before any change; the first original value captured is preserved.
        var root = plan.Root;
        var backup = Backups.Load(name) ?? new PolicyBackup { Policy = name, CreatedUtc = DateTime.UtcNow, WindowsBuild = System.FullBuild };
        foreach (var c in plan.Effective)
        {
            if (backup.Find(root, c.Key, c.Name) is not null) continue;
            var entry = new PolicyValueBackup { Root = root, Key = c.Key, Name = c.Name, Existed = c.Before is not null };
            if (c.Before is { } before)
            {
                var (kind, data, multi) = PolicyValues.ToBackup(before);
                (entry.Kind, entry.Data, entry.MultiData) = (kind, data, multi);
            }
            backup.Values.Add(entry);
        }
        foreach (var c in plan.Effective.Where(c => !c.IsDelete))
        {
            if (FirstMissingKey(plan.Scope, c.Key) is not { } missing) continue;
            if (CoveredByCreated(backup, root, missing)) continue;
            backup.CreatedKeys.Add(new PolicyKeyBackup { Root = root, Key = missing });
        }
        foreach (var k in plan.KeysToDelete)
        {
            if (CoveredByCreated(backup, root, k) || backup.DeletedKeys.Any(d => SameKey(d, root, k))) continue;
            backup.DeletedKeys.Add(new PolicyKeyBackup { Root = root, Key = k });
        }
        backup.UpdatedUtc = DateTime.UtcNow;
        backup.History.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {setting}");
        if (backup.History.Count > 20) backup.History.RemoveRange(0, backup.History.Count - 20);
        Backups.Save(backup);

        // 2) Write.
        var errors = new List<string>();
        var done = new List<string>();
        var lines = plan.Describe().ToList();
        int li = 0;
        foreach (var c in plan.Changes)
        {
            if (c.IsNoOp) continue;
            var line = lines[li++];
            try
            {
                if (c.After is { } after) Registry.Write(plan.Scope, c.Key, c.Name, after);
                else Registry.Delete(plan.Scope, c.Key, c.Name);
                done.Add(line);
                log?.Write($"  {line}");
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException or InvalidOperationException)
            {
                errors.Add($"{line}: {ex.Message}");
                log?.Write($"  error: {line}: {ex.Message}");
            }
        }
        foreach (var k in plan.KeysToDelete)
        {
            try
            {
                if (Registry.DeleteKeyIfEmpty(plan.Scope, k))
                {
                    done.Add($@"- {root}\{k}\");
                    log?.Write($@"  - {root}\{k}\ (key deleted)");
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
            {
                log?.Write($@"  warning: key {root}\{k} was kept: {ex.Message}");
            }
        }

        // 3) Check: recomputing the plan must leave nothing to do.
        var check = Plan(policy, setting);
        var state = ReadCurrent(policy, plan.Scope).State;
        if (errors.Count == 0 && check.Effective.Any())
            errors.Add("after writing, the registry still differs from the request: " + string.Join("; ", check.Describe()));
        var ok = errors.Count == 0;
        var message = ok
            ? $"Applied ({PolicySetting.StateText(setting.State)}, {root}): {done.Count} change(s). State read: {StateWord(state)}."
            : $"Applied with {errors.Count} error(s); backup kept for revert. State read: {StateWord(state)}.";
        return new PolicyOperationResult(name, ok, message, errors) { Operations = done, StateAfter = state };
    }

    // ---------------- revert ----------------

    /// <summary>Restores exactly the original values from the backup. The definition is optional (only used to read the state afterwards).</summary>
    public PolicyOperationResult Revert(string qualifiedName, AdmxPolicy? policy = null, OperationLog? log = null)
    {
        EnsureElevated();
        var backup = Backups.Load(qualifiedName);
        if (backup is null)
        {
            return new PolicyOperationResult(qualifiedName, false,
                "No backup for this policy (it was not changed by this program, or the backup was deleted). Nothing was reverted.", []);
        }

        var errors = new List<string>();
        var done = new List<string>();
        void Try(string what, Action action)
        {
            try
            {
                action();
                done.Add(what);
                log?.Write($"  {what}");
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException or InvalidOperationException or FormatException)
            {
                errors.Add($"{what}: {ex.Message}");
                log?.Write($"  error: {what}: {ex.Message}");
            }
        }

        foreach (var k in backup.DeletedKeys)
            Try($@"+ {k.Root}\{k.Key}\ (key recreated)", () => Registry.CreateKey(ScopeOf(k.Root), k.Key));

        foreach (var v in backup.Values)
        {
            var scope = ScopeOf(v.Root);
            var path = $@"{v.Root}\{v.Key}\{v.Name}";
            if (v.Existed)
            {
                var value = PolicyValues.FromBackup(v.Kind ?? throw new FormatException("backup without 'kind'."), v.Data, v.MultiData);
                Try($"+ {path} = {PolicyValues.Describe(value)} (original)", () => Registry.Write(scope, v.Key, v.Name, value));
            }
            else
            {
                Try($"- {path} (did not exist)", () => Registry.Delete(scope, v.Key, v.Name));
            }
        }

        // Keys created by the apply: delete the ones left empty, bottom-up (best-effort).
        foreach (var created in backup.CreatedKeys)
        {
            var scope = ScopeOf(created.Root);
            var paths = backup.Values.Where(v => SameRoot(v.Root, created.Root)).Select(v => v.Key)
                .Concat(backup.DeletedKeys.Where(d => SameRoot(d.Root, created.Root)).Select(d => d.Key))
                .Append(created.Key)
                .Where(k => IsSameOrUnder(k, created.Key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(k => k.Length);
            foreach (var start in paths)
            {
                var current = start.Trim('\\');
                try
                {
                    while (IsSameOrUnder(current, created.Key))
                    {
                        if (!Registry.DeleteKeyIfEmpty(scope, current) && Registry.KeyExists(scope, current)) break;
                        if (current.Length <= created.Key.Trim('\\').Length) break;
                        current = current[..current.LastIndexOf('\\')];
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
                {
                    log?.Write($@"  warning: empty key {created.Root}\{current} was kept: {ex.Message}");
                    break;
                }
            }
        }

        if (errors.Count == 0) Backups.Delete(qualifiedName);
        AdmxPolicyState? state = null;
        if (policy is not null)
        {
            var scope = backup.Values.FirstOrDefault() is { } first ? ScopeOf(first.Root) : PolicySetting.DefaultScope(policy.Class);
            state = ReadCurrent(policy, scope).State;
        }
        var stateText = state is { } s ? $" State read: {StateWord(s)}." : "";
        return errors.Count == 0
            ? new PolicyOperationResult(qualifiedName, true, $"Reverted to original ({backup.Values.Count} value(s) restored).{stateText}", errors) { Operations = done, StateAfter = state }
            : new PolicyOperationResult(qualifiedName, false, $"Partially reverted ({errors.Count} error(s)); backup kept.{stateText}", errors) { Operations = done, StateAfter = state };
    }

    // ---------------- helpers ----------------

    public static string StateWord(AdmxPolicyState s) => s switch
    {
        AdmxPolicyState.Enabled => "Enabled",
        AdmxPolicyState.Disabled => "Disabled",
        AdmxPolicyState.NotConfigured => "Not configured",
        _ => "Custom values",
    };

    private static PolicyScope ScopeOf(string root) =>
        root.Equals("HKCU", StringComparison.OrdinalIgnoreCase) ? PolicyScope.User : PolicyScope.Machine;

    private static bool SameRoot(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    private static bool SameKey(PolicyKeyBackup k, string root, string key) =>
        SameRoot(k.Root, root) && k.Key.Trim('\\').Equals(key.Trim('\\'), StringComparison.OrdinalIgnoreCase);

    private static bool IsSameOrUnder(string key, string ancestor)
    {
        key = key.Trim('\\');
        ancestor = ancestor.Trim('\\');
        return key.Equals(ancestor, StringComparison.OrdinalIgnoreCase)
            || key.StartsWith(ancestor + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool CoveredByCreated(PolicyBackup backup, string root, string key) =>
        backup.CreatedKeys.Any(c => SameRoot(c.Root, root) && IsSameOrUnder(key, c.Key));

    private string? FirstMissingKey(PolicyScope scope, string key)
    {
        var parts = key.Trim('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i <= parts.Length; i++)
        {
            var partial = string.Join('\\', parts.Take(i));
            if (!Registry.KeyExists(scope, partial)) return partial;
        }
        return null;
    }

    private void EnsureElevated()
    {
        if (_options.RequireElevation && !System.IsElevated) throw new ElevationRequiredException();
    }
}
