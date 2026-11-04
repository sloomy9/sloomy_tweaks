using System.Security.Cryptography;
using System.Text;
using SloomyTweaks.Core.Benchmark;
using SloomyTweaks.Core.Diagnostics;
using SloomyTweaks.Core.Engine;
using SloomyTweaks.Core.Licensing;
using SloomyTweaks.Core.Optimizations;
using SloomyTweaks.Core.Settings;
using SloomyTweaks.Licensing;
using SloomyTweaks.Presentation.Services;
using SloomyTweaks.Presentation.ViewModels;
using SloomyTweaks.Tests.TestFramework;
using Ids = SloomyTweaks.Core.Optimizations.OptimizationCatalog.Ids;

namespace SloomyTweaks.Tests;

public sealed class ViewModelTests : IDisposable
{
    private readonly FakeMouse _mouse = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly InMemoryBackupStore _backups = new();
    private readonly InMemoryHistoryStore _history = new();
    private readonly InMemoryBenchmarkRepository _benchmarks = new();
    private readonly InMemoryTokenStore _tokens = new();
    private readonly ECDsa _signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly FakeClock _clock = new();
    private readonly string _tmp = Directory.CreateTempSubdirectory("sloomy-vm-").FullName;

    private readonly FakePower _power = new();
    private readonly FakeAccessibility _a11y = new();
    private readonly FakeFileSystem _fs = new() { DisableDeleteNotify = 1 };
    private readonly FakeAppActions _app = new() { IsElevated = true };

    private AppServices Services(bool licensed, bool developmentMode = false)
    {
        var engine = new OptimizationEngine(TestCatalog.Create(new FakeRegistry(), _power, _mouse, _a11y, _fs),
            () => TestSystems.Win11(), new FakePrivileges(_app.IsElevated), _backups, _history, _clock, NullLogger.Instance);
        var pub = ECDsa.Create();
        pub.ImportSubjectPublicKeyInfo(_signing.ExportSubjectPublicKeyInfo(), out _);
        var device = new FakeDevice();
        if (licensed)
        {
            _tokens.Save(LicenseTokenCodec.Sign(new LicenseTokenPayload
            {
                KeyId = "k1", LicenseId = "lic", DeviceId = device.DeviceId, Plan = LicensePlan.Days30,
                IssuedAtUnix = _clock.UtcNow.ToUnixTimeSeconds(), TokenExpiresAtUnix = _clock.UtcNow.AddDays(3).ToUnixTimeSeconds(),
                LicenseExpiresAtUnix = _clock.UtcNow.AddDays(30).ToUnixTimeSeconds(),
            }, _signing));
        }
        var license = new LicenseService(new NoNetworkApi(), _tokens, device, new Dictionary<string, ECDsa> { ["k1"] = pub }, _clock,
            NullLogger.Instance, "0.2.0", developmentMode);
        license.InitializeAsync().GetAwaiter().GetResult();
        return new AppServices(engine, new FakeHardware(), new FakeUsage(), license, _history, _backups, _benchmarks,
            new InMemorySettingsStore(), new FakeProcesses(), _dialogs, _app, NullLogger.Instance, new FakeDiagnostics());
    }

    private sealed class NoNetworkApi : ILicenseApi
    {
        private static Task<LicenseApiResult> Down() => Task.FromResult(new LicenseApiResult(null, new ApiError("unreachable", "offline"), true));
        public Task<LicenseApiResult> ActivateAsync(ActivationRequest r, CancellationToken ct) => Down();
        public Task<LicenseApiResult> RefreshAsync(TokenRequest r, CancellationToken ct) => Down();
        public Task<LicenseApiResult> DeactivateAsync(TokenRequest r, CancellationToken ct) => Down();
    }

    private async Task<OptimizationCardViewModel> Card(AppServices s, string id)
    {
        var page = new CategoryPageViewModel("T", "", s, o => o.Id == id, () => s.License.Current.HasPremiumAccess);
        await page.OnNavigatedToAsync();
        return page.Cards.Single();
    }

