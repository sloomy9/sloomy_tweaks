using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using SloomyTweaks.App;
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

namespace SloomyTweaks.UiPreview;

/// <summary>
/// QA harness: same composition as the real app, but the license public key is verified with
/// BouncyCastle so it runs where Windows NCrypt is unavailable. Usage:
///   SloomyTweaks.UiPreview.exe --server http://127.0.0.1:5077/ --pubkey public-key.pem [--activate KEY] [--page cpu] [--expand]
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : ""; }
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/SloomyTweaks;component/Views/Pages.xaml") });
        app.DispatcherUnhandledException += (_, e) =>
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "sloomy-uipreview-errors.txt"), e.Exception + Environment.NewLine);
            e.Handled = true;
        };
        app.Startup += async (_, _) =>
        {
            UiThread.Dispatch = a => { if (app.Dispatcher.CheckAccess()) a(); else app.Dispatcher.BeginInvoke(a); };
            string data = Path.Combine(Path.GetTempPath(), "SloomyTweaksUiPreview");
            Directory.CreateDirectory(data);
            var log = new JsonLinesFileLogger(Path.Combine(data, "logs"));
            var store = new AppDataStore(Path.Combine(data, "preview.db"));

            var registry = new WindowsRegistryAccess();
            var power = new WindowsPowerManagement();
            var mouse = new WindowsMouseSettings();
            var accessibility = new WindowsAccessibilitySettings();
            var fs = new WindowsFileSystemBehavior();
            var privileges = new WindowsPrivilegeService();
            var hardware = new WindowsHardwareInfoProvider(power, privileges);
            var engine = new OptimizationEngine(OptimizationCatalog.Create(registry, power, mouse, accessibility, fs),
                hardware.GetContext, privileges, store, store, new SystemClock(), log);

            var keys = new Dictionary<string, ECDsa> { ["k1"] = new ManagedEcdsaVerifier(File.ReadAllText(Arg("--pubkey"))) };
            var http = new HttpClient { BaseAddress = new Uri(Arg("--server")), Timeout = TimeSpan.FromSeconds(15) };
            var device = new PreviewDevice();
            var license = new LicenseService(new HttpLicenseApi(http), new InMemoryTokenStore(), device, keys, new SystemClock(), log, "0.2.0", developmentMode: true);

            var services = new AppServices(engine, hardware, new WindowsUsageSampler(), license, store, store, store, store,
                new WindowsProcessServiceInfo(), new WpfDialogService(), new WpfAppActions(data, Path.Combine(data, "logs"), privileges.IsElevated, () => { }), log,
                new WindowsSystemDiagnostics(power, fs));
            var main = new MainViewModel(services, device.DeviceId);
            var window = new MainWindow { DataContext = main, Width = 1440, Height = 900, Left = 40, Top = 40, WindowStartupLocation = WindowStartupLocation.Manual };
            app.MainWindow = window;
            window.Show();
            await main.InitializeAsync();

            if (Arg("--activate") is { Length: > 0 } key)
            {
                main.Activation.LicenseKey = key;
                await main.Activation.ActivateCommand.ExecuteAsync();
                await Task.Delay(500);
            }
            if (Arg("--page") is { Length: > 0 } page && main.NavItems.FirstOrDefault(n => n.Key == page) is { } item)
            {
                await main.NavigateAsync(item);
                if (args.Contains("--expand") && main.CurrentPage is CategoryPageViewModel cat && cat.Cards.Count > 0)
                    cat.Cards[0].IsExpanded = true;
            }
        };
        return app.Run();
    }
}

internal sealed class PreviewDevice : IDeviceIdentity
{
    public string DeviceId => "a1b2c3d4e5f60718293a4b5c6d7e8f90";
    public string DeviceName => "UI-PREVIEW";
}

/// <summary>Verify-only P-256 ECDSA backed by BouncyCastle (QA harness only).</summary>
internal sealed class ManagedEcdsaVerifier : ECDsa
{
    private readonly ECPublicKeyParameters _key;

    public ManagedEcdsaVerifier(string pem)
    {
        string b64 = string.Concat(pem.Split('\n').Where(l => !l.StartsWith("-----", StringComparison.Ordinal)).Select(l => l.Trim()));
        _key = (ECPublicKeyParameters)PublicKeyFactory.CreateKey(Convert.FromBase64String(b64));
        KeySizeValue = 256;
    }

    public override byte[] SignHash(byte[] hash) => throw new NotSupportedException();

    public override bool VerifyHash(byte[] hash, byte[] signature)
    {
        if (signature.Length != 64) return false;
        var r = new Org.BouncyCastle.Math.BigInteger(1, signature, 0, 32);
        var s = new Org.BouncyCastle.Math.BigInteger(1, signature, 32, 32);
        var verifier = new ECDsaSigner();
        verifier.Init(false, _key);
        return verifier.VerifySignature(hash, r, s);
    }

    protected override byte[] HashData(byte[] data, int offset, int count, HashAlgorithmName hashAlgorithm) =>
        SHA256.HashData(data.AsSpan(offset, count));

    protected override byte[] HashData(Stream data, HashAlgorithmName hashAlgorithm) => SHA256.HashData(data);

    public override ECParameters ExportParameters(bool includePrivateParameters) => throw new NotSupportedException();
    public override ECParameters ExportExplicitParameters(bool includePrivateParameters) => throw new NotSupportedException();
    public override void ImportParameters(ECParameters parameters) => throw new NotSupportedException();
    public override void GenerateKey(ECCurve curve) => throw new NotSupportedException();
}
