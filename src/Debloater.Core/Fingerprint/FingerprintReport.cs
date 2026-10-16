using System.Globalization;
using System.Text;
using Debloater.Core.Inventory;

namespace Debloater.Core.Fingerprint;

/// <summary>Relatório Markdown (pt-BR) de um <see cref="FingerprintDiffResult"/>, em ordem de utilidade.</summary>
public static class FingerprintReport
{
    public const string HumanReadingHeading = "## Leitura humana";

    private const int MaxRowsPerList = 150;

    public static string ToMarkdown(FingerprintDiffResult d, int top = 100)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Diff de builds: {d.Old.Build} → {d.New.Build}");
        sb.AppendLine();
        sb.AppendLine($"Gerado por `Debloater.Cli fingerprint diff` em {d.CreatedUtc:yyyy-MM-dd HH:mm} UTC. Só nomes, caminhos e versões; " +
                      "nenhum conteúdo de binário além das cadeias que casam com os padrões. Veja \"Como ler\" no fim.");
        sb.AppendLine();
        sb.AppendLine(HumanReadingHeading);
        sb.AppendLine();
        sb.AppendLine("_(A preencher pelo mantenedor: os achados mais interessantes, com o grau de confiança em cada um.)_");
        sb.AppendLine();

        // ---- resumo ----
        sb.AppendLine("## Resumo");
        sb.AppendLine();
        sb.AppendLine("| | Antigo | Novo |");
        sb.AppendLine("|---|---|---|");
        Row(sb, "Build", d.Old.Build, d.New.Build);
        Row(sb, "Edição / versão", $"{d.Old.Edition} {d.Old.DisplayVersion}", $"{d.New.Edition} {d.New.DisplayVersion}");
        Row(sb, "Origem", Origin(d.Old), Origin(d.New));
        Row(sb, "Varredura de binários", string.Create(Pt, $"{d.Old.BinaryMode}: {d.Old.Binaries:N0} PE, {d.Old.Strings:N0} cadeias"), string.Create(Pt, $"{d.New.BinaryMode}: {d.New.Binaries:N0} PE, {d.New.Strings:N0} cadeias"));
        Row(sb, "Políticas ADMX", d.Old.Policies.ToString("N0", Pt), d.New.Policies.ToString("N0", Pt));
        Row(sb, "Itens de inventário", d.Old.InventoryItems.ToString("N0", Pt), d.New.InventoryItems.ToString("N0", Pt));
        sb.AppendLine();
        sb.AppendLine("| Categoria | Quantidade |");
        sb.AppendLine("|---|---:|");
        foreach (var c in d.Counts) sb.AppendLine($"| {Esc(c.Label)} | {c.Value.ToString("N0", Pt)} |");
        sb.AppendLine();
        if (d.Warnings.Count > 0)
        {
            sb.AppendLine("Avisos:");
            sb.AppendLine();
            foreach (var w in d.Warnings) sb.AppendLine($"- {w}");
            sb.AppendLine();
        }

        // ---- candidatos ----
        var open = d.Candidates.Where(c => c.CoveredBy.Count == 0).Take(top).ToList();
        sb.AppendLine($"## Top {open.Count} candidatos (não cobertos pelo catálogo)");
        sb.AppendLine();
        sb.AppendLine("Pontuação = ordem de investigação, não prova. `new` = não existia no build antigo; `moved` = existia em outro binário.");
        sb.AppendLine("A lista completa (com SHA-256 dos binários) está no JSON (`candidates`).");
        sb.AppendLine();
        CandidateTable(sb, open);