    [Fact]
    public async Task Toggle_turns_on_only_after_verification_and_off_restores_exactly()
    {
        AppServices s = Services(licensed: true);
        OptimizationCardViewModel card = await Card(s, Ids.PointerPrecision);
        Assert.False(card.IsOn);
        Assert.True(card.CanToggle);
        Assert.Equal("Off", card.StatusText);

        await card.ToggleAsync();
        Assert.Empty(_dialogs.Confirmations); // documented, no restart: no confirmation needed
        Assert.True(card.IsOn);
        Assert.False(card.ResultIsError);
        Assert.Equal(0, _mouse.Value.Acceleration);
        Assert.NotNull(card.RestoreInfo);

        await card.ToggleAsync();
        Assert.False(card.IsOn);
        Assert.Equal(1, _mouse.Value.Acceleration);
        Assert.Null(card.RestoreInfo);
    }

    [Fact]
    public async Task Failed_apply_shows_an_error_and_the_toggle_stays_off()
    {
        AppServices s = Services(licensed: true);
        _a11y.FailWrites = true;
        OptimizationCardViewModel card = await Card(s, Ids.AccessibilityShortcuts);
        await card.ToggleAsync();
        Assert.False(card.IsOn);
        Assert.True(card.ResultIsError);
        Assert.True(_a11y.Value.AnyShortcutActive);
    }

    [Fact]
    public async Task Experimental_toggle_asks_first_and_declining_changes_nothing()
    {
        _power.AcValues[FakePower.AcKey(_power.Active, OptimizationCatalog.UsbSubgroupGuid, OptimizationCatalog.UsbSelectiveSuspendGuid)] = 1;
        AppServices s = Services(licensed: true);
        _dialogs.ConfirmResult = false;
        OptimizationCardViewModel card = await Card(s, Ids.UsbSelectiveSuspend);
        await card.ToggleAsync();
        Assert.Equal(1, _dialogs.Confirmations.Count);
        Assert.False(card.IsOn);
        Assert.Empty(_history.List());
    }

    [Fact]
    public async Task Unsupported_toggle_is_disabled_off_and_explains_why()
    {
        AppServices s = Services(licensed: true); // USB setting absent on the fake plan
        OptimizationCardViewModel card = await Card(s, Ids.UsbSelectiveSuspend);
        Assert.True(card.IsUnsupported);
        Assert.False(card.IsOn);
        Assert.False(card.CanToggle);
        Assert.Contains("USB selective suspend", card.ToggleBlockedReason);
    }

    [Fact]
    public async Task Admin_toggle_is_blocked_for_standard_users()
    {
        _app.IsElevated = false;
        AppServices s = Services(licensed: true);
        OptimizationCardViewModel card = await Card(s, Ids.Trim);
        Assert.False(card.CanToggle);
        Assert.Contains("administrator", card.ToggleBlockedReason);
        Assert.Equal(1, _fs.DisableDeleteNotify);
    }

    [Fact]
    public async Task Already_on_by_default_is_shown_on_but_cannot_be_turned_off_without_a_backup()
    {
        AppServices s = Services(licensed: true);
        OptimizationCardViewModel card = await Card(s, Ids.GameMode); // absent value = on (Windows default)
        Assert.True(card.IsOn);
        Assert.False(card.CanToggle);
        Assert.Contains("Already on", card.ToggleBlockedReason);
        Assert.Null(card.RestoreInfo); // no duplicate text
    }

    [Fact]
    public async Task Selector_card_changes_the_plan_and_restores_the_original()
    {
        AppServices s = Services(licensed: true);
        OptimizationCardViewModel card = await Card(s, Ids.PowerPlan);
        Assert.True(card.IsSelector);
        Assert.Equal("Balanced", card.SelectedOption!.Label);
        Assert.False(card.CanRestoreOriginal);

        await card.SelectAsync(card.Options.Single(o => o.Label == "High performance"));
        Assert.Equal(1, _dialogs.Confirmations.Count); // X3D warning on the default test CPU
        Assert.Equal(OptimizationCatalog.HighPerformanceSchemeGuid, _power.Active);
        Assert.Equal("High performance", card.SelectedOption!.Label);
        Assert.True(card.CanRestoreOriginal);

        await card.RestoreOriginalCommand.ExecuteAsync();
        Assert.Equal(OptimizationCatalog.BalancedSchemeGuid, _power.Active);
    }

