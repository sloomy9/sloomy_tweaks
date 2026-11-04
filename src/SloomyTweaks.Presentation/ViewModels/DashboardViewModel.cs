using SloomyTweaks.Core.Benchmark;
using SloomyTweaks.Core.Hardware;
using SloomyTweaks.Core.Optimizations;
using SloomyTweaks.Presentation.Mvvm;
using SloomyTweaks.Presentation.Services;

namespace SloomyTweaks.Presentation.ViewModels;

/// <summary>System overview built from real Windows data; values Windows does not report show "Unknown".</summary>
public sealed class DashboardViewModel : PageViewModel
{
    private readonly AppServices _services;
    private CancellationTokenSource? _liveCts;
    private SystemSnapshot? _snapshot;
    private double _cpuPercent;
    private double _memoryPercent;
    private string _memoryUsage = "—";
    private int _appliedCount;
    private int _pendingCount;
    private string _lastBenchmark = "No benchmark recorded yet.";
    private string _uptime = "—";

    public DashboardViewModel(AppServices services)
    {
        _services = services;
        RefreshCommand = new AsyncRelayCommand(OnNavigatedToAsync);
    }

    public override string Title => "Dashboard";
    public override string Subtitle => "Your system at a glance, read directly from Windows.";
    public AsyncRelayCommand RefreshCommand { get; }

    public string CpuName => _snapshot?.CpuName ?? "Unknown";
    public string CpuDetail => _snapshot is null ? "—" : $"{_snapshot.PhysicalCores?.ToString() ?? "?"} cores · {_snapshot.LogicalProcessors} threads · {_snapshot.CpuArchitecture}";
    public string GpuName => _snapshot?.Gpus.FirstOrDefault(g => g.Vendor is not GpuVendor.Microsoft)?.Name ?? _snapshot?.Gpus.FirstOrDefault()?.Name ?? "Unknown";
    public string GpuDriver => _snapshot?.Gpus.FirstOrDefault(g => g.Vendor is not GpuVendor.Microsoft)?.DriverVersion ?? "Unknown";
    public string AdditionalGpus => _snapshot is { Gpus.Count: > 1 } s ? "Also: " + string.Join(", ", s.Gpus.Skip(1).Select(g => g.Name)) : string.Empty;
    public string InstalledRam => ByteFormat.ToGiB(_snapshot?.TotalPhysicalMemoryBytes);
    public string Motherboard => _snapshot is null ? "Unknown"
        : string.Join(" ", new[] { _snapshot.MotherboardManufacturer, _snapshot.MotherboardProduct }.Where(s => !string.IsNullOrEmpty(s))) is { Length: > 0 } m ? m : "Unknown";
    public string WindowsVersion => _snapshot is null ? "Unknown" : $"{_snapshot.WindowsProductName ?? "Windows"} {_snapshot.WindowsDisplayVersion}".Trim();
    public string WindowsBuild => _snapshot is null ? "—" : $"Build {_snapshot.WindowsBuild}.{_snapshot.WindowsUbr}";
    public string PowerPlan => _snapshot?.ActivePowerPlan ?? "Unknown";
    public string Uptime { get => _uptime; private set => SetProperty(ref _uptime, value); }

    public double CpuPercent { get => _cpuPercent; private set => SetProperty(ref _cpuPercent, value); }
    public double MemoryPercent { get => _memoryPercent; private set => SetProperty(ref _memoryPercent, value); }
    public string MemoryUsage { get => _memoryUsage; private set => SetProperty(ref _memoryUsage, value); }

    public string LicenseStatus => _services.License.Current.Status switch
    {
        Core.Licensing.LicenseStatus.Active => "Active",
        Core.Licensing.LicenseStatus.OfflineGrace => "Active (offline)",
        Core.Licensing.LicenseStatus.Expired => "Expired",
        Core.Licensing.LicenseStatus.Revoked => "Revoked",
        Core.Licensing.LicenseStatus.NotConfigured => "Not configured",
        _ => "Not activated",
    };
    public bool LicenseActive => _services.License.Current.HasPremiumAccess;

    public int AppliedCount { get => _appliedCount; private set => SetProperty(ref _appliedCount, value); }
    /// <summary>Recommended (Verified/Documented) optimizations that are compatible and not yet enabled.</summary>
    public int PendingCount { get => _pendingCount; private set => SetProperty(ref _pendingCount, value); }
    public string LastBenchmark { get => _lastBenchmark; private set => SetProperty(ref _lastBenchmark, value); }

    public override async Task OnNavigatedToAsync()
    {
        await RunSafeAsync(async () =>
        {
            var (snapshot, applied, pending, last) = await Task.Run(() =>
            {
                SystemSnapshot snap = _services.Hardware.GetSnapshot();
                var statuses = _services.Engine.Catalog
                    .Where(o => o.Definition.Kind == OptimizationKind.Actionable)
                    .Select(o => _services.Engine.GetStatus(o.Id)).ToList();
                int appliedCount = _services.Backups.List().Count;
                int pendingCount = statuses.Count(s => s.Optimization.Definition.EligibleForRecommendedFlow
                    && s.Compatibility.Status != CompatibilityStatus.Unsupported
                    && s.State is OptimizationState.Disabled);
                BenchmarkRun? run = _services.Benchmarks.List().FirstOrDefault();
                return (snap, appliedCount, pendingCount, run);
            });
            _snapshot = snapshot;
            AppliedCount = applied;
            PendingCount = pending;
            LastBenchmark = last is null
                ? "No benchmark recorded yet."
                : $"{last.ConfigurationLabel} · {last.Application}: {last.Stats.AverageFps:0} avg FPS, {last.Stats.OnePercentLowFps:0} 1% low ({last.CreatedAt.LocalDateTime:g})";
            foreach (string p in new[] { nameof(CpuName), nameof(CpuDetail), nameof(GpuName), nameof(GpuDriver), nameof(AdditionalGpus), nameof(InstalledRam),
                         nameof(Motherboard), nameof(WindowsVersion), nameof(WindowsBuild), nameof(PowerPlan), nameof(LicenseStatus), nameof(LicenseActive) })
                OnPropertyChanged(p);
            SampleUsage();
        });
        StartLiveUpdates();
    }

    public override void OnNavigatedFrom()
    {
        _liveCts?.Cancel();
        _liveCts = null;
    }

    private void StartLiveUpdates()
    {
        _liveCts?.Cancel();
        _liveCts = new CancellationTokenSource();
        CancellationToken ct = _liveCts.Token;
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            try
            {
                while (await timer.WaitForNextTickAsync(ct))
                {
                    UsageSample sample = _services.Usage.Sample();
                    PostUsage(sample);
                }
            }
            catch (OperationCanceledException) { }
        }, ct);
    }

    private void SampleUsage() => PostUsage(_services.Usage.Sample());

    private void PostUsage(UsageSample s) => UiThread.Dispatch(() =>
    {
        CpuPercent = Math.Round(s.CpuPercent, 1);
        MemoryPercent = Math.Round(s.MemoryPercent, 1);
        MemoryUsage = $"{ByteFormat.ToGiB(s.MemoryUsedBytes)} of {ByteFormat.ToGiB(s.MemoryTotalBytes)}";
        TimeSpan up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        Uptime = up.TotalDays >= 1 ? $"{(int)up.TotalDays}d {up.Hours}h {up.Minutes}m" : $"{up.Hours}h {up.Minutes}m";
    });
}
