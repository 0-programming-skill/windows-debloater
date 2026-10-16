using System.Runtime.InteropServices;
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
        /// <summary>Pacotes de todos os usuários (nome, nome completo); null quando não foi possível listar (requer administrador).
        /// Inclui pacotes só "Staged" (preparados, sem usuário com o app instalado): ver <see cref="InstalledForSomeone"/>.</summary>
        public List<(string Name, string FullName)>? AllUsers { get; init; }
        /// <summary>null quando não foi possível listar (requer administrador).</summary>
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

    /// <summary>Casa o nome do pacote; aceita "*" no fim como curinga de prefixo.</summary>
    internal static bool NameMatches(string pattern, string name) =>
        pattern.EndsWith('*')
            ? name.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(pattern, name, StringComparison.OrdinalIgnoreCase);

    private static bool AnyMatch(IEnumerable<string>? names, string pattern) =>
        names is not null && names.Any(n => NameMatches(pattern, n));

    /// <summary>
    /// Algum pacote com o nome está INSTALADO para algum usuário? FindPackages() também devolve pacotes só
    /// preparados (Staged), ex.: o MSTeams 1.0.0.1 do SYSTEM no 26300, que não são apps instalados e não
    /// podem ser removidos "para todos os usuários" (0x80073D19).
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
            return true; // na dúvida, conta como instalado
        }
    }

    public async Task<Detection> DetectAsync(ActionSpec spec, ScanContext ctx, CancellationToken ct = default)
    {
        try
        {
            var inv = await GetInventoryAsync(ctx).ConfigureAwait(false);
            var name = spec.Name!;
            if (AnyMatch(inv.CurrentUser, name)) return Detection.NotDesired("instalado (usuário atual)");
            if (InstalledForSomeone(new PackageManager(), inv.AllUsers, name)) return Detection.NotDesired("instalado para outro usuário");
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
                errors.Add($"remover {fullName}: {error}");
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
                errors.Add($"desprovisionar {family}: {error}");
        }

        ctx.Invalidate(CacheKey);
        if (errors.Count == 0) return;

        // Etapas podem "falhar" por já estarem feitas: remover o pacote principal leva junto os pacotes
        // de recursos, e RemoveForAllUsers já desprovisiona ("Elemento não encontrado"). Quem decide é o
        // estado final: só é erro se o pacote ainda está instalado para alguém ou provisionado.
        var remaining = Remaining(pm, spec.Name!);
        if (remaining.Count == 0)
        {
            ctx.AddNote($"{spec.Name}: removido; o Windows reportou erro em etapas que já estavam feitas (ignorado): {string.Join("; ", errors)}");
            return;
        }
        throw new InvalidOperationException($"ainda presente: {string.Join(", ", remaining)}. Erros: {string.Join("; ", errors)}");
    }

    /// <summary>Executa uma operação de implantação; devolve a mensagem de erro, ou null se deu certo.</summary>
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

    /// <summary>Pacotes que ainda casam com o nome: instalados para algum usuário ou provisionados.</summary>
    private static List<string> Remaining(PackageManager pm, string name)
    {
        var left = pm.FindPackages().Where(p => NameMatches(name, p.Id.Name) && IsInstalled(pm, p.Id.FullName))
            .Select(p => $"{p.Id.FullName} (instalado)").ToList();
        try
        {
            left.AddRange(pm.FindProvisionedPackages().Where(p => NameMatches(name, p.Id.Name)).Select(p => $"{p.Id.FullName} (provisionado)"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or COMException) { }
        return left;
    }

    public Task RestoreAsync(ActionSpec spec, ActionBackup? backup, ScanContext ctx, CancellationToken ct = default) =>
        throw new NotSupportedException(
            $"A remoção do pacote '{spec.Name}' não pode ser desfeita pelo engine." +
            (spec.StoreId is null ? "" : $" Reinstale com: winget install --id {spec.StoreId} --source msstore"));
}
