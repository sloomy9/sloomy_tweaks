using System.Globalization;
using System.Text;
using System.Text.Json;
using SloomyTweaks.Core.Benchmark;
using SloomyTweaks.Core.Hardware;
using SloomyTweaks.Core.Licensing;
using SloomyTweaks.Core.Network;
using SloomyTweaks.Core.Processes;
using SloomyTweaks.Core.Settings;
using SloomyTweaks.Licensing;
using SloomyTweaks.Presentation.Mvvm;
using SloomyTweaks.Presentation.Services;

namespace SloomyTweaks.Presentation.ViewModels;

// ------------------------------------------------------------------ Benchmark Center
public sealed class BenchmarkViewModel : PageViewModel
{
    private readonly AppServices _services;
    private IReadOnlyList<BenchmarkRun> _runs = Array.Empty<BenchmarkRun>();
    private IReadOnlyList<string> _labels = Array.Empty<string>();
    private IReadOnlyList<MetricComparison> _comparison = Array.Empty<MetricComparison>();
    private string _importLabel = "Baseline";
    private string? _baselineLabel;
    private string? _candidateLabel;
    private string? _statusMessage;

    public BenchmarkViewModel(AppServices services)
    {
        _services = services;
        ImportCommand = new AsyncRelayCommand(ImportAsync, onError: e => StatusMessage = e.Message);
        CompareCommand = new RelayCommand(Compare, () => BaselineLabel is not null && CandidateLabel is not null && BaselineLabel != CandidateLabel);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => Runs.Count > 0, e => StatusMessage = e.Message);
        DeleteCommand = new AsyncRelayCommand(async p => { if (p is BenchmarkRun r) { _services.Benchmarks.Delete(r.Id); await OnNavigatedToAsync(); } });
        SetBaselineCommand = new RelayCommand(p => BaselineLabel = p as string);
        SetCandidateCommand = new RelayCommand(p => CandidateLabel = p as string);
    }

    public override string Title => "Benchmark Center";
    public override string Subtitle => "Import real frame-time captures, repeat them, and compare configurations statistically.";

    public string Methodology =>
        "1. Capture with Intel PresentMon (or a tool that exports PresentMon-format CSV) on the same replay or scene each time.\n" +
        $"2. Record at least {BenchmarkComparer.MinimumRunsPerSide} runs per configuration; give runs of the same configuration the same label.\n" +
        "3. Change one thing, then capture again under a new label.\n" +
        "4. Compare. Sloomy Tweaks reports an improvement only when Welch's t-test shows a significant difference (p < 0.05).\n" +
        "Sloomy Tweaks never generates or simulates results. Software captures measure frame times, not end-to-end mouse-to-photon latency, which requires external hardware such as NVIDIA LDAT or a high-speed camera.";

    public IReadOnlyList<BenchmarkRun> Runs { get => _runs; private set { SetProperty(ref _runs, value); OnPropertyChanged(nameof(IsEmpty)); ExportCommand.RaiseCanExecuteChanged(); } }
    public bool IsEmpty => Runs.Count == 0;
    public IReadOnlyList<string> Labels { get => _labels; private set => SetProperty(ref _labels, value); }
    public string ImportLabel { get => _importLabel; set => SetProperty(ref _importLabel, value); }
    public string? BaselineLabel { get => _baselineLabel; set { SetProperty(ref _baselineLabel, value); CompareCommand.RaiseCanExecuteChanged(); } }
    public string? CandidateLabel { get => _candidateLabel; set { SetProperty(ref _candidateLabel, value); CompareCommand.RaiseCanExecuteChanged(); } }
    public IReadOnlyList<MetricComparison> Comparison { get => _comparison; private set { SetProperty(ref _comparison, value); OnPropertyChanged(nameof(HasComparison)); } }
    public bool HasComparison => Comparison.Count > 0;
    public string? StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    public AsyncRelayCommand ImportCommand { get; }
    public RelayCommand CompareCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public RelayCommand SetBaselineCommand { get; }
    public RelayCommand SetCandidateCommand { get; }

    public override Task OnNavigatedToAsync() => RunSafeAsync(async () =>
    {
        Runs = await Task.Run(() => _services.Benchmarks.List());
        Labels = Runs.Select(r => r.ConfigurationLabel).Distinct(StringComparer.Ordinal).OrderBy(l => l, StringComparer.CurrentCulture).ToList();
    });

    /// <summary>Imports a capture file. Exposed for tests with an explicit path.</summary>
    public async Task ImportFileAsync(string path)
    {
        string label = string.IsNullOrWhiteSpace(ImportLabel) ? "Unlabeled" : ImportLabel.Trim();
        var (capture, stats, summary) = await Task.Run(() =>
        {
            FrameCapture c = PresentMonCsvParser.ParseFile(path);
            FrameTimeStats s = FrameTimeAnalyzer.Analyze(c.FrameTimesMs);
            SystemSnapshot snap = _services.Hardware.GetSnapshot();
            string sys = $"{snap.CpuName}; {string.Join(", ", snap.Gpus.Select(g => $"{g.Name} {g.DriverVersion}"))}; {snap.WindowsProductName} build {snap.WindowsBuild}.{snap.WindowsUbr}; plan {snap.ActivePowerPlan}";
            return (c, s, sys);
        });
        _services.Benchmarks.Save(new BenchmarkRun
        {
            ConfigurationLabel = label,
            CreatedAt = DateTimeOffset.UtcNow,
            Application = capture.Application,
            Source = $"PresentMon CSV: {Path.GetFileName(path)} ({capture.SourceColumn})",
            Stats = stats,
            SystemSummary = summary,
        });
        StatusMessage = $"Imported {stats.FrameCount:N0} frames of {capture.Application} as '{label}'." +
                        (capture.SkippedRows > 0 ? $" {capture.SkippedRows} invalid rows were skipped." : "");
        await OnNavigatedToAsync();
    }

    private async Task ImportAsync()
    {
        string? path = _services.Dialogs.PickOpenFile("Import PresentMon capture", "PresentMon CSV (*.csv)|*.csv");
        if (path is not null) await ImportFileAsync(path);
    }

    public void Compare()
    {
        var baseline = Runs.Where(r => r.ConfigurationLabel == BaselineLabel).ToList();
        var candidate = Runs.Where(r => r.ConfigurationLabel == CandidateLabel).ToList();
        Comparison = BenchmarkComparer.Compare(baseline, candidate);
        var apps = baseline.Concat(candidate).Select(r => r.Application).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        StatusMessage = apps.Count > 1 ? $"Warning: the runs come from different applications ({string.Join(", ", apps)}); the comparison may not be meaningful." : null;
    }

    private Task ExportAsync()
    {
        string? path = _services.Dialogs.PickSaveFile("Export benchmark results", "CSV (*.csv)|*.csv|JSON (*.json)|*.json", "sloomy-benchmarks.csv");
        if (path is null) return Task.CompletedTask;
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllText(path, JsonSerializer.Serialize(Runs, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            var sb = new StringBuilder("id,label,created_utc,application,source,frames,duration_s,avg_fps,one_percent_low_fps,point_one_percent_low_fps,median_ms,p95_ms,p99_ms,max_ms,stddev_ms,system\n");
            foreach (BenchmarkRun r in Runs)
            {
                FrameTimeStats s = r.Stats;
                sb.AppendLine(string.Join(",", new[]
                {
                    r.Id.ToString(CultureInfo.InvariantCulture), Csv(r.ConfigurationLabel), r.CreatedAt.UtcDateTime.ToString("O"), Csv(r.Application), Csv(r.Source),
                    s.FrameCount.ToString(CultureInfo.InvariantCulture), F(s.DurationSeconds), F(s.AverageFps), F(s.OnePercentLowFps), F(s.PointOnePercentLowFps),
                    F(s.MedianFrameTimeMs), F(s.P95FrameTimeMs), F(s.P99FrameTimeMs), F(s.MaxFrameTimeMs), F(s.FrameTimeStdDevMs), Csv(r.SystemSummary ?? ""),
                }));
            }
            File.WriteAllText(path, sb.ToString());
        }
        StatusMessage = $"Exported {Runs.Count} run(s) to {path}.";
        return Task.CompletedTask;

        static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        static string Csv(string v) => v.Contains(',') || v.Contains('"') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }
}

