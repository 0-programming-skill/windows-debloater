using System.Runtime.InteropServices;
using Debloater.Core.Actions;
using Debloater.Core.Catalog;
using Debloater.Core.Engine;
using Debloater.Core.Inventory;
using Debloater.Core.Model;
using Debloater.Core.Platform;
using Debloater.Core.Platform.FeatureFlags;
using Native = Debloater.Core.Platform.FeatureFlags.FeatureConfigurationNative;

namespace Debloater.Core.Tests;

/// <summary>In-memory feature store: runtime layers per (id, priority) and registry overrides.</summary>
internal sealed class FakeFeatureStore : IFeatureStore
{
    public Dictionary<(uint Id, FeaturePriority P), FeatureConfiguration> Runtime { get; } = [];
    public Dictionary<(uint Id, FeaturePriority P), FeatureConfiguration> Boot { get; } = [];
    public Dictionary<(uint Id, FeaturePriority P), SortedDictionary<string, uint>> Registry { get; } = [];
    public List<FeatureConfigurationUpdate> Updates { get; } = [];
    public bool FailRuntime { get; set; }

    public void AddLayer(uint id, FeaturePriority p, FeatureEnabledState s, bool boot = true)
    {
        Runtime[(id, p)] = new FeatureConfiguration(id, p, s);
        if (boot) Boot[(id, p)] = new FeatureConfiguration(id, p, s);
    }

    private Dictionary<(uint Id, FeaturePriority P), FeatureConfiguration> Store(FeatureConfigurationType t) =>
        t == FeatureConfigurationType.Runtime ? Runtime : Boot;

    public FeatureConfiguration? Query(uint featureId, FeatureConfigurationType type = FeatureConfigurationType.Runtime) =>
        Store(type).Values.Where(c => c.FeatureId == featureId).MaxBy(c => (int)c.Priority);

    public IReadOnlyList<FeatureConfiguration> QueryAll(FeatureConfigurationType type = FeatureConfigurationType.Runtime) =>
        Store(type).Values.OrderBy(c => c.FeatureId).ThenBy(c => (int)c.Priority).ToList();

    public void SetRuntime(FeatureConfigurationUpdate update)
    {
        if (FailRuntime) throw new InvalidOperationException("simulated failure");
        Updates.Add(update);
        if (update.Reset) Runtime.Remove((update.FeatureId, update.Priority));
        else Runtime[(update.FeatureId, update.Priority)] = new FeatureConfiguration(update.FeatureId, update.Priority, update.State);
    }

    public FeatureOverride? ReadOverride(FeaturePriority priority, uint featureId) =>
        Registry.TryGetValue((featureId, priority), out var v) ? new FeatureOverride(featureId, priority, new SortedDictionary<string, uint>(v, StringComparer.Ordinal)) : null;

    public void WriteOverride(FeatureOverride value) =>
        Registry[(value.FeatureId, value.Priority)] = new SortedDictionary<string, uint>(value.Values.ToDictionary(), StringComparer.Ordinal);

    public void DeleteOverride(FeaturePriority priority, uint featureId) => Registry.Remove((featureId, priority));
}

public sealed class FeatureFlagTests : IDisposable
{
    private readonly string _backupDir = Path.Combine(Path.GetTempPath(), "DebloaterTests", Guid.NewGuid().ToString("N"));
    private readonly FakeFeatureStore _store = new();

    public void Dispose()
    {
        if (Directory.Exists(_backupDir)) Directory.Delete(_backupDir, recursive: true);
    }

    private TweakEngine Engine() => new(new TweakEngineOptions
    {
        BackupDirectory = _backupDir,
        RequireElevation = false,
        System = SystemInfo.Current with { Build = 26300 },
        FeatureStore = _store,
    });

    private static Tweak FlagTweak(uint id, string state, string? priority = null) => new()
    {
        Id = "test.flag",
        Name = "Flag",
        Description = "d",
        Category = "Test",
        Risk = RiskLevel.Medium,
        Actions = [new ActionSpec { Type = ActionSpec.FeatureFlag, FeatureId = id, State = state, Priority = priority }],
    };

    // ---------------- native layout ----------------

