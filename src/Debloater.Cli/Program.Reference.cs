using Debloater.Core.Admx;
using Debloater.Core.Catalog;
using Debloater.Core.Model;
using Debloater.Core.Reference;

namespace Debloater.Cli;

internal static partial class Program
{
    private const string CspFileName = "msdocs-policy-csp.json";

    /// <summary>Raiz do repositório (pasta com Debloater.slnx), a partir da pasta atual ou do executável.</summary>
    private static string? FindRepoRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "Debloater.slnx"))) return dir.FullName;
        return null;
    }

    private sealed record References(AdmxCatalog? Admx, CspReference? Csp, string? CspPath, ReferenceMatcher Matcher);

    private static References LoadReferences(Args a)
    {
        AdmxCatalog? admx = null;
        if (Directory.Exists(a.AdmxDir))
        {
            admx = AdmxParser.ParseDirectory(a.AdmxDir, a.Lang is null ? AdmxParser.DefaultLanguages : [a.Lang]);
            Console.WriteLine($"ADMX: {admx.Policies.Count} políticas de {admx.FileCount} arquivo(s) em {a.AdmxDir}.");
        }
        else
        {
            Console.WriteLine($"ADMX: pasta {a.AdmxDir} não encontrada; verificação ADMX desativada.");
        }

        string? cspPath = a.CspFile;
        if (cspPath is null)
        {
            var root = FindRepoRoot();
            cspPath = new[]
            {
                root is null ? null : Path.Combine(root, "reference", CspFileName),
                Path.Combine(AppContext.BaseDirectory, "reference", CspFileName),
            }.FirstOrDefault(p => p is not null && File.Exists(p));
        }
        CspReference? csp = null;
        if (cspPath is not null && File.Exists(cspPath))
        {
            csp = CspMarkdownParser.Load(cspPath);
            Console.WriteLine($"Policy CSP: {csp.Policies.Count} políticas de {csp.Pages.Count} páginas ({cspPath}).");
        }
        else
        {
            Console.WriteLine("Policy CSP: referência não encontrada (rode tools/Debloater.RefGen); verificação CSP desativada.");
        }
        return new References(admx, csp, cspPath, new ReferenceMatcher(admx?.Policies, csp?.Policies));
    }

    private static int ValidateReference(Args a)
    {
        int baseResult = Validate(a);
        var catalog = CatalogLoader.LoadDirectory(a.CatalogDir);
        if (catalog.Tweaks.Count == 0) return baseResult == 0 ? 1 : baseResult;

        Console.WriteLine();
        var refs = LoadReferences(a);
        var findings = ReferenceValidator.Validate(catalog.Tweaks, refs.Matcher, refs.Admx?.Policies, refs.Csp?.Policies);

        Console.WriteLine();
        foreach (var group in findings.OrderBy(f => f.File, StringComparer.Ordinal).ThenBy(f => f.Severity).GroupBy(f => f.File))
        {
            Console.WriteLine($"{group.Key}:");
            foreach (var f in group.Where(f => a.Verbose || f.Severity == FindingSeverity.Warning))
                Console.WriteLine($"  {(f.Severity == FindingSeverity.Warning ? "AVISO" : "info ")} [{f.TweakId}] {f.Code}: {f.Message}");
            int infos = group.Count(f => f.Severity == FindingSeverity.Info);
            if (!a.Verbose && infos > 0) Console.WriteLine($"  ({infos} informativo(s); use --verbose para ver)");
        }

        // Cobertura: confiança sugerida pelas referências x declarada no YAML.
        var suggested = catalog.Tweaks
            .Select(t => ReferenceMatcher.SuggestConfidence(refs.Matcher.Match(t), findings))
            .GroupBy(c => c).OrderBy(g => g.Key).Select(g => $"{ReferenceValidator.Lower(g.Key)} {g.Count()}");
        var declared = catalog.Tweaks
            .GroupBy(t => t.Confidence is { } c ? ReferenceValidator.Lower(c) : "(não declarado)")
            .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}");
        Console.WriteLine();
        Console.WriteLine($"Confiança sugerida: {string.Join(" | ", suggested)}");
        Console.WriteLine($"Confiança declarada: {string.Join(" | ", declared)}");
        var byCode = findings.GroupBy(f => (f.Severity, f.Code)).OrderBy(g => g.Key.Severity).ThenBy(g => g.Key.Code, StringComparer.Ordinal)
            .Select(g => $"{g.Key.Code} {g.Count()}");
        Console.WriteLine($"{findings.Count(f => f.Severity == FindingSeverity.Warning)} aviso(s), {findings.Count(f => f.Severity == FindingSeverity.Info)} informativo(s): {string.Join(", ", byCode)}");

        if (a.WarningsOut is not null)
        {
            var lines = findings.Select(f => f.BaselineKey).Distinct().OrderBy(l => l, StringComparer.Ordinal);
            var dir = Path.GetDirectoryName(a.WarningsOut);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(a.WarningsOut, string.Join("\n", lines) + "\n");
            Console.WriteLine($"Achados gravados em {a.WarningsOut}.");
        }
        // Avisos de referência não são erros: o resultado é o da validação do catálogo.
        return baseResult;
    }

    private static int Reference(Args a)
    {
        var sub = a.Positional.Count > 0 ? a.Positional[0].ToLowerInvariant() : null;
        if (sub != "annotate")
            return Fail("Uso: reference annotate [--write] [--force] [--catalog DIR] [--admx-dir DIR] [--csp FILE]");

        // Escreve nos YAML do repositório, não na cópia ao lado do executável.
        var catalogDir = a.CatalogDir;
        if (catalogDir == Path.Combine(AppContext.BaseDirectory, "catalog"))
        {
            var root = FindRepoRoot();
            if (root is null) return Fail("Raiz do repositório não encontrada; informe --catalog <pasta catalog do repositório>.");
            catalogDir = Path.Combine(root, "catalog");
        }
        var catalog = CatalogLoader.LoadDirectory(catalogDir);
        if (!catalog.IsValid) return Fail($"Catálogo inválido; rode 'validate'. Primeiro erro: {catalog.Errors[0]}");

        var refs = LoadReferences(a);
        if (refs.Admx is null || refs.Csp is null)
            return Fail("annotate exige as duas referências (ADMX e Policy CSP).");
        var findings = ReferenceValidator.Validate(catalog.Tweaks, refs.Matcher, refs.Admx.Policies, refs.Csp.Policies);

        var plan = new Dictionary<string, Dictionary<string, CatalogAnnotator.Annotation>>(StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<Confidence, int>();
        foreach (var tweak in catalog.Tweaks)
        {
            var match = refs.Matcher.Match(tweak);
            var confidence = ReferenceMatcher.SuggestConfidence(match, findings);
            var sources = ReferenceMatcher.SuggestSources(match);
            // Mantém fontes declaradas à mão que o matcher não encontra (url, community...).
            foreach (var s in tweak.Sources)
                if (!sources.Any(x => x.Kind == s.Kind && x.Ref.Equals(s.Ref, StringComparison.OrdinalIgnoreCase))) sources.Add(s);
            counts[confidence] = counts.GetValueOrDefault(confidence) + 1;
            if (!plan.TryGetValue(tweak.SourceFile!, out var perFile)) plan[tweak.SourceFile!] = perFile = new(StringComparer.Ordinal);
            perFile[tweak.Id] = new CatalogAnnotator.Annotation(confidence, sources);
            Console.WriteLine($"  {tweak.Id,-48} {ReferenceValidator.Lower(confidence),-12} {string.Join(", ", sources.Select(s => s.Ref))}");
        }

        int total = 0;
        foreach (var (file, annotations) in plan.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var path = Path.Combine(catalogDir, file);
            var text = File.ReadAllText(path);
            var (newText, changed) = CatalogAnnotator.Annotate(text, annotations, a.Force);
            total += changed;
            if (changed > 0 && a.Write) File.WriteAllText(path, newText, new System.Text.UTF8Encoding(false));
            if (changed > 0) Console.WriteLine($"{file}: {changed} tweak(s) {(a.Write ? "atualizado(s)" : "a atualizar")}");
        }

        Console.WriteLine();
        Console.WriteLine($"Sugestão: {string.Join(" | ", counts.OrderBy(c => c.Key).Select(c => $"{ReferenceValidator.Lower(c.Key)} {c.Value}"))}");
        Console.WriteLine(a.Write
            ? $"{total} tweak(s) anotados em {catalogDir}."
            : $"{total} tweak(s) seriam anotados. Rode com --write para gravar{(a.Force ? "" : " (tweaks que já têm 'confidence' são mantidos; --force sobrescreve)")}.");

        if (a.Write)
        {
            var check = CatalogLoader.LoadDirectory(catalogDir);
            if (!check.IsValid) return Fail($"ATENÇÃO: o catálogo ficou inválido após anotar: {check.Errors[0]}");
        }
        return 0;
    }
}
