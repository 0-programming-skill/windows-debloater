using System.Text.Json;
using Debloater.Core.Engine;
using Debloater.Core.Model;
using Debloater.Core.Platform;
using Debloater.Core.SelfTest;

namespace Debloater.Core.Tests;

/// <summary>
/// Testes do selftest com um "sistema" falso em memória: nenhuma alteração real é feita.
/// </summary>
public sealed class SelfTestTests
{
    /// <summary>
    /// Mundo falso: cada alvo de ação tem um valor em texto. O valor desejado de uma ação é o seu
    /// value/state/startup. Aplicar grava o backup (se não existir) e o valor desejado; reverter restaura.
    /// Falhas são injetadas pelas propriedades.
    /// </summary>
    private sealed class FakeHost : ISelfTestHost
    {
        public const string Missing = "<não existe>";
        public Dictionary<string, string> World { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Backups { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Calls { get; } = [];
        public List<string> RebootReasons { get; } = [];

        public bool ApplyThrows { get; set; }
        public HashSet<string> ApplyIgnores { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> RevertIgnores { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool RevertReportsFailure { get; set; }
        public bool SkipBackupWrite { get; set; }
        public bool ReapplyRewritesBackup { get; set; }
        public bool RevertKeepsBackup { get; set; }
        public bool ApplyCausesReboot { get; set; }

        public static string Desired(ActionSpec a) => a.Value?.ToString() ?? a.Startup ?? a.State ?? "x";

        public Task<TweakStatus> GetStatusAsync(Tweak tweak, CancellationToken ct = default)
        {
            var actions = tweak.Actions.Select(a =>
            {
                var current = World.GetValueOrDefault(a.Target, "ausente");
                var state = current == Missing ? ActionState.NotApplicable
                    : current == Desired(a) ? ActionState.Desired : ActionState.NotDesired;
                return new ActionStatus(a, state, current == Missing ? "não existe" : $"atual: {current}");
            }).ToList();
            return Task.FromResult(new TweakStatus(tweak, StatusAggregator.Aggregate(actions.Select(s => s.State)), actions));
        }

        public Task<OperationResult> ApplyAsync(Tweak tweak, CancellationToken ct = default)
        {
            bool reapply = Backups.ContainsKey(tweak.Id);
            Calls.Add((reapply ? "reapply:" : "apply:") + tweak.Id);
            if (ApplyThrows) throw new InvalidOperationException("dism falhou (87)");
            if (!SkipBackupWrite && (!reapply || ReapplyRewritesBackup))
            {
                // Re-aplicação "com bug": regrava o arquivo (mesmos originais + um campo novo).
                var originals = reapply
                    ? JsonSerializer.Deserialize<Dictionary<string, string>>(Backups[tweak.Id])!
                    : tweak.Actions.ToDictionary(a => a.Target, a => World.GetValueOrDefault(a.Target, "ausente"));
                if (reapply) originals["rewritten"] = "sim";
                Backups[tweak.Id] = JsonSerializer.Serialize(originals);
            }
            foreach (var a in tweak.Actions)
            {
                if (ApplyIgnores.Contains(a.Target) || World.GetValueOrDefault(a.Target) == Missing) continue;
                World[a.Target] = Desired(a);
            }
            if (ApplyCausesReboot) RebootReasons.Add("CBS RebootPending");
            return Task.FromResult(new OperationResult(tweak.Id, true, "Aplicado.", []));
        }

        public Task<OperationResult> RevertAsync(Tweak tweak, CancellationToken ct = default)
        {
            Calls.Add("revert:" + tweak.Id);
            if (!Backups.TryGetValue(tweak.Id, out var json))
                return Task.FromResult(new OperationResult(tweak.Id, false, "Nenhum backup encontrado.", []));
            if (RevertReportsFailure)
                return Task.FromResult(new OperationResult(tweak.Id, false, "Revertido parcialmente (1 erro(s)).", ["service:X: acesso negado"]));
            var originals = JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;
            foreach (var (target, value) in originals)
            {
                if (RevertIgnores.Contains(target) || target == "rewritten") continue;
                if (value == "ausente") World.Remove(target); else World[target] = value;
            }
            if (!RevertKeepsBackup) Backups.Remove(tweak.Id);
            return Task.FromResult(new OperationResult(tweak.Id, true, "Revertido.", []));
        }

        public string? ReadBackupJson(string tweakId) => Backups.GetValueOrDefault(tweakId);

        public IReadOnlyList<string> RebootPendingReasons() => [.. RebootReasons];
    }

    private static Tweak MakeTweak(string id = "test.tweak", RiskLevel risk = RiskLevel.Low, bool reversible = true, params ActionSpec[] actions) => new()
    {
        Id = id,
        Name = "Teste",
        Description = "d",
        Category = "Teste",
        Risk = risk,
        Reversible = reversible,
        Actions = actions.Length > 0
            ? [.. actions]
            :
            [
                new ActionSpec { Type = ActionSpec.Registry, Path = @"HKLM\SOFTWARE\T", Name = "A", Kind = "dword", Value = "1" },
                new ActionSpec { Type = ActionSpec.Service, Name = "DiagTrack", Startup = "disabled" },
                new ActionSpec { Type = ActionSpec.ScheduledTask, Path = @"\Microsoft\T", State = "disabled" },
            ],
    };

    private static FakeHost HostWithOriginals()
    {
        var host = new FakeHost();
        host.World[@"HKLM\SOFTWARE\T\A"] = "0";
        host.World["DiagTrack"] = "automatic";
        host.World[@"\Microsoft\T"] = "enabled";
        return host;
    }

    private static Task<TweakSelfTestResult> Run(FakeHost host, Tweak tweak, SelfTestOptions? options = null) =>
        new SelfTestRunner(host, options).RunOneAsync(tweak);

    [Fact]
    public async Task HealthyTweak_Passes_AndRecordsEveryStep()
    {
        var host = HostWithOriginals();
        host.World.Remove(@"HKLM\SOFTWARE\T\A"); // valor de registro que não existia: deve voltar a "ausente"
        var original = new Dictionary<string, string>(host.World);

        var r = await Run(host, MakeTweak());

        Assert.Equal(SelfTestVerdict.Pass, r.Verdict);
        Assert.Null(r.Reason);
        Assert.Equal(TweakState.NotApplied, r.StateBefore);
        Assert.Equal(TweakState.Applied, r.StateAfterApply);
        Assert.Equal(TweakState.NotApplied, r.StateAfterRevert);
        Assert.Equal(["apply:test.tweak", "reapply:test.tweak", "revert:test.tweak"], host.Calls);
        Assert.True(r.Backup.WrittenOnApply);
        Assert.True(r.Backup.KeptOnReapply);
        Assert.True(r.Backup.DeletedAfterRevert);
        Assert.NotNull(r.Backup.Json);
        Assert.All(r.Actions, a => Assert.True(a.Restored));
        Assert.Equal("atual: ausente", r.Actions[0].Before!.Detail);
        Assert.Equal("atual: 1", r.Actions[0].AfterApply!.Detail);
        Assert.Equal(original, host.World);
        Assert.Equal(["registry", "service", "scheduledTask"], r.ActionTypes);
    }

    [Fact]
    public async Task ApplyThrows_IsApplyFailed_WithExceptionText()
    {
        var host = HostWithOriginals();
        host.ApplyThrows = true;

        var r = await Run(host, MakeTweak());

        Assert.Equal(SelfTestVerdict.ApplyFailed, r.Verdict);
        Assert.Contains("dism falhou (87)", r.Reason);
        Assert.Contains("InvalidOperationException", r.Apply!.Exception);
        Assert.False(r.Backup.WrittenOnApply);
        Assert.Null(r.Revert); // sem backup não há o que reverter
    }

    [Fact]
    public async Task ApplyWithoutEffect_IsNotAppliedAfterApply_NamingTheAction()
    {
        var host = HostWithOriginals();
        host.ApplyIgnores.Add("DiagTrack");

        var r = await Run(host, MakeTweak());

        Assert.Equal(SelfTestVerdict.NotAppliedAfterApply, r.Verdict);
        Assert.Equal(TweakState.Partial, r.StateAfterApply);
        Assert.Contains("service:DiagTrack", r.Reason);
        Assert.Contains("automatic", r.Reason);
        Assert.NotNull(r.Revert); // ainda reverte para deixar o sistema como estava
    }

    [Fact]
    public async Task RevertWithoutEffectOnOneAction_IsNotRestored()
    {
        var host = HostWithOriginals();
        host.RevertIgnores.Add(@"\Microsoft\T");

        var r = await Run(host, MakeTweak());

        Assert.Equal(SelfTestVerdict.NotRestored, r.Verdict);
        Assert.Contains(@"scheduledTask:\Microsoft\T", r.Reason);
        Assert.Contains("atual: enabled", r.Reason);
        Assert.Contains("atual: disabled", r.Reason);
        Assert.Equal([true, true, false], r.Actions.Select(a => a.Restored));
    }

    [Fact]
    public async Task RevertReportsFailure_IsRevertFailed()
    {
        var host = HostWithOriginals();
        host.RevertReportsFailure = true;

        var r = await Run(host, MakeTweak());

        Assert.Equal(SelfTestVerdict.RevertFailed, r.Verdict);
        Assert.Contains("acesso negado", r.Reason);
    }

    [Fact]
    public async Task ReapplyRewritingBackup_IsBackupProblem()
    {
        var host = HostWithOriginals();
        host.ReapplyRewritesBackup = true;

        var r = await Run(host, MakeTweak());

        Assert.Equal(SelfTestVerdict.BackupProblem, r.Verdict);
        Assert.False(r.Backup.KeptOnReapply);
        Assert.Contains("re-aplicar alterou o backup", r.Reason);
    }

    [Fact]
    public async Task BackupNotDeletedAfterRevert_IsBackupProblem()
    {
        var host = HostWithOriginals();
        host.RevertKeepsBackup = true;

        var r = await Run(host, MakeTweak());

        Assert.Equal(SelfTestVerdict.BackupProblem, r.Verdict);
        Assert.False(r.Backup.DeletedAfterRevert);
    }

    [Fact]
    public async Task Irreversible_SkippedByDefault_PassesWithFlag_WithoutRevert()
    {
        var appx = new ActionSpec { Type = ActionSpec.Appx, Name = "Microsoft.BingNews", State = "removed" };
        var host = new FakeHost();
        host.World["Microsoft.BingNews"] = "installed";
        var tweak = MakeTweak("apps.bingnews", reversible: false, actions: appx);

        var skipped = await Run(host, tweak);
        Assert.Equal(SelfTestVerdict.Skipped, skipped.Verdict);
        Assert.Contains("--include-irreversible", skipped.Reason);
        Assert.Empty(host.Calls);

        var r = await Run(host, tweak, new SelfTestOptions { IncludeIrreversible = true });
        Assert.Equal(SelfTestVerdict.Pass, r.Verdict);
        Assert.Null(r.Revert);
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("revert:"));
        Assert.Null(r.Actions[0].Restored);
    }

    [Fact]
    public async Task RiskAboveMax_IsSkipped_WithoutTouchingTheSystem()
    {
        var host = HostWithOriginals();
        var r = await Run(host, MakeTweak(risk: RiskLevel.Critical), new SelfTestOptions { MaxRisk = RiskLevel.High });

        Assert.Equal(SelfTestVerdict.Skipped, r.Verdict);
        Assert.Contains("Critical", r.Reason);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task NotApplicableBefore_IsSkipped()
    {
        var host = new FakeHost();
        host.World["DiagTrack"] = FakeHost.Missing;
        var r = await Run(host, MakeTweak(actions: new ActionSpec { Type = ActionSpec.Service, Name = "DiagTrack", Startup = "disabled" }));

        Assert.Equal(SelfTestVerdict.Skipped, r.Verdict);
        Assert.Contains("não se aplica", r.Reason);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task ExistingBackup_IsSkipped_SoTheOriginalIsNotMasked()
    {
        var host = HostWithOriginals();
        host.Backups["test.tweak"] = "{}";

        var r = await Run(host, MakeTweak());

        Assert.Equal(SelfTestVerdict.Skipped, r.Verdict);
        Assert.True(r.Backup.ExistedBefore);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task AlreadyApplied_StillPasses_AndNotesIt()
    {
        var host = HostWithOriginals();
        host.World[@"HKLM\SOFTWARE\T\A"] = "1";
        host.World["DiagTrack"] = "disabled";
        host.World[@"\Microsoft\T"] = "disabled";

        var r = await Run(host, MakeTweak());

        Assert.Equal(SelfTestVerdict.Pass, r.Verdict);
        Assert.Contains(r.Notes, n => n.Contains("já estava aplicado"));
    }

    [Fact]
    public async Task RebootPendingCausedByTest_IsFlagged()
    {
        var host = HostWithOriginals();
        host.ApplyCausesReboot = true;
        var r = await Run(host, MakeTweak());
        Assert.True(r.RebootPending);

        var quiet = await Run(HostWithOriginals(), MakeTweak());
        Assert.False(quiet.RebootPending);
    }

    [Fact]
    public async Task RunAsync_RunsSequentially_AndRaisesEvents()
    {
        var host = HostWithOriginals();
        var runner = new SelfTestRunner(host);
        var completed = new List<string>();
        runner.TweakCompleted += r => completed.Add($"{r.Id}={r.Verdict}");

        var results = await runner.RunAsync([MakeTweak("a.one"), MakeTweak("b.two", risk: RiskLevel.Critical)]);

        Assert.Equal(2, results.Count);
        Assert.Equal(["a.one=Pass", "b.two=Pass"], completed);
        Assert.Equal(["apply:a.one", "reapply:a.one", "revert:a.one", "apply:b.two", "reapply:b.two", "revert:b.two"], host.Calls);
    }

    // ---------- avaliador puro ----------

    private static TweakSelfTestResult Facts(Action<TweakSelfTestResult>? tweak = null)
    {
        var r = new TweakSelfTestResult
        {
            Reversible = true,
            Apply = new StepResult { Success = true },
            Revert = new StepResult { Success = true },
            StateAfterApply = TweakState.Applied,
            Backup = new BackupCheck { WrittenOnApply = true, KeptOnReapply = true, DeletedAfterRevert = true },
            Actions =
            [
                new ActionSelfTest
                {
                    Type = "registry", Target = @"HKCU\X\V",
                    Before = new ActionSnapshot { State = ActionState.NotDesired, Detail = "atual: 0 (dword)" },
                    AfterApply = new ActionSnapshot { State = ActionState.Desired, Detail = "atual: 1 (dword)" },
                    AfterRevert = new ActionSnapshot { State = ActionState.NotDesired, Detail = "atual: 0 (dword)" },
                },
            ],
        };
        tweak?.Invoke(r);
        SelfTestEvaluator.CompareActions(r);
        return r;
    }

    [Fact]
    public void Evaluator_Pass()
    {
        var r = Facts();
        Assert.Equal((SelfTestVerdict.Pass, (string?)null), SelfTestEvaluator.Evaluate(r));
        Assert.True(r.Actions[0].Restored);
    }

    [Fact]
    public void Evaluator_SameStateDifferentValue_IsNotRestored()
    {
        // Ex.: o valor original era 0 e depois de reverter ficou 2: ambos "NotDesired", mas não é o original.
        var r = Facts(f => f.Actions[0].AfterRevert = new ActionSnapshot { State = ActionState.NotDesired, Detail = "atual: 2 (dword)" });
        var (verdict, reason) = SelfTestEvaluator.Evaluate(r);
        Assert.Equal(SelfTestVerdict.NotRestored, verdict);
        Assert.Contains("atual: 2 (dword)", reason);
    }

    [Fact]
    public void Evaluator_DeletedValueThatExistedBefore_IsNotRestored()
    {
        var r = Facts(f => f.Actions[0].AfterRevert = new ActionSnapshot { State = ActionState.NotDesired, Detail = "ausente" });
        Assert.Equal(SelfTestVerdict.NotRestored, SelfTestEvaluator.Evaluate(r).Verdict);
    }

    [Fact]
    public void Evaluator_SeverityOrder()
    {
        // Aplicar falhou prevalece sobre tudo.
        Assert.Equal(SelfTestVerdict.ApplyFailed, SelfTestEvaluator.Evaluate(Facts(f =>
        {
            f.Apply = new StepResult { Success = false, Errors = ["x"] };
            f.StateAfterApply = TweakState.NotApplied;
            f.Revert = new StepResult { Success = false };
        })).Verdict);

        // Não aplicado prevalece sobre falha ao reverter.
        Assert.Equal(SelfTestVerdict.NotAppliedAfterApply, SelfTestEvaluator.Evaluate(Facts(f =>
        {
            f.StateAfterApply = TweakState.Partial;
            f.Revert = new StepResult { Success = false };
        })).Verdict);

        // Falha ao reverter prevalece sobre valor não restaurado e backup.
        Assert.Equal(SelfTestVerdict.RevertFailed, SelfTestEvaluator.Evaluate(Facts(f =>
        {
            f.Revert = new StepResult { Success = false, Exception = "IOException: x" };
            f.Actions[0].AfterRevert = new ActionSnapshot { State = ActionState.Desired, Detail = "atual: 1 (dword)" };
            f.Backup.DeletedAfterRevert = false;
        })).Verdict);

        // Não restaurado prevalece sobre problema de backup.
        Assert.Equal(SelfTestVerdict.NotRestored, SelfTestEvaluator.Evaluate(Facts(f =>
        {
            f.Actions[0].AfterRevert = new ActionSnapshot { State = ActionState.Desired, Detail = "atual: 1 (dword)" };
            f.Backup.KeptOnReapply = false;
        })).Verdict);
    }

    [Fact]
    public void Evaluator_Irreversible_JudgedByApplyAndStatusOnly()
    {
        var r = Facts(f =>
        {
            f.Reversible = false;
            f.Revert = null;
            f.Actions[0].AfterRevert = null;
        });
        Assert.Equal(SelfTestVerdict.Pass, SelfTestEvaluator.Evaluate(r).Verdict);
        Assert.Null(r.Actions[0].Restored);

        var noBackup = Facts(f => { f.Reversible = false; f.Revert = null; f.Backup.WrittenOnApply = false; });
        Assert.Equal(SelfTestVerdict.BackupProblem, SelfTestEvaluator.Evaluate(noBackup).Verdict);
    }

    [Fact]
    public void Evaluator_KeepsSkipped()
    {
        var r = Facts(f => { f.Verdict = SelfTestVerdict.Skipped; f.Reason = "motivo"; });
        Assert.Equal((SelfTestVerdict.Skipped, (string?)"motivo"), SelfTestEvaluator.Evaluate(r));
    }

    [Fact]
    public void Report_SummaryAndFailures()
    {
        var report = new SelfTestReport
        {
            Results =
            [
                new TweakSelfTestResult { Verdict = SelfTestVerdict.Pass },
                new TweakSelfTestResult { Verdict = SelfTestVerdict.Skipped },
                new TweakSelfTestResult { Verdict = SelfTestVerdict.Pass },
            ],
        };
        Assert.False(report.HasFailures);
        Assert.Equal(2, report.Summary[SelfTestVerdict.Pass]);

        report.Results.Add(new TweakSelfTestResult { Verdict = SelfTestVerdict.NotRestored });
        Assert.True(report.HasFailures);
    }

    // ---------- detecção de VM ----------

    [Fact]
    public void VmDetector_HyperVGuestByWmi()
    {
        var r = VirtualMachineDetector.Evaluate("Microsoft Corporation", "Virtual Machine", null);
        Assert.True(r.IsHyperVGuest);
    }

    [Fact]
    public void VmDetector_HyperVGuestByKvpRegistry()
    {
        var r = VirtualMachineDetector.Evaluate("", "", new Dictionary<string, string> { ["HostName"] = "MEU-PC" });
        Assert.True(r.IsHyperVGuest);
        Assert.Equal("MEU-PC", r.HostName);
    }

    [Theory]
    [InlineData("Contoso Ltd.", "Desktop Board X1")]
    [InlineData("Microsoft Corporation", "Surface Laptop 5")] // hardware real da Microsoft não é VM
    [InlineData("", "")]
    [InlineData(null, null)]
    public void VmDetector_PhysicalMachine_IsNotGuest(string? manufacturer, string? model)
    {
        Assert.False(VirtualMachineDetector.Evaluate(manufacturer, model, null).IsHyperVGuest);
    }

    [Fact]
    public void VmDetector_EmptyParametersKey_IsNotEnough()
    {
        // No host com Hyper-V pode existir a chave "Guest"; só Parameters com identificação conta.
        var r = VirtualMachineDetector.Evaluate("Contoso Ltd.", "Desktop Board X1",
            new Dictionary<string, string> { ["SessionMonitor"] = "VmbusSessionMonitor", ["HostName"] = " " });
        Assert.False(r.IsHyperVGuest);
    }

    [Fact]
    public void EngineHost_ReadsBackupFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DebloaterTests", Guid.NewGuid().ToString("N"));
        try
        {
            var engine = new TweakEngine(new TweakEngineOptions { BackupDirectory = dir, RequireElevation = false });
            var host = new EngineSelfTestHost(engine);
            Assert.Null(host.ReadBackupJson("x.y"));
            engine.Backups.Save(new TweakBackup { TweakId = "x.y" });
            Assert.Contains("\"tweakId\": \"x.y\"", host.ReadBackupJson("x.y"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
