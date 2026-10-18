using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Debloater.Coverage;

public sealed record ToolResult(SourceSpec Spec, int FileCount, List<Target> Targets);

/// <summary>Itens populares que decidimos NÃO incluir (placebo, perigosos, fora do escopo), com o motivo.</summary>
public sealed class Exclusions
{
    public sealed class Entry
    {
        /// <summary>Expressão regular sobre o alvo legível (ex.: "\\Tcpip\\Parameters\\Interfaces.*TcpAckFrequency").</summary>
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

/// <summary>Comparação manual (ferramentas sem código público): só a lista de recursos da documentação oficial.</summary>
public sealed class ManualComparison
{
    public sealed class Feature
    {
        public string Name { get; set; } = "";
        /// <summary>Tweaks do catálogo que cobrem o recurso (vazio = não coberto).</summary>
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

    /// <summary>Fontes { kind: tool } do catálogo cujo alvo a ferramenta citada não altera (ver <see cref="ToolSourceCheck"/>).</summary>
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
        TargetKind.Registry => "registro",
        TargetKind.RegistryKey => "chave",
        TargetKind.Service => "serviço",
        TargetKind.Task => "tarefa",
        TargetKind.Appx => "appx",
        TargetKind.Feature => "recurso",
        TargetKind.Capability => "capacidade",
        _ => k.ToString(),
    };

    private static string Md(string s) => s.Replace("|", "\\|").Replace("<", "&lt;").Replace("*", "\\*").Replace("_", "\\_");

