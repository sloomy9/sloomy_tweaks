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

    private AppServices Services(bool licensed)
    {
        var engine = new OptimizationEngine(OptimizationCatalog.Create(new FakeRegistry(), new FakePower(), _mouse),
            () => TestSystems.Win11(), new FakePrivileges(true), _backups, _history, _clock, NullLogger.Instance);
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
        var license = new LicenseService(new NoNetworkApi(), _tokens, device, new Dictionary<string, ECDsa> { ["k1"] = pub }, _clock, NullLogger.Instance, "0.1.0");
        license.InitializeAsync().GetAwaiter().GetResult();
        return new AppServices(engine, new FakeHardware(), new FakeUsage(), license, _history, _backups, _benchmarks,
            new InMemorySettingsStore(), new FakeProcesses(), _dialogs, new FakeAppActions(), NullLogger.Instance);
    }

    private sealed class NoNetworkApi : ILicenseApi
    {
        private static Task<LicenseApiResult> Down() => Task.FromResult(new LicenseApiResult(null, new ApiError("unreachable", "offline"), true));
        public Task<LicenseApiResult> ActivateAsync(ActivationRequest r, CancellationToken ct) => Down();
        public Task<LicenseApiResult> RefreshAsync(TokenRequest r, CancellationToken ct) => Down();
        public Task<LicenseApiResult> DeactivateAsync(TokenRequest r, CancellationToken ct) => Down();
    }

    [Fact]
    public async Task Card_apply_shows_explanation_applies_and_refreshes_state()
    {
        AppServices s = Services(licensed: true);
        var page = new CategoryPageViewModel("Input", "", s, o => o.Definition.Category == OptimizationCategory.InputUsb, () => s.License.Current.HasPremiumAccess);
        await page.OnNavigatedToAsync();
        OptimizationCardViewModel card = page.Cards.Single(c => c.Id == Ids.PointerPrecision);
        Assert.Equal("Disabled", card.StateText);
        Assert.True(card.CanApply);

        await card.ApplyAsync();

        Assert.Contains("Changes:", _dialogs.Confirmations.Single());
        Assert.Contains("SPI", _dialogs.Confirmations.Single() + card.TechnicalMechanism);
        Assert.Equal("Enabled", card.StateText);
        Assert.False(card.ResultIsError);
        Assert.True(card.CanRestore);
        Assert.Equal(0, _mouse.Value.Acceleration);
    }

    [Fact]
    public async Task Declining_the_confirmation_changes_nothing()
    {
        AppServices s = Services(licensed: true);
        _dialogs.ConfirmResult = false;
        var page = new CategoryPageViewModel("Input", "", s, o => o.Id == Ids.PointerPrecision, () => true);
        await page.OnNavigatedToAsync();
        await page.Cards[0].ApplyAsync();
        Assert.Equal(1, _mouse.Value.Acceleration);
        Assert.Empty(_history.List());
    }

    [Fact]
    public async Task Without_license_apply_is_disabled_but_restore_remains_available()
    {
        AppServices licensed = Services(licensed: true);
        var page = new CategoryPageViewModel("Input", "", licensed, o => o.Id == Ids.PointerPrecision, () => true);
        await page.OnNavigatedToAsync();
        await page.Cards[0].ApplyAsync();

        _tokens.Clear();
        AppServices unlicensed = Services(licensed: false);
        Assert.False(unlicensed.License.Current.HasPremiumAccess);
        var page2 = new CategoryPageViewModel("Input", "", unlicensed, o => o.Id == Ids.PointerPrecision, () => unlicensed.License.Current.HasPremiumAccess);
        await page2.OnNavigatedToAsync();
        OptimizationCardViewModel card = page2.Cards[0];
        Assert.False(card.CanApply);
        Assert.True(card.CanRestore);

        var restore = new RestoreCenterViewModel(unlicensed);
        await restore.OnNavigatedToAsync();
        Assert.Equal(1, restore.Items.Count);
        await restore.Items[0].RestoreCommand.ExecuteAsync();
        Assert.Equal(1, _mouse.Value.Acceleration);
        Assert.True(restore.IsEmpty);
    }

    [Fact]
    public async Task Main_view_model_locks_premium_tools_and_shows_activation()
    {
        var main = new MainViewModel(Services(licensed: false), "device");
        await main.InitializeAsync();
        Assert.True(main.ShowActivation);
        Assert.Equal(16, main.NavItems.Count);
        Assert.Equal("Dashboard", main.CurrentPage!.Title);

        await main.NavigateAsync(main.NavItems.Single(n => n.Key == "benchmark"));
        Assert.True(main.CurrentPage is LockedPageViewModel);

        await main.NavigateAsync(main.NavItems.Single(n => n.Key == "restore"));
        Assert.True(main.CurrentPage is RestoreCenterViewModel);
    }

    [Fact]
    public async Task Licensed_user_reaches_benchmark_center_and_dashboard_counts_pending()
    {
        var main = new MainViewModel(Services(licensed: true), "device");
        await main.InitializeAsync();
        Assert.False(main.ShowActivation);
        var dash = (DashboardViewModel)main.CurrentPage!;
        Assert.Equal("NVIDIA GeForce RTX 4070", dash.GpuName);
        Assert.Equal("Test Board Co. B650", dash.Motherboard);
        Assert.True(dash.PendingCount >= 1);
        dash.OnNavigatedFrom();

        await main.NavigateAsync(main.NavItems.Single(n => n.Key == "benchmark"));
        Assert.True(main.CurrentPage is BenchmarkViewModel);
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
