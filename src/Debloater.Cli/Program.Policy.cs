using Debloater.Core.Admx;
using Debloater.Core.Engine;
using Debloater.Core.Model;
using Debloater.Core.Policies;
using Debloater.Core.Reference;

namespace Debloater.Cli;

internal static partial class Program
{
    private const string PolicyUsage = """
        Uso:
          policy show   <[Arquivo.admx:]Nome> [--scope machine|user] [--state S [--value id=valor]...]
          policy set    <[Arquivo.admx:]Nome> --state enabled|disabled|notconfigured [--value id=valor]...
                        [--scope machine|user] [--dry-run] [--max-risk high] [--backup-dir DIR]
          policy revert <[Arquivo.admx:]Nome> [--backup-dir DIR]
          policy backups [--backup-dir DIR]
        """;

    private static int Policy(Args a)
    {
        var sub = a.Positional.Count > 0 ? a.Positional[0].ToLowerInvariant() : null;
        if (sub == "backups") return PolicyBackups(a);
        if (sub is not ("show" or "set" or "revert") || a.Positional.Count < 2) return Fail(PolicyUsage);

        if (sub == "revert" && a.Positional[1].Contains(':'))
        {
            // Reverter não precisa da definição ADMX (o backup tem tudo); usa-a só para ler o estado depois.
            var catalogOpt = Directory.Exists(a.AdmxDir) ? LoadAdmx(a) : null;
            var def = catalogOpt?.Policies.FirstOrDefault(p => p.QualifiedName.Equals(a.Positional[1], StringComparison.OrdinalIgnoreCase));
            return PolicyRevert(a, def?.QualifiedName ?? a.Positional[1], def);
        }

        var catalog = LoadAdmx(a);
        if (catalog is null) return 1;
        var policy = FindPolicy(catalog, a.Positional[1]);
        if (policy is null) return 1;

        return sub switch
        {
            "show" => PolicyShow(a, policy),
            "set" => PolicySet(a, policy),
            _ => PolicyRevert(a, policy.QualifiedName, policy),
        };
    }

    private static AdmxPolicy? FindPolicy(AdmxCatalog catalog, string query)
    {
        var matches = catalog.Policies.Where(p => p.QualifiedName.Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) matches = catalog.Policies.Where(p => p.Name.Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1) return matches[0];
        if (matches.Count == 0) Console.Error.WriteLine($"Política '{query}' não encontrada. Use 'admx list --search {query}'.");
        else Console.Error.WriteLine($"'{query}' é ambíguo: {string.Join(", ", matches.Select(m => m.QualifiedName))}. Use Arquivo.admx:Nome.");
        return null;
    }

    private static PolicyEngine CreatePolicyEngine(Args a) =>
        new(new PolicyEngineOptions { BackupDirectory = a.BackupDir ?? BackupStore.DefaultDirectory });

    private static PolicySetting? BuildSetting(Args a, AdmxPolicy policy)
    {
        if (a.PolicyState is null)
        {
            Console.Error.WriteLine("Informe --state enabled|disabled|notconfigured.");
            return null;
        }
        if (!PolicySetting.TryParseState(a.PolicyState, out var state))
        {
            Console.Error.WriteLine($"--state inválido '{a.PolicyState}' (enabled|disabled|notconfigured).");
            return null;
        }
        var setting = new PolicySetting { Policy = policy.QualifiedName, State = state };
        if (a.Scope is { } scopeText)
        {
            if (scopeText.ToLowerInvariant() is "machine" or "computer" or "computador" or "hklm") setting.Scope = PolicyScope.Machine;
            else if (scopeText.ToLowerInvariant() is "user" or "usuario" or "usuário" or "hkcu") setting.Scope = PolicyScope.User;
            else
            {
                Console.Error.WriteLine($"--scope inválido '{scopeText}' (machine|user).");
                return null;
            }
        }
        foreach (var v in a.Values)
        {
            int eq = v.IndexOf('=');
            if (eq <= 0)
            {
                Console.Error.WriteLine($"--value '{v}' deve ser id=valor (ids: {string.Join(", ", policy.Elements.Select(e => e.Id))}).");
                return null;
            }
            var id = v[..eq].Trim();
            if (!setting.Values.TryGetValue(id, out var list)) setting.Values[id] = list = [];
            list.Add(v[(eq + 1)..]);
        }
        return setting;
    }

