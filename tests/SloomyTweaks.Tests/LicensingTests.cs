using System.Security.Cryptography;
using SloomyTweaks.Core.Diagnostics;
using SloomyTweaks.Core.Licensing;
using SloomyTweaks.Licensing;
using SloomyTweaks.Tests.TestFramework;

namespace SloomyTweaks.Tests;

public class LicenseKeyFormatTests
{
    [Fact]
    public void Generated_keys_round_trip_and_are_unique()
    {
        var keys = Enumerable.Range(0, 200).Select(_ => LicenseKeyFormat.Generate()).ToList();
        Assert.Equal(200, keys.Distinct().Count());
        foreach (string k in keys)
        {
            Assert.True(LicenseKeyFormat.TryNormalize(k, out string n));
            Assert.Equal(k, n);
            Assert.Equal(28, k.Length); // SLMY + 4 × (dash + 5)
        }
    }

    [Fact]
    public void Normalization_accepts_lowercase_spaces_and_ambiguous_letters()
    {
        string key = LicenseKeyFormat.Generate();
        string messy = "  " + key.ToLowerInvariant().Replace("-", " ").Replace('0', 'o').Replace('1', 'l') + " ";
        Assert.True(LicenseKeyFormat.TryNormalize(messy, out string n));
        Assert.Equal(key, n);
    }

    [Fact]
    public void Single_symbol_typos_are_detected()
    {
        string key = LicenseKeyFormat.Generate();
        char[] chars = key.ToCharArray();
        int pos = 7;
        chars[pos] = chars[pos] == 'A' ? 'B' : 'A';
        Assert.False(LicenseKeyFormat.TryNormalize(new string(chars), out _));
        Assert.False(LicenseKeyFormat.TryNormalize("not a key", out _));
    }
}

public class LicenseTokenTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _other = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private Dictionary<string, ECDsa> PublicKeys(ECDsa k)
    {
        var pub = ECDsa.Create();
        pub.ImportSubjectPublicKeyInfo(k.ExportSubjectPublicKeyInfo(), out _);
        return new Dictionary<string, ECDsa> { ["k1"] = pub };
    }

    private static LicenseTokenPayload Payload(DateTimeOffset? lexp = null, int hours = 72) => new()
    {
        KeyId = "k1",
        LicenseId = "lic_1",
        DeviceId = "0123456789abcdef0123456789abcdef",
        Plan = LicensePlan.Days30,
        IssuedAtUnix = Now.ToUnixTimeSeconds(),
        TokenExpiresAtUnix = Now.AddHours(hours).ToUnixTimeSeconds(),
        LicenseExpiresAtUnix = lexp?.ToUnixTimeSeconds(),
    };

    [Fact]
    public void Valid_token_verifies_with_public_key_only()
    {
        string token = LicenseTokenCodec.Sign(Payload(Now.AddDays(30)), _key);
        var r = LicenseTokenCodec.Validate(token, PublicKeys(_key), "0123456789abcdef0123456789abcdef", Now);
        Assert.Equal(TokenValidationStatus.Valid, r.Status);
        Assert.Equal("lic_1", r.Payload!.LicenseId);
    }

    [Fact]
    public void Tampered_claims_or_foreign_key_fail_signature()
    {
        string token = LicenseTokenCodec.Sign(Payload(), _key);
        string[] parts = token.Split('.');
        var claims = LicenseTokenCodec.ReadUnverified(token)! with { Plan = LicensePlan.Lifetime, LicenseExpiresAtUnix = null };
        string forgedBody = Base64Url.Encode(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(claims));
        string forged = $"{parts[0]}.{forgedBody}.{parts[2]}";
        Assert.Equal(TokenValidationStatus.BadSignature, LicenseTokenCodec.Validate(forged, PublicKeys(_key), null, Now).Status);
        Assert.Equal(TokenValidationStatus.BadSignature, LicenseTokenCodec.Validate(token, PublicKeys(_other), null, Now).Status);
        Assert.Equal(TokenValidationStatus.Malformed, LicenseTokenCodec.Validate("ST1.abc", PublicKeys(_key), null, Now).Status);
    }

    [Fact]
    public void Device_binding_and_expiry_are_enforced()
    {
        string token = LicenseTokenCodec.Sign(Payload(Now.AddDays(10)), _key);
        Assert.Equal(TokenValidationStatus.WrongDevice, LicenseTokenCodec.Validate(token, PublicKeys(_key), "ffffffffffffffffffffffffffffffff", Now).Status);
        Assert.Equal(TokenValidationStatus.TokenExpired, LicenseTokenCodec.Validate(token, PublicKeys(_key), null, Now.AddHours(73)).Status);
        Assert.Equal(TokenValidationStatus.LicenseExpired, LicenseTokenCodec.Validate(token, PublicKeys(_key), null, Now.AddDays(11)).Status);
        Assert.Equal(TokenValidationStatus.Valid, LicenseTokenCodec.Validate(token, PublicKeys(_key), null, Now.AddHours(80), allowExpiredToken: true).Status);
    }
}

