using System.Text;
using Debloater.Core.Catalog;
using Debloater.Core.Engine;
using Debloater.Core.Model;

namespace Debloater.Cli;

internal static partial class Program
{
    private const string Usage = """
        Debloater.Cli - testes e automação do catálogo de tweaks

        Uso:
          Debloater.Cli list     [--category X] [--catalog DIR]
          Debloater.Cli status   [--category X] [--catalog DIR] [--verbose]
          Debloater.Cli apply    <id...> [--max-risk low|medium|high|critical] [--catalog DIR]
          Debloater.Cli revert   <id...> [--catalog DIR]
          Debloater.Cli validate [--catalog DIR] [--reference [--admx-dir DIR] [--csp FILE] [--warnings-out FILE]]

          Debloater.Cli inventory [--kind K[,K...]] [--uncatalogued] [--json]
          Debloater.Cli inventory snapshot <arquivo.json> [--kind K]
          Debloater.Cli inventory diff <antigo.json> <novo.json> [--json]

          Debloater.Cli admx list   [--search TEXTO] [--configured] [--admx-dir DIR] [--lang pt-BR]
          Debloater.Cli admx show   <[Arquivo.admx:]Nome> [--admx-dir DIR]
          Debloater.Cli admx export <arquivo.json> [--with-state] [--admx-dir DIR]

          Debloater.Cli reference annotate [--write] [--force] [--admx-dir DIR] [--csp FILE]

          Debloater.Cli selftest [--id ID]... [--category X] [--max-risk R] [--include-irreversible]
                                 [--json RELATORIO.json] [--backup-dir DIR] [--restore-point] [--verbose]
                                 [--phase full|apply-only|verify|revert] [--state ESTADO.json] [--label ROTULO]
                                 [--i-know-this-is-not-a-vm]

        --category X     filtra pelo início da categoria (ex.: privacidade, "Sistema/Tarefas").
        --max-risk       risco máximo aceito por 'apply' (padrão: medium). High/Critical exigem opt-in explícito.
        --catalog DIR    pasta do catálogo (padrão: 'catalog' ao lado do executável).
        --kind K         services, drivers, tasks, appx, features, capabilities, autoruns.
        --admx-dir DIR   pasta PolicyDefinitions (padrão: C:\Windows\PolicyDefinitions). Aceita o pacote
                         de Modelos Administrativos mais recente baixado da Microsoft.
        --csp FILE       referência da Policy CSP (padrão: reference/msdocs-policy-csp.json do repositório).
        apply/revert exigem um prompt de administrador. Os demais comandos são somente leitura.

        selftest         SÓ PARA VM DESCARTÁVEL: para cada tweak, lê o estado, aplica, confere (Applied),
                         re-aplica (o backup original deve ficar intacto), reverte e confere que cada ação
                         voltou ao valor de antes. Recusa rodar fora de um convidado Hyper-V, a menos que
                         se passe --i-know-this-is-not-a-vm. Requer administrador. Padrão: todos os tweaks,
                         todos os riscos; irreversíveis (appx) só com --include-irreversible.
        --id ID          tweak a testar (repetível ou separado por vírgulas).
        --json ARQ       (selftest) grava o relatório JSON, atualizado a cada tweak.
        --backup-dir DIR (selftest) pasta dos backups (padrão: %ProgramData%\Debloater\backups).
        --restore-point  (selftest) cria um ponto de restauração antes e registra o resultado.
        --phase P        (selftest) teste de persistência em fases: apply-only aplica e grava o arquivo de
                         estado; verify relê o estado e diz quais tweaks o Windows desfez (repetível, com
                         --label, ex. depois de reiniciar e depois de esperar); revert reverte e compara com
                         o estado original. Padrão: full (ciclo completo de uma vez).
        --state ARQ      (selftest) arquivo de estado das fases (padrão: %ProgramData%\Debloater\selftest-state.json).
        --label TEXTO    (selftest --phase verify) rótulo da conferência (padrão: verify-<n>).
        """;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            var parsed = Args.Parse(args);
            if (parsed.Command is null or "help" or "-h" or "--help")
            {
                Console.WriteLine(Usage);
                return parsed.Command is null ? 1 : 0;
            }

