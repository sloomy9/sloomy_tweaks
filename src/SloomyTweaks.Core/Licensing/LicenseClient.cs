using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using SloomyTweaks.Core.Diagnostics;
using SloomyTweaks.Core.Platform;
using SloomyTweaks.Licensing;

namespace SloomyTweaks.Core.Licensing;

public interface IDeviceIdentity
{
    /// <summary>Stable, privacy-preserving device identifier (a salted hash; never raw hardware serials).</summary>
    string DeviceId { get; }
    /// <summary>User-visible label shown in the admin dashboard (computer name).</summary>
    string DeviceName { get; }
}

public interface ILicenseTokenStore
{
    string? Load();
    void Save(string token);
    void Clear();
}

public sealed class InMemoryTokenStore : ILicenseTokenStore
{
    private string? _token;
    public string? Load() => _token;
    public void Save(string token) => _token = token;
    public void Clear() => _token = null;
}

public sealed record LicenseApiResult(LicenseResponse? Response, ApiError? Error, bool TransportFailure)
{
    public bool Success => Response is not null;
}

public interface ILicenseApi
{
    Task<LicenseApiResult> ActivateAsync(ActivationRequest request, CancellationToken ct);
    Task<LicenseApiResult> RefreshAsync(TokenRequest request, CancellationToken ct);
    Task<LicenseApiResult> DeactivateAsync(TokenRequest request, CancellationToken ct);
}

/// <summary>HTTPS client for the license server's public API. Plain HTTP is refused except for localhost development.</summary>
public sealed class HttpLicenseApi : ILicenseApi
{
    private readonly HttpClient _http;

    public HttpLicenseApi(HttpClient http)
    {
        _http = http;
        Uri? baseUri = http.BaseAddress ?? throw new ArgumentException("HttpClient.BaseAddress must be set.");
        bool loopback = baseUri.IsLoopback;
        if (baseUri.Scheme != Uri.UriSchemeHttps && !loopback)
            throw new ArgumentException("The license server must be reached over HTTPS.");
    }

    public Task<LicenseApiResult> ActivateAsync(ActivationRequest request, CancellationToken ct) => PostAsync("api/v1/licenses/activate", request, ct);
    public Task<LicenseApiResult> RefreshAsync(TokenRequest request, CancellationToken ct) => PostAsync("api/v1/licenses/refresh", request, ct);
    public Task<LicenseApiResult> DeactivateAsync(TokenRequest request, CancellationToken ct) => PostAsync("api/v1/licenses/deactivate", request, ct);

    private async Task<LicenseApiResult> PostAsync<T>(string path, T body, CancellationToken ct)
    {
        try
        {
            using HttpResponseMessage response = await _http.PostAsJsonAsync(path, body, LicensingJson.Options, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                LicenseResponse? ok = await response.Content.ReadFromJsonAsync<LicenseResponse>(LicensingJson.Options, ct).ConfigureAwait(false);
                return ok is null || string.IsNullOrEmpty(ok.Token)
                    ? new(null, new ApiError("bad_response", "The license server returned an empty response."), false)
                    : new(ok, null, false);
            }
            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
                return new(null, new ApiError("server_unavailable", $"License server error ({(int)response.StatusCode})."), true);

            ApiError? error = null;
            try { error = await response.Content.ReadFromJsonAsync<ApiError>(LicensingJson.Options, ct).ConfigureAwait(false); }
            catch (JsonException) { }
            return new(null, error ?? new ApiError("http_" + (int)response.StatusCode, response.ReasonPhrase ?? "Request failed."), false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (e is TaskCanceledException && ct.IsCancellationRequested) throw;
            return new(null, new ApiError("unreachable", "The license server could not be reached."), true);
        }
    }
}

public enum LicenseStatus
{
    NotConfigured,
    Unlicensed,
    Active,
    /// <summary>Server unreachable but the locally stored signed token is still valid.</summary>
    OfflineGrace,
    Expired,
    Revoked,
    Invalid,
}

public sealed record LicenseSnapshot(
    LicenseStatus Status,
    string Message,
    LicensePlan? Plan = null,
    DateTimeOffset? LicenseExpiresAt = null,
    DateTimeOffset? TokenExpiresAt = null,
    DateTimeOffset? LastServerContact = null)
{
    public bool HasPremiumAccess => Status is LicenseStatus.Active or LicenseStatus.OfflineGrace;
}

/// <summary>
/// Client-side license state machine. The server is the authority: the client only verifies
/// signatures of tokens the server issued (it holds public keys, never a signing secret) and
/// tolerates temporary outages until the signed token's own expiry.
/// </summary>
public sealed class LicenseService
{
    /// <summary>Refresh with the server when the token is older than this.</summary>
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromHours(12);