// ------------------------------------------------------------------ Network
public sealed class NetworkViewModel : PageViewModel
{
    private readonly AppServices _services;
    private IReadOnlyList<AdapterInfo> _adapters = Array.Empty<AdapterInfo>();
    private LatencyStats? _gateway;
    private LatencyStats? _internet;
    private string _target = "1.1.1.1";
    private string? _interpretation;

    public NetworkViewModel(AppServices services)
    {
        _services = services;
        RunCommand = new AsyncRelayCommand(RunAsync, onError: e => ErrorMessage = e.Message);
        RefreshCommand = new AsyncRelayCommand(OnNavigatedToAsync);
    }

    public override string Title => "Network Optimization";
    public override string Subtitle => "Adapter details and diagnostics that separate local, ISP and destination problems.";
    public string Disclaimer => LatencyStats.Disclaimer;
    public IReadOnlyList<AdapterInfo> Adapters { get => _adapters; private set => SetProperty(ref _adapters, value); }
    public string Target { get => _target; set => SetProperty(ref _target, value); }
    public LatencyStats? Gateway { get => _gateway; private set => SetProperty(ref _gateway, value); }
    public LatencyStats? Internet { get => _internet; private set => SetProperty(ref _internet, value); }
    public string? Interpretation { get => _interpretation; private set => SetProperty(ref _interpretation, value); }
    public AsyncRelayCommand RunCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }

    public override Task OnNavigatedToAsync() => RunSafeAsync(async () => Adapters = await Task.Run(NetworkDiagnostics.GetActiveAdapters));

    private async Task RunAsync()
    {
        string target = Target.Trim();
        if (target.Length == 0 || target.Length > 253 || target.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':')))
        {
            ErrorMessage = "Enter a valid host name or IP address.";
            return;
        }
        ErrorMessage = null;
        IsLoading = true;
        try
        {
            string? gw = Adapters.FirstOrDefault(a => a.Gateways.Count > 0)?.Gateways[0];
            Gateway = gw is null ? null : await NetworkDiagnostics.PingAsync(gw, 30, TimeSpan.FromMilliseconds(200));
            Internet = await NetworkDiagnostics.PingAsync(target, 30, TimeSpan.FromMilliseconds(200));
            Interpretation = NetworkDiagnostics.Interpret(Gateway, Internet);
        }
        finally
        {
            IsLoading = false;
        }
    }
}

