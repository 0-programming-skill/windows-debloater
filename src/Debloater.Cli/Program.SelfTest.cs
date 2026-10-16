using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Debloater.Core.Catalog;
using Debloater.Core.Engine;
using Debloater.Core.Model;
using Debloater.Core.Platform;
using Debloater.Core.SelfTest;

namespace Debloater.Cli;

internal static partial class Program
{
    private static readonly JsonSerializerOptions s_selfTestJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string DefaultStateFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Debloater", "selftest-state.json");

    /// <summary>
    /// selftest: aplica, confere, re-aplica, reverte e confere cada tweak. ALTERA o sistema; por isso só
    /// roda dentro de um convidado Hyper-V (ou com --i-know-this-is-not-a-vm) e como administrador.
    /// Com --phase, o ciclo é dividido para o teste de persistência: apply-only, verify (repetível) e revert.
    /// </summary>
    private static async Task<int> SelfTestAsync(Args a)
    {
        var phase = a.Phase switch
        {
            "full" => SelfTestPhase.Full,
            "apply-only" => SelfTestPhase.ApplyOnly,
            "verify" => SelfTestPhase.Verify,
            "revert" => SelfTestPhase.Revert,
            _ => throw new ArgumentException($"--phase inválido '{a.Phase}' (full|apply-only|verify|revert)."),
        };

        var vm = VirtualMachineDetector.Detect();
        if (!vm.IsHyperVGuest && !a.NotAVmOverride)
        {
            Console.Error.WriteLine("RECUSADO: o selftest aplica e reverte TODOS os tweaks selecionados e só pode rodar numa VM Hyper-V descartável.");
            Console.Error.WriteLine($"Detecção: {vm.Evidence}");
            Console.Error.WriteLine("Se você tem certeza de que esta máquina é descartável, use --i-know-this-is-not-a-vm.");
            return 2;
        }
        if (!vm.IsHyperVGuest)
            Console.WriteLine($"AVISO: não parece ser uma VM Hyper-V ({vm.Evidence}); continuando por causa de --i-know-this-is-not-a-vm.");

        if (!SystemInfo.IsProcessElevated())
            return Fail("O selftest requer administrador (aplicar e reverter alteram HKLM, serviços, tarefas e recursos).");

        var catalog = CatalogLoader.LoadDirectory(a.CatalogDir);
        if (!catalog.IsValid)
            return Fail($"Catálogo com {catalog.Errors.Count} erro(s); rode 'validate'. Primeiro erro: {catalog.Errors[0]}");

        var stateFile = a.StateFile ?? DefaultStateFile;
        if (phase is SelfTestPhase.Verify or SelfTestPhase.Revert)
            return await SelfTestContinueAsync(a, phase, stateFile, catalog.Tweaks);

        var ids = a.Ids.Concat(a.Positional).ToList();
        var byId = catalog.Tweaks.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var missing = ids.Where(id => !byId.ContainsKey(id)).ToList();
        if (missing.Count > 0) return Fail($"Id(s) não encontrado(s): {string.Join(", ", missing)}");

        var selected = (ids.Count > 0 ? ids.Distinct(StringComparer.OrdinalIgnoreCase).Select(id => byId[id]) : catalog.Tweaks)
            .Where(t => a.Category is null || CategoryMatches(t.Category, a.Category))
            .OrderBy(t => t.Category, StringComparer.CurrentCulture)
            .ThenBy(t => t.Id, StringComparer.Ordinal)
            .ToList();
        if (selected.Count == 0)
        {
            Console.WriteLine("Nenhum tweak corresponde ao filtro.");
            return 0;
        }

        // Padrão do selftest: todos os riscos (é uma VM descartável). --max-risk restringe.
        var maxRisk = a.MaxRiskSet ? a.MaxRisk : RiskLevel.Critical;
        var engine = new TweakEngine(new TweakEngineOptions { BackupDirectory = a.BackupDir ?? BackupStore.DefaultDirectory });
        var report = new SelfTestReport
        {
            StartedUtc = DateTime.UtcNow,
            Windows = engine.System.ToString(),
            VmCheck = vm.Evidence,
            VmCheckOverridden = !vm.IsHyperVGuest,
            BackupDirectory = engine.Backups.Directory,
            MaxRisk = maxRisk,
            IncludeIrreversible = a.IncludeIrreversible,
            Phase = phase,
        };
        report.PhaseLog.Add($"{phase} {DateTime.UtcNow:O}");

        Console.WriteLine(engine.System);
        Console.WriteLine($"VM: {vm.Evidence}");
        Console.WriteLine($"{selected.Count} tweak(s) selecionado(s); risco máximo {maxRisk}; irreversíveis {(a.IncludeIrreversible ? "incluídos" : "pulados")}; backups em {engine.Backups.Directory}");
        if (phase == SelfTestPhase.ApplyOnly)
            Console.WriteLine($"Fase apply-only: aplica sem reverter; estado em {stateFile}. Depois: --phase verify (repetível) e --phase revert.");

        if (a.RestorePoint)
        {
            var sw = Stopwatch.StartNew();
            var rp = await RestorePoint.CreateAsync("Debloater selftest");
            report.RestorePoint = new RestorePointCheck { Created = rp.Created, Message = rp.Message, DurationMs = sw.ElapsedMilliseconds };
            Console.WriteLine($"Ponto de restauração: {(rp.Created ? "criado" : "não criado")} - {rp.Message} ({sw.ElapsedMilliseconds} ms)");
        }

        var runner = new SelfTestRunner(new EngineSelfTestHost(engine), new SelfTestOptions
        {
            MaxRisk = maxRisk,
            IncludeIrreversible = a.IncludeIrreversible,
            ApplyOnly = phase == SelfTestPhase.ApplyOnly,
        });
        int index = 0;
        runner.TweakStarting += t => Console.Write($"[{++index}/{selected.Count}] {t.Id} ... ");
        runner.TweakCompleted += r =>
        {
            report.Results.Add(r);
            Console.WriteLine($"{r.Verdict} ({r.DurationMs} ms){(r.Reason is null ? "" : $" - {r.Reason}")}{(r.RebootPending ? " [reinicialização pendente]" : "")}");
            PrintDetails(a, r);
            // Relatório parcial a cada tweak: se o processo morrer, o que já foi testado fica salvo.
            if (a.JsonOut is not null) WriteReport(report, a.JsonOut);
            if (phase == SelfTestPhase.ApplyOnly) WriteReport(report, stateFile);
        };

        await runner.RunAsync(selected);
        report.FinishedUtc = DateTime.UtcNow;
        if (phase == SelfTestPhase.ApplyOnly) WriteReport(report, stateFile);
        if (a.JsonOut is not null)
        {
            WriteReport(report, a.JsonOut);
            Console.WriteLine($"Relatório: {a.JsonOut}");
        }

        PrintSummary(report);
        return report.HasFailures ? 1 : 0;
    }