    private readonly ILicenseApi? _api;
    private readonly ILicenseTokenStore _store;
    private readonly IDeviceIdentity _device;
    private readonly IReadOnlyDictionary<string, ECDsa> _publicKeys;
    private readonly IClock _clock;
    private readonly IAppLogger _log;
    private readonly string _appVersion;

    public LicenseService(ILicenseApi? api, ILicenseTokenStore store, IDeviceIdentity device,
        IReadOnlyDictionary<string, ECDsa> publicKeys, IClock clock, IAppLogger log, string appVersion,
        bool developmentMode = false, string? notConfiguredReason = null)
    {
        IsDevelopmentMode = developmentMode;
        _api = api;
        _store = store;
        _device = device;
        _publicKeys = publicKeys;
        _clock = clock;
        _log = log;
        _appVersion = appVersion;
        Current = IsConfigured
            ? new LicenseSnapshot(LicenseStatus.Unlicensed, "Enter your Sloomy Tweaks license key to activate.")
            : new LicenseSnapshot(LicenseStatus.NotConfigured, notConfiguredReason ?? "Licensing is not configured in this build (no license server or public key).");
    }

    /// <summary>True when licensing.json declares environment "development" (e.g. a localhost test server).</summary>
    public bool IsDevelopmentMode { get; }

    public event EventHandler<LicenseSnapshot>? Changed;

    public LicenseSnapshot Current { get; private set; }

    public bool IsConfigured => _api is not null && _publicKeys.Count > 0;

    public async Task<LicenseSnapshot> InitializeAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return Current;
        string? token = _store.Load();
        if (token is null) return Set(new(LicenseStatus.Unlicensed, "Enter your Sloomy Tweaks license key to activate."));

        TokenValidationResult local = LicenseTokenCodec.Validate(token, _publicKeys, _device.DeviceId, _clock.UtcNow);
        bool shouldRefresh = local.Status is TokenValidationStatus.TokenExpired
            || (local.IsValid && _clock.UtcNow - local.Payload!.IssuedAt > RefreshAfter);

        if (local.Status is TokenValidationStatus.Malformed or TokenValidationStatus.BadSignature or TokenValidationStatus.UnknownKey or TokenValidationStatus.WrongDevice)
        {
            _store.Clear();
            _log.Warn("license.local_invalid", $"Stored license token rejected: {local.Status}");
            return Set(new(LicenseStatus.Invalid, "The stored license could not be verified for this device. Please activate again."));
        }
        if (local.Status == TokenValidationStatus.LicenseExpired)
            return Set(Snapshot(LicenseStatus.Expired, "Your Sloomy Tweaks license has expired.", local.Payload!, null));

        if (!shouldRefresh) return Set(Snapshot(LicenseStatus.Active, "License active.", local.Payload!, null));
        return await RefreshAsync(token, local, ct).ConfigureAwait(false);
    }

    public async Task<LicenseSnapshot> ActivateAsync(string licenseKey, CancellationToken ct = default)
    {
        if (!IsConfigured) return Current;
        if (!LicenseKeyFormat.TryNormalize(licenseKey, out string normalized))
            return Set(new(LicenseStatus.Unlicensed, "That doesn't look like a Sloomy Tweaks license key. Check for typos (format SLMY-XXXXX-XXXXX-XXXXX-XXXXX)."));

        LicenseApiResult result = await _api!.ActivateAsync(new ActivationRequest(normalized, _device.DeviceId, _device.DeviceName, _appVersion), ct).ConfigureAwait(false);
        if (result.TransportFailure)
            return Set(new(LicenseStatus.Unlicensed, "The license server could not be reached. Check your connection and try again."));
        if (!result.Success)
            return Set(new(StatusFor(result.Error!.Code), result.Error.Message));

        return AcceptToken(result.Response!, "License activated.");
    }