// ------------------------------------------------------------------ Processes & Services
public sealed class ProcessesViewModel : PageViewModel
{
    private readonly AppServices _services;
    private IReadOnlyList<ProcessEntry> _processes = Array.Empty<ProcessEntry>();
    private IReadOnlyList<StartupEntry> _startup = Array.Empty<StartupEntry>();
    private IReadOnlyList<ServiceEntry> _allServices = Array.Empty<ServiceEntry>();
    private string _serviceFilter = string.Empty;

    public ProcessesViewModel(AppServices services)
    {
        _services = services;
        RefreshCommand = new AsyncRelayCommand(OnNavigatedToAsync);
    }

    public override string Title => "Processes & Services";
    public override string Subtitle => "What is running and what starts with Windows. This view is read-only in this version.";
    public string Policy => "Sloomy Tweaks does not end processes or disable services from generic lists. Critical Windows processes and security/update services are marked as protected.";
    public IReadOnlyList<ProcessEntry> Processes { get => _processes; private set => SetProperty(ref _processes, value); }
    public IReadOnlyList<StartupEntry> Startup { get => _startup; private set => SetProperty(ref _startup, value); }
    public IReadOnlyList<ServiceEntry> Services => string.IsNullOrWhiteSpace(_serviceFilter)
        ? _allServices
        : _allServices.Where(s => s.DisplayName.Contains(_serviceFilter, StringComparison.CurrentCultureIgnoreCase) || s.Name.Contains(_serviceFilter, StringComparison.OrdinalIgnoreCase)).ToList();
    public string ServiceFilter { get => _serviceFilter; set { if (SetProperty(ref _serviceFilter, value)) OnPropertyChanged(nameof(Services)); } }
    public AsyncRelayCommand RefreshCommand { get; }

