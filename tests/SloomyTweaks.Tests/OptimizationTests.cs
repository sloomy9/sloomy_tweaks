using SloomyTweaks.Core.Diagnostics;
using SloomyTweaks.Core.Engine;
using SloomyTweaks.Core.Hardware;
using SloomyTweaks.Core.Optimizations;
using SloomyTweaks.Core.Platform;
using SloomyTweaks.Tests.TestFramework;
using Ids = SloomyTweaks.Core.Optimizations.OptimizationCatalog.Ids;

namespace SloomyTweaks.Tests;

public class TokenStringTests
{
    [Fact]
    public void With_replaces_value_and_preserves_other_tokens_in_order()
    {
        string updated = TokenString.With("VRROptimizeEnable=0;SwapEffectUpgradeEnable=0;AutoHDREnable=1;", "SwapEffectUpgradeEnable", "1");
        Assert.Equal("VRROptimizeEnable=0;SwapEffectUpgradeEnable=1;AutoHDREnable=1;", updated);
    }

    [Fact]
    public void With_null_removes_token_and_appends_when_missing()
    {
        Assert.Equal("A=1;", TokenString.With("A=1;B=2;", "B", null));
        Assert.Equal("A=1;B=2;", TokenString.With("A=1;", "B", "2"));
        Assert.Equal("", TokenString.With("B=2;", "B", null));
    }

    [Fact]
    public void Parse_ignores_malformed_parts()
    {
        var t = TokenString.Parse("A=1; junk ;=x;B=;C=3");
        Assert.Equal("1", t["A"]);
        Assert.Equal("", t["B"]);
        Assert.Equal("3", t["C"]);
        Assert.Equal(3, t.Count);
    }
}

public class SettingPrimitiveTests
{
    [Fact]
    public void Dword_absent_value_uses_documented_default_or_unknown()
    {
        var reg = new FakeRegistry();
        var withDefault = new RegistryDwordPrimitive(reg, RegistryRoot.CurrentUser, "K", "V", desired: 1, valueWhenAbsent: 1);
        var unknownDefault = new RegistryDwordPrimitive(reg, RegistryRoot.CurrentUser, "K", "V", desired: 1, valueWhenAbsent: null);
        Assert.Equal(true, withDefault.IsDesired(withDefault.Read()));
        Assert.Equal((bool?)null, unknownDefault.IsDesired(unknownDefault.Read()));
    }

    [Fact]
    public void Dword_restore_of_absent_value_deletes_it()
    {
        var reg = new FakeRegistry();
        var p = new RegistryDwordPrimitive(reg, RegistryRoot.LocalMachine, "K", "V", 2, null);
        SettingValue original = p.Read();
        p.ApplyDesired();
        Assert.Equal(true, p.IsDesired(p.Read()));
        p.Restore(original);
        Assert.Equal(0, reg.Values.Count);
        Assert.True(p.IsRestored(p.Read(), original));
    }

    [Fact]
    public void Token_restore_preserves_tokens_changed_by_the_user_afterwards()
    {
        var reg = new FakeRegistry();
        reg.WriteString(RegistryRoot.CurrentUser, "DX", "S", "VRROptimizeEnable=0;");
        var p = new RegistryStringTokenPrimitive(reg, RegistryRoot.CurrentUser, "DX", "S", "SwapEffectUpgradeEnable", "1", null);
        SettingValue original = p.Read();
        p.ApplyDesired();
        // User later enables Auto HDR through Windows Settings.
        string current = (string)reg.Read(RegistryRoot.CurrentUser, "DX", "S")!.Value;
        reg.WriteString(RegistryRoot.CurrentUser, "DX", "S", current + "AutoHDREnable=1;");
        p.Restore(original);
        Assert.Equal("VRROptimizeEnable=0;AutoHDREnable=1;", (string)reg.Read(RegistryRoot.CurrentUser, "DX", "S")!.Value);
    }

