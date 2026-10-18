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
    /// selftest: applies, checks, re-applies, reverts and checks each tweak. It CHANGES the system, so it only
    /// runs inside a Hyper-V guest (or with --i-know-this-is-not-a-vm) and as administrator.
    /// With --phase, the cycle is split for the persistence test: apply-only, verify (repeatable) and revert.
    /// </summary>
    private static async Task<int> SelfTestAsync(Args a)
    {
        var phase = a.Phase switch
        {
            "full" => SelfTestPhase.Full,
            "apply-only" => SelfTestPhase.ApplyOnly,
            "verify" => SelfTestPhase.Verify,
            "revert" => SelfTestPhase.Revert,
            _ => throw new ArgumentException(L.F("St_InvalidPhase", a.Phase)),
        };

        var vm = VirtualMachineDetector.Detect();
        if (!vm.IsHyperVGuest && !a.NotAVmOverride)
        {
            Console.Error.WriteLine(L.T("St_Refused"));
            Console.Error.WriteLine(L.F("St_Detection", vm.Evidence));
            Console.Error.WriteLine(L.T("St_RefusedHint"));
            return 2;
        }
        if (!vm.IsHyperVGuest)
            Console.WriteLine(L.F("St_NotVmWarning", vm.Evidence));

        if (!SystemInfo.IsProcessElevated())
            return Fail(L.T("St_NeedAdmin"));

        var catalog = CatalogLoader.LoadDirectory(a.CatalogDir);
        if (!catalog.IsValid)
            return Fail(L.F("Catalog_HasErrors", catalog.Errors.Count, catalog.Errors[0]));

        var stateFile = a.StateFile ?? DefaultStateFile;
        if (phase is SelfTestPhase.Verify or SelfTestPhase.Revert)
            return await SelfTestContinueAsync(a, phase, stateFile, catalog.Tweaks);

        var ids = a.Ids.Concat(a.Positional).ToList();
        var byId = catalog.Tweaks.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var missing = ids.Where(id => !byId.ContainsKey(id)).ToList();
        if (missing.Count > 0) return Fail(L.F("Select_IdsNotFound", string.Join(", ", missing)));

        var selected = (ids.Count > 0 ? ids.Distinct(StringComparer.OrdinalIgnoreCase).Select(id => byId[id]) : catalog.Tweaks)
            .Where(t => a.Category is null || CategoryMatches(t.Category, a.Category, catalog.Categories))
            .OrderBy(t => t.Category, StringComparer.CurrentCulture)
            .ThenBy(t => t.Id, StringComparer.Ordinal)
            .ToList();
        if (selected.Count == 0)
        {
            Console.WriteLine(L.T("NoTweakMatches"));
            return 0;
        }

        // selftest default: every risk level (it is a disposable VM). --max-risk restricts it.
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
        Console.WriteLine(L.F("St_Selected", selected.Count, maxRisk, L.T(a.IncludeIrreversible ? "St_Included" : "St_Skipped"), engine.Backups.Directory));
        if (phase == SelfTestPhase.ApplyOnly)
            Console.WriteLine(L.F("St_ApplyOnly", stateFile));

        if (a.RestorePoint)
        {
            var sw = Stopwatch.StartNew();
            var rp = await RestorePoint.CreateAsync("Debloater selftest");
            report.RestorePoint = new RestorePointCheck { Created = rp.Created, Message = rp.Message, DurationMs = sw.ElapsedMilliseconds };
            Console.WriteLine(L.F("St_RestorePoint", L.T(rp.Created ? "St_Created" : "St_NotCreated"), rp.Message, sw.ElapsedMilliseconds));
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
            Console.WriteLine($"{r.Verdict} ({r.DurationMs} ms){(r.Reason is null ? "" : $" - {r.Reason}")}{(r.RebootPending ? L.T("St_RebootPending") : "")}");
            PrintDetails(a, r);
            // Partial report after each tweak: if the process dies, what was already tested is saved.
            if (a.JsonOut is not null) WriteReport(report, a.JsonOut);
            if (phase == SelfTestPhase.ApplyOnly) WriteReport(report, stateFile);
        };

        await runner.RunAsync(selected);
        report.FinishedUtc = DateTime.UtcNow;
        if (phase == SelfTestPhase.ApplyOnly) WriteReport(report, stateFile);
        if (a.JsonOut is not null)
        {
            WriteReport(report, a.JsonOut);
            Console.WriteLine(L.F("St_Report", a.JsonOut));
        }

        PrintSummary(report);
        return report.HasFailures ? 1 : 0;
    }

    /// <summary>verify and revert phases of the persistence test: they continue from the state file.</summary>
    private static async Task<int> SelfTestContinueAsync(Args a, SelfTestPhase phase, string stateFile, IReadOnlyList<Tweak> tweaks)
    {
        if (!File.Exists(stateFile))
            return Fail(L.F("St_StateNotFound", stateFile));
        var report = JsonSerializer.Deserialize<SelfTestReport>(File.ReadAllText(stateFile), s_selfTestJson);
        if (report is null) return Fail(L.F("St_StateEmpty", stateFile));
        if (report.Phase == SelfTestPhase.Full)
            return Fail(L.T("St_StateIsFull"));
        if (report.Phase == SelfTestPhase.Revert)
            return Fail(L.T("St_StateReverted"));

        var engine = new TweakEngine(new TweakEngineOptions { BackupDirectory = report.BackupDirectory });
        var byId = tweaks.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var runner = new SelfTestRunner(new EngineSelfTestHost(engine));
        Console.WriteLine(engine.System);
        Console.WriteLine(L.F("St_State", stateFile, string.Join(", ", report.PhaseLog)));

        if (phase == SelfTestPhase.Verify)
        {
            int n = report.Results.Count == 0 ? 1 : report.Results.Max(r => r.Persistence.Count) + 1;
            var label = string.IsNullOrWhiteSpace(a.Label) ? $"verify-{n}" : a.Label.Trim();
            Console.WriteLine(L.F("St_VerifyStart", label));
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
            Console.WriteLine(L.F("St_PersistenceFor", label, string.Join(" | ",
                checks.GroupBy(c => c.Verdict).OrderBy(g => g.Key).Select(g => $"{g.Key}: {g.Count()}"))));
            return checks.Any(c => c.Verdict != PersistenceVerdict.Persisted) ? 1 : 0;
        }

        // Revert.
        var toRevert = report.Results.Count(r => r.Verdict != SelfTestVerdict.Skipped && r.Reversible && r.Apply is not null);
        int index = 0;
        runner.TweakStarting += t => Console.Write(L.F("St_RevertProgress", ++index, toRevert, t.Id));
        runner.TweakCompleted += r =>
        {
            Console.WriteLine($"{r.Verdict} ({r.Revert?.DurationMs ?? 0} ms){(r.Reason is null ? "" : $" - {r.Reason}")}{(r.RebootPending ? L.T("St_RebootPending") : "")}");
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

    /// <param name="revertOnly">revert phase: the apply warnings were already shown in the apply-only phase.</param>
    private static void PrintDetails(Args a, TweakSelfTestResult r, bool revertOnly = false)
    {
        foreach (var step in revertOnly ? [r.Revert] : new[] { r.Apply, r.Revert })
            foreach (var note in step?.Notes ?? []) Console.WriteLine($"      {L.T("Label_Warning")}: {note}");
        if (!a.Verbose) return;
        foreach (var act in r.Actions)
        {
            var checks = string.Concat(act.Checks.Select(c => $"  {c.Label}={c.Snapshot}"));
            Console.WriteLine("      " + L.F("St_ActionDetail", act.Type, act.Target, act.Before, act.AfterApply, checks, act.AfterRevert));
        }
        foreach (var note in r.Notes) Console.WriteLine($"      {L.T("Label_Note")}: {note}");
    }

    private static void PrintSummary(SelfTestReport report)
    {
        Console.WriteLine();
        Console.WriteLine(L.F("St_Summary", string.Join(" | ", report.Summary.Select(kv => $"{kv.Key}: {kv.Value}"))));
        foreach (var r in report.Results.Where(r => r.Verdict is not (SelfTestVerdict.Pass or SelfTestVerdict.Skipped)))
            Console.WriteLine($"  {r.Verdict,-21} {r.Id}: {r.Reason}");
        if (report.PersistenceSummary.Count > 0)
        {
            Console.WriteLine(L.F("St_Persistence", string.Join(" | ", report.PersistenceSummary.Select(kv => $"{kv.Key}: {kv.Value}"))));
            foreach (var r in report.Results.Where(r => r.PersistenceVerdict is not (null or PersistenceVerdict.Persisted)))
                Console.WriteLine($"  {r.PersistenceVerdict,-21} {L.F("St_RevertedSince", r.Id, r.RevertedAfter, string.Join("; ", r.Persistence[^1].RevertedActions))}");
        }
        if (report.Results.Any(r => r.RebootPending))
            Console.WriteLine(L.T("St_RebootWarning"));
    }

    private static void SaveStateAndReport(Args a, SelfTestReport report, string stateFile)
    {
        WriteReport(report, stateFile);
        if (a.JsonOut is not null)
        {
            WriteReport(report, a.JsonOut);
            Console.WriteLine(L.F("St_Report", a.JsonOut));
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
