using System.Text.Json;
using Debloater.Core.Admx;
using Debloater.Core.Inventory;

namespace Debloater.Cli;

internal static partial class Program
{
    private static int Admx(Args a)
    {
        var sub = a.Positional.Count > 0 ? a.Positional[0].ToLowerInvariant() : null;
        if (sub is not ("list" or "show" or "export"))
            return Fail(L.T("Admx_Usage"));

        var catalog = LoadAdmx(a);
        if (catalog is null) return 1;
        return sub switch
        {
            "list" => AdmxList(a, catalog),
            "show" => AdmxShow(a, catalog),
            _ => AdmxExport(a, catalog),
        };
    }

    private static AdmxCatalog? LoadAdmx(Args a)
    {
        // ADML language: follows the UI language (global --lang option).
        var catalog = AdmxParser.ParseDirectory(a.AdmxDir, AdmxParser.DefaultLanguages);
        if (catalog.FileCount == 0)
        {
            Console.Error.WriteLine(L.F("Admx_NoFiles", a.AdmxDir, string.Join(" ", catalog.Errors)));
            return null;
        }
        return catalog;
    }

    private static string StateText(AdmxPolicyState s) => s switch
    {
        AdmxPolicyState.NotConfigured => L.T("AdmxState_NotConfigured"),
        AdmxPolicyState.Enabled => L.T("AdmxState_Enabled"),
        AdmxPolicyState.Disabled => L.T("AdmxState_Disabled"),
        _ => L.T("AdmxState_Custom"),
    };

    private static int AdmxList(Args a, AdmxCatalog catalog)
    {
        IEnumerable<AdmxPolicy> policies = catalog.Policies;
        if (a.Search is { Length: > 0 } q)
        {
            policies = policies.Where(p =>
                Has(p.Name, q) || Has(p.DisplayName, q) || Has(p.Key, q) || Has(p.ValueName, q) || Has(p.File, q)
                || p.CategoryPath.Any(c => Has(c, q))
                || p.Elements.Any(e => Has(e.ValueName, q) || Has(e.Key, q)));
        }

        var rows = policies
            .Select(p => (Policy: p, State: AdmxPolicyStateReader.Combined(AdmxPolicyStateReader.Read(p))))
            .Where(r => !a.Configured || r.State != AdmxPolicyState.NotConfigured)
            .OrderBy(r => r.Policy.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Policy.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (a.Json)
        {
            foreach (var (p, _) in rows) p.LiveState = AdmxPolicyStateReader.Read(p);
            Console.WriteLine(JsonSerializer.Serialize(rows.Select(r => r.Policy), InventoryDiff.JsonOptions));
            return 0;
        }

        foreach (var (p, state) in rows)
            Console.WriteLine($"{p.QualifiedName,-60} {p.Class,-7} {StateText(state),-16} {p.DisplayName}");

        Console.WriteLine();
        Console.WriteLine(L.F("Admx_ListSummary", rows.Count, catalog.Policies.Count, catalog.FileCount, catalog.Directory, string.Join(", ", catalog.Languages)));
        if (catalog.Errors.Count > 0)
            Console.WriteLine(L.F("Admx_ReadWarnings", catalog.Errors.Count, catalog.Errors[0]));
        return 0;
    }

    private static bool Has(string? text, string q) => text is not null && text.Contains(q, StringComparison.OrdinalIgnoreCase);

    private static int AdmxShow(Args a, AdmxCatalog catalog)
    {
        if (a.Positional.Count < 2) return Fail(L.T("AdmxShow_Usage"));
        var query = a.Positional[1];
        var matches = catalog.Policies.Where(p =>
            p.QualifiedName.Equals(query, StringComparison.OrdinalIgnoreCase)
            || p.Name.Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
            return Fail(L.F("Policy_NotFound", query));

        foreach (var p in matches)
        {
            Console.WriteLine($"{p.QualifiedName}  ({p.Namespace})");
            PrintField("Field_DisplayName", p.DisplayName);
            PrintField("Field_Class", p.Class);
            PrintField("Field_Category", string.Join(" > ", p.CategoryPath));
            PrintField("Field_SupportedOn", p.SupportedOn);
            PrintField("Field_Key", p.Key);
            if (p.ValueName is not null) PrintField("Field_Value", p.ValueName);
            if (p.EnabledValue is not null) PrintField("Field_Enabled", p.EnabledValue);
            if (p.DisabledValue is not null) PrintField("Field_Disabled", p.DisabledValue);
            PrintList(L.T("Field_EnabledList"), p.EnabledList);
            PrintList(L.T("Field_DisabledList"), p.DisabledList);
            foreach (var e in p.Elements)
            {
                var range = e.MinValue is not null || e.MaxValue is not null ? $" [{e.MinValue}..{e.MaxValue}]" : "";
                var key = e.Key.Equals(p.Key, StringComparison.OrdinalIgnoreCase) ? "" : L.F("Elem_OtherKey", e.Key);
                Console.WriteLine($"  {L.F("Elem_Header", e.Type, e.Id)}: {e.ValueName}{range}{key}{(e.Label is null ? "" : $" — {e.Label}")}");
                foreach (var item in e.Items ?? []) Console.WriteLine($"      {item.Value,-12} {item.DisplayName}");
                if (e.TrueValue is not null || e.FalseValue is not null) Console.WriteLine($"      {L.F("Elem_Checked", e.TrueValue, e.FalseValue)}");
            }
            foreach (var s in AdmxPolicyStateReader.Read(p))
                Console.WriteLine($"  {L.F("Field_State", s.Scope),-13}: {StateText(s.State)}{(s.Values.Count > 0 ? "  (" + string.Join("; ", s.Values) + ")" : "")}");
            Console.WriteLine();
            Console.WriteLine(Indent(p.ExplainText.Trim(), "  "));
            Console.WriteLine();
        }
        return 0;
    }

    /// <summary>"  Label        : value", with the label padded so the colons line up.</summary>
    private static void PrintField(string labelKey, object? value) =>
        Console.WriteLine($"  {L.T(labelKey),-13}: {value}");

    private static void PrintList(string label, List<AdmxListItem>? list)
    {
        if (list is null) return;
        foreach (var i in list) Console.WriteLine($"  {label}: {i.Key}\\{i.ValueName} = {i.Value}");
    }

    private static string Indent(string text, string prefix) =>
        string.Join(Environment.NewLine, text.Split('\n').Select(l => prefix + l.TrimEnd('\r')));

    private static int AdmxExport(Args a, AdmxCatalog catalog)
    {
        if (a.Positional.Count < 2) return Fail(L.T("AdmxExport_Usage"));
        var file = Path.GetFullPath(a.Positional[1]);
        if (a.WithState)
            foreach (var p in catalog.Policies) p.LiveState = AdmxPolicyStateReader.Read(p);
        var dir = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(file, JsonSerializer.Serialize(catalog, InventoryDiff.JsonOptions));
        Console.WriteLine(L.F("AdmxExport_Done", catalog.Policies.Count, catalog.FileCount, file));
        return 0;
    }
}
