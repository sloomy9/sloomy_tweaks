using SloomyTweaks.Core.Diagnostics;
using SloomyTweaks.Core.Engine;
using SloomyTweaks.Core.Hardware;
using SloomyTweaks.Core.Licensing;
using SloomyTweaks.Core.Optimizations;
using SloomyTweaks.Core.Platform;
using SloomyTweaks.Data;
using SloomyTweaks.Platform.Windows;
using SloomyTweaks.Tests.TestFramework;
using Ids = SloomyTweaks.Core.Optimizations.OptimizationCatalog.Ids;

namespace SloomyTweaks.Tests;

/// <summary>Primitives added in 0.2.0. All run against fakes (no real Windows APIs).</summary>
public class NewPrimitiveTests
{
    [Fact]
    public void Accessibility_shortcuts_clear_and_restore_only_the_hotkey_bits()
    {
        var a11y = new FakeAccessibility { Value = new AccessibilityFlags(510, 126, 62) };
        var p = new AccessibilityShortcutsPrimitive(a11y);
        SettingValue original = p.Read();
        Assert.Equal(false, p.IsDesired(original));

        p.ApplyDesired();
        Assert.Equal(true, p.IsDesired(p.Read()));
        Assert.False(a11y.Value.AnyShortcutActive);

        // The user changes an unrelated Sticky Keys option (bit 0x1) afterwards; restore must keep it.
        a11y.Value = a11y.Value with { StickyKeys = a11y.Value.StickyKeys ^ 0x1 };
        uint userSticky = a11y.Value.StickyKeys;
        p.Restore(original);
        Assert.True(p.IsRestored(p.Read(), original));
        Assert.Equal(userSticky | AccessibilityFlags.HotkeyActive, a11y.Value.StickyKeys);
        Assert.Equal(126u, a11y.Value.FilterKeys);
    }

    [Fact]
    public void Accessibility_flags_round_trip_and_reject_garbage()
    {
        Assert.True(AccessibilityFlags.TryParse("510,126,62", out var f));
        Assert.Equal(new AccessibilityFlags(510, 126, 62), f);
        Assert.False(AccessibilityFlags.TryParse("1,2", out _));
        Assert.False(AccessibilityFlags.TryParse(null, out _));
    }

    [Fact]
    public void Power_setting_restore_writes_back_to_the_plan_it_was_read_from()
    {
        var power = new FakePower();
        Guid balanced = power.Active, sub = OptimizationCatalog.UsbSubgroupGuid, set = OptimizationCatalog.UsbSelectiveSuspendGuid;
        Guid high = OptimizationCatalog.HighPerformanceSchemeGuid;
        power.AcValues[FakePower.AcKey(balanced, sub, set)] = 1;
        power.AcValues[FakePower.AcKey(high, sub, set)] = 1;
        var p = new PowerSettingPrimitive(power, sub, set, 0, "USB");

        SettingValue original = p.Read();
        p.ApplyDesired();
        Assert.Equal(0u, power.AcValues[FakePower.AcKey(balanced, sub, set)]);

        power.Active = high; // user switches plan
        p.Restore(original);
        Assert.Equal(1u, power.AcValues[FakePower.AcKey(balanced, sub, set)]);
        Assert.Equal(1u, power.AcValues[FakePower.AcKey(high, sub, set)]);
    }

    [Fact]
    public void Power_setting_absent_is_unknown_not_desired()
    {
        var p = new PowerSettingPrimitive(new FakePower(), OptimizationCatalog.UsbSubgroupGuid, OptimizationCatalog.UsbSelectiveSuspendGuid, 0, "USB");
        Assert.Equal((bool?)null, p.IsDesired(p.Read()));
    }

    [Fact]
    public void Trim_enables_and_restores_previous_value()
    {
        var fs = new FakeFileSystem { DisableDeleteNotify = 1 };
        var p = new TrimPrimitive(fs);
        SettingValue original = p.Read();
        Assert.Equal(false, p.IsDesired(original));
        p.ApplyDesired();
        Assert.Equal(0, fs.DisableDeleteNotify);
        p.Restore(original);
        Assert.Equal(1, fs.DisableDeleteNotify);
    }

    [Fact]
    public void Trim_status_that_cannot_be_queried_is_unknown()
    {
        var p = new TrimPrimitive(new FakeFileSystem { DisableDeleteNotify = null });
        Assert.Equal((bool?)null, p.IsDesired(p.Read()));
    }
}

