using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using SloomyTweaks.App.Services;
using SloomyTweaks.Core.Diagnostics;
using SloomyTweaks.Core.Engine;
using SloomyTweaks.Core.Licensing;
using SloomyTweaks.Core.Optimizations;
using SloomyTweaks.Core.Platform;
using SloomyTweaks.Data;
using SloomyTweaks.Platform.Windows;
using SloomyTweaks.Presentation.Mvvm;
using SloomyTweaks.Presentation.Services;
using SloomyTweaks.Presentation.ViewModels;

namespace SloomyTweaks.App;

/// <summary>Composition root: wires real Windows services into the view models.</summary>
public partial class App : Application
{
    private Mutex? _singleInstance;
    private IAppLogger _log = NullLogger.Instance;
    private AppDataStore? _store;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, @"Local\SloomyTweaks.SingleInstance", out bool isFirst);
        if (!isFirst && e.Args.Contains("--after-elevation"))
        {
            // The unelevated instance is shutting down; wait briefly for it to release the mutex.
            isFirst = _singleInstance.WaitOne(TimeSpan.FromSeconds(10));
        }
        if (!isFirst)
        {
            MessageBox.Show("Sloomy Tweaks is already running.", "Sloomy Tweaks", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        UiThread.Dispatch = action =>
        {
            if (Dispatcher.CheckAccess()) action();
            else Dispatcher.BeginInvoke(action);
        };

        string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sloomy Tweaks");
        string logDir = Path.Combine(dataDir, "logs");
        Directory.CreateDirectory(dataDir);

        try
        {
            _store = new AppDataStore(Path.Combine(dataDir, "sloomy.db"));
            var settings = _store.Load();
            _log = new JsonLinesFileLogger(logDir, LogLevel.Information, settings.LogRetentionDays);
            _store.PruneBenchmarks(settings.BenchmarkHistoryLimit);

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                _log.Error("app.unobserved_task", args.Exception.Message, args.Exception);
                args.SetObserved();
            };

            var registry = new WindowsRegistryAccess();
            var power = new WindowsPowerManagement();
            var mouse = new WindowsMouseSettings();
            var privileges = new WindowsPrivilegeService();
            var hardware = new WindowsHardwareInfoProvider(power, privileges);
            var device = new WindowsDeviceIdentity();

            var engine = new OptimizationEngine(
                OptimizationCatalog.Create(registry, power, mouse),
                hardware.GetContext,
                privileges,
                backups: _store,
                history: _store,
                new SystemClock(),
                _log,
                new WindowsRestorePointService())
            {
                CreateRestorePoints = settings.CreateRestorePoints,
            };

            LicenseService license = CreateLicenseService(device, Path.Combine(dataDir, "license.bin"));

            var services = new AppServices(
                engine, hardware, new WindowsUsageSampler(), license,
                History: _store, Backups: _store, Benchmarks: _store, Settings: _store,
                new WindowsProcessServiceInfo(),
                new WpfDialogService(),
                new WpfAppActions(dataDir, logDir, privileges.IsElevated, ReleaseSingleInstance),
                _log);

            var main = new MainViewModel(services, device.DeviceId);
            var window = new MainWindow { DataContext = main };
            MainWindow = window;
            if (e.Args.Contains("--minimized")) window.WindowState = WindowState.Minimized;
            window.Show();
            _log.Info("app.started", "Sloomy Tweaks started", new Dictionary<string, object?> { ["elevated"] = privileges.IsElevated });

            await main.InitializeAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Error("app.startup_failed", ex.Message, ex);
            MessageBox.Show($"Sloomy Tweaks could not start:\n\n{ex.Message}", "Sloomy Tweaks", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private LicenseService CreateLicenseService(IDeviceIdentity device, string tokenPath)
    {
        string version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        LicensingConfig? config = LicensingConfig.TryLoad(Path.Combine(AppContext.BaseDirectory, "licensing.json"));
        if (config is null)
        {
            _log.Warn("license.not_configured", "licensing.json is missing or invalid; licensing is disabled in this build.");
            return new LicenseService(null, new DpapiTokenStore(tokenPath), device, new Dictionary<string, ECDsa>(), new SystemClock(), _log, version);
        }

        var http = new HttpClient { BaseAddress = new Uri(config.ServerUrl!.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"SloomyTweaks/{version}");
        var keys = LicensePublicKeys.FromPem(config.Keys.Select(k => (k.Kid, k.PublicKeyPem)));
        return new LicenseService(new HttpLicenseApi(http), new DpapiTokenStore(tokenPath), device, keys, new SystemClock(), _log, version);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log.Error("app.unhandled", e.Exception.Message, e.Exception);
        MessageBox.Show($"An unexpected error occurred and was logged:\n\n{e.Exception.Message}", "Sloomy Tweaks", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    private void ReleaseSingleInstance()
    {
        try { _singleInstance?.ReleaseMutex(); }
        catch (ApplicationException) { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _store?.Dispose();
        ReleaseSingleInstance();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