    /// <summary>Fases verify e revert do teste de persistência: continuam a partir do arquivo de estado.</summary>
    private static async Task<int> SelfTestContinueAsync(Args a, SelfTestPhase phase, string stateFile, IReadOnlyList<Tweak> tweaks)
    {
        if (!File.Exists(stateFile))
            return Fail($"Arquivo de estado não encontrado: {stateFile}. Rode antes 'selftest --phase apply-only'.");
        var report = JsonSerializer.Deserialize<SelfTestReport>(File.ReadAllText(stateFile), s_selfTestJson);
        if (report is null) return Fail($"Arquivo de estado vazio: {stateFile}.");
        if (report.Phase == SelfTestPhase.Full)
            return Fail("O arquivo de estado é de um selftest completo (full), não de --phase apply-only.");
        if (report.Phase == SelfTestPhase.Revert)
            return Fail("Este arquivo de estado já passou pela fase revert.");

        var engine = new TweakEngine(new TweakEngineOptions { BackupDirectory = report.BackupDirectory });
        var byId = tweaks.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var runner = new SelfTestRunner(new EngineSelfTestHost(engine));
        Console.WriteLine(engine.System);
        Console.WriteLine($"Estado: {stateFile} (fases: {string.Join(", ", report.PhaseLog)})");

        if (phase == SelfTestPhase.Verify)
        {
            int n = report.Results.Count == 0 ? 1 : report.Results.Max(r => r.Persistence.Count) + 1;
            var label = string.IsNullOrWhiteSpace(a.Label) ? $"verify-{n}" : a.Label.Trim();
            Console.WriteLine($"Conferência de persistência '{label}'...");
            var checks = new List<PersistenceCheck>();
            await runner.VerifyAsync(report.Results, byId, label, (r, check) =>
            {
                checks.Add(check);
                if (check.Verdict == PersistenceVerdict.Persisted && !a.Verbose) return;
                Console.WriteLine($"  {check.Verdict,-18} {r.Id}");
                foreach (var act in check.RevertedActions) Console.WriteLine($"      {act}");
            });
            report.Phase = SelfTestPhase.Verify;
            report.PhaseLog.Add($"Verify {label} {DateTime.UtcNow:O}");
            SaveStateAndReport(a, report, stateFile);
            Console.WriteLine();
            Console.WriteLine($"Persistência ({label}): " + string.Join(" | ",
                checks.GroupBy(c => c.Verdict).OrderBy(g => g.Key).Select(g => $"{g.Key}: {g.Count()}")));
            return checks.Any(c => c.Verdict != PersistenceVerdict.Persisted) ? 1 : 0;
        }

        // Revert.
        var toRevert = report.Results.Count(r => r.Verdict != SelfTestVerdict.Skipped && r.Reversible && r.Apply is not null);
        int index = 0;
        runner.TweakStarting += t => Console.Write($"[{++index}/{toRevert}] reverter {t.Id} ... ");
        runner.TweakCompleted += r =>
        {
            Console.WriteLine($"{r.Verdict} ({r.Revert?.DurationMs ?? 0} ms){(r.Reason is null ? "" : $" - {r.Reason}")}{(r.RebootPending ? " [reinicialização pendente]" : "")}");
            PrintDetails(a, r, revertOnly: true);
        };
        await runner.RevertAppliedAsync(report.Results, byId);
        report.Phase = SelfTestPhase.Revert;
        report.PhaseLog.Add($"Revert {DateTime.UtcNow:O}");
        report.FinishedUtc = DateTime.UtcNow;
        SaveStateAndReport(a, report, stateFile);
        PrintSummary(report);
        return report.HasFailures || report.HasPersistenceFailures ? 1 : 0;
    }