    public override Task OnNavigatedToAsync() => RunSafeAsync(async () =>
    {
        var (p, st, sv) = await Task.Run(() =>
        {
            _services.Processes.GetProcesses(); // prime CPU deltas
            Thread.Sleep(500);
            return (_services.Processes.GetProcesses().Take(80).ToList(), _services.Processes.GetStartupEntries(), _services.Processes.GetServices());
        });
        Processes = p;
        Startup = st;
        _allServices = sv;
        OnPropertyChanged(nameof(Services));
    });
}

// ------------------------------------------------------------------ Settings
public sealed class SettingsViewModel : PageViewModel
{
    private readonly AppServices _services;
    private AppSettings _settings = new();
    private string? _statusMessage;

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        SaveCommand = new RelayCommand(Save);
        OpenLogsCommand = new RelayCommand(() => _services.Dialogs.OpenFolder(_services.App.LogDirectory));
        OpenDataCommand = new RelayCommand(() => _services.Dialogs.OpenFolder(_services.App.DataDirectory));
    }

    public override string Title => "Settings";
    public override string Subtitle => "Preferences for Sloomy Tweaks.";
    public string Version => _services.App.ApplicationVersion;
    public string About => "Sloomy Tweaks is an evidence-based Windows gaming optimization tool. Every change is explained, verified after it is applied, and reversible from the Restore Center. Sloomy Tweaks does not disable Windows security features, Windows Update or Microsoft Defender.";
    public string ThemeNote => "Dark theme. A light theme is not available yet.";
    public string UpdateNote => "Automatic update checks are under development and are not active in this preview.";

    public bool StartWithWindows { get => _settings.StartWithWindows; set { _settings = _settings with { StartWithWindows = value }; OnPropertyChanged(); } }
    public bool ShowNotifications { get => _settings.ShowNotifications; set { _settings = _settings with { ShowNotifications = value }; OnPropertyChanged(); } }
    public bool CreateRestorePoints { get => _settings.CreateRestorePoints; set { _settings = _settings with { CreateRestorePoints = value }; OnPropertyChanged(); } }
    public int BenchmarkHistoryLimit { get => _settings.BenchmarkHistoryLimit; set { _settings = _settings with { BenchmarkHistoryLimit = Math.Clamp(value, 10, 5000) }; OnPropertyChanged(); } }
    public int LogRetentionDays { get => _settings.LogRetentionDays; set { _settings = _settings with { LogRetentionDays = Math.Clamp(value, 1, 365) }; OnPropertyChanged(); } }
    public string LicenseSummary => _services.License.Current.Message;
    public string? StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    public RelayCommand SaveCommand { get; }
    public RelayCommand OpenLogsCommand { get; }
    public RelayCommand OpenDataCommand { get; }

    public override Task OnNavigatedToAsync()
    {
        _settings = _services.Settings.Load() with { StartWithWindows = _services.App.IsStartWithWindowsEnabled() };
        foreach (string p in new[] { nameof(StartWithWindows), nameof(ShowNotifications), nameof(CreateRestorePoints), nameof(BenchmarkHistoryLimit), nameof(LogRetentionDays), nameof(LicenseSummary) })
            OnPropertyChanged(p);
        return Task.CompletedTask;
    }

    private void Save()
    {
        try
        {
            _services.Settings.Save(_settings);
            _services.App.SetStartWithWindows(_settings.StartWithWindows);
            _services.Engine.CreateRestorePoints = _settings.CreateRestorePoints;
            StatusMessage = "Settings saved.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusMessage = "Settings could not be saved: " + e.Message;
        }
    }
}

