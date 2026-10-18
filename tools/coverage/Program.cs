using System.Text.Json;
using Debloater.Core.Catalog;
using Debloater.Coverage;

// Debloater.Coverage: compara o catálogo com outras ferramentas de "debloat" (ver docs/coverage/README.md).
//
//   dotnet run --project tools/coverage -- report [--record [--label X]] [--catalog DIR]
//   dotnet run --project tools/coverage -- latest [--write]
//
// report  baixa (com cache em tools/coverage/.cache) os arquivos de cada fonte fixada em sources.json,
//         extrai os alvos (registro, serviços, tarefas, appx, recursos, capacidades), casa com o catálogo
//         e grava docs/coverage/README.md. --record acrescenta a linha do dia em docs/coverage/history.json.
// latest  mostra a última versão (release) de cada fonte; --write atualiza tag/sha em sources.json.

var root = FindRepoRoot();
var toolDir = Path.Combine(root, "tools", "coverage");
var sourcesPath = Path.Combine(toolDir, "sources.json");
var cacheDir = Path.Combine(toolDir, ".cache");
var docsDir = Path.Combine(root, "docs", "coverage");
var command = args.FirstOrDefault() ?? "report";
var sources = JsonSerializer.Deserialize<SourcesFile>(File.ReadAllText(sourcesPath), SourceFetcher.Json)!;
using var fetcher = new SourceFetcher(cacheDir);
using var cts = new CancellationTokenSource();

switch (command)
{
    case "latest":
    {
        bool write = args.Contains("--write");
        foreach (var s in sources.Sources)
        {
            var (tag, sha) = await fetcher.LatestAsync(s, cts.Token);
            var changed = sha != s.Sha;
            Console.WriteLine($"{s.Id,-14} {s.Tag ?? "-",-16} {s.Sha[..10]}  ->  {tag ?? "(sem release)",-16} {sha[..10]} {(changed ? "MUDOU" : "")}");
            if (write && changed) { s.Tag = tag; s.Sha = sha; }
        }
        if (write)
        {
            File.WriteAllText(sourcesPath, JsonSerializer.Serialize(sources, SourceFetcher.Json) + Environment.NewLine);
            Console.WriteLine($"Gravado: {sourcesPath}. Rode 'report' de novo.");
        }
        return 0;
    }
    case "report":
    {
        // --catalog DIR: outro catálogo (ex.: o de um commit antigo, para registrar a linha de base no histórico).
        var catalogDir = ArgValue(args, "--catalog") ?? Path.Combine(root, "catalog");
        bool alternateCatalog = ArgValue(args, "--catalog") is not null;
        var catalog = CatalogLoader.LoadDirectory(catalogDir);
        if (!catalog.IsValid)
        {
            foreach (var e in catalog.Errors) Console.Error.WriteLine(e);
            return 1;
        }
        var index = new CatalogIndex(catalog.Tweaks);
        var exclusions = Exclusions.Load(Path.Combine(toolDir, "exclusions.yaml"));
        var manual = ManualComparison.Load(Path.Combine(toolDir, "manual.yaml"));
        var known = catalog.Tweaks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var m in manual.Tools)
            foreach (var f in m.Features)
            {
                var unknown = f.Tweaks.Where(id => !known.Contains(id)).ToList();
                if (unknown.Count == 0) continue;
                if (!alternateCatalog) Console.WriteLine($"AVISO manual.yaml ({m.Name} / {f.Name}): tweaks inexistentes: {string.Join(", ", unknown)}");
                f.Tweaks.RemoveAll(unknown.Contains);
            }

        var perTool = new List<ToolResult>();
        foreach (var s in sources.Sources)
        {
            Console.Write($"{s.Id}: ");
            var files = await fetcher.FetchAsync(s, cts.Token);
            var texts = files.Select(f => (f.Path, Extractors.ReadText(f.Bytes))).ToList();
            var targets = Parsers.Parse(s.Parser, texts);
            perTool.Add(new ToolResult(s, files.Count, targets));
            Console.WriteLine($"{files.Count} arquivos, {targets.Count} alvos");
        }

        var report = Report.Build(perTool, index, exclusions, catalog.Tweaks.Count);
        report.ToolSourceMismatches.AddRange(ToolSourceCheck.Check(catalog.Tweaks, perTool));
        foreach (var m in report.ToolSourceMismatches) Console.WriteLine($"AVISO fonte tool sem correspondência: {m}");
        var historyPath = Path.Combine(docsDir, "history.json");
        var history = File.Exists(historyPath)
            ? JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(historyPath), SourceFetcher.Json)!
            : [];
        if (args.Contains("--record"))
        {
            var label = ArgValue(args, "--label") ?? "";
            history.RemoveAll(h => h.Date == report.Date && h.Label == label);
            history.Add(report.ToHistory(label));
            Directory.CreateDirectory(docsDir);
            File.WriteAllText(historyPath, JsonSerializer.Serialize(history, SourceFetcher.Json) + Environment.NewLine);
        }
        if (alternateCatalog)
        {
            Console.WriteLine("Catálogo alternativo: o relatório não foi regravado (só o histórico, com --record).");
            return 0;
        }
        Directory.CreateDirectory(docsDir);
        File.WriteAllText(Path.Combine(docsDir, "README.md"), Report.Markdown(report, history, manual));
        // Lista completa do que falta (para o mantenedor), fora do git.
        File.WriteAllText(Path.Combine(cacheDir, "missing.json"), JsonSerializer.Serialize(report.Missing, SourceFetcher.Json));
        Console.WriteLine();
        foreach (var t in report.Tools) Console.WriteLine($"{t.Spec.Id,-14} {t.Covered,5}/{t.Total,-5} {t.Percent,6:0.0}%  (decidido {t.DecidedPercent:0.0}%)");
        Console.WriteLine($"Relatório: {Path.Combine(docsDir, "README.md")}");
        return 0;
    }
    default:
        Console.Error.WriteLine("Uso: Debloater.Coverage report [--record [--label X]] [--catalog DIR] | latest [--write]");
        return 2;
}

static string? ArgValue(string[] args, string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Debloater.slnx"))) dir = dir.Parent;
    return dir?.FullName ?? Directory.GetCurrentDirectory();
}
