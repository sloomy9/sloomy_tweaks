using SloomyTweaks.Core.Licensing;
using SloomyTweaks.Core.Optimizations;
using SloomyTweaks.Presentation.Mvvm;
using SloomyTweaks.Presentation.Services;

namespace SloomyTweaks.Presentation.ViewModels;

public sealed class NavItemViewModel : ObservableObject
{
    private bool _isSelected;

    public NavItemViewModel(string key, string title, string glyph, Func<PageViewModel> factory)
    {
        Key = key;
        Title = title;
        Glyph = glyph;
        Factory = factory;
    }

    public string Key { get; }
    public string Title { get; }
    /// <summary>Segoe Fluent Icons / Segoe MDL2 Assets code point.</summary>
    public string Glyph { get; }
    internal Func<PageViewModel> Factory { get; }
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}

/// <summary>
/// Activation screen shown instead of the application whenever there is no valid license.
/// The only other thing reachable from here is restoring changes Sloomy Tweaks made earlier.
/// </summary>
public sealed class ActivationViewModel : ObservableObject
{
    private readonly AppServices _services;
    private string _licenseKey = string.Empty;
    private string? _message;
    private bool _showRestore;

    public ActivationViewModel(AppServices services, string deviceId)
    {
        _services = services;
        DeviceId = deviceId;
        RestoreCenter = new RestoreCenterViewModel(services);
        ActivateCommand = new AsyncRelayCommand(ActivateAsync, () => IsConfigured && !string.IsNullOrWhiteSpace(LicenseKey), e => Message = e.Message);
        ShowRestoreCommand = new AsyncRelayCommand(async () => { ShowRestore = true; await RestoreCenter.OnNavigatedToAsync(); });
        BackCommand = new RelayCommand(() => ShowRestore = false);
    }

    public string DeviceId { get; }
    public bool IsConfigured => _services.License.IsConfigured;
    public bool IsDevelopmentMode => _services.License.IsDevelopmentMode;
    public string LicenseKey { get => _licenseKey; set { SetProperty(ref _licenseKey, value); ActivateCommand.RaiseCanExecuteChanged(); } }
    public string? Message { get => _message; set => SetProperty(ref _message, value); }
    public bool ShowRestore { get => _showRestore; private set => SetProperty(ref _showRestore, value); }
    public RestoreCenterViewModel RestoreCenter { get; }
    public AsyncRelayCommand ActivateCommand { get; }
    public AsyncRelayCommand ShowRestoreCommand { get; }
    public RelayCommand BackCommand { get; }

    private async Task ActivateAsync()
    {
        LicenseSnapshot s = await _services.License.ActivateAsync(LicenseKey);
        Message = s.Message;
        if (s.HasPremiumAccess) LicenseKey = string.Empty;
    }
}

/// <summary>Shell: license gate, sidebar navigation, elevation state and periodic license validation.</summary>
public sealed class MainViewModel : ObservableObject
{
    public static readonly TimeSpan LicenseRecheckInterval = TimeSpan.FromHours(6);

    private readonly AppServices _services;
    private readonly Dictionary<string, PageViewModel> _pages = new();
    private PageViewModel? _currentPage;
    private NavItemViewModel? _selected;
    private bool _initialized;
    private CancellationTokenSource? _recheck;

    public MainViewModel(AppServices services, string deviceId)
    {
        _services = services;
        Func<bool> premium = () => _services.License.Current.HasPremiumAccess;
        bool Cat(Optimization o, OptimizationCategory c) => o.Definition.Category == c;

        Activation = new ActivationViewModel(services, deviceId);
        NavItems = new List<NavItemViewModel>
        {
            new("dashboard", "Dashboard", "", () => new DashboardViewModel(services)),
            new("cpu", "CPU", "", () => new CategoryPageViewModel("CPU",
                "Processor detection and the power policy that controls clocks and core parking.", services, o => Cat(o, OptimizationCategory.Cpu), premium, CategoryInfo.Cpu)),
            new("gpu", "GPU", "", () => new CategoryPageViewModel("GPU",
                "Graphics scheduling, presentation and latency features for NVIDIA, AMD and Intel.", services, o => Cat(o, OptimizationCategory.Gpu), premium, CategoryInfo.Gpu)),
            new("memory", "RAM", "", () => new CategoryPageViewModel("RAM",
                "Memory usage, commit charge and page-file configuration. No placebo memory cleaning.", services, o => Cat(o, OptimizationCategory.Memory), premium, CategoryInfo.Memory)),
            new("windows", "Windows", "", () => new CategoryPageViewModel("Windows",
                "Game Mode, capture features and other Windows settings with documented effects.", services, o => Cat(o, OptimizationCategory.Windows), premium, CategoryInfo.Windows)),
            new("processes", "Processes & Services", "", () => new ProcessesViewModel(services)),
            new("gaming", "Gaming", "", () => new CategoryPageViewModel("Gaming",
                "Game profiles, starting with Fortnite. Measured, per-system configuration.", services,
                o => Cat(o, OptimizationCategory.Gaming) || o.Id is OptimizationCatalog.Ids.NvidiaReflex or OptimizationCatalog.Ids.WindowedGameOptimizations or OptimizationCatalog.Ids.GameMode,
                premium, CategoryInfo.Gaming)),
            new("input", "Input & USB", "", () => new CategoryPageViewModel("Input & USB",
                "Pointer behavior, keyboard interruptions and input diagnostics.", services,
                o => Cat(o, OptimizationCategory.InputUsb) || o.Id == OptimizationCatalog.Ids.UsbSelectiveSuspend, premium, CategoryInfo.Input)),
            new("network", "Network", "", () => new NetworkViewModel(services)),
            new("storage", "Storage", "", () => new CategoryPageViewModel("Storage",
                "Drive health indicators and TRIM.", services, o => Cat(o, OptimizationCategory.Storage), premium, CategoryInfo.Storage)),
            new("power", "Power Management", "", () => new CategoryPageViewModel("Power Management",
                "Power plans, processor policy and USB power saving.", services, o => Cat(o, OptimizationCategory.Power), premium, CategoryInfo.Power)),
            new("benchmark", "Benchmarks", "", () => new BenchmarkViewModel(services)),
            new("restore", "Restore Center", "", () => new RestoreCenterViewModel(services)),
            new("settings", "Settings", "", () => new SettingsViewModel(services)),
            new("license", "License & Account", "", () => new LicenseViewModel(services, deviceId)),
        };

        RestartAsAdminCommand = new RelayCommand(() =>
        {
            if (!_services.App.RestartElevated())
                _ = _services.Dialogs.ShowMessageAsync("Administrator rights", "Sloomy Tweaks is still running without administrator rights.");
        }, () => !IsElevated);
        NavigateCommand = new AsyncRelayCommand(async p => { if (p is NavItemViewModel item) await NavigateAsync(item); });

        _services.License.Changed += (_, _) => UiThread.Dispatch(() => _ = OnLicenseChangedAsync());
    }