    public async Task<LicenseSnapshot> DeactivateAsync(CancellationToken ct = default)
    {
        string? token = _store.Load();
        if (!IsConfigured || token is null) return Current;
        LicenseApiResult result = await _api!.DeactivateAsync(new TokenRequest(token, _device.DeviceId), ct).ConfigureAwait(false);
        if (result.TransportFailure)
            return Current with { Message = "The license server could not be reached; this device was not deactivated." };
        _store.Clear();
        return Set(new(LicenseStatus.Unlicensed, result.Success || result.Error?.Code == ApiErrorCodes.NotActivated
            ? "This device was deactivated. The activation slot is free again."
            : result.Error!.Message));
    }

    private async Task<LicenseSnapshot> RefreshAsync(string token, TokenValidationResult local, CancellationToken ct)
    {
        LicenseApiResult result = await _api!.RefreshAsync(new TokenRequest(token, _device.DeviceId), ct).ConfigureAwait(false);
        if (result.Success) return AcceptToken(result.Response!, "License active.");

        if (result.TransportFailure)
        {
            if (local.IsValid)
            {
                _log.Info("license.offline_grace", "License server unreachable; using signed offline token.");
                return Set(Snapshot(LicenseStatus.OfflineGrace,
                    $"License server unreachable. Working offline until {local.Payload!.TokenExpiresAt.LocalDateTime:g}.", local.Payload, null));
            }
            return Set(new(LicenseStatus.Unlicensed, "The offline license period ended and the license server could not be reached."));
        }

        LicenseStatus status = StatusFor(result.Error!.Code);
        if (status is LicenseStatus.Revoked or LicenseStatus.Expired or LicenseStatus.Invalid) _store.Clear();
        return Set(new(status, result.Error.Message));
    }

    private LicenseSnapshot AcceptToken(LicenseResponse response, string message)
    {
        TokenValidationResult check = LicenseTokenCodec.Validate(response.Token, _publicKeys, _device.DeviceId, _clock.UtcNow);
        if (!check.IsValid)
        {
            _log.Warn("license.server_token_rejected", $"Server token failed local verification: {check.Status}");
            return Set(new(LicenseStatus.Invalid, $"The license server returned a token that failed verification ({check.Status})."));
        }
        _store.Save(response.Token);
        return Set(Snapshot(LicenseStatus.Active, message, check.Payload!, _clock.UtcNow));
    }

    private static LicenseSnapshot Snapshot(LicenseStatus status, string message, LicenseTokenPayload p, DateTimeOffset? contact) =>
        new(status, message, p.Plan, p.LicenseExpiresAt, p.TokenExpiresAt, contact);

    private static LicenseStatus StatusFor(string code) => code switch
    {
        ApiErrorCodes.Revoked => LicenseStatus.Revoked,
        ApiErrorCodes.Expired => LicenseStatus.Expired,
        ApiErrorCodes.InvalidToken or ApiErrorCodes.NotActivated => LicenseStatus.Invalid,
        _ => LicenseStatus.Unlicensed,
    };

    private LicenseSnapshot Set(LicenseSnapshot snapshot)
    {
        Current = snapshot;
        Changed?.Invoke(this, snapshot);
        return snapshot;
    }
}

public static class LicensePublicKeys
{
    /// <summary>Parses "kid=PEM" pairs. Clients ship only public keys.</summary>
    public static IReadOnlyDictionary<string, ECDsa> FromPem(IEnumerable<(string KeyId, string Pem)> keys)
    {
        var result = new Dictionary<string, ECDsa>(StringComparer.Ordinal);
        foreach (var (kid, pem) in keys)
        {
            var key = ECDsa.Create();
            key.ImportFromPem(pem);
            if (key.KeySize != 256) throw new CryptographicException($"Key '{kid}' is not a P-256 key.");
            result[kid] = key;
        }
        return result;
    }
}