    private static CspPolicy? FindCsp(Args a, AdmxPolicy policy)
    {
        var path = a.CspFile ?? new[]
        {
            FindRepoRoot() is { } root ? Path.Combine(root, "reference", CspFileName) : null,
            Path.Combine(AppContext.BaseDirectory, "reference", CspFileName),
        }.FirstOrDefault(p => p is not null && File.Exists(p));
        if (path is null || !File.Exists(path)) return null;
        try
        {
            return PolicyAdvisor.IndexCspByAdmx(CspMarkdownParser.Load(path).Policies).GetValueOrDefault(policy.QualifiedName);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static void PrintWarnings(Args a, AdmxPolicy policy, PolicySetting setting)
    {
        var warnings = PolicyAdvisor.Warnings(policy, FindCsp(a, policy), PolicyEnvironment.Current(), setting.State, setting.EffectiveScope(policy.Class));
        foreach (var w in warnings) Console.WriteLine($"  {(w.Level == PolicyWarningLevel.Warning ? "!" : "i")} {w.Text}");
    }

    private static void PrintPlan(PolicyWritePlan plan)
    {
        foreach (var e in plan.Errors) Console.WriteLine($"  x {e}");
        foreach (var n in plan.Notes) Console.WriteLine($"  i {n}");
        if (!plan.IsValid) return;
        var lines = plan.Describe().ToList();
        if (lines.Count == 0) Console.WriteLine("  (nada a mudar: o registro já está assim)");
        foreach (var l in lines) Console.WriteLine($"  {l}");
    }

    private static int PolicyShow(Args a, AdmxPolicy p)
    {
        var engine = CreatePolicyEngine(a);
        Console.WriteLine($"{p.QualifiedName}  ({p.Namespace})");
        Console.WriteLine($"  Nome exibido : {p.DisplayName}");
        Console.WriteLine($"  Classe       : {p.Class}");
        Console.WriteLine($"  Categoria    : {string.Join(" > ", p.CategoryPath)}");
        Console.WriteLine($"  Com suporte  : {p.SupportedOn}");
        Console.WriteLine($"  Risco        : {RiskText(PolicyAdvisor.Risk(p))}");
        Console.WriteLine($"  Chave        : {p.Key}" + (p.ValueName is null ? "" : $"\\{p.ValueName}"));
        if (p.EnabledValue is not null || p.ValueName is not null) Console.WriteLine($"  Habilitada   : {p.EnabledValue?.ToString() ?? "1 (padrão)"}");
        if (p.DisabledValue is not null || p.ValueName is not null) Console.WriteLine($"  Desabilitada : {p.DisabledValue?.ToString() ?? "(apaga o valor)"}");
        PrintList("Lista habilitada", p.EnabledList);
        PrintList("Lista desabilitada", p.DisabledList);
        foreach (var e in p.Elements) Console.WriteLine("  " + DescribeElement(p, e));

        var scopes = p.Class == AdmxClass.Both ? new[] { PolicyScope.Machine, PolicyScope.User } : [PolicySetting.DefaultScope(p.Class)];
        foreach (var scope in scopes)
        {
            var (state, current) = engine.ReadCurrent(p, scope);
            Console.WriteLine($"  Atual {PolicySetting.ScopeText(scope)}   : {StateText(state)}");
            foreach (var (id, values) in current.Values) Console.WriteLine($"      {id} = {string.Join(" | ", values)}");
        }
        Console.WriteLine($"  Backup       : {(engine.Backups.Exists(p.QualifiedName) ? engine.Backups.PathFor(p.QualifiedName) : "nenhum")}");

        if (a.PolicyState is not null)
        {
            var setting = BuildSetting(a, p);
            if (setting is null) return 2;
            Console.WriteLine();
            Console.WriteLine($"Prévia de {setting}:");
            PrintWarnings(a, p, setting);
            PrintPlan(engine.Plan(p, setting));
        }
        return 0;
    }

    private static string DescribeElement(AdmxPolicy p, AdmxElement e)
    {
        var parts = new List<string> { $"Elemento {e.Type} '{e.Id}'" };
        if (e.Label is not null) parts.Add($"\"{e.Label}\"");
        var key = e.Key.Equals(p.Key, StringComparison.OrdinalIgnoreCase) ? "" : e.Key + "\\";
        if (e.ValueName is not null) parts.Add($"→ {key}{e.ValueName}");
        else if (e.Type == "list") parts.Add($"→ chave {e.Key}" + (e.ValuePrefix is { } vp ? $" (prefixo \"{vp}\")" : "") + (e.ExplicitValue ? " (nome=valor)" : "") + (e.Additive ? " (aditiva)" : ""));
        if (e.Type is "decimal" or "longDecimal") parts.Add($"[{e.MinValue ?? 0}..{e.MaxValue?.ToString() ?? "∞"}]" + (e.StoreAsText ? " (texto)" : ""));
        if (e.Type is "text" or "multiText") parts.Add($"máx. {e.MaxLength ?? PolicyWriter.DefaultTextMaxLength} caracteres" + (e.MaxStrings is > 0 ? $", {e.MaxStrings} linhas" : "") + (e.Expandable ? " (expansível)" : ""));
        if (e.Required) parts.Add("obrigatório");
        if (e.DefaultValue is not null) parts.Add($"padrão {e.DefaultValue}");
        var text = string.Join(" ", parts);
        if (e.Type == "enum")
            text += Environment.NewLine + string.Join(Environment.NewLine, (e.Items ?? []).Select((it, i) => $"      {PolicyWriter.EnumToken(e, i),-12} {it.DisplayName}"));
        if (e.Type == "boolean")
            text += $"  (marcado = {e.TrueValue?.ToString() ?? "1"}, desmarcado = {e.FalseValue?.ToString() ?? "0"})";
        return text;
    }

    private static int PolicySet(Args a, AdmxPolicy p)
    {
        var setting = BuildSetting(a, p);
        if (setting is null) return 2;
        var engine = CreatePolicyEngine(a);
        var plan = engine.Plan(p, setting);
        var risk = PolicyAdvisor.Risk(p);

        Console.WriteLine($"{setting}  (risco {RiskText(risk)})");
        PrintWarnings(a, p, setting);
        PrintPlan(plan);
        if (!plan.IsValid) return 1;
        if (a.DryRun)
        {
            Console.WriteLine("--dry-run: nada foi gravado.");
            return 0;
        }
        if (risk > a.MaxRisk)
        {
            Console.Error.WriteLine($"x Risco {RiskText(risk)} acima do permitido ({RiskText(a.MaxRisk)}). Use --max-risk {risk.ToString().ToLowerInvariant()} para confirmar.");
            return 1;
        }

        using var log = new OperationLog();
        log.Write($"policy set {setting} ({engine.System})");
        var result = engine.Apply(p, setting, log);
        log.Write($"  {(result.Success ? "OK" : "FALHA")}: {result.Message}");
        Console.WriteLine($"{(result.Success ? "ok" : "x ")} {result.Message}");
        foreach (var e in result.Errors) Console.WriteLine($"    {e}");
        Console.WriteLine($"Backup: {engine.Backups.PathFor(p.QualifiedName)}");
        Console.WriteLine($"Log: {log.FilePath}");
        return result.Success ? 0 : 1;
    }

    private static int PolicyRevert(Args a, string qualifiedName, AdmxPolicy? policy)
    {
        var engine = CreatePolicyEngine(a);
        using var log = new OperationLog();
        log.Write($"policy revert {qualifiedName} ({engine.System})");
        var result = engine.Revert(qualifiedName, policy, log);
        log.Write($"  {(result.Success ? "OK" : "FALHA")}: {result.Message}");
        Console.WriteLine($"{(result.Success ? "ok" : "x ")} {qualifiedName}: {result.Message}");
        foreach (var op in result.Operations) Console.WriteLine($"    {op}");
        foreach (var e in result.Errors) Console.WriteLine($"    erro: {e}");
        return result.Success ? 0 : 1;
    }

    private static int PolicyBackups(Args a)
    {
        var engine = CreatePolicyEngine(a);
        var list = engine.Backups.List();
        foreach (var name in list.Order(StringComparer.OrdinalIgnoreCase))
        {
            var b = engine.Backups.Load(name)!;
            Console.WriteLine($"{name,-70} {b.Values.Count} valor(es), desde {b.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}");
        }
        Console.WriteLine($"{list.Count} política(s) com backup em {engine.Backups.Directory}.");
        return 0;
    }
}