        // ---- ADMX ----
        var addedPolicies = d.Policies.Where(p => p.Change == DiffChange.Added).ToList();
        sb.AppendLine($"## Políticas ADMX novas ({addedPolicies.Count})");
        sb.AppendLine();
        if (addedPolicies.Count == 0) sb.AppendLine("Nenhuma.");
        else
        {
            sb.AppendLine("| Política | Classe | Chave \\ valor | Com suporte em | Nome exibido |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var p in addedPolicies.Take(MaxRowsPerList))
                sb.AppendLine($"| {Code(p.Policy.QualifiedName)}{(p.Note is null ? "" : $" ({p.Note})")} | {p.Policy.Class} | {Code(string.Join("; ", p.Policy.Values.Take(4)))}{(p.Policy.Values.Count > 4 ? $" (+{p.Policy.Values.Count - 4})" : "")} | {Esc(p.Policy.SupportedOn)} | {Esc(p.Policy.DisplayName)} |");
            More(sb, addedPolicies.Count);
        }
        sb.AppendLine();

        var changedPolicies = d.Policies.Where(p => p.Change == DiffChange.Changed).ToList();
        sb.AppendLine($"## Políticas ADMX alteradas ({changedPolicies.Count})");
        sb.AppendLine();
        if (changedPolicies.Count == 0) sb.AppendLine("Nenhuma.");
        foreach (var p in changedPolicies.Take(MaxRowsPerList))
            sb.AppendLine($"- {Code(p.Policy.QualifiedName)}: {Esc(string.Join("; ", p.Details))}");
        More(sb, changedPolicies.Count);
        sb.AppendLine();

        var removedPolicies = d.Policies.Where(p => p.Change == DiffChange.Removed).ToList();
        sb.AppendLine($"## Políticas ADMX removidas ({removedPolicies.Count})");
        sb.AppendLine();
        if (removedPolicies.Count == 0) sb.AppendLine("Nenhuma.");
        foreach (var g in removedPolicies.GroupBy(p => p.Note).OrderBy(g => g.Key is null ? 0 : 1))
        {
            if (g.Key is not null) sb.AppendLine($"- _{g.Key}_: {string.Join(", ", g.Select(p => p.Policy.File).Distinct(StringComparer.OrdinalIgnoreCase).Select(Code))} ({g.Count()} políticas)");
            else foreach (var p in g.Take(MaxRowsPerList)) sb.AppendLine($"- {Code(p.Policy.QualifiedName)} ({Code(string.Join("; ", p.Policy.Values.Take(3)))})");
        }
        sb.AppendLine();

        // ---- catálogo ----
        sb.AppendLine($"## Tweaks do catálogo possivelmente obsoletos ({d.PossiblyObsolete.Count})");
        sb.AppendLine();
        sb.AppendLine("O nome do valor (ou a chave) estava em algum binário do build antigo e não está em nenhum do novo, ou saiu do ADMX. " +
                      "Não prova que o tweak parou de funcionar (o valor pode ser lido por um binário fora da varredura, ou montado em tempo de execução): confira na VM.");
        sb.AppendLine();
        if (d.PossiblyObsolete.Count == 0) sb.AppendLine("Nenhum.");
        foreach (var t in d.PossiblyObsolete)
            sb.AppendLine($"- {Code(t.TweakId)}: {Code(string.Join("; ", t.Targets))}. {Esc(string.Join("; ", t.Reasons))}. Antes em: {Esc(string.Join(", ", t.OldBinaries.Take(3)))}");
        sb.AppendLine();

        // ---- inventário ----
        sb.AppendLine("## Inventário");
        sb.AppendLine();
        sb.AppendLine("Mudanças marcadas como \"ambiente\" vêm de hardware, apps instalados à parte ou do usuário (ruído quando as máquinas são diferentes).");
        sb.AppendLine();
        foreach (var kind in InventoryKinds.All)
        {
            var items = d.Inventory.Where(i => i.Kind == kind).ToList();
            if (items.Count == 0) continue;
            sb.AppendLine($"### {KindTitle(kind)}");
            sb.AppendLine();
            foreach (var change in new[] { DiffChange.Added, DiffChange.Removed, DiffChange.Changed })
            {
                var sys = items.Where(i => i.Change == change && i.Noise is null).ToList();
                if (sys.Count == 0) continue;
                sb.AppendLine($"**{ChangeTitle(change)} ({sys.Count})**");
                sb.AppendLine();
                foreach (var i in sys.Take(MaxRowsPerList))
                {
                    var name = i.DisplayName is { Length: > 0 } dn && !dn.Equals(i.Id, StringComparison.OrdinalIgnoreCase) ? $" — {Esc(dn)}" : "";
                    var tweaks = i.TweakIds.Count > 0 ? $" (tweaks: {string.Join(", ", i.TweakIds.Select(Code))})" : "";
                    var details = change == DiffChange.Changed || kind is InventoryKinds.Services ? $": {Esc(Short(string.Join("; ", i.Details), 160))}" : "";
                    sb.AppendLine($"- {Code(i.Id)}{name}{tweaks}{details}");
                }
                More(sb, sys.Count);
                sb.AppendLine();
            }
            var noise = items.Where(i => i.Noise is not null).ToList();
            if (noise.Count > 0)
            {
                sb.AppendLine($"<details><summary>{noise.Count} prováveis do ambiente</summary>");
                sb.AppendLine();
                foreach (var g in noise.GroupBy(i => i.Noise))
                    sb.AppendLine($"- {Esc(g.Key)}: {string.Join(", ", g.Take(40).Select(i => $"{Sign(i.Change)}{Code(i.Id)}"))}{(g.Count() > 40 ? $" (+{g.Count() - 40})" : "")}");
                sb.AppendLine();
                sb.AppendLine("</details>");
                sb.AppendLine();
            }
        }

        // ---- binários ----
        sb.AppendLine("## Binários");
        sb.AppendLine();
        foreach (var change in new[] { DiffChange.Added, DiffChange.Removed })
        {
            var list = d.Binaries.Where(b => b.Change == change).ToList();
            sb.AppendLine($"**{ChangeTitle(change)} ({list.Count}; {list.Count(b => b.Curated)} da lista curada)**");
            sb.AppendLine();
            foreach (var b in list.Take(MaxRowsPerList))
                sb.AppendLine($"- {Code(b.Path)} {b.NewVersion ?? b.OldVersion}{(b.Curated ? " ★" : "")}");
            More(sb, list.Count);
            sb.AppendLine();
        }
        var versions = d.Binaries.Where(b => b.Change == DiffChange.Changed).ToList();
        sb.AppendLine($"**Versão alterada: {versions.Count}** ({versions.Count(b => b.Curated)} da lista curada). Versões mais comuns no novo: " +
                      string.Join(", ", versions.GroupBy(b => b.NewVersion).OrderByDescending(g => g.Count()).Take(5).Select(g => $"{g.Key} ({g.Count()})")) + ".");
        sb.AppendLine();

        // ---- já cobertos e sumidos ----
        var covered = d.Candidates.Where(c => c.CoveredBy.Count > 0).ToList();
        sb.AppendLine($"## Candidatos já cobertos pelo catálogo ({covered.Count})");
        sb.AppendLine();
        foreach (var c in covered.Take(MaxRowsPerList))
            sb.AppendLine($"- {Code(c.Value)} ({c.Kind}, {c.Status}) → {string.Join(", ", c.CoveredBy.Select(Code))}");
        if (covered.Count == 0) sb.AppendLine("Nenhum.");
        More(sb, covered.Count);
        sb.AppendLine();

        var removed = d.RemovedStrings.Where(c => c.Score >= 40).Take(60).ToList();
        sb.AppendLine($"## Cadeias relevantes que sumiram (top {removed.Count})");
        sb.AppendLine();
        sb.AppendLine("Presentes em algum binário do build antigo e em nenhum do novo. Com máquinas diferentes, muitas vêm de binários de terceiros/hardware que só existem no antigo.");
        sb.AppendLine();
        if (removed.Count == 0) sb.AppendLine("Nenhuma.");
        else
        {
            sb.AppendLine("| Cadeia | Tipo | Pontos | Estava em |");
            sb.AppendLine("|---|---|---:|---|");
            foreach (var c in removed)
                sb.AppendLine($"| {Code(Short(c.Value, 120))} | {c.Kind} | {c.Score} | {Esc(BinaryText(c))} |");
        }
        sb.AppendLine();

        // ---- como ler ----
        sb.AppendLine("## Como ler");
        sb.AppendLine();
        sb.AppendLine("- **Cadeias**: caminhos de registro (`Software\\...`, `SYSTEM\\...`, `...\\Policies\\...`), nomes de valores com padrões de debloat " +
                      "(`Disable*`, `Allow*`, `Enable*`, `Show*`, `Hide*`, `Taskbar*`, `*Enabled`, `SubscribedContent-*Enabled`, nomes com Copilot/Recall/Widget...), " +
                      "caminhos de CSP (`./Device/Vendor/MSFT/...`), nomes WNF e de features, mais identificadores perto (no arquivo) de um caminho de registro (`nearName`).");
        sb.AppendLine("- **Pontuação**: base por tipo (política ADMX nova 55, caminho de política 45, SubscribedContent 45, CSP 40, serviço 35, nome de valor 25...), " +
                      "+ chave relevante (Explorer\\Advanced, ContentDeliveryManager, WindowsCopilot...), + palavras-chave (AI, Copilot, Recall, Widget, Ads, Suggest, " +
                      "Recommend, Telemetry, Diag...), + mesma chave/binário de um tweak do catálogo, + binário curado/novo; − log/trace/debug/teste, chaves COM/dispositivos, " +
                      "nomes curtos; `moved` vale 30%; já coberto pelo catálogo −40.");
        sb.AppendLine("- **Binário + versão**: onde a cadeia aparece no build novo (caminho relativo à raiz). É a entrada da camada 2 (engenharia reversa).");
        return sb.ToString();
    }

    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    private static void CandidateTable(StringBuilder sb, List<Candidate> list)
    {
        if (list.Count == 0) { sb.AppendLine("Nenhum."); sb.AppendLine(); return; }
        sb.AppendLine("| # | Cadeia | Tipo | Pontos | Binário (versão) | Motivo |");
        sb.AppendLine("|---:|---|---|---:|---|---|");
        foreach (var c in list)
        {
            var value = Code(Short(c.Value, 110));
            if (c.RegistryPath is not null && !c.RegistryPath.Equals(c.Value, StringComparison.OrdinalIgnoreCase))
                value += $"<br>em {Code(Short(c.RegistryPath, 110))}";
            if (c.Description is not null) value += $"<br>{Esc(Short(c.Description, 90))}";
            sb.AppendLine($"| {c.Rank} | {value} | {c.Kind}{(c.Status == "new" ? "" : $" ({c.Status})")} | {c.Score} | {Esc(BinaryText(c))} | {Esc(string.Join("; ", c.Reasons.Skip(1)))} |");
        }
        sb.AppendLine();
    }