    public static string Markdown(CoverageReport r, List<HistoryEntry> history, ManualComparison manual)
    {
        var ci = CultureInfo.GetCultureInfo("pt-BR");
        var sb = new StringBuilder();
        var ids = r.Tools.Select(t => t.Spec.Id).ToList();
        int parsed = r.Tools.Count;

        sb.AppendLine("# Cobertura comparada com outras ferramentas");
        sb.AppendLine();
        sb.AppendLine($"Gerado por `tools/coverage` em {r.Date}, com o catálogo de **{r.CatalogTweaks} tweaks**. Não edite à mão:");
        sb.AppendLine("rode `dotnet run --project tools/coverage -- report` (seção [Como atualizar](#como-atualizar)).");
        sb.AppendLine();
        sb.AppendLine("O relatório compara **o que cada ferramenta altera** (alvos: valor de registro, chave, serviço, tarefa agendada,");
        sb.AppendLine("pacote appx, recurso opcional, capacidade) com os alvos das ações do nosso catálogo. Ele contém só nomes,");
        sb.AppendLine("caminhos e contagens: nenhum texto, descrição ou script das outras ferramentas é copiado.");
        sb.AppendLine();
        sb.AppendLine("## Resumo por ferramenta");
        sb.AppendLine();
        sb.AppendLine("| Ferramenta | Licença | Versão (commit) | Método | Arquivos lidos | Alvos | Cobertos | % coberto | % decidido¹ |");
        sb.AppendLine("|---|---|---|---|--:|--:|--:|--:|--:|");
        foreach (var t in r.Tools)
        {
            var s = t.Spec;
            sb.AppendLine($"| [{Md(s.Name)}](https://github.com/{s.Repo}) (`{s.Id}`) | {s.License} | {s.Tag ?? "-"} ([`{s.Sha[..7]}`](https://github.com/{s.Repo}/tree/{s.Sha})) | código | {t.Files} | {t.Total} | {t.Covered} | {t.Percent.ToString("0.0", ci)} % | {t.DecidedPercent.ToString("0.0", ci)} % |");
        }
        foreach (var m in manual.Tools)
        {
            int n = m.Features.Count, c = m.Features.Count(f => f.Tweaks.Count > 0);
            var pct = n == 0 ? "-" : (100.0 * c / n).ToString("0.0", ci) + " %";
            sb.AppendLine($"| [{Md(m.Name)}]({m.Url}) | {m.License} | {Md(m.Version)} | manual, sem código² | - | {n} recursos | {c} | {pct} | - |");
        }
        sb.AppendLine();
        sb.AppendLine("¹ cobertos + itens que decidimos não incluir (seção [Excluídos de propósito](#excluídos-de-propósito)).");
        sb.AppendLine("² comparação manual, recurso a recurso, a partir da documentação pública oficial: menos precisa, e um recurso conta como coberto se ao menos parte dele está no catálogo (ver [Ferramentas sem código](#ferramentas-sem-código-comparação-manual)).");
        sb.AppendLine();
        sb.AppendLine("Por tipo de alvo (cobertos/total):");
        sb.AppendLine();
        var kinds = new[] { "registro", "chave", "serviço", "tarefa", "appx", "recurso", "capacidade" };
        sb.AppendLine("| Ferramenta | " + string.Join(" | ", kinds) + " |");
        sb.AppendLine("|---|" + string.Concat(kinds.Select(_ => "--:|")));
        foreach (var t in r.Tools)
            sb.AppendLine($"| `{t.Spec.Id}` | " + string.Join(" | ", kinds.Select(k => t.ByKind.TryGetValue(k, out var v) ? $"{v.Covered}/{v.Total}" : "-")) + " |");
        sb.AppendLine();

        if (history.Count > 0)
        {
            sb.AppendLine("## Histórico");
            sb.AppendLine();
            sb.AppendLine("| Data | Marco | Tweaks no catálogo | " + string.Join(" | ", ids.Select(i => $"`{i}`")) + " |");
            sb.AppendLine("|---|---|--:|" + string.Concat(ids.Select(_ => "--:|")));
            foreach (var h in history)
                sb.AppendLine($"| {h.Date} | {Md(h.Label)} | {h.CatalogTweaks} | " + string.Join(" | ", ids.Select(i => h.Percent.TryGetValue(i, out var p) ? p.ToString("0.0", ci) + " %" : "-")) + " |");
            sb.AppendLine();
        }

        sb.AppendLine("## O que falta, por popularidade");
        sb.AppendLine();
        sb.AppendLine($"Alvos que alguma ferramenta altera e o catálogo não, deduplicados entre as {parsed} ferramentas com código. A coluna **N**");
        sb.AppendLine("diz em quantas ferramentas o alvo aparece: é o sinal de prioridade. Abaixo estão os itens com **N ≥ 2**; os que só uma");
        sb.AppendLine("ferramenta altera aparecem só como contagem por categoria (a lista completa fica em `tools/coverage/.cache/missing.json`,");
        sb.AppendLine("fora do git). Aparecer aqui **não** quer dizer que o item deveria entrar: muitas ferramentas mudam coisas sem efeito");
        sb.AppendLine("ou perigosas; a triagem fica na seção seguinte e no catálogo.");
        sb.AppendLine();
        sb.AppendLine("| Categoria | N ≥ 3 | N = 2 | N = 1 |");
        sb.AppendLine("|---|--:|--:|--:|");
        foreach (var g in r.Missing.GroupBy(m => m.Category))
            sb.AppendLine($"| {g.Key} | {g.Count(m => m.Tools.Count >= 3)} | {g.Count(m => m.Tools.Count == 2)} | {g.Count(m => m.Tools.Count == 1)} |");
        sb.AppendLine($"| **Total** | {r.Missing.Count(m => m.Tools.Count >= 3)} | {r.Missing.Count(m => m.Tools.Count == 2)} | {r.Missing.Count(m => m.Tools.Count == 1)} |");
        sb.AppendLine();
        foreach (var g in r.Missing.Where(m => m.Tools.Count >= 2).GroupBy(m => m.Category))
        {
            sb.AppendLine($"### {g.Key}");
            sb.AppendLine();
            sb.AppendLine("| N | Tipo | Alvo | Ferramentas |");
            sb.AppendLine("|--:|---|---|---|");
            foreach (var m in g)
                sb.AppendLine($"| {m.Tools.Count} | {m.Kind} | `{m.Target.Replace("`", "'")}` | {string.Join(", ", m.Tools)} |");
            sb.AppendLine();
        }

        sb.AppendLine("## Conferência das fontes `tool` do catálogo");
        sb.AppendLine();
        sb.AppendLine("Cada tweak que cita outra ferramenta em `sources` (`{ kind: tool, ref: \"<id>:<item>\" }`) precisa ter ao menos um");
        sb.AppendLine("alvo que essa ferramenta também altera, segundo a extração acima (tweaks só com ações `powershell` não são conferidos).");
        sb.AppendLine();
        if (r.ToolSourceMismatches.Count == 0) sb.AppendLine("Todas as fontes `tool` conferem.");
        else foreach (var m in r.ToolSourceMismatches) sb.AppendLine($"- {Md(m)}");
        sb.AppendLine();

        sb.AppendLine("## Excluídos de propósito");
        sb.AppendLine();
        sb.AppendLine("Itens que outras ferramentas alteram e que **decidimos não incluir** (placebo, efeito colateral ruim, fora do escopo).");
        sb.AppendLine("A regra e o motivo ficam em `tools/coverage/exclusions.yaml`.");
        sb.AppendLine();
        if (r.Excluded.Count == 0) sb.AppendLine("(nenhum)");
        else
        {
            sb.AppendLine("| N | Tipo | Alvo | Motivo |");
            sb.AppendLine("|--:|---|---|---|");
            foreach (var m in r.Excluded.OrderByDescending(m => m.Tools.Count).ThenBy(m => m.Target, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"| {m.Tools.Count} | {m.Kind} | `{m.Target.Replace("`", "'")}` | {Md(m.ExcludedReason ?? "")} |");
        }
        sb.AppendLine();

        if (manual.Tools.Count > 0)
        {
            sb.AppendLine("## Ferramentas sem código (comparação manual)");
            sb.AppendLine();
            sb.AppendLine("Programas fechados: **comparação manual, sem código**, feita a partir da lista de recursos da documentação ou página");
            sb.AppendLine("oficial (nenhum programa foi baixado ou executado). A precisão é menor: um \"recurso\" deles pode corresponder a vários");
            sb.AppendLine("tweaks nossos, ou a parte de um. Os dados ficam em `tools/coverage/manual.yaml`.");
            sb.AppendLine();
            foreach (var m in manual.Tools)
            {
                sb.AppendLine($"### {m.Name}");
                sb.AppendLine();
                sb.AppendLine($"Fonte: <{m.Url}> ({Md(m.Version)}). Licença: {m.License}.");
                if (!string.IsNullOrWhiteSpace(m.Note)) { sb.AppendLine(); sb.AppendLine(m.Note.Trim()); }
                sb.AppendLine();
                if (m.Features.Count > 0)
                {
                    sb.AppendLine("| Recurso | Coberto por | Observação |");
                    sb.AppendLine("|---|---|---|");
                    foreach (var f in m.Features)
                        sb.AppendLine($"| {Md(f.Name)} | {(f.Tweaks.Count == 0 ? "**não**" : string.Join(", ", f.Tweaks.Select(id => $"`{id}`")))} | {Md(f.Note ?? "")} |");
                    sb.AppendLine();
                }
                if (m.Backlog.Count > 0)
                {
                    sb.AppendLine("Backlog (modo ISO offline, futuro):");
                    sb.AppendLine();
                    foreach (var b in m.Backlog) sb.AppendLine($"- {b}");
                    sb.AppendLine();
                }
            }
        }

        sb.AppendLine("## Método e limitações");
        sb.AppendLine();
        sb.AppendLine("- Cada fonte é baixada do GitHub **fixada num commit** (`tools/coverage/sources.json`), com cache local em");
        sb.AppendLine("  `tools/coverage/.cache` (fora do git). Só os arquivos de configuração/código que descrevem as alterações são lidos.");
        sb.AppendLine("- Extração por formato: JSON do WinUtil (`registry`, `service`, `feature`, scripts `InvokeScript`), coleção YAML do");
        sb.AppendLine("  privacy.sexy (só as chamadas de função com parâmetros: registro, serviço, tarefa, appx, recurso, capacidade, políticas do");
        sb.AppendLine("  Edge), arquivos `.reg` e `Apps.json` do Win11Debloat, PowerShell do Sophia Script, C# (SophiApp, Winhance,");
        sb.AppendLine("  optimizerDuck: literais de caminho de registro seguidos do nome do valor, constantes, propriedades) e playbooks AME");
        sb.AppendLine("  (AtlasOS, ReviOS: `!registryValue`, `!service`, `!scheduledTask`, `!appx` e comandos embutidos).");
        sb.AppendLine("- É **heurístico**: scripts com variáveis, laços e caminhos montados em tempo de execução escapam; listas de \"manter\"");
        sb.AppendLine("  podem entrar como se fossem de remover. Os números são aproximados e servem para priorizar, não como nota.");
        sb.AppendLine("- Casamento por alvo, não por valor: se a ferramenta grava `1` e nós `0` no mesmo valor, conta como coberto (é o mesmo");
        sb.AppendLine("  ajuste, em geral nos dois sentidos). A mesma política em `HKCU\\Software\\Policies` e `HKLM\\SOFTWARE\\Policies` conta");
        sb.AppendLine("  como coberta. Tarefas casam pelo nome (muitas ferramentas não dão a pasta). `Services\\X\\Start` = serviço X.");
        sb.AppendLine("  Scripts das ações `powershell` do catálogo também passam pelo extrator.");
        sb.AppendLine();
        sb.AppendLine("## Créditos e licenças");
        sb.AppendLine();
        sb.AppendLine("Obrigado aos autores das ferramentas comparadas. Delas usamos só **o que existe** (nomes de valores, chaves, serviços,");
        sb.AppendLine("tarefas e pacotes), nunca textos, descrições ou scripts. Ferramentas com licença que não permite cópia sem herdar a");
        sb.AppendLine("licença (AGPL-3.0, GPL-3.0, CC-BY-SA-4.0) ou com licença própria são **só referência**. Tweaks do catálogo que vieram");
        sb.AppendLine("desta comparação citam a ferramenta em `sources` (`{ kind: tool, ref: \"<id>:<item>\" }`), com descrição nossa.");
        sb.AppendLine();
        foreach (var t in r.Tools)
            sb.AppendLine($"- **{Md(t.Spec.Name)}**: <https://github.com/{t.Spec.Repo}>, {t.Spec.License}{(t.Spec.Usage == "reference" ? ", só referência" : "")}.{(t.Spec.Note is null ? "" : " " + t.Spec.Note)}");
        foreach (var m in manual.Tools)
            sb.AppendLine($"- **{Md(m.Name)}**: <{m.Url}>, {m.License}, comparação manual.");
        sb.AppendLine();
        sb.AppendLine("## Como atualizar");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine("dotnet run --project tools/coverage -- latest            # há versão nova de alguma ferramenta?");
        sb.AppendLine("dotnet run --project tools/coverage -- latest --write    # fixa tag/commit novos em sources.json");
        sb.AppendLine("dotnet run --project tools/coverage -- report            # baixa (cache), compara e regrava este arquivo");
        sb.AppendLine("dotnet run --project tools/coverage -- report --record --label \"descrição\"   # e registra no histórico");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("Sem token, a API do GitHub permite 60 chamadas por hora (o relatório usa uma por ferramenta; os arquivos vêm de");
        sb.AppendLine("`raw.githubusercontent.com`). Com `GITHUB_TOKEN` ou `GH_TOKEN` no ambiente, o limite é maior.");
        return sb.ToString();
    }
}

/// <summary>Confere se as fontes { kind: tool } do catálogo batem com o que cada ferramenta realmente altera.</summary>
public static class ToolSourceCheck
{
    public static IEnumerable<string> Check(IEnumerable<Debloater.Core.Model.Tweak> tweaks, IReadOnlyList<ToolResult> tools)
    {
        var byId = tools.ToDictionary(t => t.Spec.Id, t => new CatalogIndex([]).With(t.Targets), StringComparer.OrdinalIgnoreCase);
        foreach (var tweak in tweaks)
        {
            // Tweaks só com scripts: a extração dos dois lados é heurística demais para conferir.
            if (tweak.Actions.All(a => a.Type == Debloater.Core.Model.ActionSpec.PowerShell)) continue;
            var targets = tweak.Actions.SelectMany(CatalogIndex.TargetsOf).ToList();
            foreach (var src in tweak.Sources.Where(s => s.Kind == Debloater.Core.Model.SourceRef.ToolKind))
            {
                var toolId = src.Ref.Split(':')[0];
                if (!byId.TryGetValue(toolId, out var toolIndex))
                {
                    yield return $"{tweak.Id}: ferramenta '{toolId}' não está em sources.json";
                    continue;
                }
                if (!targets.Any(t => toolIndex.CoveredBy(t).Count > 0))
                    yield return $"{tweak.Id}: '{src.Ref}' não altera nenhum alvo deste tweak";
            }
        }
    }
}
