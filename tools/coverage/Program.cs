using System.Text.Json;
using Debloater.Core.Catalog;
using Debloater.Coverage;

// Debloater.Coverage: compares the catalog with other "debloat" tools.
//
//   dotnet run --project tools/coverage -- report [--record [--label X]] [--catalog DIR] [--out DIR]
//   dotnet run --project tools/coverage -- latest [--write]
//
// report  downloads (cached in tools/coverage/.cache) the files of each source pinned in sources.json,
//         extracts the targets (registry, services, tasks, appx, features, capabilities), matches them against
//         the catalog and writes README.md to .claude/docs/coverage (git-ignored; --out DIR overrides it).
//         --record appends the day's row to tools/coverage/history.json.
// latest  shows the latest version (release) of each source; --write updates tag/sha in sources.json.

var root = FindRepoRoot();
var toolDir = Path.Combine(root, "tools", "coverage");
var sourcesPath = Path.Combine(toolDir, "sources.json");
var cacheDir = Path.Combine(toolDir, ".cache");
var historyPath = Path.Combine(toolDir, "history.json");
var outDir = ArgValue(args, "--out") is { } o ? Path.GetFullPath(o) : Path.Combine(root, ".claude", "docs", "coverage");
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
            Console.WriteLine($"{s.Id,-14} {s.Tag ?? "-",-16} {s.Sha[..10]}  ->  {tag ?? "(no release)",-16} {sha[..10]} {(changed ? "CHANGED" : "")}");
            if (write && changed) { s.Tag = tag; s.Sha = sha; }
        }
        if (write)
        {
            File.WriteAllText(sourcesPath, JsonSerializer.Serialize(sources, SourceFetcher.Json) + Environment.NewLine);
            Console.WriteLine($"Written: {sourcesPath}. Run 'report' again.");
        }
        return 0;
    }
    case "report":
    {
        // --catalog DIR: another catalog (e.g. from an old commit, to record the baseline in the history).
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
                if (!alternateCatalog) Console.WriteLine($"WARNING manual.yaml ({m.Name} / {f.Name}): unknown tweaks: {string.Join(", ", unknown)}");
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
            Console.WriteLine($"{files.Count} files, {targets.Count} targets");
        }

        var report = Report.Build(perTool, index, exclusions, catalog.Tweaks.Count);
        report.ToolSourceMismatches.AddRange(ToolSourceCheck.Check(catalog.Tweaks, perTool));
        foreach (var m in report.ToolSourceMismatches) Console.WriteLine($"WARNING unmatched tool source: {m}");
        var history = File.Exists(historyPath)
            ? JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(historyPath), SourceFetcher.Json)!
            : [];
        if (args.Contains("--record"))
        {
            var label = ArgValue(args, "--label") ?? "";
            history.RemoveAll(h => h.Date == report.Date && h.Label == label);
            history.Add(report.ToHistory(label));
            File.WriteAllText(historyPath, JsonSerializer.Serialize(history, SourceFetcher.Json) + Environment.NewLine);
        }
        if (alternateCatalog)
        {
            Console.WriteLine("Alternate catalog: the report was not rewritten (only the history, with --record).");
            return 0;
        }
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "README.md"), Report.Markdown(report, history, manual));
        // Full list of what is missing (for the maintainer), outside git.
        File.WriteAllText(Path.Combine(cacheDir, "missing.json"), JsonSerializer.Serialize(report.Missing, SourceFetcher.Json));
        Console.WriteLine();
        foreach (var t in report.Tools) Console.WriteLine($"{t.Spec.Id,-14} {t.Covered,5}/{t.Total,-5} {t.Percent,6:0.0}%  (decided {t.DecidedPercent:0.0}%)");
        Console.WriteLine($"Report: {Path.Combine(outDir, "README.md")}");
        return 0;
    }
    default:
        Console.Error.WriteLine("Usage: Debloater.Coverage report [--record [--label X]] [--catalog DIR] [--out DIR] | latest [--write]");
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