/// <summary>
/// Data-driven matrix over every catalog item that changes the system: detection, compatibility on several
/// Windows builds, permission handling, apply + verify, failed apply + rollback, restore + verify restore.
/// Executed against fakes only — this proves the engine logic, not real Windows behavior.
/// </summary>
public class PerTweakMatrixTests
{
    private sealed class Rig
    {
        public readonly FakeRegistry Registry = new();
        public readonly FakePower Power = new();
        public readonly FakeMouse Mouse = new();
        public readonly FakeAccessibility A11y = new();
        public readonly FakeFileSystem Fs = new() { DisableDeleteNotify = 1 };
        public readonly FakePrivileges Privileges = new(true);
        public readonly InMemoryBackupStore Backups = new();
        public readonly InMemoryHistoryStore History = new();
        public SystemContext Context = TestSystems.Win11();
        public readonly IReadOnlyList<Optimization> Catalog;
        public readonly OptimizationEngine Engine;

        public Rig()
        {
            Power.AcValues[FakePower.AcKey(Power.Active, OptimizationCatalog.UsbSubgroupGuid, OptimizationCatalog.UsbSelectiveSuspendGuid)] = 1;
            // Put registry-backed items into a non-desired state so apply really writes.
            Registry.WriteDword(RegistryRoot.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled", 1);
            Catalog = TestCatalog.Create(Registry, Power, Mouse, A11y, Fs);
            Engine = new OptimizationEngine(Catalog, () => Context, Privileges, Backups, History, new FakeClock(), NullLogger.Instance);
        }
    }

    private static IEnumerable<string> ToggleIds() =>
        new Rig().Catalog.Where(o => o.Definition.Kind == OptimizationKind.Actionable).Select(o => o.Id);

    [Fact]
    public void Every_toggle_is_detectable_on_win10_and_win11_builds_without_throwing()
    {
        foreach (int build in new[] { 19045, 22000, 22631, 26100 })
        {
            var rig = new Rig { Context = TestSystems.Win11(build: build) };
            foreach (string id in ToggleIds())
            {
                OptimizationStatus s = rig.Engine.GetStatus(id);
                Assert.Null(s.DetectionError);
                Assert.NotNull(s.Compatibility);
            }
        }
    }

    [Fact]
    public async Task Every_compatible_toggle_applies_verifies_restores_and_verifies_restore()
    {
        var rig = new Rig();
        int exercised = 0;
        foreach (string id in ToggleIds())
        {
            Optimization opt = rig.Catalog.Single(o => o.Id == id);
            OptimizationStatus before = rig.Engine.GetStatus(id);
            if (before.Compatibility.Status == CompatibilityStatus.Unsupported) continue;
            var originals = opt.Primitives.ToDictionary(p => p.Id, p => p.Read());

            OperationResult apply = await rig.Engine.ApplyAsync(id, warningsAcknowledged: true);
            if (apply.Outcome == OperationOutcome.AlreadyInDesiredState) continue;
            Assert.Equal(OperationOutcome.Succeeded, apply.Outcome, $"{id}: {apply.Message} {apply.ErrorDetail}");
            foreach (ISettingPrimitive p in opt.Primitives) Assert.Equal(true, p.IsDesired(p.Read()), id);
            Assert.Equal(OptimizationState.Enabled, rig.Engine.GetStatus(id).ConfigState, id);
            Assert.NotNull(rig.Backups.Get(id));

            OperationResult restore = await rig.Engine.RestoreAsync(id);
            Assert.Equal(OperationOutcome.Succeeded, restore.Outcome, id);
            foreach (ISettingPrimitive p in opt.Primitives) Assert.True(p.IsRestored(p.Read(), originals[p.Id]), $"{id}/{p.Id}");
            Assert.Null(rig.Backups.Get(id));
            exercised++;
        }
        Assert.True(exercised >= 5, $"only {exercised} toggles were exercised");
    }

    [Fact]
    public async Task Admin_only_toggles_refuse_without_elevation_and_write_nothing()
    {
        var rig = new Rig();
        rig.Privileges.IsElevated = false;
        foreach (Optimization o in rig.Catalog.Where(o => o.Definition.Kind == OptimizationKind.Actionable && o.Definition.RequiresAdministrator))
        {
            int writes = rig.Registry.Writes;
            int? trim = rig.Fs.DisableDeleteNotify;
            OperationResult r = await rig.Engine.ApplyAsync(o.Id, warningsAcknowledged: true);
            Assert.True(r.Outcome is OperationOutcome.AdministratorRequired or OperationOutcome.Unsupported or OperationOutcome.AlreadyInDesiredState, $"{o.Id}: {r.Outcome}");
            Assert.Equal(writes, rig.Registry.Writes);
            Assert.Equal(trim, rig.Fs.DisableDeleteNotify);
        }
    }