    [Fact]
    public void NativeStructs_HaveTheNtdllLayout()
    {
        Assert.Equal(12, Marshal.SizeOf<Native.RtlFeatureConfiguration>());
        Assert.Equal(4, (int)Marshal.OffsetOf<Native.RtlFeatureConfiguration>(nameof(Native.RtlFeatureConfiguration.Flags)));
        Assert.Equal(8, (int)Marshal.OffsetOf<Native.RtlFeatureConfiguration>(nameof(Native.RtlFeatureConfiguration.VariantPayload)));

        Assert.Equal(32, Marshal.SizeOf<Native.RtlFeatureConfigurationUpdate>());
        Assert.Equal(4, (int)Marshal.OffsetOf<Native.RtlFeatureConfigurationUpdate>(nameof(Native.RtlFeatureConfigurationUpdate.Priority)));
        Assert.Equal(8, (int)Marshal.OffsetOf<Native.RtlFeatureConfigurationUpdate>(nameof(Native.RtlFeatureConfigurationUpdate.EnabledState)));
        Assert.Equal(12, (int)Marshal.OffsetOf<Native.RtlFeatureConfigurationUpdate>(nameof(Native.RtlFeatureConfigurationUpdate.EnabledStateOptions)));
        Assert.Equal(16, (int)Marshal.OffsetOf<Native.RtlFeatureConfigurationUpdate>(nameof(Native.RtlFeatureConfigurationUpdate.Variant)));
        Assert.Equal(20, (int)Marshal.OffsetOf<Native.RtlFeatureConfigurationUpdate>(nameof(Native.RtlFeatureConfigurationUpdate.VariantPayloadKind)));
        Assert.Equal(24, (int)Marshal.OffsetOf<Native.RtlFeatureConfigurationUpdate>(nameof(Native.RtlFeatureConfigurationUpdate.VariantPayload)));
        Assert.Equal(28, (int)Marshal.OffsetOf<Native.RtlFeatureConfigurationUpdate>(nameof(Native.RtlFeatureConfigurationUpdate.Operation)));
    }

    [Theory]
    // Values observed through the API on 26200/26300.
    [InlineData(0x64u, FeaturePriority.Service, FeatureEnabledState.Enabled, true, false, 0)]
    [InlineData(0xE4u, FeaturePriority.Service, FeatureEnabledState.Enabled, true, true, 0)]
    [InlineData(0x1Fu, FeaturePriority.ImageOverride, FeatureEnabledState.Disabled, false, false, 0)]
    [InlineData(0x19u, FeaturePriority.Security, FeatureEnabledState.Disabled, false, false, 0)]
    [InlineData(0x2728u, FeaturePriority.User, FeatureEnabledState.Enabled, false, false, 39)]
    public void Flags_Unpack(uint flags, FeaturePriority priority, FeatureEnabledState state, bool wexp, bool subscriptions, int variant)
    {
        var c = FeatureConfiguration.From(new Native.RtlFeatureConfiguration { FeatureId = 1, Flags = flags, VariantPayload = 7 });
        Assert.Equal(priority, c.Priority);
        Assert.Equal(state, c.State);
        Assert.Equal(wexp, c.IsWexpConfiguration);
        Assert.Equal(subscriptions, c.HasSubscriptions);
        Assert.Equal(variant, c.Variant);
        Assert.Equal(7u, c.VariantPayload);
        Assert.Equal(flags, Native.PackFlags(priority, state, wexp, subscriptions, variant));
    }

    [Theory]
    // Registry key name -> feature id, measured in the 26300 VM (keys written by hand, ids read back after a reboot).
    [InlineData(1u, 0x447F3780u)]
    [InlineData(2u, 0x457F3700u)]
    [InlineData(0x80000000u, 0x447F3740u)]
    [InlineData(12345678u, 3813632256u)]
    [InlineData(61825274u, 0x39CFE001u)]
    [InlineData(0xFFFFFFFFu, 0xBB80C8FFu)]
    public void OverrideKey_DecodesLikeWindows(uint keyName, uint featureId)
    {
        Assert.Equal(featureId, FeatureOverrideKey.Decode(keyName));
        Assert.Equal(keyName, FeatureOverrideKey.Encode(featureId));
    }

