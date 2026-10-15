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

    /// <summary>
    /// selftest: aplica, confere, re-aplica, reverte e confere cada tweak. ALTERA o sistema; por isso só
    /// roda dentro de um convidado Hyper-V (ou com --i-know-this-is-not-a-vm) e como administrador.
    /// </summary>
    private static async Task<int> SelfTestAsync(Args a)
    {
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
        };

        Console.WriteLine(engine.System);
        Console.WriteLine($"VM: {vm.Evidence}");
        Console.WriteLine($"{selected.Count} tweak(s) selecionado(s); risco máximo {maxRisk}; irreversíveis {(a.IncludeIrreversible ? "incluídos" : "pulados")}; backups em {engine.Backups.Directory}");

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
        });
        int index = 0;
        runner.TweakStarting += t => Console.Write($"[{++index}/{selected.Count}] {t.Id} ... ");
        runner.TweakCompleted += r =>
        {
            report.Results.Add(r);
            Console.WriteLine($"{r.Verdict} ({r.DurationMs} ms){(r.Reason is null ? "" : $" - {r.Reason}")}{(r.RebootPending ? " [reinicialização pendente]" : "")}");
            if (a.Verbose)
            {
                foreach (var act in r.Actions)
                    Console.WriteLine($"      {act.Type}:{act.Target}  antes={act.Before}  aplicado={act.AfterApply}  revertido={act.AfterRevert}");
                foreach (var note in r.Notes) Console.WriteLine($"      nota: {note}");
            }
            // Relatório parcial a cada tweak: se o processo morrer, o que já foi testado fica salvo.
            if (a.JsonOut is not null) WriteReport(report, a.JsonOut);
        };

        await runner.RunAsync(selected);
        report.FinishedUtc = DateTime.UtcNow;
        if (a.JsonOut is not null)
        {
            WriteReport(report, a.JsonOut);
            Console.WriteLine($"Relatório: {a.JsonOut}");
        }

        Console.WriteLine();
        Console.WriteLine("Resumo: " + string.Join(" | ", report.Summary.Select(kv => $"{kv.Key}: {kv.Value}")));
        foreach (var r in report.Results.Where(r => r.Verdict is not (SelfTestVerdict.Pass or SelfTestVerdict.Skipped)))
            Console.WriteLine($"  {r.Verdict,-21} {r.Id}: {r.Reason}");
        if (report.Results.Any(r => r.RebootPending))
            Console.WriteLine("Atenção: há reinicialização pendente causada pelo teste (recursos opcionais).");
        return report.HasFailures ? 1 : 0;
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