    [Fact]
    public void Pointer_acceleration_restore_is_exact()
    {
        var mouse = new FakeMouse { Value = new MouseParameters(4, 12, 2) };
        var p = new PointerAccelerationPrimitive(mouse);
        SettingValue original = p.Read();
        p.ApplyDesired();
        Assert.Equal(new MouseParameters(4, 12, 0), mouse.Value);
        p.Restore(original);
        Assert.Equal(new MouseParameters(4, 12, 2), mouse.Value);
    }
}

public class CatalogTests
{
    private static IReadOnlyList<Optimization> Catalog() => OptimizationCatalog.Create(new FakeRegistry(), new FakePower(), new FakeMouse());

    [Fact]
    public void Every_actionable_or_guidance_item_cites_at_least_one_https_source()
    {
        foreach (Optimization o in Catalog().Where(o => o.Definition.Kind != OptimizationKind.NotOffered))
        {
            Assert.NotEmpty(o.Definition.Sources);
            foreach (EvidenceSource s in o.Definition.Sources)
                Assert.True(s.Url.StartsWith("https://", StringComparison.Ordinal), $"{o.Id}: {s.Url}");
        }
    }

    [Fact]
    public void Not_offered_items_are_not_recommended_and_have_no_actions()
    {
        foreach (Optimization o in Catalog().Where(o => o.Definition.Kind == OptimizationKind.NotOffered))
        {
            Assert.Equal(EvidenceLevel.NotRecommended, o.Definition.Evidence);
            Assert.Empty(o.Primitives);
            Assert.False(o.Definition.EligibleForRecommendedFlow);
        }
    }

    [Fact]
    public void Recommended_flow_contains_only_verified_or_documented_actionable_items()
    {
        foreach (Optimization o in Catalog().Where(o => o.Definition.EligibleForRecommendedFlow))
        {
            Assert.Equal(OptimizationKind.Actionable, o.Definition.Kind);
            Assert.True(o.Definition.Evidence is EvidenceLevel.Verified or EvidenceLevel.Documented, o.Id);
        }
        Assert.False(Catalog().Single(o => o.Id == Ids.HighPerformancePlan).Definition.EligibleForRecommendedFlow);
    }

    [Fact]
    public void Actionable_items_never_claim_numeric_gains()
    {
        foreach (Optimization o in Catalog())
        {
            string text = o.Definition.ExpectedBenefit;
            Assert.False(System.Text.RegularExpressions.Regex.IsMatch(text, @"\d+\s*(%|fps|FPS|ms)"), $"{o.Id} states a number: {text}");
        }
    }

    [Fact]
    public void Catalog_does_not_include_security_or_update_disabling_actions()
    {
        foreach (Optimization o in Catalog().Where(o => o.Definition.Kind == OptimizationKind.Actionable))
        {
            foreach (ISettingPrimitive p in o.Primitives)
            {
                Assert.False(p.Id.Contains("Defender", StringComparison.OrdinalIgnoreCase), p.Id);
                Assert.False(p.Id.Contains("WindowsUpdate", StringComparison.OrdinalIgnoreCase), p.Id);
                Assert.False(p.Id.Contains("FeatureSettingsOverride", StringComparison.OrdinalIgnoreCase), p.Id);
            }
        }
    }
}

public class CompatibilityTests
{
    private readonly FakePower _power = new();
    private IReadOnlyList<Optimization> Catalog() => OptimizationCatalog.Create(new FakeRegistry(), _power, new FakeMouse());
    private Optimization Get(string id) => Catalog().Single(o => o.Id == id);

    [Fact]
    public void Hags_requires_acknowledgement_because_driver_support_is_unverified()
    {
        CompatibilityResult r = Get(Ids.Hags).CheckCompatibility(TestSystems.Win11());
        Assert.Equal(CompatibilityStatus.Unverified, r.Status);
        Assert.True(r.RequiresAcknowledgement);
    }

