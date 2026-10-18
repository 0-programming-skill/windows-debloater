using System.Security;
using Debloater.Core.Admx;
using Debloater.Core.Engine;
using Debloater.Core.Platform;

namespace Debloater.Core.Policies;

public sealed class PolicyEngineOptions
{
    /// <summary>Pasta de backups dos tweaks; os das políticas ficam na subpasta "policies".</summary>
    public string BackupDirectory { get; init; } = BackupStore.DefaultDirectory;

    /// <summary>Exigir processo elevado para aplicar/reverter. Só desligue em testes.</summary>
    public bool RequireElevation { get; init; } = true;

    public SystemInfo? System { get; init; }

    /// <summary>Registro alvo. Padrão: o real (HKLM/HKCU). Testes usam <see cref="WindowsPolicyRegistry.Redirected"/>.</summary>
    public IPolicyRegistry? Registry { get; init; }
}

/// <summary>Resultado de aplicar/reverter uma política.</summary>
public sealed record PolicyOperationResult(string Policy, bool Success, string Message, IReadOnlyList<string> Errors)
{
    /// <summary>Operações executadas (linhas de prévia: "+ ...", "- ...").</summary>
    public IReadOnlyList<string> Operations { get; init; } = [];

    /// <summary>Estado lido depois da operação (quando a definição é conhecida).</summary>
    public AdmxPolicyState? StateAfter { get; init; }
}

/// <summary>
/// Aplica e reverte configurações de políticas ADMX gravando direto no registro (como WinUtil/O&amp;O),
/// com backup do valor original de tudo o que for tocado. Não altera o Registry.pol: o gpedit.msc não
/// mostra o que foi gravado, e uma GPO de domínio pode sobrescrever.
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

    // ---------------- aplicar ----------------

    public PolicyOperationResult Apply(AdmxPolicy policy, PolicySetting setting, OperationLog? log = null)
    {
        EnsureElevated();
        var name = policy.QualifiedName;
        var plan = Plan(policy, setting);
        if (!plan.IsValid)
        {
            log?.Write($"  {name}: configuração inválida: {string.Join(" ", plan.Errors)}");
            return new PolicyOperationResult(name, false, "Configuração inválida; nada foi alterado.", plan.Errors);
        }
        if (!plan.HasEffect)
        {
            log?.Write($"  {name}: já está assim; nada a mudar.");
            return new PolicyOperationResult(name, true, "Já estava assim; nada foi alterado.", [])
            {
                StateAfter = ReadCurrent(policy, plan.Scope).State,
            };
        }

        // 1) Backup antes de qualquer alteração; o primeiro valor original capturado é preservado.
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

        // 2) Gravação.
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
                log?.Write($"  erro: {line}: {ex.Message}");
            }
        }
        foreach (var k in plan.KeysToDelete)
        {
            try
            {
                if (Registry.DeleteKeyIfEmpty(plan.Scope, k))
                {
                    done.Add($@"- {root}\{k}\");
                    log?.Write($@"  - {root}\{k}\ (chave apagada)");
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
            {
                log?.Write($@"  aviso: a chave {root}\{k} foi mantida: {ex.Message}");
            }
        }

        // 3) Conferência: recalcular o plano não deve sobrar nada a fazer.
        var check = Plan(policy, setting);
        var state = ReadCurrent(policy, plan.Scope).State;
        if (errors.Count == 0 && check.Effective.Any())
            errors.Add("depois de gravar, o registro ainda difere do pedido: " + string.Join("; ", check.Describe()));
        var ok = errors.Count == 0;
        var message = ok
            ? $"Aplicada ({PolicySetting.StateText(setting.State)}, {root}): {done.Count} alteração(ões). Estado lido: {StateWord(state)}."
            : $"Aplicada com {errors.Count} erro(s); backup mantido para reverter. Estado lido: {StateWord(state)}.";
        return new PolicyOperationResult(name, ok, message, errors) { Operations = done, StateAfter = state };
    }

    // ---------------- reverter ----------------

    /// <summary>Restaura exatamente os valores originais do backup. A definição é opcional (só para ler o estado depois).</summary>
    public PolicyOperationResult Revert(string qualifiedName, AdmxPolicy? policy = null, OperationLog? log = null)
    {
        EnsureElevated();
        var backup = Backups.Load(qualifiedName);
        if (backup is null)
        {
            return new PolicyOperationResult(qualifiedName, false,
                "Nenhum backup para esta política (ela não foi alterada por este programa, ou o backup foi apagado). Nada foi revertido.", []);
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
                log?.Write($"  erro: {what}: {ex.Message}");
            }
        }

        foreach (var k in backup.DeletedKeys)
            Try($@"+ {k.Root}\{k.Key}\ (chave recriada)", () => Registry.CreateKey(ScopeOf(k.Root), k.Key));

        foreach (var v in backup.Values)
        {
            var scope = ScopeOf(v.Root);
            var path = $@"{v.Root}\{v.Key}\{v.Name}";
            if (v.Existed)
            {
                var value = PolicyValues.FromBackup(v.Kind ?? throw new FormatException("backup sem 'kind'."), v.Data, v.MultiData);
                Try($"+ {path} = {PolicyValues.Describe(value)} (original)", () => Registry.Write(scope, v.Key, v.Name, value));
            }
            else
            {
                Try($"- {path} (não existia)", () => Registry.Delete(scope, v.Key, v.Name));
            }
        }

        // Chaves criadas pela aplicação: apaga as que ficaram vazias, de baixo para cima (best-effort).
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
                    log?.Write($@"  aviso: a chave vazia {created.Root}\{current} foi mantida: {ex.Message}");
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
        var stateText = state is { } s ? $" Estado lido: {StateWord(s)}." : "";
        return errors.Count == 0
            ? new PolicyOperationResult(qualifiedName, true, $"Revertida ao original ({backup.Values.Count} valor(es) restaurado(s)).{stateText}", errors) { Operations = done, StateAfter = state }
            : new PolicyOperationResult(qualifiedName, false, $"Revertida parcialmente ({errors.Count} erro(s)); backup mantido.{stateText}", errors) { Operations = done, StateAfter = state };
    }

    // ---------------- helpers ----------------

    public static string StateWord(AdmxPolicyState s) => s switch
    {
        AdmxPolicyState.Enabled => "Habilitada",
        AdmxPolicyState.Disabled => "Desabilitada",
        AdmxPolicyState.NotConfigured => "Não configurada",
        _ => "Valores próprios",
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