            return parsed.Command switch
            {
                "validate" => parsed.Reference ? ValidateReference(parsed) : Validate(parsed),
                "list" => List(parsed),
                "status" => await StatusAsync(parsed),
                "apply" => await ApplyAsync(parsed),
                "revert" => await RevertAsync(parsed),
                "inventory" => await InventoryAsync(parsed),
                "admx" => Admx(parsed),
                "reference" => Reference(parsed),
                "selftest" => await SelfTestAsync(parsed),
                _ => Fail($"Comando desconhecido '{parsed.Command}'.\n\n{Usage}"),
            };
        }
        catch (ArgumentException ex)
        {
            return Fail(ex.Message);
        }
        catch (ElevationRequiredException ex)
        {
            return Fail(ex.Message);
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    // ---------------- comandos ----------------

    private static int Validate(Args a)
    {
        var result = CatalogLoader.LoadDirectory(a.CatalogDir);
        Console.WriteLine($"Catálogo: {a.CatalogDir}");
        Console.WriteLine($"{result.FileCount} arquivo(s), {result.Tweaks.Count} tweak(s) válido(s), {result.Errors.Count} erro(s).");
        foreach (var error in result.Errors) Console.WriteLine($"  ERRO {error}");
        return result.IsValid ? 0 : 1;
    }

    private static int List(Args a)
    {
        var tweaks = LoadOrFail(a);
        if (tweaks is null) return 1;
        foreach (var group in tweaks.GroupBy(t => t.Category))
        {
            Console.WriteLine($"\n[{group.Key}]");
            foreach (var t in group)
                Console.WriteLine($"  {t.Id,-48} {RiskText(t.Risk),-8} {t.Name}");
        }
        Console.WriteLine($"\n{tweaks.Count} tweak(s).");
        return 0;
    }

    private static async Task<int> StatusAsync(Args a)
    {
        var tweaks = LoadOrFail(a);
        if (tweaks is null) return 1;
        if (tweaks.Count == 0)
        {
            Console.WriteLine("Nenhum tweak corresponde ao filtro.");
            return 0;
        }
        var engine = new TweakEngine();
        Console.WriteLine(engine.System);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var statuses = await engine.GetStatusesAsync(tweaks);
        sw.Stop();

        foreach (var group in statuses.GroupBy(s => s.Tweak.Category))
        {
            Console.WriteLine($"\n[{group.Key}]");
            foreach (var s in group)
            {
                Console.WriteLine($"  {s.Tweak.Id,-48} {s.State,-13} {RiskText(s.Tweak.Risk),-8} {s.Tweak.Name}");
                bool showDetails = a.Verbose || s.State is TweakState.Error or TweakState.Partial;
                if (s.Message is not null && (a.Verbose || s.State == TweakState.Error))
                    Console.WriteLine($"      {s.Message}");
                if (showDetails)
                    foreach (var act in s.Actions)
                        Console.WriteLine($"      - {act.State,-13} {act.Action}  ({act.Detail})");
            }
        }

        Console.WriteLine();
        var summary = statuses.GroupBy(s => s.State).OrderBy(g => g.Key).Select(g => $"{g.Key}: {g.Count()}");
        Console.WriteLine($"Total {statuses.Count} | {string.Join(" | ", summary)} | {sw.ElapsedMilliseconds} ms");
        return statuses.Any(s => s.State == TweakState.Error) ? 3 : 0;
    }

    private static async Task<int> ApplyAsync(Args a)
    {
        var selected = SelectByIds(a);
        if (selected is null) return 1;
        var engine = new TweakEngine();
        int failures = 0;
        foreach (var tweak in selected)
        {
            try
            {
                if (!tweak.Reversible) Console.WriteLine($"! {tweak.Id}: NÃO reversível pelo programa.");
                if (tweak.Breaks is not null) Console.WriteLine($"! {tweak.Id}: pode afetar: {tweak.Breaks.Trim()}");
                var result = await engine.ApplyAsync(tweak, a.MaxRisk);
                Report(result);
                if (!result.Success) failures++;
            }
            catch (RiskNotAllowedException ex)
            {
                Console.Error.WriteLine($"x {ex.Message} Use --max-risk {ex.Tweak.Risk.ToString().ToLowerInvariant()} para confirmar.");
                failures++;
            }
        }
        return failures == 0 ? 0 : 1;
    }

    private static async Task<int> RevertAsync(Args a)
    {
        var selected = SelectByIds(a);
        if (selected is null) return 1;
        var engine = new TweakEngine();
        int failures = 0;
        foreach (var tweak in selected)
        {
            var result = await engine.RevertAsync(tweak);
            Report(result);
            if (!result.Success) failures++;
        }
        return failures == 0 ? 0 : 1;
    }

    // ---------------- helpers ----------------

    private static void Report(OperationResult r)
    {
        Console.WriteLine($"{(r.Success ? "ok" : "x ")} {r.TweakId}: {r.Message}");
        foreach (var e in r.Errors) Console.WriteLine($"    {e}");
        foreach (var n in r.Notes) Console.WriteLine($"    aviso: {n}");
    }

    private static List<Tweak>? LoadOrFail(Args a)
    {
        var result = CatalogLoader.LoadDirectory(a.CatalogDir);
        if (!result.IsValid)
        {
            Console.Error.WriteLine($"Catálogo com {result.Errors.Count} erro(s); rode 'validate'. Primeiro erro: {result.Errors[0]}");
            if (result.Tweaks.Count == 0) return null;
        }
        return result.Tweaks
            .Where(t => a.Category is null || CategoryMatches(t.Category, a.Category))
            .OrderBy(t => t.Category, StringComparer.CurrentCulture)
            .ThenBy(t => t.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Prefixo da categoria, sem diferenciar maiúsculas (ex.: "privacidade", "Sistema/Tarefas").</summary>
    private static bool CategoryMatches(string category, string filter) =>
        category.StartsWith(filter.Trim().TrimEnd('/'), StringComparison.CurrentCultureIgnoreCase);

    private static List<Tweak>? SelectByIds(Args a)
    {
        if (a.Positional.Count == 0)
        {
            Console.Error.WriteLine("Informe ao menos um id de tweak.");
            return null;
        }
        var all = CatalogLoader.LoadDirectory(a.CatalogDir);
        var byId = all.Tweaks.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var missing = a.Positional.Where(id => !byId.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            Console.Error.WriteLine($"Id(s) não encontrado(s): {string.Join(", ", missing)}");
            return null;
        }
        return a.Positional.Select(id => byId[id]).ToList();
    }

    private static string RiskText(RiskLevel risk) => risk switch
    {
        RiskLevel.Low => "baixo",
        RiskLevel.Medium => "médio",
        RiskLevel.High => "ALTO",
        RiskLevel.Critical => "CRÍTICO",
        _ => risk.ToString(),
    };

    internal sealed class Args
    {
        public string? Command { get; private set; }
        public List<string> Positional { get; } = [];
        public string? Category { get; private set; }
        public string CatalogDir { get; private set; } = Path.Combine(AppContext.BaseDirectory, "catalog");
        public RiskLevel MaxRisk { get; private set; } = RiskLevel.Medium;
        public bool Verbose { get; private set; }
        public List<string> Kinds { get; } = [];
        public bool Uncatalogued { get; private set; }
        public bool Json { get; private set; }
        public string? Search { get; private set; }
        public bool Configured { get; private set; }
        public bool WithState { get; private set; }
        public string AdmxDir { get; private set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "PolicyDefinitions");
        public string? Lang { get; private set; }
        public bool Reference { get; private set; }
        public string? CspFile { get; private set; }
        public string? WarningsOut { get; private set; }
        public bool Write { get; private set; }
        public bool Force { get; private set; }
        public bool MaxRiskSet { get; private set; }
        public List<string> Ids { get; } = [];
        public bool IncludeIrreversible { get; private set; }
        public string? JsonOut { get; private set; }
        public string? BackupDir { get; private set; }
        public bool RestorePoint { get; private set; }
        public bool NotAVmOverride { get; private set; }
        public string Phase { get; private set; } = "full";
        public string? StateFile { get; private set; }
        public string? Label { get; private set; }

        public static Args Parse(string[] args)
        {
            var a = new Args();
            for (int i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"'{args[i]}' exige um valor.");
                switch (args[i])
                {
                    case "--category": a.Category = Next(); break;
                    case "--catalog": a.CatalogDir = Path.GetFullPath(Next()); break;
                    case "--verbose": case "-v": a.Verbose = true; break;
                    case "--kind": a.Kinds.AddRange(Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); break;
                    case "--uncatalogued": a.Uncatalogued = true; break;
                    case "--json":
                        // No selftest, --json recebe o caminho do relatório; nos demais é uma chave.
                        if (a.Command == "selftest") a.JsonOut = Path.GetFullPath(Next());
                        else a.Json = true;
                        break;
                    case "--id": a.Ids.AddRange(Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); break;
                    case "--include-irreversible": a.IncludeIrreversible = true; break;
                    case "--backup-dir": a.BackupDir = Path.GetFullPath(Next()); break;
                    case "--restore-point": a.RestorePoint = true; break;
                    case "--i-know-this-is-not-a-vm": a.NotAVmOverride = true; break;
                    case "--phase": a.Phase = Next().ToLowerInvariant(); break;
                    case "--state": a.StateFile = Path.GetFullPath(Next()); break;
                    case "--label": a.Label = Next(); break;
                    case "--search": a.Search = Next(); break;
                    case "--configured": a.Configured = true; break;
                    case "--with-state": a.WithState = true; break;
                    case "--admx-dir": a.AdmxDir = Path.GetFullPath(Next()); break;
                    case "--lang": a.Lang = Next(); break;
                    case "--reference": a.Reference = true; break;
                    case "--csp": a.CspFile = Path.GetFullPath(Next()); break;
                    case "--warnings-out": a.WarningsOut = Path.GetFullPath(Next()); break;
                    case "--write": a.Write = true; break;
                    case "--force": a.Force = true; break;
                    case "--max-risk":
                        var v = Next();
                        a.MaxRisk = Enum.TryParse<RiskLevel>(v, ignoreCase: true, out var r) && Enum.IsDefined(r)
                            ? r
                            : throw new ArgumentException($"--max-risk inválido '{v}' (low|medium|high|critical).");
                        a.MaxRiskSet = true;
                        break;
                    default:
                        if (args[i].StartsWith("--")) throw new ArgumentException($"Opção desconhecida '{args[i]}'.");
                        if (a.Command is null) a.Command = args[i].ToLowerInvariant();
                        else a.Positional.Add(args[i]);
                        break;
                }
            }
            return a;
        }
    }
}