    [Fact]
    public async Task Failed_writes_are_reported_and_never_shown_as_on()
    {
        var rig = new Rig();
        rig.A11y.FailWrites = true;
        rig.Fs.FailWrites = true;
        rig.Power.FailWrites = true;
        rig.Registry.FailWrite = _ => true;
        rig.Mouse.Value = rig.Mouse.Value; // mouse fake cannot fail; skipped below
        foreach (string id in ToggleIds().Where(i => i != Ids.PointerPrecision))
        {
            OptimizationStatus before = rig.Engine.GetStatus(id);
            if (before.Compatibility.Status == CompatibilityStatus.Unsupported || before.ConfigState == OptimizationState.Enabled) continue;
            OperationResult r = await rig.Engine.ApplyAsync(id, warningsAcknowledged: true);
            Assert.False(r.Success, id);
            Assert.NotEqual(OptimizationState.Enabled, rig.Engine.GetStatus(id).ConfigState);
            Assert.Null(rig.Backups.Get(id));
        }
    }
}

public class SelectorEngineTests
{
    [Fact]
    public async Task Selecting_a_plan_saves_the_first_original_and_restore_returns_to_it()
    {
        var power = new FakePower();
        Guid original = power.Active;
        var backups = new InMemoryBackupStore();
        var engine = new OptimizationEngine(TestCatalog.Create(new FakeRegistry(), power, new FakeMouse()),
            () => TestSystems.Win11(cpu: "Intel(R) Core(TM) i5-12400", vendor: CpuVendor.Intel, hybrid: false),
            new FakePrivileges(true), backups, new InMemoryHistoryStore(), new FakeClock(), NullLogger.Instance);

        Assert.Equal(original.ToString("D"), engine.GetStatus(Ids.PowerPlan).SelectedValue);
        OperationResult r = await engine.SelectAsync(Ids.PowerPlan, OptimizationCatalog.HighPerformanceSchemeGuid.ToString("D"), true);
        Assert.True(r.Success, r.Message);
        Assert.Equal(OptimizationCatalog.HighPerformanceSchemeGuid, power.Active);
        Assert.True(engine.GetStatus(Ids.PowerPlan).HasBackup);

        // Selecting again keeps the earliest original.
        await engine.SelectAsync(Ids.PowerPlan, original.ToString("D"), true);
        await engine.SelectAsync(Ids.PowerPlan, OptimizationCatalog.HighPerformanceSchemeGuid.ToString("D"), true);
        Assert.True((await engine.RestoreAsync(Ids.PowerPlan)).Success);
        Assert.Equal(original, power.Active);
        Assert.False(engine.GetStatus(Ids.PowerPlan).HasBackup);
    }

    [Fact]
    public async Task Selecting_a_plan_that_does_not_exist_is_rejected()
    {
        var power = new FakePower();
        var engine = new OptimizationEngine(TestCatalog.Create(new FakeRegistry(), power, new FakeMouse()),
            () => TestSystems.Win11(), new FakePrivileges(true), new InMemoryBackupStore(), new InMemoryHistoryStore(), new FakeClock(), NullLogger.Instance);
        OperationResult r = await engine.SelectAsync(Ids.PowerPlan, Guid.NewGuid().ToString("D"), true);
        Assert.False(r.Success);
        Assert.Equal(OptimizationCatalog.BalancedSchemeGuid, power.Active);
    }
}

/// <summary>Simulates an app restart and a PC reboot with the real SQLite store.</summary>
public sealed class RestartPersistenceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sloomy-persist-").FullName;

