using System.Runtime.InteropServices;
using Debloater.Core.Engine;
using Debloater.Core.Model;
using Windows.Management.Deployment;

namespace Debloater.Core.Actions;

/// <summary>
/// "appx" action: removes a Store/inbox package for all users and deprovisions it
/// (so it does not come back on new accounts). Not reversible by the engine; "storeId" is recorded
/// for a later reinstall via winget.
/// </summary>
public sealed class AppxActionHandler : IActionHandler
{
    private const string CacheKey = "appx.inventory";

    public string Type => ActionSpec.Appx;
    public bool CanRestore => false;

    public void Validate(ActionSpec spec, IList<string> errors)
    {
        ActionHandlers.RequireField(spec.Name, "name", Type, errors);
        ActionHandlers.RequireOneOf(spec.State, "state", Type, errors, "removed");
    }

    /// <summary>Package inventory, collected once per scan.</summary>
    internal sealed class Inventory
    {
        public required HashSet<string> CurrentUser { get; init; }
        /// <summary>Packages of all users (name, full name); null when they could not be listed (requires administrator).
        /// Includes packages that are only "Staged" (prepared, with no user having the app installed): see <see cref="InstalledForSomeone"/>.</summary>
        public List<(string Name, string FullName)>? AllUsers { get; init; }
        /// <summary>null when they could not be listed (requires administrator).</summary>
        public HashSet<string>? Provisioned { get; init; }
    }

    private static Task<Inventory> GetInventoryAsync(ScanContext ctx) =>
        ctx.GetOrAddAsync(CacheKey, () => Task.Run(() =>
        {
            var pm = new PackageManager();
            var current = pm.FindPackagesForUser("").Select(p => p.Id.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            List<(string Name, string FullName)>? all = null;
            HashSet<string>? provisioned = null;
            if (ctx.System.IsElevated)
            {
                try { all = pm.FindPackages().Select(p => (p.Id.Name, p.Id.FullName)).ToList(); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or COMException) { }
                try { provisioned = pm.FindProvisionedPackages().Select(p => p.Id.Name).ToHashSet(StringComparer.OrdinalIgnoreCase); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or COMException) { }
            }
            return new Inventory { CurrentUser = current, AllUsers = all, Provisioned = provisioned };
        }));

    /// <summary>Matches the package name; accepts a trailing "*" as a prefix wildcard.</summary>
    internal static bool NameMatches(string pattern, string name) =>
        pattern.EndsWith('*')
            ? name.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(pattern, name, StringComparison.OrdinalIgnoreCase);

    private static bool AnyMatch(IEnumerable<string>? names, string pattern) =>
        names is not null && names.Any(n => NameMatches(pattern, n));

    /// <summary>
    /// Is any package with this name INSTALLED for some user? FindPackages() also returns packages that are only
    /// prepared (Staged), e.g. SYSTEM's MSTeams 1.0.0.1 on 26300, which are not installed apps and cannot
    /// be removed "for all users" (0x80073D19).
    /// </summary>
    private static bool InstalledForSomeone(PackageManager pm, IEnumerable<(string Name, string FullName)>? packages, string pattern) =>
        packages is not null && packages.Where(p => NameMatches(pattern, p.Name)).Any(p => IsInstalled(pm, p.FullName));

    private static bool IsInstalled(PackageManager pm, string fullName)
    {
        try
        {
            return pm.FindUsers(fullName).Any(u => u.InstallState == PackageInstallState.Installed);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or COMException)
        {
            return true; // when in doubt, count it as installed
        }
    }

    public async Task<Detection> DetectAsync(ActionSpec spec, ScanContext ctx, CancellationToken ct = default)
    {
        try
        {
            var inv = await GetInventoryAsync(ctx).ConfigureAwait(false);
            var name = spec.Name!;
            if (AnyMatch(inv.CurrentUser, name)) return Detection.NotDesired("installed (current user)");
            if (InstalledForSomeone(new PackageManager(), inv.AllUsers, name)) return Detection.NotDesired("installed for another user");
            if (AnyMatch(inv.Provisioned, name)) return Detection.NotDesired("provisioned for new users");
            return Detection.Desired(inv.Provisioned is null
                ? "not installed (current user; provisioning not checked without administrator)"
                : "neither installed nor provisioned");
        }
        catch (Exception ex)
        {
            return Detection.Error($"PackageManager: {ex.Message}");
        }
    }

    public async Task<ActionBackup?> CaptureBackupAsync(ActionSpec spec, ScanContext ctx, CancellationToken ct = default)
    {
        var inv = await GetInventoryAsync(ctx).ConfigureAwait(false);
        return new ActionBackup
        {
            Type = Type,
            Target = spec.Target,
            Existed = AnyMatch(inv.CurrentUser, spec.Name!) || InstalledForSomeone(new PackageManager(), inv.AllUsers, spec.Name!) || AnyMatch(inv.Provisioned, spec.Name!),
            Data = spec.StoreId,
        };
    }

    public async Task ApplyAsync(ActionSpec spec, ScanContext ctx, CancellationToken ct = default)
    {
        var pm = new PackageManager();
        var errors = new List<string>();
        var families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pkg in pm.FindPackages().Where(p => NameMatches(spec.Name!, p.Id.Name)).ToList())
        {
            ct.ThrowIfCancellationRequested();
            families.Add(pkg.Id.FamilyName);
            var fullName = pkg.Id.FullName;
            if (await TryDeployAsync(() => pm.RemovePackageAsync(fullName, RemovalOptions.RemoveForAllUsers)).ConfigureAwait(false) is { } error)
                errors.Add($"remove {fullName}: {error}");
        }

        try
        {
            foreach (var pkg in pm.FindProvisionedPackages().Where(p => NameMatches(spec.Name!, p.Id.Name)))
                families.Add(pkg.Id.FamilyName);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or COMException) { }

        foreach (var family in families)
        {
            ct.ThrowIfCancellationRequested();
            if (await TryDeployAsync(() => pm.DeprovisionPackageForAllUsersAsync(family)).ConfigureAwait(false) is { } error)
                errors.Add($"deprovision {family}: {error}");
        }

        ctx.Invalidate(CacheKey);
        if (errors.Count == 0) return;

        // Steps may "fail" because they are already done: removing the main package takes its resource
        // packages with it, and RemoveForAllUsers already deprovisions ("Element not found"). The final
        // state decides: it is only an error if the package is still installed for someone or provisioned.
        var remaining = Remaining(pm, spec.Name!);
        if (remaining.Count == 0)
        {
            ctx.AddNote($"{spec.Name}: removed; Windows reported errors in steps that were already done (ignored): {string.Join("; ", errors)}");
            return;
        }
        throw new InvalidOperationException($"still present: {string.Join(", ", remaining)}. Errors: {string.Join("; ", errors)}");
    }