    [Fact]
    public void Hags_is_unsupported_without_a_supported_gpu_vendor()
    {
        var ctx = TestSystems.Win11() with { Gpus = new[] { new GpuInfo("Microsoft Basic Display Adapter", GpuVendor.Microsoft, null, null) } };
        Assert.Equal(CompatibilityStatus.Unsupported, Get(Ids.Hags).CheckCompatibility(ctx).Status);
    }

    [Fact]
    public void Windowed_optimizations_require_22H2()
    {
        Assert.Equal(CompatibilityStatus.Unsupported, Get(Ids.WindowedGameOptimizations).CheckCompatibility(TestSystems.Win11(build: 22000)).Status);
        Assert.Equal(CompatibilityStatus.Compatible, Get(Ids.WindowedGameOptimizations).CheckCompatibility(TestSystems.Win11(build: 22631)).Status);
    }

    [Fact]
    public void High_performance_plan_warns_on_x3d_and_hybrid_and_fails_when_plan_missing()
    {
        var x3d = Get(Ids.HighPerformancePlan).CheckCompatibility(TestSystems.Win11());
        Assert.Equal(CompatibilityStatus.CompatibleWithWarnings, x3d.Status);
        Assert.Contains("X3D", string.Join(" ", x3d.Reasons));

        var intel = Get(Ids.HighPerformancePlan).CheckCompatibility(TestSystems.Win11(cpu: "Intel(R) Core(TM) i7-14700K", vendor: CpuVendor.Intel, hybrid: true));
        Assert.Contains("Hybrid", string.Join(" ", intel.Reasons));

        _power.Schemes.RemoveAll(s => s.Id == OptimizationCatalog.HighPerformanceSchemeGuid);
        Assert.Equal(CompatibilityStatus.Unsupported, Get(Ids.HighPerformancePlan).CheckCompatibility(TestSystems.Win11()).Status);
    }

    [Fact]
    public void Game_mode_warns_on_ltsc_with_x3d()
    {
        var r = Get(Ids.GameMode).CheckCompatibility(TestSystems.Win11(edition: "EnterpriseS"));
        Assert.Equal(CompatibilityStatus.CompatibleWithWarnings, r.Status);
        Assert.Contains("Game Bar", r.Reasons[0]);
    }
}

public class OptimizationEngineTests
{
    private readonly FakeRegistry _registry = new();
    private readonly FakePower _power = new();
    private readonly FakeMouse _mouse = new();
    private readonly FakePrivileges _privileges = new(elevated: true);
    private readonly InMemoryBackupStore _backups = new();
    private readonly InMemoryHistoryStore _history = new();
    private readonly FakeClock _clock = new();
    private readonly FakeRestorePoints _restorePoints = new();
    private SystemContext _context = TestSystems.Win11();

    private OptimizationEngine Engine(IEnumerable<Optimization>? catalog = null) => new(
        catalog ?? OptimizationCatalog.Create(_registry, _power, _mouse), () => _context, _privileges, _backups, _history, _clock, NullLogger.Instance, _restorePoints);

    [Fact]
    public async Task Apply_saves_original_verifies_and_records_history()
    {
        var engine = Engine();
        Assert.Equal(OptimizationState.Disabled, engine.GetStatus(Ids.PointerPrecision).State);

        OperationResult r = await engine.ApplyAsync(Ids.PointerPrecision, warningsAcknowledged: false);

        Assert.Equal(OperationOutcome.Succeeded, r.Outcome);
        Assert.Equal(0, _mouse.Value.Acceleration);
        Assert.Equal(OptimizationState.Enabled, engine.GetStatus(Ids.PointerPrecision).State);
        Assert.NotNull(_backups.Get(Ids.PointerPrecision));
        Assert.True(_history.LatestSuccessful(Ids.PointerPrecision)!.Success);
        Assert.Contains("Verified", string.Join("\n", r.Steps));
    }

