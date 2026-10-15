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
            return Fail("Uso: admx list [--search TEXTO] [--configured] | admx show <[Arquivo.admx:]Nome> | admx export <arquivo.json> [--with-state]");

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
        var langs = a.Lang is null ? AdmxParser.DefaultLanguages : [a.Lang];
        var catalog = AdmxParser.ParseDirectory(a.AdmxDir, langs);
        if (catalog.FileCount == 0)
        {
            Console.Error.WriteLine($"Nenhum .admx em {a.AdmxDir}. {string.Join(" ", catalog.Errors)}");
            return null;
        }
        return catalog;
    }

    private static string StateText(AdmxPolicyState s) => s switch
    {
        AdmxPolicyState.NotConfigured => "Não configurada",
        AdmxPolicyState.Enabled => "Habilitada",
        AdmxPolicyState.Disabled => "Desabilitada",
        _ => "Valores próprios",
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
        Console.WriteLine($"{rows.Count} política(s) listada(s) de {catalog.Policies.Count} em {catalog.FileCount} arquivo(s) ({catalog.Directory}; idiomas: {string.Join(", ", catalog.Languages)}).");
        if (catalog.Errors.Count > 0)
            Console.WriteLine($"{catalog.Errors.Count} aviso(s) de leitura; primeiro: {catalog.Errors[0]}");
        return 0;
    }

    private static bool Has(string? text, string q) => text is not null && text.Contains(q, StringComparison.OrdinalIgnoreCase);

    private static int AdmxShow(Args a, AdmxCatalog catalog)
    {
        if (a.Positional.Count < 2) return Fail("Uso: admx show <[Arquivo.admx:]Nome>");
        var query = a.Positional[1];
        var matches = catalog.Policies.Where(p =>
            p.QualifiedName.Equals(query, StringComparison.OrdinalIgnoreCase)
            || p.Name.Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
            return Fail($"Política '{query}' não encontrada. Use 'admx list --search {query}'.");

        foreach (var p in matches)
        {
            Console.WriteLine($"{p.QualifiedName}  ({p.Namespace})");
            Console.WriteLine($"  Nome exibido : {p.DisplayName}");
            Console.WriteLine($"  Classe       : {p.Class}");
            Console.WriteLine($"  Categoria    : {string.Join(" > ", p.CategoryPath)}");
            Console.WriteLine($"  Com suporte  : {p.SupportedOn}");
            Console.WriteLine($"  Chave        : {p.Key}");
            if (p.ValueName is not null) Console.WriteLine($"  Valor        : {p.ValueName}");
            if (p.EnabledValue is not null) Console.WriteLine($"  Habilitada   : {p.EnabledValue}");
            if (p.DisabledValue is not null) Console.WriteLine($"  Desabilitada : {p.DisabledValue}");
            PrintList("Lista habilitada", p.EnabledList);
            PrintList("Lista desabilitada", p.DisabledList);
            foreach (var e in p.Elements)
            {
                var range = e.MinValue is not null || e.MaxValue is not null ? $" [{e.MinValue}..{e.MaxValue}]" : "";
                var key = e.Key.Equals(p.Key, StringComparison.OrdinalIgnoreCase) ? "" : $" (chave {e.Key})";
                Console.WriteLine($"  Elemento {e.Type} '{e.Id}': {e.ValueName}{range}{key}{(e.Label is null ? "" : $" — {e.Label}")}");
                foreach (var item in e.Items ?? []) Console.WriteLine($"      {item.Value,-12} {item.DisplayName}");
                if (e.TrueValue is not null || e.FalseValue is not null) Console.WriteLine($"      marcado={e.TrueValue} desmarcado={e.FalseValue}");
            }
            foreach (var s in AdmxPolicyStateReader.Read(p))
                Console.WriteLine($"  Estado {s.Scope}  : {StateText(s.State)}{(s.Values.Count > 0 ? "  (" + string.Join("; ", s.Values) + ")" : "")}");
            Console.WriteLine();
            Console.WriteLine(Indent(p.ExplainText.Trim(), "  "));
            Console.WriteLine();
        }
        return 0;
    }

    private static void PrintList(string label, List<AdmxListItem>? list)
    {
        if (list is null) return;
        foreach (var i in list) Console.WriteLine($"  {label}: {i.Key}\\{i.ValueName} = {i.Value}");
    }

    private static string Indent(string text, string prefix) =>
        string.Join(Environment.NewLine, text.Split('\n').Select(l => prefix + l.TrimEnd('\r')));

    private static int AdmxExport(Args a, AdmxCatalog catalog)
    {
        if (a.Positional.Count < 2) return Fail("Uso: admx export <arquivo.json> [--with-state]");
        var file = Path.GetFullPath(a.Positional[1]);
        if (a.WithState)
            foreach (var p in catalog.Policies) p.LiveState = AdmxPolicyStateReader.Read(p);
        var dir = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(file, JsonSerializer.Serialize(catalog, InventoryDiff.JsonOptions));
        Console.WriteLine($"{catalog.Policies.Count} política(s) de {catalog.FileCount} arquivo(s) exportada(s) para {file}.");
        return 0;
    }
}