    public IReadOnlyList<NavItemViewModel> NavItems { get; }
    public ActivationViewModel Activation { get; }
    public string ProductName => "Sloomy Tweaks";
    public string Version => _services.App.ApplicationVersion;
    public bool IsElevated => _services.App.IsElevated;
    public string ElevationText => IsElevated ? "Administrator" : "Standard user";

    /// <summary>The application is usable only with a valid license; otherwise only the activation screen is shown.</summary>
    public bool IsUnlocked => _initialized && _services.License.Current.HasPremiumAccess;
    public bool IsLocked => _initialized && !IsUnlocked;
    public bool IsStarting => !_initialized;

    /// <summary>Shown permanently in development builds (licensing.json environment = development).</summary>
    public string? DevelopmentBadge => _services.License.IsDevelopmentMode ? "DEVELOPMENT MODE" : null;

    public string LicenseBadge => _services.License.Current.Status switch
    {
        LicenseStatus.Active => "Licensed",
        LicenseStatus.OfflineGrace => "Licensed · offline",
        LicenseStatus.Expired => "License expired",
        LicenseStatus.Revoked => "License revoked",
        LicenseStatus.NotConfigured => "Licensing not configured",
        _ => "Not activated",
    };

    public PageViewModel? CurrentPage { get => _currentPage; private set => SetProperty(ref _currentPage, value); }

    public RelayCommand RestartAsAdminCommand { get; }
    public AsyncRelayCommand NavigateCommand { get; }

    public async Task InitializeAsync()
    {
        try
        {
            await _services.License.InitializeAsync();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _services.Log.Log(Core.Diagnostics.LogLevel.Error, "license.init_failed", e.Message, exception: e);
        }
        _initialized = true;
        Activation.Message = _services.License.Current.HasPremiumAccess ? null : _services.License.Current.Message;
        RaiseLicense();
        if (IsUnlocked) await NavigateAsync(NavItems[0]);
        StartLicenseRecheck(LicenseRecheckInterval);
    }

    public async Task NavigateAsync(NavItemViewModel item)
    {
        if (!IsUnlocked) return; // nothing behind the activation screen is reachable without a license
        CurrentPage?.OnNavigatedFrom();
        if (_selected is not null) _selected.IsSelected = false;
        _selected = item;
        item.IsSelected = true;
        if (!_pages.TryGetValue(item.Key, out PageViewModel? page))
        {
            page = item.Factory();
            _pages[item.Key] = page;
        }
        CurrentPage = page;
        await page.OnNavigatedToAsync();
    }

    /// <summary>Re-validates the license periodically so revocation and expiry reach long-running sessions.</summary>
    public void StartLicenseRecheck(TimeSpan interval)
    {
        _recheck?.Cancel();
        _recheck = new CancellationTokenSource();
        CancellationToken ct = _recheck.Token;
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(interval);
            try
            {
                while (await timer.WaitForNextTickAsync(ct))
                {
                    try { await _services.License.InitializeAsync(ct); }
                    catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
                    {
                        _services.Log.Log(Core.Diagnostics.LogLevel.Warning, "license.recheck_failed", e.Message, exception: e);
                    }
                }
            }
            catch (OperationCanceledException) { }
        }, ct);
    }

    public void StopLicenseRecheck() => _recheck?.Cancel();

    private async Task OnLicenseChangedAsync()
    {
        if (!_initialized) return;
        RaiseLicense();
        if (IsUnlocked)
        {
            Activation.Message = null;
            foreach (var page in _pages.Values.OfType<CategoryPageViewModel>()) page.RefreshLicenseState();
            if (CurrentPage is null) await NavigateAsync(_selected ?? NavItems[0]);
        }
        else
        {
            // Lock: leave the current page (stops its live updates) and drop cached pages.
            CurrentPage?.OnNavigatedFrom();
            CurrentPage = null;
            _pages.Clear();
            Activation.Message = _services.License.Current.Message;
        }
    }

    private void RaiseLicense()
    {
        OnPropertyChanged(nameof(LicenseBadge));
        OnPropertyChanged(nameof(IsUnlocked));
        OnPropertyChanged(nameof(IsLocked));
        OnPropertyChanged(nameof(IsStarting));
        OnPropertyChanged(nameof(DevelopmentBadge));
    }
}