    [Fact]
    public void OverrideKey_RoundTrips()
    {
        var rng = new Random(42);
        for (int i = 0; i < 1000; i++)
        {
            uint id = (uint)rng.NextInt64(0, uint.MaxValue);
            Assert.Equal(id, FeatureOverrideKey.Decode(FeatureOverrideKey.Encode(id)));
        }
    }

    // ---------------- action: detect / backup / apply / restore ----------------

    [Fact]
    public async Task ApplyAndRevert_FromAbsent_RemovesEverything()
    {
        _store.AddLayer(58336780, FeaturePriority.Service, FeatureEnabledState.Disabled);
        var engine = Engine();
        var tweak = FlagTweak(58336780, "enabled");

        var before = await engine.GetStatusAsync(tweak);
        Assert.Equal(TweakState.NotApplied, before.State);

        var applied = await engine.ApplyAsync(tweak, RiskLevel.Medium);
        Assert.True(applied.Success, string.Join("; ", applied.Errors));
        Assert.Equal(TweakState.Applied, applied.StatusAfter!.State);
        Assert.Equal(2u, _store.Registry[(58336780, FeaturePriority.User)]["EnabledState"]);
        Assert.Equal(0u, _store.Registry[(58336780, FeaturePriority.User)]["EnabledStateOptions"]);
        Assert.Equal(FeatureEnabledState.Enabled, _store.Query(58336780)!.State);
        Assert.Contains(applied.Notes, n => n.Contains("restart", StringComparison.OrdinalIgnoreCase));

        var backup = engine.Backups.Load("test.flag")!;
        var entry = Assert.Single(backup.Actions);
        Assert.False(entry.Existed);
        Assert.Equal("58336780@user", entry.Target);

        // Windows adds TelemetryFlags after loading the override at boot; revert must still remove the whole key.
        _store.Registry[(58336780, FeaturePriority.User)]["TelemetryFlags"] = 1;

        var reverted = await engine.RevertAsync(tweak);
        Assert.True(reverted.Success, string.Join("; ", reverted.Errors));
        Assert.False(_store.Registry.ContainsKey((58336780, FeaturePriority.User)));
        Assert.False(_store.Runtime.ContainsKey((58336780, FeaturePriority.User)));
        Assert.Equal(FeaturePriority.Service, _store.Query(58336780)!.Priority);
        Assert.Equal(before.Actions[0].Detail, reverted.StatusAfter!.Actions[0].Detail);
        Assert.Null(engine.Backups.Load("test.flag"));
    }

    [Fact]
    public async Task ApplyAndRevert_RestoresPreviousOverrideExactly()
    {
        var original = new SortedDictionary<string, uint>(StringComparer.Ordinal)
        {
            ["EnabledState"] = 1, ["EnabledStateOptions"] = 0, ["Variant"] = 3, ["VariantPayload"] = 9, ["VariantPayloadKind"] = 1,
        };
        _store.Registry[(61532758, FeaturePriority.User)] = new(original, StringComparer.Ordinal);
        _store.AddLayer(61532758, FeaturePriority.User, FeatureEnabledState.Disabled);
        var engine = Engine();
        var tweak = FlagTweak(61532758, "enabled");

        var applied = await engine.ApplyAsync(tweak, RiskLevel.Medium);
        Assert.True(applied.Success);
        // Other values of the existing key are kept; only the state changes.
        Assert.Equal(2u, _store.Registry[(61532758, FeaturePriority.User)]["EnabledState"]);
        Assert.Equal(3u, _store.Registry[(61532758, FeaturePriority.User)]["Variant"]);

        // Re-apply must keep the original backup.
        var first = File.ReadAllText(Path.Combine(_backupDir, "test.flag.json"));
        await engine.ApplyAsync(tweak, RiskLevel.Medium);
        Assert.Equal(first, File.ReadAllText(Path.Combine(_backupDir, "test.flag.json")));

        var reverted = await engine.RevertAsync(tweak);
        Assert.True(reverted.Success, string.Join("; ", reverted.Errors));
        Assert.Equal(original, _store.Registry[(61532758, FeaturePriority.User)]);
        Assert.Equal(FeatureEnabledState.Disabled, _store.Runtime[(61532758, FeaturePriority.User)].State);
    }