    [Fact]
    public async Task Without_a_license_only_activation_and_restore_are_reachable()
    {
        var main = new MainViewModel(Services(licensed: false), "device");
        await main.InitializeAsync();
        Assert.True(main.IsLocked);
        Assert.False(main.IsUnlocked);
        Assert.Null(main.CurrentPage);
        Assert.Equal(15, main.NavItems.Count);

        await main.NavigateAsync(main.NavItems.Single(n => n.Key == "benchmark"));
        Assert.Null(main.CurrentPage);

        await main.Activation.ShowRestoreCommand.ExecuteAsync();
        Assert.True(main.Activation.ShowRestore);
        main.StopLicenseRecheck();
    }

    [Fact]
    public async Task Restore_still_works_after_the_license_expires()
    {
        AppServices licensed = Services(licensed: true);
        await (await Card(licensed, Ids.PointerPrecision)).ToggleAsync();
        Assert.Equal(0, _mouse.Value.Acceleration);

        _tokens.Clear();
        var main = new MainViewModel(Services(licensed: false), "device");
        await main.InitializeAsync();
        Assert.True(main.IsLocked);
        await main.Activation.ShowRestoreCommand.ExecuteAsync();
        RestoreCenterViewModel restore = main.Activation.RestoreCenter;
        Assert.Equal(1, restore.Items.Count);
        await restore.Items[0].RestoreCommand.ExecuteAsync();
        Assert.Equal(1, _mouse.Value.Acceleration);
        Assert.True(restore.IsEmpty);
        main.StopLicenseRecheck();
    }

    [Fact]
    public async Task Licensed_user_gets_all_sections_and_development_mode_is_labelled()
    {
        var main = new MainViewModel(Services(licensed: true, developmentMode: true), "device");
        await main.InitializeAsync();
        Assert.True(main.IsUnlocked);
        Assert.Equal("DEVELOPMENT MODE", main.DevelopmentBadge);
        var dash = (DashboardViewModel)main.CurrentPage!;
        Assert.Equal("NVIDIA GeForce RTX 4070", dash.GpuName);
        Assert.Equal("Test Board Co. B650", dash.Motherboard);
        dash.OnNavigatedFrom();

        foreach (NavItemViewModel item in main.NavItems)
        {
            await main.NavigateAsync(item);
            Assert.NotNull(main.CurrentPage);
            main.CurrentPage!.OnNavigatedFrom();
        }
        await main.NavigateAsync(main.NavItems.Single(n => n.Key == "benchmark"));
        Assert.True(main.CurrentPage is BenchmarkViewModel);
        main.StopLicenseRecheck();
    }

    [Fact]
    public async Task Production_build_has_no_development_badge()
    {
        var main = new MainViewModel(Services(licensed: true), "device");
        await main.InitializeAsync();
        Assert.Null(main.DevelopmentBadge);
        main.StopLicenseRecheck();
    }

    [Fact]
    public async Task Benchmark_import_and_compare_requires_repeated_runs()
    {
        var vm = new BenchmarkViewModel(Services(licensed: true));
        await vm.OnNavigatedToAsync();
        var rng = new Random(42);
        async Task Import(string label, double baseMs)
        {
            string path = Path.Combine(_tmp, $"{label}-{Guid.NewGuid():N}.csv");
            var sb = new StringBuilder("Application,ProcessID,MsBetweenPresents\n");
            for (int i = 0; i < 2000; i++) sb.AppendLine($"game.exe,1,{(baseMs + rng.NextDouble() * 0.2).ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            File.WriteAllText(path, sb.ToString());
            vm.ImportLabel = label;
            await vm.ImportFileAsync(path);
        }

        await Import("Baseline", 5.0);
        await Import("Candidate", 4.0);
        vm.BaselineLabel = "Baseline";
        vm.CandidateLabel = "Candidate";
        vm.Compare();
        Assert.Equal(ComparisonVerdict.InsufficientData, vm.Comparison[0].Verdict);

        for (int i = 0; i < 2; i++) { await Import("Baseline", 5.0); await Import("Candidate", 4.0); }
        vm.Compare();
        Assert.Equal(ComparisonVerdict.Improved, vm.Comparison.Single(c => c.Metric == "Average FPS").Verdict);
        Assert.Equal(6, vm.Runs.Count);
        Assert.True(vm.Runs.All(r => r.Source.StartsWith("PresentMon CSV", StringComparison.Ordinal)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { }
    }
}