    [Fact]
    public async Task Backup_and_pending_restart_survive_an_app_restart_and_clear_after_reboot()
    {
        string db = Path.Combine(_dir, "app.db");
        var registry = new FakeRegistry();
        var clock = new FakeClock();
        SystemContext ctx = TestSystems.Win11();

        using (var store = new AppDataStore(db))
        {
            var engine = new OptimizationEngine(TestCatalog.Create(registry, new FakePower(), new FakeMouse()), () => ctx,
                new FakePrivileges(true), store, store, clock, NullLogger.Instance);
            Assert.True((await engine.ApplyAsync(Ids.Hags, true)).Success);
        }

        using (var store = new AppDataStore(db)) // app restarted, PC not rebooted
        {
            var engine = new OptimizationEngine(TestCatalog.Create(registry, new FakePower(), new FakeMouse()), () => ctx,
                new FakePrivileges(true), store, store, clock, NullLogger.Instance);
            OptimizationStatus s = engine.GetStatus(Ids.Hags);
            Assert.True(s.HasBackup);
            Assert.True(s.RestartPending);

            ctx = ctx with { LastBootTime = clock.UtcNow.AddMinutes(10) }; // rebooted
            s = engine.GetStatus(Ids.Hags);
            Assert.False(s.RestartPending);
            Assert.Equal(OptimizationState.Enabled, s.State);
            Assert.True((await engine.RestoreAsync(Ids.Hags)).Success);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}

public class LicensingConfigurationTests
{
    private const string Pem = "-----BEGIN PUBLIC KEY-----\\nMFkw\\n-----END PUBLIC KEY-----";

    private static LicensingConfiguration? Parse(string env, string url, string pem = Pem) =>
        LicensingConfiguration.Parse($$"""{"environment":"{{env}}","serverUrl":"{{url}}","keys":[{"kid":"k1","publicKeyPem":"{{pem}}"}]}""", out _);

    private static string? Problem(string env, string url, string pem = Pem)
    {
        LicensingConfiguration.Parse($$"""{"environment":"{{env}}","serverUrl":"{{url}}","keys":[{"kid":"k1","publicKeyPem":"{{pem}}"}]}""", out string? p);
        return p;
    }

    [Fact]
    public void Production_requires_https_remote_non_example_server()
    {
        Assert.NotNull(Parse("production", "https://licenses.sloomy.gg/"));
        Assert.Contains("https", Problem("production", "http://licenses.sloomy.gg/"));
        Assert.Contains("localhost", Problem("production", "https://localhost:5001/"));
        Assert.Contains("localhost", Problem("production", "https://127.0.0.1/"));
        Assert.Contains("example", Problem("production", "https://licenses.yourdomain.com/"));
    }

    [Fact]
    public void Development_allows_plain_http_only_on_loopback()
    {
        LicensingConfiguration cfg = Parse("development", "http://127.0.0.1:5077/")!;
        Assert.True(cfg.IsDevelopment);
        Assert.NotNull(Problem("development", "http://192.168.1.10:5077/"));
    }

    [Fact]
    public void Private_keys_and_missing_files_are_rejected()
    {
        Assert.Contains("PRIVATE", Problem("production", "https://licenses.sloomy.gg/", "-----BEGIN PUBLIC KEY-----\\n-----BEGIN PRIVATE KEY-----"));
        Assert.Null(LicensingConfiguration.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "licensing.json"), out string? p));
        Assert.Contains("not configured", p);
    }
}

public class DiagnosticParserTests
{
    [Fact]
    public void Fortnite_reader_extracts_known_keys_and_describes_them()
    {
        const string ini = "[/Script/FortniteGame.FortGameUserSettings]\nFrameRateLimit=0.000000\nbUseVSync=False\nFullscreenMode=1\nResolutionSizeX=1920\n;comment=1\nUnknown=5\n";
        var rows = FortniteSettingsReader.Parse(ini);
        Assert.Equal(4, rows.Count);
        Assert.Equal("Unlimited", rows.Single(r => r.Label == "Frame-rate limit").Value);
        Assert.Equal("Off", rows.Single(r => r.Label == "V-Sync").Value);
        Assert.Equal("Windowed fullscreen", rows.Single(r => r.Label == "Window mode").Value);
        Assert.Contains("not found", FortniteSettingsReader.Parse(null)[0].Value);
    }

    [Fact]
    public void Page_file_description_distinguishes_automatic_custom_and_disabled()
    {
        Assert.Equal(DiagnosticSeverity.Good, WindowsSystemDiagnostics.DescribePageFile(new[] { "?:\\pagefile.sys" }).Severity);
        Assert.Equal(DiagnosticSeverity.Warning, WindowsSystemDiagnostics.DescribePageFile(Array.Empty<string>()).Severity);
        Assert.Contains("custom 4096–8192 MB", WindowsSystemDiagnostics.DescribePageFile(new[] { "C:\\pagefile.sys 4096 8192" }).Value);
        Assert.Contains("system managed", WindowsSystemDiagnostics.DescribePageFile(new[] { "D:\\pagefile.sys 0 0" }).Value);
    }

    [Fact]
    public void Fsutil_output_parser_handles_both_formats()
    {
        Assert.Equal(0, WindowsFileSystemBehavior.ParseNtfs("NTFS DisableDeleteNotify = 0  (Allows TRIM operations to be sent to the storage device)\nReFS DisableDeleteNotify = 0"));
        Assert.Equal(1, WindowsFileSystemBehavior.ParseNtfs("DisableDeleteNotify = 1\n"));
        Assert.Equal((int?)null, WindowsFileSystemBehavior.ParseNtfs("Error: access denied"));
    }
}
