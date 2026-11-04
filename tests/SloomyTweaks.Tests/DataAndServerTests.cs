using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using SloomyTweaks.Core.Benchmark;
using SloomyTweaks.Core.Diagnostics;
using SloomyTweaks.Core.Engine;
using SloomyTweaks.Core.Licensing;
using SloomyTweaks.Core.Optimizations;
using SloomyTweaks.Core.Settings;
using SloomyTweaks.Data;
using SloomyTweaks.LicenseServer;
using SloomyTweaks.Licensing;
using SloomyTweaks.Tests.TestFramework;

namespace SloomyTweaks.Tests;

public sealed class AppDataStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sloomy-test-").FullName;
    private string DbPath => Path.Combine(_dir, "app.db");

    [Fact]
    public void Backups_history_benchmarks_and_settings_persist_across_reopen()
    {
        var values = new Dictionary<string, SettingValue> { ["HKCU\\X\\Y"] = SettingValue.Absent("dword"), ["P"] = new(true, "guid", Guid.Empty.ToString()) };
        using (var store = new AppDataStore(DbPath))
        {
            ((IBackupStore)store).Save(new OptimizationBackup("opt.a", DateTimeOffset.UtcNow, values));
            ((IHistoryStore)store).Append(new OperationRecord(0, "opt.a", OperationAction.Apply, DateTimeOffset.UtcNow, true, "ok", values, values));
            ((IBenchmarkRepository)store).Save(new BenchmarkRun
            {
                ConfigurationLabel = "Baseline", CreatedAt = DateTimeOffset.UtcNow, Application = "game.exe", Source = "PresentMon CSV: a.csv",
                Stats = FrameTimeAnalyzer.Analyze(Enumerable.Repeat(5.0, 200).ToList()),
            });
            ((ISettingsStore)store).Save(new AppSettings { BenchmarkHistoryLimit = 42, CreateRestorePoints = false });
        }

        using (var reopened = new AppDataStore(DbPath))
        {
            OptimizationBackup b = Assert.NotNull(((IBackupStore)reopened).Get("opt.a"));
            Assert.False(b.OriginalValues["HKCU\\X\\Y"].Exists);
            Assert.True(Assert.NotNull(((IHistoryStore)reopened).LatestSuccessful("opt.a")).Success);
            BenchmarkRun run = ((IBenchmarkRepository)reopened).List().Single();
            Assert.Equal(200.0, run.Stats.AverageFps, 1e-9);
            Assert.Equal(42, ((ISettingsStore)reopened).Load().BenchmarkHistoryLimit);
            Assert.False(((ISettingsStore)reopened).Load().CreateRestorePoints);
        }
    }

    [Fact]
    public void Sql_parameters_are_bound_not_concatenated()
    {
        using var store = new AppDataStore(DbPath);
        string hostile = "x'); DROP TABLE backups; --";
        ((IBackupStore)store).Save(new OptimizationBackup(hostile, DateTimeOffset.UtcNow, new Dictionary<string, SettingValue>()));
        Assert.NotNull(((IBackupStore)store).Get(hostile));
        Assert.Equal(1, ((IBackupStore)store).List().Count);
    }

    [Fact]
    public void Engine_works_end_to_end_with_sqlite_persistence()
    {
        using var store = new AppDataStore(DbPath);
        var mouse = new FakeMouse();
        var engine = new OptimizationEngine(TestCatalog.Create(new FakeRegistry(), new FakePower(), mouse),
            () => TestSystems.Win11(), new FakePrivileges(true), store, store, new FakeClock(), NullLogger.Instance);
        Assert.True(engine.ApplyAsync(OptimizationCatalog.Ids.PointerPrecision, false).Result.Success);
        Assert.NotNull(((IBackupStore)store).Get(OptimizationCatalog.Ids.PointerPrecision));
        Assert.True(engine.RestoreAsync(OptimizationCatalog.Ids.PointerPrecision).Result.Success);
        Assert.Equal(1, mouse.Value.Acceleration);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}