    /// <param name="revertOnly">Fase revert: os avisos de aplicar já foram mostrados na fase apply-only.</param>
    private static void PrintDetails(Args a, TweakSelfTestResult r, bool revertOnly = false)
    {
        foreach (var step in revertOnly ? [r.Revert] : new[] { r.Apply, r.Revert })
            foreach (var note in step?.Notes ?? []) Console.WriteLine($"      aviso: {note}");
        if (!a.Verbose) return;
        foreach (var act in r.Actions)
        {
            var checks = string.Concat(act.Checks.Select(c => $"  {c.Label}={c.Snapshot}"));
            Console.WriteLine($"      {act.Type}:{act.Target}  antes={act.Before}  aplicado={act.AfterApply}{checks}  revertido={act.AfterRevert}");
        }
        foreach (var note in r.Notes) Console.WriteLine($"      nota: {note}");
    }

    private static void PrintSummary(SelfTestReport report)
    {
        Console.WriteLine();
        Console.WriteLine("Resumo: " + string.Join(" | ", report.Summary.Select(kv => $"{kv.Key}: {kv.Value}")));
        foreach (var r in report.Results.Where(r => r.Verdict is not (SelfTestVerdict.Pass or SelfTestVerdict.Skipped)))
            Console.WriteLine($"  {r.Verdict,-21} {r.Id}: {r.Reason}");
        if (report.PersistenceSummary.Count > 0)
        {
            Console.WriteLine("Persistência: " + string.Join(" | ", report.PersistenceSummary.Select(kv => $"{kv.Key}: {kv.Value}")));
            foreach (var r in report.Results.Where(r => r.PersistenceVerdict is not (null or PersistenceVerdict.Persisted)))
                Console.WriteLine($"  {r.PersistenceVerdict,-21} {r.Id} (desde {r.RevertedAfter}): {string.Join("; ", r.Persistence[^1].RevertedActions)}");
        }
        if (report.Results.Any(r => r.RebootPending))
            Console.WriteLine("Atenção: há reinicialização pendente causada pelo teste (recursos opcionais).");
    }

    private static void SaveStateAndReport(Args a, SelfTestReport report, string stateFile)
    {
        WriteReport(report, stateFile);
        if (a.JsonOut is not null)
        {
            WriteReport(report, a.JsonOut);
            Console.WriteLine($"Relatório: {a.JsonOut}");
        }
    }

    private static void WriteReport(SelfTestReport report, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(report, s_selfTestJson));
        File.Move(temp, path, overwrite: true);
    }
}
