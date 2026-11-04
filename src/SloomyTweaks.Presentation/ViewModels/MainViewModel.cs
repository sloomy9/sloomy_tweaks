using SloomyTweaks.Core.Licensing;
using SloomyTweaks.Core.Optimizations;
using SloomyTweaks.Presentation.Mvvm;
using SloomyTweaks.Presentation.Services;

namespace SloomyTweaks.Presentation.ViewModels;

public sealed class NavItemViewModel : ObservableObject
{
    private bool _isSelected;

    public NavItemViewModel(string key, string title, string glyph, bool requiresLicense, Func<PageViewModel> factory)
    {
        Key = key;
        Title = title;
        Glyph = glyph;
        RequiresLicense = requiresLicense;
        Factory = factory;
    }

    public string Key { get; }
    public string Title { get; }
    /// <summary>Segoe Fluent Icons / Segoe MDL2 Assets code point.</summary>
    public string Glyph { get; }
    public bool RequiresLicense { get; }
    internal Func<PageViewModel> Factory { get; }
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}

/// <summary>Shell: sidebar navigation, license gating, elevation state and first-run activation.</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Dictionary<string, PageViewModel> _pages = new();
    private PageViewModel? _currentPage;
    private NavItemViewModel? _selected;
    private bool _showActivation;
    private string _activationKey = string.Empty;
    private string? _activationMessage;

    public MainViewModel(AppServices services, string deviceId)
    {
        _services = services;
        Func<bool> premium = () => _services.License.Current.HasPremiumAccess;
        bool Cat(Optimization o, OptimizationCategory c) => o.Definition.Category == c;

        NavItems = new List<NavItemViewModel>
        {
            new("dashboard", "Dashboard", "", false, () => new DashboardViewModel(services)),
            new("cpu", "CPU Optimization", "", false, () => new CategoryPageViewModel("CPU Optimization",
                "Processor detection, scheduling and power behavior.", services, o => Cat(o, OptimizationCategory.Cpu), premium, CategoryInfo.Cpu)),
            new("gpu", "GPU Optimization", "", false, () => new CategoryPageViewModel("GPU Optimization",
                "Graphics scheduling, presentation and latency features for NVIDIA, AMD and Intel.", services, o => Cat(o, OptimizationCategory.Gpu), premium, CategoryInfo.Gpu)),
            new("memory", "RAM & Memory", "", false, () => new CategoryPageViewModel("RAM & Memory",
                "Memory capacity and usage. No placebo memory cleaning.", services, o => Cat(o, OptimizationCategory.Memory), premium, CategoryInfo.Memory)),
            new("windows", "Windows Optimization", "", false, () => new CategoryPageViewModel("Windows Optimization",
                "Game Mode, capture features and other Windows settings with documented effects.", services, o => Cat(o, OptimizationCategory.Windows), premium)),
            new("processes", "Processes & Services", "", true, () => new ProcessesViewModel(services)),
            new("gaming", "Gaming Optimization", "", false, () => new CategoryPageViewModel("Gaming Optimization",
                "Game profiles, starting with Fortnite. Measured, per-system configuration.", services,
                o => Cat(o, OptimizationCategory.Gaming) || o.Id is OptimizationCatalog.Ids.NvidiaReflex or OptimizationCatalog.Ids.WindowedGameOptimizations or OptimizationCatalog.Ids.GameMode, premium)),
            new("input", "Input & USB", "", false, () => new CategoryPageViewModel("Input & USB",
                "Pointer behavior and input configuration. No unsupported timer or interrupt tweaks.", services, o => Cat(o, OptimizationCategory.InputUsb), premium)),
            new("network", "Network Optimization", "", true, () => new NetworkViewModel(services)),
            new("storage", "Storage Optimization", "", false, () => new CategoryPageViewModel("Storage Optimization",
                "Drive overview.", services, o => Cat(o, OptimizationCategory.Storage), premium, CategoryInfo.Storage,
                "No storage optimizations currently meet Sloomy Tweaks' evidence requirements, so none are offered.")),
            new("power", "Power Management", "", false, () => new CategoryPageViewModel("Power Management",
                "Power plans and their trade-offs.", services, o => Cat(o, OptimizationCategory.Power), premium, CategoryInfo.Power)),
            new("benchmark", "Benchmark Center", "", true, () => new BenchmarkViewModel(services)),
            new("history", "Optimization History", "", false, () => new HistoryViewModel(services)),
            new("restore", "Restore Center", "", false, () => new RestoreCenterViewModel(services)),
            new("settings", "Settings", "", false, () => new SettingsViewModel(services)),
            new("license", "License & Account", "", false, () => new LicenseViewModel(services, deviceId)),
        };

        RestartAsAdminCommand = new RelayCommand(() =>
        {
            if (!_services.App.RestartElevated())
                _ = _services.Dialogs.ShowMessageAsync("Administrator rights", "Sloomy Tweaks is still running without administrator rights.");
        }, () => !IsElevated);
        ActivateCommand = new AsyncRelayCommand(ActivateFromOverlayAsync, () => !string.IsNullOrWhiteSpace(ActivationKey));
        SkipActivationCommand = new RelayCommand(() => ShowActivation = false);
        NavigateCommand = new AsyncRelayCommand(async p => { if (p is NavItemViewModel item) await NavigateAsync(item); });

        _services.License.Changed += (_, _) => UiThread.Dispatch(() => _ = OnLicenseChangedAsync());
    }

    public IReadOnlyList<NavItemViewModel> NavItems { get; }
    public string ProductName => "Sloomy Tweaks";
    public string Version => _services.App.ApplicationVersion;
    public bool IsElevated => _services.App.IsElevated;
    public string ElevationText => IsElevated ? "Administrator" : "Standard user";

    public PageViewModel? CurrentPage { get => _currentPage; private set => SetProperty(ref _currentPage, value); }

    public NavItemViewModel? SelectedItem
    {
        get => _selected;
        set { if (value is not null && value != _selected) _ = NavigateAsync(value); }
    }

    public string LicenseBadge => _services.License.Current.Status switch
    {
        LicenseStatus.Active => "Licensed",
        LicenseStatus.OfflineGrace => "Licensed · offline",
        LicenseStatus.Expired => "License expired",
        LicenseStatus.Revoked => "License revoked",
        LicenseStatus.NotConfigured => "Licensing not configured",
        _ => "Not activated",
    };
    public bool HasPremium => _services.License.Current.HasPremiumAccess;

    public bool ShowActivation { get => _showActivation; set => SetProperty(ref _showActivation, value); }
    public string ActivationKey { get => _activationKey; set { SetProperty(ref _activationKey, value); ActivateCommand.RaiseCanExecuteChanged(); } }
    public string? ActivationMessage { get => _activationMessage; private set => SetProperty(ref _activationMessage, value); }

    public RelayCommand RestartAsAdminCommand { get; }
    public AsyncRelayCommand ActivateCommand { get; }
    public RelayCommand SkipActivationCommand { get; }
    public AsyncRelayCommand NavigateCommand { get; }

    public async Task InitializeAsync()
    {
        LicenseSnapshot snapshot;
        try
        {
            snapshot = await _services.License.InitializeAsync();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _services.Log.Log(Core.Diagnostics.LogLevel.Error, "license.init_failed", e.Message, exception: e);
            snapshot = _services.License.Current;
        }
        ShowActivation = !snapshot.HasPremiumAccess && snapshot.Status != LicenseStatus.NotConfigured;
        ActivationMessage = snapshot.HasPremiumAccess ? null : snapshot.Message;
        RaiseLicense();
        await NavigateAsync(NavItems[0]);
    }

    public async Task NavigateAsync(NavItemViewModel item)
    {
        CurrentPage?.OnNavigatedFrom();
        if (_selected is not null) _selected.IsSelected = false;
        _selected = item;
        item.IsSelected = true;
        OnPropertyChanged(nameof(SelectedItem));

        PageViewModel page;
        if (item.RequiresLicense && !HasPremium)
        {
            page = new LockedPageViewModel(item.Title, () => _ = NavigateAsync(NavItems.First(n => n.Key == "license")));
        }
        else
        {
            if (!_pages.TryGetValue(item.Key, out page!))
            {
                page = item.Factory();
                _pages[item.Key] = page;
            }
        }
        CurrentPage = page;
        await page.OnNavigatedToAsync();
    }

    private async Task ActivateFromOverlayAsync()
    {
        LicenseSnapshot s = await _services.License.ActivateAsync(ActivationKey);
        ActivationMessage = s.Message;
        if (s.HasPremiumAccess)
        {
            ActivationKey = string.Empty;
            ShowActivation = false;
        }
    }

    private async Task OnLicenseChangedAsync()
    {
        RaiseLicense();
        foreach (var page in _pages.Values.OfType<CategoryPageViewModel>()) page.RefreshLicenseState();
        // Re-evaluate gating for the current page (unlock after activation, lock after revocation).
        if (_selected is { RequiresLicense: true }) await NavigateAsync(_selected);
    }

    private void RaiseLicense()
    {
        OnPropertyChanged(nameof(LicenseBadge));
        OnPropertyChanged(nameof(HasPremium));
    }
}