    [Fact]
    public async Task Apply_then_restore_returns_registry_to_its_exact_original_state()
    {
        var engine = Engine();
        _registry.WriteDword(RegistryRoot.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled", 1);
        Assert.Equal(OptimizationState.Disabled, engine.GetStatus(Ids.BackgroundRecording).State);

        Assert.True((await engine.ApplyAsync(Ids.BackgroundRecording, false)).Success);
        OperationResult restore = await engine.RestoreAsync(Ids.BackgroundRecording);

        Assert.Equal(OperationOutcome.Succeeded, restore.Outcome);
        Assert.Equal(1u, (uint)_registry.Read(RegistryRoot.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled")!.Value);
        Assert.Null(_backups.Get(Ids.BackgroundRecording));
    }

    [Fact]
    public async Task Already_enabled_makes_no_changes()
    {
        // Game Mode is on by default when the value is absent.
        OperationResult r = await Engine().ApplyAsync(Ids.GameMode, false);
        Assert.Equal(OperationOutcome.AlreadyInDesiredState, r.Outcome);
        Assert.Equal(0, _registry.Writes);
        Assert.Null(_backups.Get(Ids.GameMode));
    }

    [Fact]
    public async Task Unverified_compatibility_requires_acknowledgement_then_admin()
    {
        var engine = Engine();
        Assert.Equal(OperationOutcome.AcknowledgementRequired, (await engine.ApplyAsync(Ids.Hags, false)).Outcome);
        _privileges.IsElevated = false;
        Assert.Equal(OperationOutcome.AdministratorRequired, (await engine.ApplyAsync(Ids.Hags, true)).Outcome);
        Assert.Equal(0, _registry.Writes);

        _privileges.IsElevated = true;
        OperationResult ok = await engine.ApplyAsync(Ids.Hags, true);
        Assert.Equal(OperationOutcome.Succeeded, ok.Outcome);
        Assert.True(ok.RestartRequired);
        Assert.Equal(1, _restorePoints.Calls);
    }

    [Fact]
    public async Task Restart_pending_is_reported_until_the_next_boot()
    {
        var engine = Engine();
        await engine.ApplyAsync(Ids.Hags, true);
        Assert.Equal(OptimizationState.RestartPending, engine.GetStatus(Ids.Hags).State);

        _context = _context with { LastBootTime = _clock.UtcNow.AddMinutes(5) };
        Assert.Equal(OptimizationState.Enabled, engine.GetStatus(Ids.Hags).State);
    }

    [Fact]
    public async Task Unsupported_build_is_blocked_without_writing()
    {
        _context = TestSystems.Win11(build: 22000);
        OperationResult r = await Engine().ApplyAsync(Ids.WindowedGameOptimizations, true);
        Assert.Equal(OperationOutcome.Unsupported, r.Outcome);
        Assert.Equal(0, _registry.Writes);
    }

    [Fact]
    public async Task Failure_part_way_rolls_back_already_applied_primitives()
    {
        var def = TestDefinition("test.two-step");
        var first = new RegistryDwordPrimitive(_registry, RegistryRoot.CurrentUser, "T", "First", 1, null);
        var second = new RegistryDwordPrimitive(_registry, RegistryRoot.CurrentUser, "T", "Second", 1, null);
        _registry.WriteDword(RegistryRoot.CurrentUser, "T", "First", 7);
        _registry.FailWrite = key => key.EndsWith("Second", StringComparison.Ordinal);

        var engine = Engine(new[] { new Optimization(def, new ISettingPrimitive[] { first, second }) });
        OperationResult r = await engine.ApplyAsync(def.Id, true);

        Assert.Equal(OperationOutcome.FailedAndRolledBack, r.Outcome);
        Assert.Equal(7u, (uint)_registry.Read(RegistryRoot.CurrentUser, "T", "First")!.Value);
        Assert.Contains("UnauthorizedAccessException", r.ErrorDetail);
        Assert.Null(_backups.Get(def.Id));
        Assert.False(_history.List().First().Success);
    }

    [Fact]
    public async Task Verification_failure_is_reported_and_never_shown_as_success()
    {
        _registry.IgnoreWrites = true; // e.g. a Group Policy enforcing the value
        OperationResult r = await Engine().ApplyAsync(Ids.BackgroundRecording, true);
        // Absent value means "off" which is already desired → set it to on first through a separate registry.
        Assert.Equal(OperationOutcome.AlreadyInDesiredState, r.Outcome);

        _registry.IgnoreWrites = false;
        _registry.WriteDword(RegistryRoot.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled", 1);
        _registry.IgnoreWrites = true;
        r = await Engine().ApplyAsync(Ids.BackgroundRecording, true);
        Assert.Equal(OperationOutcome.VerificationFailed, r.Outcome);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Restore_without_backup_reports_no_backup()
    {
        OperationResult r = await Engine().RestoreAsync(Ids.PointerPrecision);
        Assert.Equal(OperationOutcome.NoBackup, r.Outcome);
    }

    [Fact]
    public async Task Reapplying_keeps_the_earliest_original_values()
    {
        var engine = Engine();
        await engine.ApplyAsync(Ids.PointerPrecision, false);
        _mouse.Value = _mouse.Value with { Acceleration = 1 }; // user turned it back on manually
        await engine.ApplyAsync(Ids.PointerPrecision, false);
        await engine.RestoreAsync(Ids.PointerPrecision);
        Assert.Equal(new MouseParameters(6, 10, 1), _mouse.Value);
    }

    [Fact]
    public async Task Conflicting_optimization_blocks_apply()
    {
        var a = new Optimization(TestDefinition("test.a"), new ISettingPrimitive[] { new RegistryDwordPrimitive(_registry, RegistryRoot.CurrentUser, "C", "A", 1, 0) });
        var b = new Optimization(TestDefinition("test.b") with { ConflictsWith = new[] { "test.a" } },
            new ISettingPrimitive[] { new RegistryDwordPrimitive(_registry, RegistryRoot.CurrentUser, "C", "B", 1, 0) });
        var engine = Engine(new[] { a, b });
        await engine.ApplyAsync("test.a", true);
        OperationResult r = await engine.ApplyAsync("test.b", true);
        Assert.Equal(OperationOutcome.Conflict, r.Outcome);
    }

    [Fact]
    public async Task Guidance_items_cannot_be_applied()
    {
        OperationResult r = await Engine().ApplyAsync(Ids.NvidiaReflex, true);
        Assert.Equal(OperationOutcome.NotActionable, r.Outcome);
    }

    [Fact]
    public async Task Windowed_optimization_restore_keeps_unrelated_directx_tokens()
    {
        const string path = @"Software\Microsoft\DirectX\UserGpuPreferences";
        _registry.WriteString(RegistryRoot.CurrentUser, path, "DirectXUserGlobalSettings", "VRROptimizeEnable=0;");
        var engine = Engine();
        Assert.Equal(OptimizationState.Unknown, engine.GetStatus(Ids.WindowedGameOptimizations).State);
        Assert.True((await engine.ApplyAsync(Ids.WindowedGameOptimizations, true)).Success);
        Assert.Equal("VRROptimizeEnable=0;SwapEffectUpgradeEnable=1;", (string)_registry.Read(RegistryRoot.CurrentUser, path, "DirectXUserGlobalSettings")!.Value);
        Assert.True((await engine.RestoreAsync(Ids.WindowedGameOptimizations)).Success);
        Assert.Equal("VRROptimizeEnable=0;", (string)_registry.Read(RegistryRoot.CurrentUser, path, "DirectXUserGlobalSettings")!.Value);
    }

    private static OptimizationDefinition TestDefinition(string id) => new()
    {
        Id = id,
        Name = id,
        Category = OptimizationCategory.Windows,
        Kind = OptimizationKind.Actionable,
        Description = "test",
        TechnicalMechanism = "test",
        ExpectedBenefit = "test",
        Evidence = EvidenceLevel.Documented,
        EvidenceNotes = "test",
        Sources = Array.Empty<EvidenceSource>(),
        Risks = Array.Empty<string>(),
        CompatibilitySummary = "test",
    };
}