    /// <summary>Runs a deployment operation; returns the error message, or null on success.</summary>
    private static async Task<string?> TryDeployAsync(
        Func<Windows.Foundation.IAsyncOperationWithProgress<DeploymentResult, DeploymentProgress>> operation)
    {
        try
        {
            var result = await operation();
            return result.ExtendedErrorCode is null ? null : Clean(result.ErrorText);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Clean(ex.Message);
        }
    }

    private static string Clean(string text) =>
        string.Join(" ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>Packages that still match the name: installed for some user or provisioned.</summary>
    private static List<string> Remaining(PackageManager pm, string name)
    {
        var left = pm.FindPackages().Where(p => NameMatches(name, p.Id.Name) && IsInstalled(pm, p.Id.FullName))
            .Select(p => $"{p.Id.FullName} (installed)").ToList();
        try
        {
            left.AddRange(pm.FindProvisionedPackages().Where(p => NameMatches(name, p.Id.Name)).Select(p => $"{p.Id.FullName} (provisioned)"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or COMException) { }
        return left;
    }

    public Task RestoreAsync(ActionSpec spec, ActionBackup? backup, ScanContext ctx, CancellationToken ct = default) =>
        throw new NotSupportedException(
            $"Removal of package '{spec.Name}' cannot be undone by the engine." +
            (spec.StoreId is null ? "" : $" Reinstall with: winget install --id {spec.StoreId} --source msstore"));
}