    private static string BinaryText(Candidate c)
    {
        if (c.Binaries.Count == 0) return "—";
        var first = c.Binaries[0];
        var text = $"{first.Path} {first.Version}".Trim();
        if (c.BinaryCount > 1) text += $" (+{c.BinaryCount - 1})";
        if (c.OldBinaries is { Count: > 0 }) text += $"; antes: {Path.GetFileName(c.OldBinaries[0])}";
        return text;
    }

    private static void Row(StringBuilder sb, string label, string a, string b) => sb.AppendLine($"| {label} | {Esc(a)} | {Esc(b)} |");

    private static string Origin(FingerprintSide s) =>
        $"{(s.Live ? "em execução" : "offline")} ({s.Root}), {(s.Elevated ? "elevado" : "sem elevação")}, {s.CreatedUtc:yyyy-MM-dd}";

    private static void More(StringBuilder sb, int total)
    {
        if (total > MaxRowsPerList) sb.AppendLine($"- … e mais {total - MaxRowsPerList} (veja o JSON).");
    }

    private static string KindTitle(string kind) => kind switch
    {
        InventoryKinds.Services => "Serviços",
        InventoryKinds.Drivers => "Drivers",
        InventoryKinds.Tasks => "Tarefas agendadas",
        InventoryKinds.Appx => "Apps (appx)",
        InventoryKinds.Features => "Recursos opcionais",
        InventoryKinds.Capabilities => "Capacidades",
        InventoryKinds.Autoruns => "Inicialização automática",
        _ => kind,
    };

    private static string ChangeTitle(DiffChange c) => c switch
    {
        DiffChange.Added => "Adicionados",
        DiffChange.Removed => "Removidos",
        _ => "Alterados",
    };

    private static string Sign(DiffChange c) => c switch { DiffChange.Added => "+", DiffChange.Removed => "−", _ => "~" };

    private static string Short(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>O relatório vai para o git: sem SIDs nem o nome do usuário atual (aparecem em nomes de tarefas por usuário).</summary>
    internal static string Redact(string s)
    {
        s = System.Text.RegularExpressions.Regex.Replace(s, @"S-1-5-21-[\d-]+", "S-1-5-21-…");
        var user = Environment.UserName;
        return user.Length >= 3 ? s.Replace(user, "<usuário>", StringComparison.OrdinalIgnoreCase) : s;
    }

    /// <summary>Texto seguro numa célula de tabela.</summary>
    internal static string Esc(string? s) =>
        Redact(s ?? "").Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Código inline seguro numa célula de tabela.</summary>
    internal static string Code(string? s) => $"`{Redact(s ?? "").Replace("`", "'").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ")}`";
}