/// <summary>
/// Integration tests: the real license server on a loopback port, exercised through the real
/// client (HttpLicenseApi + LicenseService) and the admin API.
/// </summary>
public sealed class LicenseServerIntegrationTests : IAsyncDisposable
{
    private const string AdminKey = "test-admin-key-please-change";
    private readonly string _dir = Directory.CreateTempSubdirectory("sloomy-server-").FullName;
    private readonly FakeTimeProvider _time = new();
    private readonly FakeClock _clientClock = new();
    private WebApplication? _app;
    private HttpClient? _http;
    private ECDsa? _publicKey;

    private async Task<(HttpClient Http, Uri Base)> StartAsync()
    {
        using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            File.WriteAllText(Path.Combine(_dir, "signing.pem"), key.ExportPkcs8PrivateKeyPem());
            _publicKey = ECDsa.Create();
            _publicKey.ImportSubjectPublicKeyInfo(key.ExportSubjectPublicKeyInfo(), out _);
        }
        _app = LicenseServerApp.Build(new[] { "--environment", "Development", "--urls", "http://127.0.0.1:0", "--Logging:LogLevel:Default=Warning" }, o =>
        {
            o.DatabasePath = Path.Combine(_dir, "licenses.db");
            o.SigningKeyPath = Path.Combine(_dir, "signing.pem");
            o.AdminKeySha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AdminKey))).ToLowerInvariant();
            o.KeyPepper = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
            o.ActivationRequestsPerMinute = 10_000;
        }, _time);
        await _app.StartAsync();
        var baseUri = new Uri(_app.Urls.First().TrimEnd('/') + "/");
        _http = new HttpClient { BaseAddress = baseUri };
        return (_http, baseUri);
    }

    private LicenseService Client(FakeDevice device, InMemoryTokenStore store) =>
        new(new HttpLicenseApi(_http!), store, device, new Dictionary<string, ECDsa> { ["k1"] = _publicKey! }, _clientClock, NullLogger.Instance, "0.1.0");

    private async Task<CreatedLicense> CreateLicenseAsync(string plan = "30", int maxDevices = 2, DateTimeOffset? expiresAt = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "admin/api/licenses")
        {
            Content = JsonContent.Create(new { plan, maxDevices, quantity = 1, customerEmail = "buyer@example.com", expiresAt }),
        };
        req.Headers.Add(LicenseServerApp.AdminKeyHeader, AdminKey);
        HttpResponseMessage res = await _http!.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<List<CreatedLicense>>(LicensingJson.Options))!.Single();
    }

    private async Task AdminPostAsync(string path, object? body = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body ?? new { }) };
        req.Headers.Add(LicenseServerApp.AdminKeyHeader, AdminKey);
        HttpResponseMessage res = await _http!.SendAsync(req);
        Assert.True(res.IsSuccessStatusCode, $"{path} → {(int)res.StatusCode}");
    }

    [Fact]
    public async Task Full_activation_refresh_revocation_cycle()
    {
        await StartAsync();
        CreatedLicense lic = await CreateLicenseAsync();
        var store = new InMemoryTokenStore();
        var device = new FakeDevice();

        LicenseSnapshot activated = await Client(device, store).ActivateAsync(lic.LicenseKey.ToLowerInvariant());
        Assert.Equal(LicenseStatus.Active, activated.Status);
        Assert.Equal(LicensePlan.Days30, activated.Plan);
        // Duration starts at first activation.
        Assert.Equal(_time.Now.AddDays(30).ToUnixTimeSeconds(), activated.LicenseExpiresAt!.Value.ToUnixTimeSeconds());

        // Refresh after 13 h succeeds.
        _time.Now = _time.Now.AddHours(13);
        _clientClock.UtcNow = _time.Now;
        Assert.Equal(LicenseStatus.Active, (await Client(device, store).InitializeAsync()).Status);

        // Revoke → next refresh reports revoked and clears the token.
        await AdminPostAsync($"admin/api/licenses/{lic.LicenseId}/revoke", new { reason = "chargeback" });
        _time.Now = _time.Now.AddHours(13);
        _clientClock.UtcNow = _time.Now;
        Assert.Equal(LicenseStatus.Revoked, (await Client(device, store).InitializeAsync()).Status);
        Assert.Null(store.Load());
    }

    [Fact]
    public async Task Device_limit_is_enforced_and_deactivation_frees_a_slot()
    {
        await StartAsync();
        CreatedLicense lic = await CreateLicenseAsync(maxDevices: 1);
        var storeA = new InMemoryTokenStore();
        var a = new FakeDevice("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "PC-A");
        var b = new FakeDevice("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "PC-B");

        Assert.Equal(LicenseStatus.Active, (await Client(a, storeA).ActivateAsync(lic.LicenseKey)).Status);
        // Re-activating the same device does not consume another slot.
        Assert.Equal(LicenseStatus.Active, (await Client(a, storeA).ActivateAsync(lic.LicenseKey)).Status);

        LicenseSnapshot second = await Client(b, new InMemoryTokenStore()).ActivateAsync(lic.LicenseKey);
        Assert.Equal(LicenseStatus.Unlicensed, second.Status);
        Assert.Contains("1 of 1", second.Message);

        await Client(a, storeA).DeactivateAsync();
        Assert.Equal(LicenseStatus.Active, (await Client(b, new InMemoryTokenStore()).ActivateAsync(lic.LicenseKey)).Status);
    }

    [Fact]
    public async Task Expired_and_unknown_keys_are_rejected_by_the_server()
    {
        await StartAsync();
        CreatedLicense lic = await CreateLicenseAsync(plan: "7");
        var device = new FakeDevice();
        var store = new InMemoryTokenStore();
        Assert.Equal(LicenseStatus.Active, (await Client(device, store).ActivateAsync(lic.LicenseKey)).Status);

        _time.Now = _time.Now.AddDays(8);
        _clientClock.UtcNow = _time.Now;
        Assert.Equal(LicenseStatus.Expired, (await Client(device, store).InitializeAsync()).Status);

        LicenseSnapshot unknown = await Client(device, new InMemoryTokenStore()).ActivateAsync(LicenseKeyFormat.Generate());
        Assert.Equal(LicenseStatus.Unlicensed, unknown.Status);
        Assert.Contains("not valid", unknown.Message);
    }

    [Fact]
    public async Task Admin_api_requires_the_admin_key_and_lifetime_has_no_expiry()
    {
        await StartAsync();
        HttpResponseMessage anon = await _http!.GetAsync("admin/api/licenses");
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);

        using var wrong = new HttpRequestMessage(HttpMethod.Get, "admin/api/licenses");
        wrong.Headers.Add(LicenseServerApp.AdminKeyHeader, "guess");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.SendAsync(wrong)).StatusCode);

        CreatedLicense lic = await CreateLicenseAsync(plan: "lifetime");
        LicenseSnapshot s = await Client(new FakeDevice(), new InMemoryTokenStore()).ActivateAsync(lic.LicenseKey);
        Assert.Equal(LicensePlan.Lifetime, s.Plan);
        Assert.Null(s.LicenseExpiresAt);
    }

    [Fact]
    public async Task Database_never_contains_plaintext_license_keys()
    {
        await StartAsync();
        CreatedLicense lic = await CreateLicenseAsync();
        await _app!.StopAsync();
        // The server keeps its SQLite connection until disposal; Windows needs shared read access to open the file meanwhile.
        static string ReadShared(string path)
        {
            if (!File.Exists(path)) return "";
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        string all = ReadShared(Path.Combine(_dir, "licenses.db")) + ReadShared(Path.Combine(_dir, "licenses.db-wal"));
        Assert.False(all.Contains(lic.LicenseKey, StringComparison.Ordinal));
        Assert.False(all.Contains(lic.LicenseKey.Replace("-", ""), StringComparison.Ordinal));
    }

    public async ValueTask DisposeAsync()
    {
        _http?.Dispose();
        if (_app is not null) await _app.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
