using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Debloater.Coverage;

public sealed record ToolResult(SourceSpec Spec, int FileCount, List<Target> Targets);

/// <summary>Popular items we decided NOT to include (placebo, dangerous, out of scope), with the reason.</summary>
public sealed class Exclusions
{
    public sealed class Entry
    {
        /// <summary>Regular expression over the readable target (e.g. "\\Tcpip\\Parameters\\Interfaces.*TcpAckFrequency").</summary>
        public string Match { get; set; } = "";
        public string Reason { get; set; } = "";
        [YamlIgnore] public Regex? Regex { get; set; }
    }

    public List<Entry> Items { get; set; } = [];

    public static Exclusions Load(string path)
    {
        if (!File.Exists(path)) return new Exclusions();
        var d = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
        var e = d.Deserialize<Exclusions>(File.ReadAllText(path)) ?? new Exclusions();
        foreach (var i in e.Items) i.Regex = new Regex(i.Match, RegexOptions.IgnoreCase);
        return e;
    }

    public Entry? Find(Target t) => Items.FirstOrDefault(i => i.Regex!.IsMatch($"{t.Kind}:{t.Display}"));
}

/// <summary>Manual comparison (tools without public code): only the feature list from the official documentation.</summary>
public sealed class ManualComparison
{
    public sealed class Feature
    {
        public string Name { get; set; } = "";
        /// <summary>Catalog tweaks that cover the feature (empty = not covered).</summary>
        public List<string> Tweaks { get; set; } = [];
        public string? Note { get; set; }
    }

    public sealed class Tool
    {
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public string License { get; set; } = "";
        public string Version { get; set; } = "";
        public string? Note { get; set; }
        public List<Feature> Features { get; set; } = [];
        public List<string> Backlog { get; set; } = [];
    }

    public List<Tool> Tools { get; set; } = [];

    public static ManualComparison Load(string path)
    {
        if (!File.Exists(path)) return new ManualComparison();
        var d = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
        return d.Deserialize<ManualComparison>(File.ReadAllText(path)) ?? new ManualComparison();
    }
}

public sealed class HistoryEntry
{
    public string Date { get; set; } = "";
    public string Label { get; set; } = "";
    public int CatalogTweaks { get; set; }
    public Dictionary<string, double> Percent { get; set; } = [];
}

public sealed class MissingItem
{
    public string Kind { get; set; } = "";
    public string Target { get; set; } = "";
    public string Category { get; set; } = "";
    public List<string> Tools { get; set; } = [];
    public string? ExcludedReason { get; set; }
}

public sealed class ToolStats
{
    public required SourceSpec Spec { get; init; }
    public int Files { get; init; }
    public int Total { get; init; }
    public int Covered { get; init; }
    public int Excluded { get; init; }
    public double Percent => Total == 0 ? 0 : 100.0 * Covered / Total;
    public double DecidedPercent => Total == 0 ? 0 : 100.0 * (Covered + Excluded) / Total;
    public Dictionary<string, (int Total, int Covered)> ByKind { get; init; } = [];
}

public sealed class CoverageReport
{
    public required string Date { get; init; }
    public required int CatalogTweaks { get; init; }
    public required List<ToolStats> Tools { get; init; }
    public required List<MissingItem> Missing { get; init; }
    public required List<MissingItem> Excluded { get; init; }

    /// <summary>Catalog sources { kind: tool } whose target the cited tool does not change (see <see cref="ToolSourceCheck"/>).</summary>
    public List<string> ToolSourceMismatches { get; } = [];

    public HistoryEntry ToHistory(string label) => new()
    {
        Date = Date,
        Label = label,
        CatalogTweaks = CatalogTweaks,
        Percent = Tools.ToDictionary(t => t.Spec.Id, t => Math.Round(t.Percent, 1)),
    };
}

