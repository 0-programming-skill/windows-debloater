using System.Text.Json;
using Debloater.Core.Engine;
using Debloater.Core.Model;
using Debloater.Core.Platform;
using Debloater.Core.SelfTest;

namespace Debloater.Core.Tests;

/// <summary>
/// Selftest tests with a fake in-memory "system": no real change is made.
/// </summary>
public sealed class SelfTestTests
{
    /// <summary>
    /// Fake world: each action target has a text value. An action's desired value is its
    /// value/state/startup. Apply writes the backup (if it does not exist) and the desired value; revert restores.
    /// Failures are injected through the properties.
    /// </summary>
    private sealed class FakeHost : ISelfTestHost
    {
        public const string Missing = "<missing>";
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
                var current = World.GetValueOrDefault(a.Target, "absent");
                var state = current == Missing ? ActionState.NotApplicable
                    : current == Desired(a) ? ActionState.Desired : ActionState.NotDesired;
                return new ActionStatus(a, state, current == Missing ? "does not exist" : $"current: {current}");
            }).ToList();
            return Task.FromResult(new TweakStatus(tweak, StatusAggregator.Aggregate(actions.Select(s => s.State)), actions));
        }

        public Task<OperationResult> ApplyAsync(Tweak tweak, CancellationToken ct = default)
        {
            bool reapply = Backups.ContainsKey(tweak.Id);
            Calls.Add((reapply ? "reapply:" : "apply:") + tweak.Id);
            if (ApplyThrows) throw new InvalidOperationException("dism failed (87)");
            if (!SkipBackupWrite && (!reapply || ReapplyRewritesBackup))
            {
                // "Buggy" re-apply: rewrites the file (same originals + a new field).
                var originals = reapply
                    ? JsonSerializer.Deserialize<Dictionary<string, string>>(Backups[tweak.Id])!
                    : tweak.Actions.ToDictionary(a => a.Target, a => World.GetValueOrDefault(a.Target, "absent"));
                if (reapply) originals["rewritten"] = "yes";
                Backups[tweak.Id] = JsonSerializer.Serialize(originals);
            }
            foreach (var a in tweak.Actions)
            {
                if (ApplyIgnores.Contains(a.Target) || World.GetValueOrDefault(a.Target) == Missing) continue;
                World[a.Target] = Desired(a);
            }
            if (ApplyCausesReboot) RebootReasons.Add("CBS RebootPending");
            return Task.FromResult(new OperationResult(tweak.Id, true, "Applied.", []));
        }

        public Task<OperationResult> RevertAsync(Tweak tweak, CancellationToken ct = default)
        {
            Calls.Add("revert:" + tweak.Id);
            if (!Backups.TryGetValue(tweak.Id, out var json))
                return Task.FromResult(new OperationResult(tweak.Id, false, "No backup found.", []));
            if (RevertReportsFailure)
                return Task.FromResult(new OperationResult(tweak.Id, false, "Partially reverted (1 error(s)).", ["service:X: access denied"]));
            var originals = JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;
            foreach (var (target, value) in originals)
            {
                if (RevertIgnores.Contains(target) || target == "rewritten") continue;
                if (value == "absent") World.Remove(target); else World[target] = value;
            }
            if (!RevertKeepsBackup) Backups.Remove(tweak.Id);
            return Task.FromResult(new OperationResult(tweak.Id, true, "Reverted.", []));
        }

        public string? ReadBackupJson(string tweakId) => Backups.GetValueOrDefault(tweakId);

        public IReadOnlyList<string> RebootPendingReasons() => [.. RebootReasons];
    }

    private static Tweak MakeTweak(string id = "test.tweak", RiskLevel risk = RiskLevel.Low, bool reversible = true, params ActionSpec[] actions) => new()
    {
        Id = id,
        Name = "Test",
        Description = "d",
        Category = "Test",
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
        host.World.Remove(@"HKLM\SOFTWARE\T\A"); // registry value that did not exist: must go back to "absent"
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
        Assert.Equal("current: absent", r.Actions[0].Before!.Detail);
        Assert.Equal("current: 1", r.Actions[0].AfterApply!.Detail);
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
        Assert.Contains("dism failed (87)", r.Reason);
        Assert.Contains("InvalidOperationException", r.Apply!.Exception);
        Assert.False(r.Backup.WrittenOnApply);
        Assert.Null(r.Revert); // without a backup there is nothing to revert
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
        Assert.NotNull(r.Revert); // still reverts to leave the system as it was
    }

    [Fact]
    public async Task RevertWithoutEffectOnOneAction_IsNotRestored()
    {
        var host = HostWithOriginals();
        host.RevertIgnores.Add(@"\Microsoft\T");

        var r = await Run(host, MakeTweak());

        Assert.Equal(SelfTestVerdict.NotRestored, r.Verdict);
        Assert.Contains(@"scheduledTask:\Microsoft\T", r.Reason);
        Assert.Contains("current: enabled", r.Reason);
        Assert.Contains("current: disabled", r.Reason);
        Assert.Equal([true, true, false], r.Actions.Select(a => a.Restored));
    }

    [Fact]
    public async Task RevertReportsFailure_IsRevertFailed()
    {
        var host = HostWithOriginals();
        host.RevertReportsFailure = true;

        var r = await Run(host, MakeTweak());

        Assert.Equal(SelfTestVerdict.RevertFailed, r.Verdict);
        Assert.Contains("access denied", r.Reason);
    }

    [Fact]
    public async Task ReapplyRewritingBackup_IsBackupProblem()
    {
        var host = HostWithOriginals();
        host.ReapplyRewritesBackup = true;

        var r = await Run(host, MakeTweak());

        Assert.Equal(SelfTestVerdict.BackupProblem, r.Verdict);
        Assert.False(r.Backup.KeptOnReapply);
        Assert.Contains("re-applying changed the original backup", r.Reason);
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
        Assert.Contains("does not apply", r.Reason);
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
        Assert.Contains(r.Notes, n => n.Contains("already applied"));
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

    // ---------- pure evaluator ----------

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
                    Before = new ActionSnapshot { State = ActionState.NotDesired, Detail = "current: 0 (dword)" },
                    AfterApply = new ActionSnapshot { State = ActionState.Desired, Detail = "current: 1 (dword)" },
                    AfterRevert = new ActionSnapshot { State = ActionState.NotDesired, Detail = "current: 0 (dword)" },
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
        // E.g. the original value was 0 and after revert it became 2: both "NotDesired", but it is not the original.
        var r = Facts(f => f.Actions[0].AfterRevert = new ActionSnapshot { State = ActionState.NotDesired, Detail = "current: 2 (dword)" });
        var (verdict, reason) = SelfTestEvaluator.Evaluate(r);
        Assert.Equal(SelfTestVerdict.NotRestored, verdict);
        Assert.Contains("current: 2 (dword)", reason);
    }

    [Fact]
    public void Evaluator_DeletedValueThatExistedBefore_IsNotRestored()
    {
        var r = Facts(f => f.Actions[0].AfterRevert = new ActionSnapshot { State = ActionState.NotDesired, Detail = "absent" });
        Assert.Equal(SelfTestVerdict.NotRestored, SelfTestEvaluator.Evaluate(r).Verdict);
    }

    [Fact]
    public void Evaluator_SeverityOrder()
    {
        // Apply failed takes precedence over everything.
        Assert.Equal(SelfTestVerdict.ApplyFailed, SelfTestEvaluator.Evaluate(Facts(f =>
        {
            f.Apply = new StepResult { Success = false, Errors = ["x"] };
            f.StateAfterApply = TweakState.NotApplied;
            f.Revert = new StepResult { Success = false };
        })).Verdict);

        // Not applied takes precedence over revert failure.
        Assert.Equal(SelfTestVerdict.NotAppliedAfterApply, SelfTestEvaluator.Evaluate(Facts(f =>
        {
            f.StateAfterApply = TweakState.Partial;
            f.Revert = new StepResult { Success = false };
        })).Verdict);

        // Revert failure takes precedence over value not restored and backup.
        Assert.Equal(SelfTestVerdict.RevertFailed, SelfTestEvaluator.Evaluate(Facts(f =>
        {
            f.Revert = new StepResult { Success = false, Exception = "IOException: x" };
            f.Actions[0].AfterRevert = new ActionSnapshot { State = ActionState.Desired, Detail = "current: 1 (dword)" };
            f.Backup.DeletedAfterRevert = false;
        })).Verdict);

        // Not restored takes precedence over backup problem.
        Assert.Equal(SelfTestVerdict.NotRestored, SelfTestEvaluator.Evaluate(Facts(f =>
        {
            f.Actions[0].AfterRevert = new ActionSnapshot { State = ActionState.Desired, Detail = "current: 1 (dword)" };
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
        var r = Facts(f => { f.Verdict = SelfTestVerdict.Skipped; f.Reason = "reason"; });
        Assert.Equal((SelfTestVerdict.Skipped, (string?)"reason"), SelfTestEvaluator.Evaluate(r));
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

    // ---------- VM detection ----------

    [Fact]
    public void VmDetector_HyperVGuestByWmi()
    {
        var r = VirtualMachineDetector.Evaluate("Microsoft Corporation", "Virtual Machine", null);
        Assert.True(r.IsHyperVGuest);
    }

    [Fact]
    public void VmDetector_HyperVGuestByKvpRegistry()
    {
        var r = VirtualMachineDetector.Evaluate("", "", new Dictionary<string, string> { ["HostName"] = "MY-PC" });
        Assert.True(r.IsHyperVGuest);
        Assert.Equal("MY-PC", r.HostName);
    }

    [Theory]
    [InlineData("Contoso Ltd.", "Desktop Board X1")]
    [InlineData("Microsoft Corporation", "Surface Laptop 5")] // real Microsoft hardware is not a VM
    [InlineData("", "")]
    [InlineData(null, null)]
    public void VmDetector_PhysicalMachine_IsNotGuest(string? manufacturer, string? model)
    {
        Assert.False(VirtualMachineDetector.Evaluate(manufacturer, model, null).IsHyperVGuest);
    }

    [Fact]
    public void VmDetector_EmptyParametersKey_IsNotEnough()
    {
        // On a Hyper-V host the "Guest" key may exist; only Parameters with identification counts.
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

    // ---------- persistence ----------

    [Fact]
    public void Evaluator_ApplyOnly_IgnoresMissingRevert()
    {
        var r = Facts(f => { f.Revert = null; f.Backup.DeletedAfterRevert = null; f.Actions[0].AfterRevert = null; });
        Assert.Equal(SelfTestVerdict.Pass, SelfTestEvaluator.Evaluate(r, applyOnly: true).Verdict);
        Assert.Equal(SelfTestVerdict.RevertFailed, SelfTestEvaluator.Evaluate(r).Verdict);

        var rewritten = Facts(f => { f.Revert = null; f.Backup.KeptOnReapply = false; });
        Assert.Equal(SelfTestVerdict.BackupProblem, SelfTestEvaluator.Evaluate(rewritten, applyOnly: true).Verdict);
        var notApplied = Facts(f => f.StateAfterApply = TweakState.Partial);
        Assert.Equal(SelfTestVerdict.NotAppliedAfterApply, SelfTestEvaluator.Evaluate(notApplied, applyOnly: true).Verdict);
    }

    private static TweakStatus StatusOf(Tweak tweak, params (ActionState State, string Detail)[] actions) =>
        new(tweak, StatusAggregator.Aggregate(actions.Select(a => a.State)),
            tweak.Actions.Zip(actions, (spec, a) => new ActionStatus(spec, a.State, a.Detail)).ToList());

    private static TweakSelfTestResult AppliedFacts(Tweak tweak, params ActionState[] afterApply)
    {
        var r = new TweakSelfTestResult { Id = tweak.Id, Verdict = SelfTestVerdict.Pass, StateAfterApply = TweakState.Applied };
        for (int i = 0; i < tweak.Actions.Count; i++)
        {
            r.Actions.Add(new ActionSelfTest
            {
                Type = tweak.Actions[i].Type,
                Target = tweak.Actions[i].Target,
                AfterApply = new ActionSnapshot { State = afterApply[i], Detail = afterApply[i] == ActionState.Desired ? "desired" : "n/a" },
            });
        }
        return r;
    }

    [Fact]
    public void Persistence_AllStillDesired_IsPersisted()
    {
        var tweak = MakeTweak();
        var r = AppliedFacts(tweak, ActionState.Desired, ActionState.Desired, ActionState.Desired);
        var check = SelfTestEvaluator.RecordPersistence(r,
            StatusOf(tweak, (ActionState.Desired, "a"), (ActionState.Desired, "b"), (ActionState.Desired, "c")), "after-restart-1", DateTime.UtcNow);

        Assert.Equal(PersistenceVerdict.Persisted, check.Verdict);
        Assert.Empty(check.RevertedActions);
        Assert.Equal(PersistenceVerdict.Persisted, r.PersistenceVerdict);
        Assert.Null(r.RevertedAfter);
        Assert.All(r.Actions, a => Assert.Equal("after-restart-1", Assert.Single(a.Checks).Label));
    }

    [Fact]
    public void Persistence_SomeActionsUndone_IsPartiallyReverted_AndNamesThem()
    {
        var tweak = MakeTweak();
        var r = AppliedFacts(tweak, ActionState.Desired, ActionState.Desired, ActionState.Desired);
        var check = SelfTestEvaluator.RecordPersistence(r,
            StatusOf(tweak, (ActionState.Desired, "a"), (ActionState.NotDesired, "current: automatic"), (ActionState.Desired, "c")), "after-wait", DateTime.UtcNow);

        Assert.Equal(PersistenceVerdict.PartiallyReverted, check.Verdict);
        var reverted = Assert.Single(check.RevertedActions);
        Assert.StartsWith("service:DiagTrack", reverted);
        Assert.Contains("current: automatic", reverted);
        Assert.Equal("after-wait", r.RevertedAfter);
    }

    [Fact]
    public void Persistence_AllUndone_IsRevertedByWindows_IgnoringNotApplicableActions()
    {
        var tweak = MakeTweak();
        // The third action did not exist (NotApplicable) on apply: it does not count.
        var r = AppliedFacts(tweak, ActionState.Desired, ActionState.Desired, ActionState.NotApplicable);
        var check = SelfTestEvaluator.RecordPersistence(r,
            StatusOf(tweak, (ActionState.NotDesired, "absent"), (ActionState.NotDesired, "current: manual"), (ActionState.NotApplicable, "n/a")), "x", DateTime.UtcNow);

        Assert.Equal(PersistenceVerdict.RevertedByWindows, check.Verdict);
        Assert.Equal(2, check.RevertedActions.Count);
    }

    [Fact]
    public void Persistence_UnreadableStatus_CountsAsNotPersisted_AndFirstRevertLabelSticks()
    {
        var tweak = MakeTweak();
        var r = AppliedFacts(tweak, ActionState.Desired, ActionState.Desired, ActionState.Desired);
        SelfTestEvaluator.RecordPersistence(r, new TweakStatus(tweak, TweakState.Error, [], "read error"), "one", DateTime.UtcNow);
        Assert.Equal(PersistenceVerdict.RevertedByWindows, r.PersistenceVerdict);
        Assert.Contains("read error", r.Persistence[0].RevertedActions[0]);

        // Then everything is fine again: the current verdict is the last check's; RevertedAfter keeps the first failure.
        SelfTestEvaluator.RecordPersistence(r,
            StatusOf(tweak, (ActionState.Desired, "a"), (ActionState.Desired, "b"), (ActionState.Desired, "c")), "two", DateTime.UtcNow);
        Assert.Equal(PersistenceVerdict.Persisted, r.PersistenceVerdict);
        Assert.Equal("one", r.RevertedAfter);
        Assert.Equal(["one", "two"], r.Persistence.Select(c => c.Label));
    }

    [Fact]
    public async Task PersistencePhases_ApplyOnly_Verify_Revert()
    {
        var host = HostWithOriginals();
        host.World[@"HKCU\X\B"] = "0";
        var tweak = MakeTweak();
        var other = MakeTweak("test.other", RiskLevel.Low, true,
            new ActionSpec { Type = ActionSpec.Registry, Path = @"HKCU\X", Name = "B", Kind = "dword", Value = "1" });
        var byId = new Dictionary<string, Tweak> { [tweak.Id] = tweak, [other.Id] = other };
        var original = new Dictionary<string, string>(host.World);

        // apply-only: applies and re-applies, without reverting.
        var applyRunner = new SelfTestRunner(host, new SelfTestOptions { ApplyOnly = true });
        var results = await applyRunner.RunAsync([tweak, other]);
        Assert.All(results, r => Assert.Equal(SelfTestVerdict.Pass, r.Verdict));
        Assert.All(results, r => Assert.Null(r.Revert));
        Assert.Equal(["apply:test.tweak", "reapply:test.tweak", "apply:test.other", "reapply:test.other"], host.Calls);
        Assert.Equal("1", host.World[@"HKCU\X\B"]);

        var runner = new SelfTestRunner(host);
        await runner.VerifyAsync(results, byId, "after-restart-1");
        Assert.All(results, r => Assert.Equal(PersistenceVerdict.Persisted, r.PersistenceVerdict));

        // "Windows" undoes the first tweak's service and the second tweak's only value.
        host.World["DiagTrack"] = "automatic";
        host.World[@"HKCU\X\B"] = "0";
        await runner.VerifyAsync(results, byId, "after-wait");
        Assert.Equal(PersistenceVerdict.PartiallyReverted, results[0].PersistenceVerdict);
        Assert.Equal("after-wait", results[0].RevertedAfter);
        Assert.Equal(PersistenceVerdict.RevertedByWindows, results[1].PersistenceVerdict);
        Assert.Equal(2, results[0].Actions[1].Checks.Count);

        // revert: everything goes back to the original and the full cycle passes.
        await runner.RevertAppliedAsync(results, byId);
        Assert.All(results, r => Assert.Equal(SelfTestVerdict.Pass, r.Verdict));
        Assert.All(results, r => Assert.True(r.Backup.DeletedAfterRevert));
        Assert.Equal(original, host.World);
        Assert.Equal(2, results[0].Persistence.Count);
    }

    [Fact]
    public async Task PersistencePhases_SkippedAndFailedTweaksAreNotVerifiedOrReverted()
    {
        var host = HostWithOriginals();
        host.ApplyIgnores.Add("DiagTrack"); // apply does not change the service: NotAppliedAfterApply
        var tweak = MakeTweak();
        var irreversible = MakeTweak("test.appx", RiskLevel.Low, false,
            new ActionSpec { Type = ActionSpec.Appx, Name = "Pkg", State = "removed" });
        var byId = new Dictionary<string, Tweak> { [tweak.Id] = tweak, [irreversible.Id] = irreversible };

        var results = await new SelfTestRunner(host, new SelfTestOptions { ApplyOnly = true }).RunAsync([tweak, irreversible]);
        Assert.Equal(SelfTestVerdict.NotAppliedAfterApply, results[0].Verdict);
        Assert.Equal(SelfTestVerdict.Skipped, results[1].Verdict);

        var runner = new SelfTestRunner(host);
        await runner.VerifyAsync(results, byId, "v");
        Assert.All(results, r => Assert.Null(r.PersistenceVerdict));

        host.Calls.Clear();
        await runner.RevertAppliedAsync(results, byId);
        // The one that failed on apply is still reverted (cleanup: there is a backup); the skipped one is not.
        Assert.Equal(["revert:test.tweak"], host.Calls);
        Assert.Equal(SelfTestVerdict.NotAppliedAfterApply, results[0].Verdict);
    }

    [Fact]
    public void Report_PersistenceState_RoundTripsThroughJson()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };
        var tweak = MakeTweak();
        var r = AppliedFacts(tweak, ActionState.Desired, ActionState.Desired, ActionState.Desired);
        r.Backup.WrittenOnApply = true;
        SelfTestEvaluator.RecordPersistence(r,
            StatusOf(tweak, (ActionState.Desired, "a"), (ActionState.NotDesired, "b"), (ActionState.Desired, "c")), "after-restart-1", DateTime.UtcNow);
        var report = new SelfTestReport { Phase = SelfTestPhase.Verify, BackupDirectory = @"C:\b", Results = [r] };
        report.PhaseLog.Add("ApplyOnly x");

        var back = JsonSerializer.Deserialize<SelfTestReport>(JsonSerializer.Serialize(report, options), options)!;

        Assert.Equal(SelfTestPhase.Verify, back.Phase);
        Assert.Equal(@"C:\b", back.BackupDirectory);
        var br = Assert.Single(back.Results);
        Assert.Equal(PersistenceVerdict.PartiallyReverted, br.PersistenceVerdict);
        Assert.Equal("after-restart-1", br.RevertedAfter);
        Assert.Equal("b", br.Actions[1].Checks[0].Snapshot.Detail);
        Assert.Equal(ActionState.Desired, br.Actions[0].AfterApply!.State);
        Assert.True(back.HasPersistenceFailures);
        Assert.Equal(1, back.PersistenceSummary[PersistenceVerdict.PartiallyReverted]);
    }
}