// ------------------------------------------------------------------ License & Account
public sealed class LicenseViewModel : PageViewModel
{
    private readonly AppServices _services;
    private string _licenseKey = string.Empty;

    public LicenseViewModel(AppServices services, string deviceId)
    {
        _services = services;
        DeviceId = deviceId;
        ActivateCommand = new AsyncRelayCommand(async () => { await _services.License.ActivateAsync(LicenseKey); Raise(); },
            () => !string.IsNullOrWhiteSpace(LicenseKey), e => ErrorMessage = e.Message);
        DeactivateCommand = new AsyncRelayCommand(DeactivateAsync, () => _services.License.Current.HasPremiumAccess, e => ErrorMessage = e.Message);
        RefreshCommand = new AsyncRelayCommand(async () => { await _services.License.InitializeAsync(); Raise(); }, onError: e => ErrorMessage = e.Message);
        _services.License.Changed += (_, _) => UiThread.Dispatch(Raise);
    }

    public override string Title => "License & Account";
    public override string Subtitle => "Activate Sloomy Tweaks on this device.";
    public string DeviceId { get; }
    public string PrivacyNote => "Device binding uses a one-way hash of the random identifier Windows creates at installation. Sloomy Tweaks does not read hardware serial numbers, MAC addresses or disk IDs. The server stores this hash, your computer name and the app version for each activation.";
    public string LicenseKey { get => _licenseKey; set { SetProperty(ref _licenseKey, value); ActivateCommand.RaiseCanExecuteChanged(); } }
    public LicenseSnapshot Snapshot => _services.License.Current;
    public string StatusText => Snapshot.Status switch
    {
        LicenseStatus.Active => "Active",
        LicenseStatus.OfflineGrace => "Active (offline)",
        LicenseStatus.Expired => "Expired",
        LicenseStatus.Revoked => "Revoked",
        LicenseStatus.Invalid => "Invalid",
        LicenseStatus.NotConfigured => "Not configured",
        _ => "Not activated",
    };
    public bool IsActive => Snapshot.HasPremiumAccess;
    public string PlanText => Snapshot.Plan?.DisplayName() ?? "—";
    public string ExpiresText => Snapshot.Plan == LicensePlan.Lifetime ? "Never" : Snapshot.LicenseExpiresAt?.LocalDateTime.ToString("D") ?? "—";
    public string OfflineUntilText => Snapshot.TokenExpiresAt?.LocalDateTime.ToString("g") ?? "—";
    public string Message => Snapshot.Message;
    public AsyncRelayCommand ActivateCommand { get; }
    public AsyncRelayCommand DeactivateCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }

    private async Task DeactivateAsync()
    {
        if (!await _services.Dialogs.ConfirmAsync("Deactivate this device", "This frees the activation slot so you can use your license on another PC. Optimizations you applied stay in place and can still be restored.", "Deactivate"))
            return;
        await _services.License.DeactivateAsync();
        Raise();
    }

    private void Raise()
    {
        foreach (string p in new[] { nameof(Snapshot), nameof(StatusText), nameof(IsActive), nameof(PlanText), nameof(ExpiresText), nameof(OfflineUntilText), nameof(Message) })
            OnPropertyChanged(p);
        DeactivateCommand.RaiseCanExecuteChanged();
        if (IsActive) LicenseKey = string.Empty;
    }
}