    [Fact]
    public async Task DefaultState_RemovesOverride_AndRevertPutsItBack()
    {
        _store.Registry[(1234, FeaturePriority.User)] = new(StringComparer.Ordinal) { ["EnabledState"] = 2, ["EnabledStateOptions"] = 0 };
        _store.AddLayer(1234, FeaturePriority.User, FeatureEnabledState.Enabled);
        var engine = Engine();
        var tweak = FlagTweak(1234, "default");

        Assert.Equal(TweakState.NotApplied, (await engine.GetStatusAsync(tweak)).State);
        var applied = await engine.ApplyAsync(tweak, RiskLevel.Medium);
        Assert.Equal(TweakState.Applied, applied.StatusAfter!.State);
        Assert.Empty(_store.Registry);
        Assert.Contains(_store.Updates, u => u.Reset && u.FeatureId == 1234);

        await engine.RevertAsync(tweak);
        Assert.Equal(2u, _store.Registry[(1234, FeaturePriority.User)]["EnabledState"]);
        Assert.Equal(FeatureEnabledState.Enabled, _store.Query(1234)!.State);
    }

    [Fact]
    public async Task Detect_WarnsWhenAHigherPriorityWins()
    {
        _store.AddLayer(58336780, FeaturePriority.Security, FeatureEnabledState.Disabled);
        var engine = Engine();
        var tweak = FlagTweak(58336780, "enabled");
        var applied = await engine.ApplyAsync(tweak, RiskLevel.Medium);

        var detail = applied.StatusAfter!.Actions[0].Detail;
        Assert.Equal(TweakState.Applied, applied.StatusAfter.State);
        Assert.Contains("overridden by the security priority", detail);

        // With userPolicy (10) the override wins over security (9).
        var tweak2 = FlagTweak(58336780, "enabled", "userPolicy");
        tweak2.Id = "test.flag2";
        var applied2 = await engine.ApplyAsync(tweak2, RiskLevel.Medium);
        Assert.DoesNotContain("overridden", applied2.StatusAfter!.Actions[0].Detail);
        Assert.Equal(FeaturePriority.UserPolicy, _store.Query(58336780)!.Priority);
    }

    [Fact]
    public async Task RuntimeFailure_StillPersists_WithANote()
    {
        _store.FailRuntime = true;
        var engine = Engine();
        var applied = await engine.ApplyAsync(FlagTweak(77, "disabled"), RiskLevel.Medium);
        Assert.True(applied.Success);
        Assert.Equal(1u, _store.Registry[(77, FeaturePriority.User)]["EnabledState"]);
        Assert.Contains(applied.Notes, n => n.Contains("after a restart"));
        Assert.Contains("takes effect after a restart", applied.StatusAfter!.Actions[0].Detail);
    }

    [Fact]
    public void Status_UsesKnownBuiltInDefault()
    {
        var unconfigured = FeatureFlagStatus.Read(_store, 61825274);
        Assert.True(unconfigured.IsEnabled);
        Assert.True(unconfigured.IsBuiltInDefault);
        Assert.Contains("built-in default", unconfigured.ToString());

        _store.AddLayer(61825274, FeaturePriority.ImageOverride, FeatureEnabledState.Disabled);
        var configured = FeatureFlagStatus.Read(_store, 61825274);
        Assert.False(configured.IsEnabled);
        Assert.Contains("imageOverride", configured.ToString());

        Assert.Null(FeatureFlagStatus.Read(_store, 999).IsEnabled);
    }

    // ---------------- inventory ----------------

