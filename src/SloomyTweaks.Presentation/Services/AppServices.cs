using SloomyTweaks.Core.Benchmark;
using SloomyTweaks.Core.Diagnostics;
using SloomyTweaks.Core.Engine;
using SloomyTweaks.Core.Hardware;
using SloomyTweaks.Core.Licensing;
using SloomyTweaks.Core.Processes;
using SloomyTweaks.Core.Settings;

namespace SloomyTweaks.Presentation.Services;

/// <summary>UI-framework services the view models need. Implemented by the WPF app; faked in tests.</summary>
public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = false);
    Task ShowMessageAsync(string title, string message);
    string? PickOpenFile(string title, string filter);
    string? PickSaveFile(string title, string filter, string defaultFileName);
    void OpenUrl(string url);
    void OpenFolder(string path);
    /// <summary>Opens a Windows Settings page (ms-settings: URI from <see cref="WindowsSettingsLinks"/>).</summary>
    void OpenWindowsSettings(string uri);
}

/// <summary>Documented ms-settings: pages Sloomy Tweaks links to. Only these URIs are ever opened.</summary>
public static class WindowsSettingsLinks
{
    public const string GameMode = "ms-settings:gaming-gamemode";
    public const string Captures = "ms-settings:gaming-gamedvr";
    public const string Graphics = "ms-settings:display-advancedgraphics";
    public const string StartupApps = "ms-settings:startupapps";
    public const string Power = "ms-settings:powersleep";
    public const string Mouse = "ms-settings:mousetouchpad";
    public const string Keyboard = "ms-settings:easeofaccess-keyboard";
    public const string Storage = "ms-settings:storagesense";
    public const string Network = "ms-settings:network-status";
    public const string Display = "ms-settings:display";

    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        GameMode, Captures, Graphics, StartupApps, Power, Mouse, Keyboard, Storage, Network, Display,
    };
}

/// <summary>Application-level actions that depend on the host process.</summary>
public interface IAppActions
{
    string ApplicationVersion { get; }
    string LogDirectory { get; }
    string DataDirectory { get; }
    bool IsElevated { get; }
    /// <summary>Relaunches Sloomy Tweaks elevated (UAC prompt). Returns false if the user cancelled.</summary>
    bool RestartElevated();
    bool IsStartWithWindowsEnabled();
    void SetStartWithWindows(bool enabled);
}

/// <summary>Everything the view models depend on, assembled by the composition root.</summary>
public sealed record AppServices(
    OptimizationEngine Engine,
    IHardwareInfoProvider Hardware,
    IUsageSampler Usage,
    LicenseService License,
    IHistoryStore History,
    IBackupStore Backups,
    IBenchmarkRepository Benchmarks,
    ISettingsStore Settings,
    IProcessServiceInfo Processes,
    IDialogService Dialogs,
    IAppActions App,
    IAppLogger Log,
    ISystemDiagnostics Diagnostics);
