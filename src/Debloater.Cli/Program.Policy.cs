using Debloater.Core.Admx;
using Debloater.Core.Engine;
using Debloater.Core.Model;
using Debloater.Core.Policies;
using Debloater.Core.Reference;

namespace Debloater.Cli;

internal static partial class Program
{
    private static string PolicyUsage => L.T("PolicyUsage");

    private static int Policy(Args a)
    {
        var sub = a.Positional.Count > 0 ? a.Positional[0].ToLowerInvariant() : null;
        if (sub == "backups") return PolicyBackups(a);
        if (sub is not ("show" or "set" or "revert") || a.Positional.Count < 2) return Fail(PolicyUsage);

        if (sub == "revert" && a.Positional[1].Contains(':'))
        {
            // Reverting does not need the ADMX definition (the backup has everything); it is only used to read the state afterwards.
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
        if (matches.Count == 0) Console.Error.WriteLine(L.F("Policy_NotFound", query));
        else Console.Error.WriteLine(L.F("Policy_Ambiguous", query, string.Join(", ", matches.Select(m => m.QualifiedName))));
        return null;
    }

    private static PolicyEngine CreatePolicyEngine(Args a) =>
        new(new PolicyEngineOptions { BackupDirectory = a.BackupDir ?? BackupStore.DefaultDirectory });

    private static PolicySetting? BuildSetting(Args a, AdmxPolicy policy)
    {
        if (a.PolicyState is null)
        {
            Console.Error.WriteLine(L.T("Policy_NeedState"));
            return null;
        }
        if (!PolicySetting.TryParseState(a.PolicyState, out var state))
        {
            Console.Error.WriteLine(L.F("Policy_InvalidState", a.PolicyState));
            return null;
        }
        var setting = new PolicySetting { Policy = policy.QualifiedName, State = state };
        if (a.Scope is { } scopeText)
        {
            if (scopeText.ToLowerInvariant() is "machine" or "computer" or "computador" or "hklm") setting.Scope = PolicyScope.Machine;
            else if (scopeText.ToLowerInvariant() is "user" or "usuario" or "usuário" or "hkcu") setting.Scope = PolicyScope.User;
            else
            {
                Console.Error.WriteLine(L.F("Policy_InvalidScope", scopeText));
                return null;
            }
        }
        foreach (var v in a.Values)
        {
            int eq = v.IndexOf('=');
            if (eq <= 0)
            {
                Console.Error.WriteLine(L.F("Policy_InvalidValue", v, string.Join(", ", policy.Elements.Select(e => e.Id))));
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
        if (lines.Count == 0) Console.WriteLine("  " + L.T("Policy_NothingToChange"));
        foreach (var l in lines) Console.WriteLine($"  {l}");
    }

    private static int PolicyShow(Args a, AdmxPolicy p)
    {
        var engine = CreatePolicyEngine(a);
        Console.WriteLine($"{p.QualifiedName}  ({p.Namespace})");
        PrintField("Field_DisplayName", p.DisplayName);
        PrintField("Field_Class", p.Class);
        PrintField("Field_Category", string.Join(" > ", p.CategoryPath));
        PrintField("Field_SupportedOn", p.SupportedOn);
        PrintField("Field_Risk", RiskText(PolicyAdvisor.Risk(p)));
        PrintField("Field_Key", p.Key + (p.ValueName is null ? "" : $"\\{p.ValueName}"));
        if (p.EnabledValue is not null || p.ValueName is not null) PrintField("Field_Enabled", p.EnabledValue?.ToString() ?? L.T("Policy_EnabledDefault"));
        if (p.DisabledValue is not null || p.ValueName is not null) PrintField("Field_Disabled", p.DisabledValue?.ToString() ?? L.T("Policy_DisabledDeletes"));
        PrintList(L.T("Field_EnabledList"), p.EnabledList);
        PrintList(L.T("Field_DisabledList"), p.DisabledList);
        foreach (var e in p.Elements) Console.WriteLine("  " + DescribeElement(p, e));

        var scopes = p.Class == AdmxClass.Both ? new[] { PolicyScope.Machine, PolicyScope.User } : [PolicySetting.DefaultScope(p.Class)];
        foreach (var scope in scopes)
        {
            var (state, current) = engine.ReadCurrent(p, scope);
            Console.WriteLine($"  {L.F("Field_Current", PolicySetting.ScopeText(scope)),-13}: {StateText(state)}");
            foreach (var (id, values) in current.Values) Console.WriteLine($"      {id} = {string.Join(" | ", values)}");
        }
        PrintField("Field_Backup", engine.Backups.Exists(p.QualifiedName) ? engine.Backups.PathFor(p.QualifiedName) : L.T("Policy_NoBackup"));

        if (a.PolicyState is not null)
        {
            var setting = BuildSetting(a, p);
            if (setting is null) return 2;
            Console.WriteLine();
            Console.WriteLine(L.F("Policy_Preview", setting));
            PrintWarnings(a, p, setting);
            PrintPlan(engine.Plan(p, setting));
        }
        return 0;
    }

    private static string DescribeElement(AdmxPolicy p, AdmxElement e)
    {
        var parts = new List<string> { L.F("Elem_Header", e.Type, e.Id) };
        if (e.Label is not null) parts.Add($"\"{e.Label}\"");
        var key = e.Key.Equals(p.Key, StringComparison.OrdinalIgnoreCase) ? "" : e.Key + "\\";
        if (e.ValueName is not null) parts.Add($"→ {key}{e.ValueName}");
        else if (e.Type == "list") parts.Add(L.F("Elem_ListKey", e.Key) + (e.ValuePrefix is { } vp ? L.F("Elem_Prefix", vp) : "") + (e.ExplicitValue ? L.T("Elem_NameValue") : "") + (e.Additive ? L.T("Elem_Additive") : ""));
        if (e.Type is "decimal" or "longDecimal") parts.Add($"[{e.MinValue ?? 0}..{e.MaxValue?.ToString() ?? "∞"}]" + (e.StoreAsText ? L.T("Elem_AsText") : ""));
        if (e.Type is "text" or "multiText") parts.Add(L.F("Elem_MaxChars", e.MaxLength ?? PolicyWriter.DefaultTextMaxLength) + (e.MaxStrings is > 0 ? L.F("Elem_Lines", e.MaxStrings) : "") + (e.Expandable ? L.T("Elem_Expandable") : ""));
        if (e.Required) parts.Add(L.T("Elem_Required"));
        if (e.DefaultValue is not null) parts.Add(L.F("Elem_Default", e.DefaultValue));
        var text = string.Join(" ", parts);
        if (e.Type == "enum")
            text += Environment.NewLine + string.Join(Environment.NewLine, (e.Items ?? []).Select((it, i) => $"      {PolicyWriter.EnumToken(e, i),-12} {it.DisplayName}"));
        if (e.Type == "boolean")
            text += L.F("Elem_Boolean", e.TrueValue?.ToString() ?? "1", e.FalseValue?.ToString() ?? "0");
        return text;
    }

    private static int PolicySet(Args a, AdmxPolicy p)
    {
        var setting = BuildSetting(a, p);
        if (setting is null) return 2;
        var engine = CreatePolicyEngine(a);
        var plan = engine.Plan(p, setting);
        var risk = PolicyAdvisor.Risk(p);

        Console.WriteLine(L.F("PolicySet_Header", setting, RiskText(risk)));
        PrintWarnings(a, p, setting);
        PrintPlan(plan);
        if (!plan.IsValid) return 1;
        if (a.DryRun)
        {
            Console.WriteLine(L.T("Policy_DryRun"));
            return 0;
        }
        if (risk > a.MaxRisk)
        {
            Console.Error.WriteLine(L.F("Policy_RiskTooHigh", RiskText(risk), RiskText(a.MaxRisk), risk.ToString().ToLowerInvariant()));
            return 1;
        }

        using var log = new OperationLog();
        log.Write($"policy set {setting} ({engine.System})");
        var result = engine.Apply(p, setting, log);
        log.Write($"  {(result.Success ? "OK" : "FAILED")}: {result.Message}");
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
        log.Write($"  {(result.Success ? "OK" : "FAILED")}: {result.Message}");
        Console.WriteLine($"{(result.Success ? "ok" : "x ")} {qualifiedName}: {result.Message}");
        foreach (var op in result.Operations) Console.WriteLine($"    {op}");
        foreach (var e in result.Errors) Console.WriteLine($"    {L.T("Label_Error")}: {e}");
        return result.Success ? 0 : 1;
    }

    private static int PolicyBackups(Args a)
    {
        var engine = CreatePolicyEngine(a);
        var list = engine.Backups.List();
        foreach (var name in list.Order(StringComparer.OrdinalIgnoreCase))
        {
            var b = engine.Backups.Load(name)!;
            Console.WriteLine($"{name,-70} {L.F("PolicyBackups_Item", b.Values.Count, b.CreatedUtc.ToLocalTime())}");
        }
        Console.WriteLine(L.F("PolicyBackups_Total", list.Count, engine.Backups.Directory));
        return 0;
    }
}
