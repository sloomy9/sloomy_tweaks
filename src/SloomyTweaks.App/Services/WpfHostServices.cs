using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using SloomyTweaks.App.Views;
using SloomyTweaks.Platform.Windows;
using SloomyTweaks.Presentation.Services;

namespace SloomyTweaks.App.Services;

public sealed class WpfDialogService : IDialogService
{
    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = false)
    {
        var dialog = new ConfirmDialog(title, message, confirmText, destructive, showCancel: true) { Owner = Application.Current.MainWindow };
        return Task.FromResult(dialog.ShowDialog() == true);
    }

    public Task ShowMessageAsync(string title, string message)
    {
        new ConfirmDialog(title, message, "OK", false, showCancel: false) { Owner = Application.Current.MainWindow }.ShowDialog();
        return Task.CompletedTask;
    }

    public string? PickOpenFile(string title, string filter)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dlg.ShowDialog(Application.Current.MainWindow) == true ? dlg.FileName : null;
    }

    public string? PickSaveFile(string title, string filter, string defaultFileName)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Title = title, Filter = filter, FileName = defaultFileName, OverwritePrompt = true };
        return dlg.ShowDialog(Application.Current.MainWindow) == true ? dlg.FileName : null;
    }

    public void OpenUrl(string url)
    {
        // Only web links from the evidence registry are opened; anything else is ignored.
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    public void OpenFolder(string path)
    {
        if (Directory.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false });
    }
}

public sealed class WpfAppActions : IAppActions
{
    private readonly StartupRegistration _startup = new();
    private readonly Action _beforeRestart;

    public WpfAppActions(string dataDirectory, string logDirectory, bool isElevated, Action beforeRestart)
    {
        DataDirectory = dataDirectory;
        LogDirectory = logDirectory;
        IsElevated = isElevated;
        _beforeRestart = beforeRestart;
        ApplicationVersion = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.0.0";
    }

    public string ApplicationVersion { get; }
    public string LogDirectory { get; }
    public string DataDirectory { get; }
    public bool IsElevated { get; }

    public bool RestartElevated()
    {
        string? exe = Environment.ProcessPath;
        if (exe is null) return false;
        try
        {
            _beforeRestart();
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas", ArgumentList = { "--after-elevation" } });
            Application.Current.Shutdown();
            return true;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) // ERROR_CANCELLED: the user declined the UAC prompt
        {
            return false;
        }
    }

    public bool IsStartWithWindowsEnabled() => _startup.IsEnabled();

    public void SetStartWithWindows(bool enabled)
    {
        string? exe = Environment.ProcessPath;
        if (exe is not null) _startup.SetEnabled(enabled, exe);
    }
}

/// <summary>
/// Licensing configuration shipped next to the executable (licensing.json). Contains only the
/// license server URL and public verification keys — never a secret. Treated as untrusted input.
/// </summary>
public sealed class LicensingConfig
{
    public string? ServerUrl { get; set; }
    public List<PublicKeyEntry> Keys { get; set; } = new();

    public sealed class PublicKeyEntry
    {
        public string Kid { get; set; } = string.Empty;
        public string PublicKeyPem { get; set; } = string.Empty;
    }

    public static LicensingConfig? TryLoad(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var cfg = JsonSerializer.Deserialize<LicensingConfig>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (cfg?.ServerUrl is null || !Uri.TryCreate(cfg.ServerUrl, UriKind.Absolute, out Uri? uri)) return null;
            if (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback) return null;
            if (cfg.Keys.Count == 0 || cfg.Keys.Any(k => string.IsNullOrWhiteSpace(k.Kid) || !k.PublicKeyPem.Contains("PUBLIC KEY", StringComparison.Ordinal))) return null;
            return cfg;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