public static class Report
{
    public static CoverageReport Build(List<ToolResult> results, CatalogIndex index, Exclusions exclusions, int catalogTweaks)
    {
        var tools = new List<ToolStats>();
        var missing = new Dictionary<(TargetKind, string), MissingItem>();
        var excluded = new Dictionary<(TargetKind, string), MissingItem>();
        foreach (var r in results)
        {
            int covered = 0, excl = 0;
            var byKind = new Dictionary<string, (int, int)>();
            foreach (var t in r.Targets)
            {
                var kind = KindLabel(t.Kind);
                var (kt, kc) = byKind.GetValueOrDefault(kind);
                bool isCovered = index.CoveredBy(t).Count > 0;
                byKind[kind] = (kt + 1, kc + (isCovered ? 1 : 0));
                if (isCovered) { covered++; continue; }
                var ex = exclusions.Find(t);
                var bag = ex is null ? missing : excluded;
                if (ex is not null) excl++;
                if (!bag.TryGetValue((t.Kind, t.Key), out var item))
                    bag[(t.Kind, t.Key)] = item = new MissingItem { Kind = kind, Target = t.Display, Category = Categorizer.Of(t), ExcludedReason = ex?.Reason };
                if (!item.Tools.Contains(r.Spec.Id)) item.Tools.Add(r.Spec.Id);
            }
            tools.Add(new ToolStats { Spec = r.Spec, Files = r.FileCount, Total = r.Targets.Count, Covered = covered, Excluded = excl, ByKind = byKind });
        }
        static List<MissingItem> Sort(IEnumerable<MissingItem> items) => items
            .OrderBy(m => Categorizer.Order.ToList().IndexOf(m.Category))
            .ThenByDescending(m => m.Tools.Count).ThenBy(m => m.Target, StringComparer.OrdinalIgnoreCase).ToList();
        return new CoverageReport
        {
            Date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            CatalogTweaks = catalogTweaks,
            Tools = tools,
            Missing = Sort(missing.Values),
            Excluded = Sort(excluded.Values),
        };
    }

    public static string KindLabel(TargetKind k) => k switch
    {
        TargetKind.Registry => "registry",
        TargetKind.RegistryKey => "key",
        TargetKind.Service => "service",
        TargetKind.Task => "task",
        TargetKind.Appx => "appx",
        TargetKind.Feature => "feature",
        TargetKind.Capability => "capability",
        _ => k.ToString(),
    };

    private static string Md(string s) => s.Replace("|", "\\|").Replace("<", "&lt;").Replace("*", "\\*").Replace("_", "\\_");

    public static string Markdown(CoverageReport r, List<HistoryEntry> history, ManualComparison manual)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        var ids = r.Tools.Select(t => t.Spec.Id).ToList();
        int parsed = r.Tools.Count;

        sb.AppendLine("# Coverage compared with other tools");
        sb.AppendLine();
        sb.AppendLine($"Generated by `tools/coverage` on {r.Date}, with the catalog of **{r.CatalogTweaks} tweaks**. Do not edit by hand:");
        sb.AppendLine("run `dotnet run --project tools/coverage -- report` (section [How to update](#how-to-update)).");
        sb.AppendLine();
        sb.AppendLine("The report compares **what each tool changes** (targets: registry value, key, service, scheduled task,");
        sb.AppendLine("appx package, optional feature, capability) with the targets of our catalog's actions. It contains only names,");
        sb.AppendLine("paths and counts: no text, description or script from the other tools is copied.");
        sb.AppendLine();
        sb.AppendLine("## Summary per tool");
        sb.AppendLine();
        sb.AppendLine("| Tool | License | Version (commit) | Method | Files read | Targets | Covered | % covered | % decided¹ |");
        sb.AppendLine("|---|---|---|---|--:|--:|--:|--:|--:|");
        foreach (var t in r.Tools)
        {
            var s = t.Spec;
            sb.AppendLine($"| [{Md(s.Name)}](https://github.com/{s.Repo}) (`{s.Id}`) | {s.License} | {s.Tag ?? "-"} ([`{s.Sha[..7]}`](https://github.com/{s.Repo}/tree/{s.Sha})) | code | {t.Files} | {t.Total} | {t.Covered} | {t.Percent.ToString("0.0", ci)} % | {t.DecidedPercent.ToString("0.0", ci)} % |");
        }
        foreach (var m in manual.Tools)
        {
            int n = m.Features.Count, c = m.Features.Count(f => f.Tweaks.Count > 0);
            var pct = n == 0 ? "-" : (100.0 * c / n).ToString("0.0", ci) + " %";
            sb.AppendLine($"| [{Md(m.Name)}]({m.Url}) | {m.License} | {Md(m.Version)} | manual, no code² | - | {n} features | {c} | {pct} | - |");
        }
        sb.AppendLine();
        sb.AppendLine("¹ covered + items we decided not to include (section [Excluded on purpose](#excluded-on-purpose)).");
        sb.AppendLine("² manual comparison, feature by feature, based on the official public documentation: less precise, and a feature counts as covered if at least part of it is in the catalog (see [Closed-source tools](#closed-source-tools-manual-comparison)).");
        sb.AppendLine();
        sb.AppendLine("By target type (covered/total):");
        sb.AppendLine();
        var kinds = new[] { "registry", "key", "service", "task", "appx", "feature", "capability" };
        sb.AppendLine("| Tool | " + string.Join(" | ", kinds) + " |");
        sb.AppendLine("|---|" + string.Concat(kinds.Select(_ => "--:|")));
        foreach (var t in r.Tools)
            sb.AppendLine($"| `{t.Spec.Id}` | " + string.Join(" | ", kinds.Select(k => t.ByKind.TryGetValue(k, out var v) ? $"{v.Covered}/{v.Total}" : "-")) + " |");
        sb.AppendLine();

