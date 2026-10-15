using Debloater.Core.Engine;
using Debloater.Core.Model;
using Windows.Management.Deployment;

namespace Debloater.Core.Actions;

/// <summary>
/// Ação "appx": remove um pacote da Store/inbox para todos os usuários e o desprovisiona
/// (para não voltar em novas contas). Não é reversível pelo engine; "storeId" fica registrado
/// para reinstalação futura via winget.
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

    /// <summary>Inventário de pacotes, coletado uma vez por varredura.</summary>
    internal sealed class Inventory
    {
        public required HashSet<string> CurrentUser { get; init; }
        /// <summary>null quando não foi possível listar (requer administrador).</summary>
        public HashSet<string>? AllUsers { get; init; }
        /// <summary>null quando não foi possível listar (requer administrador).</summary>
        public HashSet<string>? Provisioned { get; init; }
    }

    private static Task<Inventory> GetInventoryAsync(ScanContext ctx) =>
        ctx.GetOrAddAsync(CacheKey, () => Task.Run(() =>
        {
            var pm = new PackageManager();
            var current = pm.FindPackagesForUser("").Select(p => p.Id.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            HashSet<string>? all = null;
            HashSet<string>? provisioned = null;
            if (ctx.System.IsElevated)
            {
                try { all = pm.FindPackages().Select(p => p.Id.Name).ToHashSet(StringComparer.OrdinalIgnoreCase); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }
                try { provisioned = pm.FindProvisionedPackages().Select(p => p.Id.Name).ToHashSet(StringComparer.OrdinalIgnoreCase); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }
            }
            return new Inventory { CurrentUser = current, AllUsers = all, Provisioned = provisioned };
        }));

    /// <summary>Casa o nome do pacote; aceita "*" no fim como curinga de prefixo.</summary>
    internal static bool NameMatches(string pattern, string name) =>
        pattern.EndsWith('*')
            ? name.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(pattern, name, StringComparison.OrdinalIgnoreCase);

    private static bool AnyMatch(IEnumerable<string>? names, string pattern) =>
        names is not null && names.Any(n => NameMatches(pattern, n));

    public async Task<Detection> DetectAsync(ActionSpec spec, ScanContext ctx, CancellationToken ct = default)
    {
        try
        {
            var inv = await GetInventoryAsync(ctx).ConfigureAwait(false);
            var name = spec.Name!;
            if (AnyMatch(inv.CurrentUser, name)) return Detection.NotDesired("instalado (usuário atual)");
            if (AnyMatch(inv.AllUsers, name)) return Detection.NotDesired("instalado para outro usuário");
            if (AnyMatch(inv.Provisioned, name)) return Detection.NotDesired("provisionado para novos usuários");
            return Detection.Desired(inv.Provisioned is null
                ? "não instalado (usuário atual; provisionamento não verificado sem administrador)"
                : "não instalado nem provisionado");
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
            Existed = AnyMatch(inv.CurrentUser, spec.Name!) || AnyMatch(inv.AllUsers, spec.Name!) || AnyMatch(inv.Provisioned, spec.Name!),
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
            var result = await pm.RemovePackageAsync(pkg.Id.FullName, RemovalOptions.RemoveForAllUsers);
            if (result.ExtendedErrorCode is not null)
                errors.Add($"remover {pkg.Id.FullName}: {result.ErrorText}");
        }

        var provisioned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var pkg in pm.FindProvisionedPackages().Where(p => NameMatches(spec.Name!, p.Id.Name)))
                provisioned.Add(pkg.Id.FamilyName);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }

        foreach (var family in families.Union(provisioned, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var result = await pm.DeprovisionPackageForAllUsersAsync(family);
            // Só é erro real se o pacote estava de fato provisionado.
            if (result.ExtendedErrorCode is not null && provisioned.Contains(family))
                errors.Add($"desprovisionar {family}: {result.ErrorText}");
        }

        ctx.Invalidate(CacheKey);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("; ", errors));
    }

    public Task RestoreAsync(ActionSpec spec, ActionBackup? backup, ScanContext ctx, CancellationToken ct = default) =>
        throw new NotSupportedException(
            $"A remoção do pacote '{spec.Name}' não pode ser desfeita pelo engine." +
            (spec.StoreId is null ? "" : $" Reinstale com: winget install --id {spec.StoreId} --source msstore"));
}