    [Fact]
    public void Inventory_OneItemPerFeature_WithLayers()
    {
        _store.AddLayer(61754985, FeaturePriority.Service, FeatureEnabledState.Enabled);
        _store.AddLayer(61754985, FeaturePriority.Security, FeatureEnabledState.Disabled);
        _store.AddLayer(5, FeaturePriority.User, FeatureEnabledState.Enabled, boot: false);

        var section = InventoryCollector.CollectFeatureFlags(_store);
        Assert.Equal(2, section.Items.Count);
        var known = section.Items.Single(i => i.Id == "61754985");
        Assert.Equal("disabled", known.Properties["state"]);
        Assert.Equal("security", known.Properties["priority"]);
        Assert.Equal("security: disabled, service: enabled", known.Properties["layers"]);
        Assert.False(known.Properties.ContainsKey("boot"));
        Assert.NotNull(known.DisplayName);
        var runtimeOnly = section.Items.Single(i => i.Id == "5");
        Assert.Equal("absent", runtimeOnly.Properties["boot"]);

        var tweak = FlagTweak(61754985, "disabled");
        CatalogCrossReference.Annotate(section, [tweak]);
        Assert.Equal(["test.flag"], known.TweakIds);
        Assert.Empty(runtimeOnly.TweakIds);
    }

    // ---------------- YAML / validation ----------------

    private static CatalogLoadResult LoadOne(string tweakYaml) =>
        CatalogLoader.Load([("t.yaml", () => "category: Test\ntweaks:\n" + tweakYaml)]);

    private const string Head = """
          - id: test.flag
            name: N
            description: D
            risk: medium

        """;

    [Fact]
    public void Yaml_ParsesFeatureFlagAndRequiresFeature()
    {
        var r = LoadOne(Head + """
                requiresFeature: [61825274, 58336780]
                actions:
                  - { type: featureFlag, id: 58336780, state: enabled, priority: userPolicy }
            """);
        Assert.True(r.IsValid, string.Join("\n", r.Errors));
        var t = Assert.Single(r.Tweaks);
        Assert.Equal([61825274u, 58336780u], t.RequiresFeature);
        var a = Assert.Single(t.Actions);
        Assert.Equal(58336780u, a.FeatureId);
        Assert.Equal("userPolicy", a.Priority);
        Assert.Equal("58336780@userPolicy", a.Target);
    }

    [Theory]
    [InlineData("{ type: featureFlag, id: 1, state: enabled, path: X }", "does not use the field(s) 'path'")]
    [InlineData("{ type: featureFlag, state: enabled }", "requires the field 'id'")]
    [InlineData("{ type: featureFlag, id: 0, state: enabled }", "positive")]
    [InlineData("{ type: featureFlag, id: 1, state: on }", "invalid 'state'")]
    [InlineData("{ type: featureFlag, id: 1, state: enabled, priority: imageOverride }", "invalid 'priority'")]
    [InlineData("{ type: featureFlag, id: -1, state: enabled }", "invalid YAML")]
    [InlineData("{ type: registry, path: HKCU\\\\X, name: V, kind: dword, value: 1, id: 5 }", "does not use the field(s) 'id'")]
    [InlineData("{ type: service, name: X, startup: disabled, state: disabled }", "does not use the field(s) 'state'")]
    public void Yaml_RejectsInvalidActions(string action, string expected)
    {
        var r = LoadOne(Head + "    actions:\n      - " + action + "\n");
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Message.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Yaml_RejectsUnknownFieldAnywhere()
    {
        var r = LoadOne(Head + "    actions:\n      - { type: featureFlag, id: 1, state: enabled, variant: 2 }\n");
        Assert.False(r.IsValid);
    }

    [Fact]
    public void Validation_FeatureFlagNeedsAtLeastMediumRisk()
    {
        var r = LoadOne(Head.Replace("risk: medium", "risk: low") + "    actions:\n      - { type: featureFlag, id: 1, state: enabled }\n");
        Assert.Contains(r.Errors, e => e.Message.Contains("at least medium"));
    }

    [Theory]
    [InlineData("[]", "empty")]
    [InlineData("[0]", "positive")]
    [InlineData("[5, 5]", "twice")]
    public void Validation_RequiresFeature(string list, string expected)
    {
        var r = LoadOne(Head + $"    requiresFeature: {list}\n    actions:\n      - {{ type: featureFlag, id: 1, state: enabled }}\n");
        Assert.Contains(r.Errors, e => e.Message.Contains(expected));
    }
}