/// <summary>Client state machine against a scripted API.</summary>
public class LicenseServiceTests
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly FakeClock _clock = new();
    private readonly InMemoryTokenStore _store = new();
    private readonly FakeDevice _device = new();
    private readonly ScriptedApi _api = new();

    private sealed class ScriptedApi : ILicenseApi
    {
        public Func<LicenseApiResult>? Next { get; set; }
        public int Calls { get; private set; }
        public Task<LicenseApiResult> ActivateAsync(ActivationRequest request, CancellationToken ct) { Calls++; return Task.FromResult(Next!()); }
        public Task<LicenseApiResult> RefreshAsync(TokenRequest request, CancellationToken ct) { Calls++; return Task.FromResult(Next!()); }
        public Task<LicenseApiResult> DeactivateAsync(TokenRequest request, CancellationToken ct) { Calls++; return Task.FromResult(Next!()); }
    }

    private LicenseService Service()
    {
        var pub = ECDsa.Create();
        pub.ImportSubjectPublicKeyInfo(_key.ExportSubjectPublicKeyInfo(), out _);
        return new LicenseService(_api, _store, _device, new Dictionary<string, ECDsa> { ["k1"] = pub }, _clock, NullLogger.Instance, "0.1.0");
    }

    private LicenseApiResult Issued(DateTimeOffset now, int hours = 72)
    {
        var payload = new LicenseTokenPayload
        {
            KeyId = "k1", LicenseId = "lic_1", DeviceId = _device.DeviceId, Plan = LicensePlan.Days30,
            IssuedAtUnix = now.ToUnixTimeSeconds(), TokenExpiresAtUnix = now.AddHours(hours).ToUnixTimeSeconds(),
            LicenseExpiresAtUnix = now.AddDays(30).ToUnixTimeSeconds(),
        };
        return new(new LicenseResponse(LicenseTokenCodec.Sign(payload, _key), LicensePlan.Days30, now.AddDays(30), now.AddHours(hours), 1, 2), null, false);
    }

    [Fact]
    public async Task Malformed_key_is_rejected_without_contacting_the_server()
    {
        LicenseSnapshot s = await Service().ActivateAsync("hello");
        Assert.Equal(LicenseStatus.Unlicensed, s.Status);
        Assert.Equal(0, _api.Calls);
    }

    [Fact]
    public async Task Activation_stores_verified_token_and_grants_access()
    {
        _api.Next = () => Issued(_clock.UtcNow);
        LicenseSnapshot s = await Service().ActivateAsync(LicenseKeyFormat.Generate());
        Assert.Equal(LicenseStatus.Active, s.Status);
        Assert.True(s.HasPremiumAccess);
        Assert.NotNull(_store.Load());
    }

    [Fact]
    public async Task Server_outage_uses_offline_grace_until_token_expiry()
    {
        _api.Next = () => Issued(_clock.UtcNow);
        await Service().ActivateAsync(LicenseKeyFormat.Generate());

        _api.Next = () => new LicenseApiResult(null, new ApiError("unreachable", "down"), TransportFailure: true);
        _clock.UtcNow = _clock.UtcNow.AddHours(24);
        Assert.Equal(LicenseStatus.OfflineGrace, (await Service().InitializeAsync()).Status);

        _clock.UtcNow = _clock.UtcNow.AddHours(60); // past the 72 h token lifetime
        LicenseSnapshot after = await Service().InitializeAsync();
        Assert.Equal(LicenseStatus.Unlicensed, after.Status);
        Assert.False(after.HasPremiumAccess);
    }

    [Fact]
    public async Task Revocation_from_server_clears_local_token()
    {
        _api.Next = () => Issued(_clock.UtcNow);
        await Service().ActivateAsync(LicenseKeyFormat.Generate());
        _clock.UtcNow = _clock.UtcNow.AddHours(13); // triggers refresh
        _api.Next = () => new LicenseApiResult(null, new ApiError(ApiErrorCodes.Revoked, "revoked"), false);
        Assert.Equal(LicenseStatus.Revoked, (await Service().InitializeAsync()).Status);
        Assert.Null(_store.Load());
    }

    [Fact]
    public async Task Token_signed_by_unknown_key_is_rejected_even_from_the_api()
    {
        var rogue = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var payload = new LicenseTokenPayload
        {
            KeyId = "k1", LicenseId = "x", DeviceId = _device.DeviceId, Plan = LicensePlan.Lifetime,
            IssuedAtUnix = _clock.UtcNow.ToUnixTimeSeconds(), TokenExpiresAtUnix = _clock.UtcNow.AddDays(1).ToUnixTimeSeconds(),
        };
        _api.Next = () => new(new LicenseResponse(LicenseTokenCodec.Sign(payload, rogue), LicensePlan.Lifetime, null, _clock.UtcNow.AddDays(1), 1, 1), null, false);
        LicenseSnapshot s = await Service().ActivateAsync(LicenseKeyFormat.Generate());
        Assert.Equal(LicenseStatus.Invalid, s.Status);
        Assert.Null(_store.Load());
    }

    [Fact]
    public void Unconfigured_service_never_grants_access()
    {
        var s = new LicenseService(null, _store, _device, new Dictionary<string, ECDsa>(), _clock, NullLogger.Instance, "0.1.0");
        Assert.Equal(LicenseStatus.NotConfigured, s.Current.Status);
        Assert.False(s.Current.HasPremiumAccess);
    }
}