        if (history.Count > 0)
        {
            sb.AppendLine("## History");
            sb.AppendLine();
            sb.AppendLine("| Date | Milestone | Catalog tweaks | " + string.Join(" | ", ids.Select(i => $"`{i}`")) + " |");
            sb.AppendLine("|---|---|--:|" + string.Concat(ids.Select(_ => "--:|")));
            foreach (var h in history)
                sb.AppendLine($"| {h.Date} | {Md(h.Label)} | {h.CatalogTweaks} | " + string.Join(" | ", ids.Select(i => h.Percent.TryGetValue(i, out var p) ? p.ToString("0.0", ci) + " %" : "-")) + " |");
            sb.AppendLine();
        }

        sb.AppendLine("## What is missing, by popularity");
        sb.AppendLine();
        sb.AppendLine($"Targets that some tool changes and the catalog does not, deduplicated across the {parsed} tools with code. The **N**");
        sb.AppendLine("column says in how many tools the target appears: it is the priority signal. Below are the items with **N ≥ 2**; those that");
        sb.AppendLine("only one tool changes appear only as a count per category (the full list is in `tools/coverage/.cache/missing.json`,");
        sb.AppendLine("outside git). Appearing here does **not** mean the item should be added: many tools change things that have no effect");
        sb.AppendLine("or are dangerous; triage happens in the next section and in the catalog.");
        sb.AppendLine();
        sb.AppendLine("| Category | N ≥ 3 | N = 2 | N = 1 |");
        sb.AppendLine("|---|--:|--:|--:|");
        foreach (var g in r.Missing.GroupBy(m => m.Category))
            sb.AppendLine($"| {g.Key} | {g.Count(m => m.Tools.Count >= 3)} | {g.Count(m => m.Tools.Count == 2)} | {g.Count(m => m.Tools.Count == 1)} |");
        sb.AppendLine($"| **Total** | {r.Missing.Count(m => m.Tools.Count >= 3)} | {r.Missing.Count(m => m.Tools.Count == 2)} | {r.Missing.Count(m => m.Tools.Count == 1)} |");
        sb.AppendLine();
        foreach (var g in r.Missing.Where(m => m.Tools.Count >= 2).GroupBy(m => m.Category))
        {
            sb.AppendLine($"### {g.Key}");
            sb.AppendLine();
            sb.AppendLine("| N | Type | Target | Tools |");
            sb.AppendLine("|--:|---|---|---|");
            foreach (var m in g)
                sb.AppendLine($"| {m.Tools.Count} | {m.Kind} | `{m.Target.Replace("`", "'")}` | {string.Join(", ", m.Tools)} |");
            sb.AppendLine();
        }

        sb.AppendLine("## Check of the catalog's `tool` sources");
        sb.AppendLine();
        sb.AppendLine("Each tweak that cites another tool in `sources` (`{ kind: tool, ref: \"<id>:<item>\" }`) must have at least one");
        sb.AppendLine("target that this tool also changes, according to the extraction above (tweaks with only `powershell` actions are not checked).");
        sb.AppendLine();
        if (r.ToolSourceMismatches.Count == 0) sb.AppendLine("All `tool` sources check out.");
        else foreach (var m in r.ToolSourceMismatches) sb.AppendLine($"- {Md(m)}");
        sb.AppendLine();

        sb.AppendLine("## Excluded on purpose");
        sb.AppendLine();
        sb.AppendLine("Items that other tools change and that **we decided not to include** (placebo, bad side effect, out of scope).");
        sb.AppendLine("The rule and the reason are in `tools/coverage/exclusions.yaml`.");
        sb.AppendLine();
        if (r.Excluded.Count == 0) sb.AppendLine("(none)");
        else
        {
            sb.AppendLine("| N | Type | Target | Reason |");
            sb.AppendLine("|--:|---|---|---|");
            foreach (var m in r.Excluded.OrderByDescending(m => m.Tools.Count).ThenBy(m => m.Target, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"| {m.Tools.Count} | {m.Kind} | `{m.Target.Replace("`", "'")}` | {Md(m.ExcludedReason ?? "")} |");
        }
        sb.AppendLine();

        if (manual.Tools.Count > 0)
        {
            sb.AppendLine("## Closed-source tools (manual comparison)");
            sb.AppendLine();
            sb.AppendLine("Closed programs: **manual comparison, no code**, based on the feature list in the official documentation or page");
            sb.AppendLine("(no program was downloaded or run). Precision is lower: one of their \"features\" may correspond to several");
            sb.AppendLine("of our tweaks, or to part of one. The data is in `tools/coverage/manual.yaml`.");
            sb.AppendLine();
            foreach (var m in manual.Tools)
            {
                sb.AppendLine($"### {m.Name}");
                sb.AppendLine();
                sb.AppendLine($"Source: <{m.Url}> ({Md(m.Version)}). License: {m.License}.");
                if (!string.IsNullOrWhiteSpace(m.Note)) { sb.AppendLine(); sb.AppendLine(m.Note.Trim()); }
                sb.AppendLine();
                if (m.Features.Count > 0)
                {
                    sb.AppendLine("| Feature | Covered by | Note |");
                    sb.AppendLine("|---|---|---|");
                    foreach (var f in m.Features)
                        sb.AppendLine($"| {Md(f.Name)} | {(f.Tweaks.Count == 0 ? "**no**" : string.Join(", ", f.Tweaks.Select(id => $"`{id}`")))} | {Md(f.Note ?? "")} |");
                    sb.AppendLine();
                }
                if (m.Backlog.Count > 0)
                {
                    sb.AppendLine("Backlog (offline ISO mode, future):");
                    sb.AppendLine();
                    foreach (var b in m.Backlog) sb.AppendLine($"- {b}");
                    sb.AppendLine();
                }
            }
        }

        sb.AppendLine("## Method and limitations");
        sb.AppendLine();
        sb.AppendLine("- Each source is downloaded from GitHub **pinned to a commit** (`tools/coverage/sources.json`), with a local cache in");
        sb.AppendLine("  `tools/coverage/.cache` (outside git). Only the configuration/code files that describe the changes are read.");
        sb.AppendLine("- Extraction by format: WinUtil JSON (`registry`, `service`, `feature`, `InvokeScript` scripts), privacy.sexy YAML");
        sb.AppendLine("  collection (only function calls with parameters: registry, service, task, appx, feature, capability, Edge");
        sb.AppendLine("  policies), Win11Debloat `.reg` files and `Apps.json`, Sophia Script PowerShell, C# (SophiApp, Winhance,");
        sb.AppendLine("  optimizerDuck: registry path literals followed by the value name, constants, properties) and AME playbooks");
        sb.AppendLine("  (AtlasOS, ReviOS: `!registryValue`, `!service`, `!scheduledTask`, `!appx` and embedded commands).");
        sb.AppendLine("- It is **heuristic**: scripts with variables, loops and paths built at run time slip through; \"keep\" lists");
        sb.AppendLine("  may be counted as if they were removal lists. The numbers are approximate and meant for prioritizing, not as a score.");
        sb.AppendLine("- Matching by target, not by value: if the tool writes `1` and we write `0` to the same value, it counts as covered (it is the same");
        sb.AppendLine("  setting, usually in both directions). The same policy in `HKCU\\Software\\Policies` and `HKLM\\SOFTWARE\\Policies` counts");
        sb.AppendLine("  as covered. Tasks match by name (many tools do not give the folder). `Services\\X\\Start` = service X.");
        sb.AppendLine("  Scripts of the catalog's `powershell` actions also go through the extractor.");
        sb.AppendLine();
        sb.AppendLine("## Credits and licenses");
        sb.AppendLine();
        sb.AppendLine("Thanks to the authors of the compared tools. From them we use only **what exists** (names of values, keys, services,");
        sb.AppendLine("tasks and packages), never texts, descriptions or scripts. Tools whose license does not allow copying without inheriting the");
        sb.AppendLine("license (AGPL-3.0, GPL-3.0, CC-BY-SA-4.0) or that have their own license are **reference only**. Catalog tweaks that came");
        sb.AppendLine("from this comparison cite the tool in `sources` (`{ kind: tool, ref: \"<id>:<item>\" }`), with our own description.");
        sb.AppendLine();
        foreach (var t in r.Tools)
            sb.AppendLine($"- **{Md(t.Spec.Name)}**: <https://github.com/{t.Spec.Repo}>, {t.Spec.License}{(t.Spec.Usage == "reference" ? ", reference only" : "")}.{(t.Spec.Note is null ? "" : " " + t.Spec.Note)}");
        foreach (var m in manual.Tools)
            sb.AppendLine($"- **{Md(m.Name)}**: <{m.Url}>, {m.License}, manual comparison.");
        sb.AppendLine();
        sb.AppendLine("## How to update");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine("dotnet run --project tools/coverage -- latest            # is there a new version of any tool?");
        sb.AppendLine("dotnet run --project tools/coverage -- latest --write    # pins the new tag/commit in sources.json");
        sb.AppendLine("dotnet run --project tools/coverage -- report            # downloads (cached), compares and rewrites this file");
        sb.AppendLine("dotnet run --project tools/coverage -- report --record --label \"description\"   # and records it in the history");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("This file is written to `.claude/docs/coverage/README.md` (outside git; `--out DIR` changes the folder). The history");
        sb.AppendLine("is kept in `tools/coverage/history.json`.");
        sb.AppendLine();
        sb.AppendLine("Without a token, the GitHub API allows 60 calls per hour (the report uses one per tool; the files come from");
        sb.AppendLine("`raw.githubusercontent.com`). With `GITHUB_TOKEN` or `GH_TOKEN` in the environment, the limit is higher.");
        return sb.ToString();
    }
}

/// <summary>Checks that the catalog's { kind: tool } sources match what each tool actually changes.</summary>
public static class ToolSourceCheck
{
    public static IEnumerable<string> Check(IEnumerable<Debloater.Core.Model.Tweak> tweaks, IReadOnlyList<ToolResult> tools)
    {
        var byId = tools.ToDictionary(t => t.Spec.Id, t => new CatalogIndex([]).With(t.Targets), StringComparer.OrdinalIgnoreCase);
        foreach (var tweak in tweaks)
        {
            // Script-only tweaks: extraction on both sides is too heuristic to check.
            if (tweak.Actions.All(a => a.Type == Debloater.Core.Model.ActionSpec.PowerShell)) continue;
            var targets = tweak.Actions.SelectMany(CatalogIndex.TargetsOf).ToList();
            foreach (var src in tweak.Sources.Where(s => s.Kind == Debloater.Core.Model.SourceRef.ToolKind))
            {
                var toolId = src.Ref.Split(':')[0];
                if (!byId.TryGetValue(toolId, out var toolIndex))
                {
                    yield return $"{tweak.Id}: tool '{toolId}' is not in sources.json";
                    continue;
                }
                if (!targets.Any(t => toolIndex.CoveredBy(t).Count > 0))
                    yield return $"{tweak.Id}: '{src.Ref}' does not change any target of this tweak";
            }
        }
    }
}
