using System.Security.Cryptography;
using System.Text;
using SloomyTweaks.Licensing;

namespace SloomyTweaks.LicenseServer;

public sealed record ServiceResult<T>(T? Value, ApiError? Error, int StatusCode)
{
    public static ServiceResult<T> Ok(T value) => new(value, null, 200);
    public static ServiceResult<T> Fail(int status, string code, string message) => new(default, new ApiError(code, message), status);
}

public sealed record CreatedLicense(string LicenseId, string LicenseKey, LicensePlan Plan, int MaxDevices, DateTimeOffset? ExpiresAt, string? CustomerId);

/// <summary>
/// Server-side license authority. All validity decisions (key, expiry, revocation, device limit)
/// are made here; clients only receive signed tokens.
/// </summary>
public sealed class LicenseManager
{
    private readonly LicenseRepository _repo;
    private readonly LicenseServerOptions _options;
    private readonly ECDsa _signingKey;
    private readonly Dictionary<string, ECDsa> _verificationKeys;
    private readonly TimeProvider _time;
    private readonly byte[] _pepper;

    public LicenseManager(LicenseRepository repo, LicenseServerOptions options, ECDsa signingKey, TimeProvider time)
    {
        _repo = repo;
        _options = options;
        _signingKey = signingKey;
        _time = time;
        _pepper = Encoding.UTF8.GetBytes(options.KeyPepper);
        var pub = ECDsa.Create();
        pub.ImportSubjectPublicKeyInfo(signingKey.ExportSubjectPublicKeyInfo(), out _);
        _verificationKeys = new Dictionary<string, ECDsa> { [options.SigningKeyId] = pub };
    }

    public string PublicKeyPem => _signingKey.ExportSubjectPublicKeyInfoPem();

    public string HashKey(string normalizedKey) =>
        Convert.ToHexString(HMACSHA256.HashData(_pepper, Encoding.UTF8.GetBytes(normalizedKey))).ToLowerInvariant();

    // ---------------- Public client API ----------------

    public ServiceResult<LicenseResponse> Activate(ActivationRequest request)
    {
        DateTimeOffset now = _time.GetUtcNow();
        if (!ValidDevice(request.DeviceId) || request.DeviceName is null || request.DeviceName.Length > 128 || request.AppVersion is null || request.AppVersion.Length > 32)
            return ServiceResult<LicenseResponse>.Fail(400, ApiErrorCodes.InvalidRequest, "The activation request is invalid.");
        if (!LicenseKeyFormat.TryNormalize(request.LicenseKey, out string normalized))
            return ServiceResult<LicenseResponse>.Fail(404, ApiErrorCodes.InvalidKey, "This license key is not valid.");

        return _repo.InTransaction(() =>
        {
            LicenseRecord? license = _repo.FindLicenseByHash(HashKey(normalized));
            if (license is null)
                return ServiceResult<LicenseResponse>.Fail(404, ApiErrorCodes.InvalidKey, "This license key is not valid.");

            ServiceResult<LicenseResponse>? blocked = CheckUsable(license, now);
            if (blocked is not null) return blocked;

            ActivationRecord? existing = _repo.FindActiveActivation(license.Id, request.DeviceId);
            if (existing is null)
            {
                int active = _repo.ListActivations(license.Id, activeOnly: true).Count;
                if (active >= license.MaxDevices)
                    return ServiceResult<LicenseResponse>.Fail(409, ApiErrorCodes.DeviceLimit,
                        $"This license is already active on {active} of {license.MaxDevices} allowed devices. Deactivate another device first.");
                _repo.InsertActivation(license.Id, request.DeviceId, request.DeviceName.Trim(), request.AppVersion, now);
                _repo.Audit("client", "activation.created", $"license={license.Id} device={request.DeviceId}", now);
            }
            else
            {
                _repo.TouchActivation(existing.Id, now, request.AppVersion);
            }

            if (license.ActivatedAt is null)
            {
                // Paid duration starts at first activation unless the admin set a fixed expiry.
                DateTimeOffset? expiry = license.ExpiresAt ?? (license.Plan.Duration() is { } d ? now + d : null);
                _repo.MarkActivated(license.Id, now, expiry);
                license = _repo.GetLicense(license.Id)!;
            }

            return ServiceResult<LicenseResponse>.Ok(Issue(license, request.DeviceId, now));
        });
    }

    public ServiceResult<LicenseResponse> Refresh(TokenRequest request)
    {
        DateTimeOffset now = _time.GetUtcNow();
        var (license, activation, error) = Authenticate(request, now);
        if (error is not null) return ServiceResult<LicenseResponse>.Fail(error.StatusCode, error.Error!.Code, error.Error.Message);

        ServiceResult<LicenseResponse>? blocked = CheckUsable(license!, now);
        if (blocked is not null) return blocked;

        _repo.TouchActivation(activation!.Id, now);
        return ServiceResult<LicenseResponse>.Ok(Issue(license!, request.DeviceId, now));
    }

    public ServiceResult<bool> Deactivate(TokenRequest request)
    {
        DateTimeOffset now = _time.GetUtcNow();
        var (license, activation, error) = Authenticate(request, now, ignoreRefreshAge: true);
        if (error is not null) return ServiceResult<bool>.Fail(error.StatusCode, error.Error!.Code, error.Error.Message);
        _repo.DeactivateActivation(activation!.Id, now);
        _repo.Audit("client", "activation.deactivated", $"license={license!.Id} device={request.DeviceId}", now);
        return ServiceResult<bool>.Ok(true);
    }

    private (LicenseRecord? License, ActivationRecord? Activation, ServiceResult<LicenseResponse>? Error) Authenticate(
        TokenRequest request, DateTimeOffset now, bool ignoreRefreshAge = false)
    {
        if (request is null || !ValidDevice(request.DeviceId))
            return (null, null, ServiceResult<LicenseResponse>.Fail(400, ApiErrorCodes.InvalidRequest, "The request is invalid."));

        TokenValidationResult check = LicenseTokenCodec.Validate(request.Token, _verificationKeys, request.DeviceId, now, allowExpiredToken: true);
        if (check.Status is TokenValidationStatus.Malformed or TokenValidationStatus.BadSignature or TokenValidationStatus.UnknownKey or TokenValidationStatus.WrongDevice)
            return (null, null, ServiceResult<LicenseResponse>.Fail(401, ApiErrorCodes.InvalidToken, "The license token is not valid for this device. Please activate again."));

        LicenseTokenPayload payload = check.Payload!;
        if (!ignoreRefreshAge && now - payload.TokenExpiresAt > TimeSpan.FromDays(_options.MaxRefreshAgeDays))
            return (null, null, ServiceResult<LicenseResponse>.Fail(401, ApiErrorCodes.InvalidToken, "This device has been offline too long. Please enter your license key again."));

        LicenseRecord? license = _repo.GetLicense(payload.LicenseId);
        if (license is null)
            return (null, null, ServiceResult<LicenseResponse>.Fail(401, ApiErrorCodes.InvalidToken, "The license no longer exists."));

        ActivationRecord? activation = _repo.FindActiveActivation(license.Id, request.DeviceId);
        if (activation is null)
            return (license, null, ServiceResult<LicenseResponse>.Fail(403, ApiErrorCodes.NotActivated, "This device is not activated for this license."));

        return (license, activation, null);
    }

    private static ServiceResult<LicenseResponse>? CheckUsable(LicenseRecord license, DateTimeOffset now)
    {
        if (license.RevokedAt is not null)
            return ServiceResult<LicenseResponse>.Fail(403, ApiErrorCodes.Revoked, "This license has been revoked. Contact Sloomy Tweaks support.");
        if (license.ExpiresAt is { } exp && now >= exp)
            return ServiceResult<LicenseResponse>.Fail(403, ApiErrorCodes.Expired, $"This license expired on {exp:yyyy-MM-dd}.");
        return null;
    }

    private LicenseResponse Issue(LicenseRecord license, string deviceId, DateTimeOffset now)
    {
        DateTimeOffset tokenExpiry = now.AddHours(_options.TokenLifetimeHours);
        if (license.ExpiresAt is { } lexp && lexp < tokenExpiry) tokenExpiry = lexp;
        var payload = new LicenseTokenPayload
        {
            KeyId = _options.SigningKeyId,
            LicenseId = license.Id,
            DeviceId = deviceId,
            Plan = license.Plan,
            IssuedAtUnix = now.ToUnixTimeSeconds(),
            TokenExpiresAtUnix = tokenExpiry.ToUnixTimeSeconds(),
            LicenseExpiresAtUnix = license.ExpiresAt?.ToUnixTimeSeconds(),
        };
        int active = _repo.ListActivations(license.Id, activeOnly: true).Count;
        return new LicenseResponse(LicenseTokenCodec.Sign(payload, _signingKey), license.Plan, license.ExpiresAt, tokenExpiry, active, license.MaxDevices);
    }

    private static bool ValidDevice(string? deviceId) =>
        deviceId is { Length: >= 16 and <= 128 } && deviceId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    // ---------------- Administrative operations ----------------

    public IReadOnlyList<CreatedLicense> CreateLicenses(string actor, LicensePlan plan, int maxDevices, int quantity, string? customerId, DateTimeOffset? fixedExpiry)
    {
        if (quantity is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be 1–500.");
        if (maxDevices is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(maxDevices), "Max devices must be 1–100.");
        if (customerId is not null && _repo.GetCustomer(customerId) is null) throw new ArgumentException("Unknown customer.");
        DateTimeOffset now = _time.GetUtcNow();
        return _repo.InTransaction(() =>
        {
            var created = new List<CreatedLicense>();
            for (int i = 0; i < quantity; i++)
            {
                string key = LicenseKeyFormat.Generate();
                string id = "lic_" + Guid.NewGuid().ToString("N")[..20];
                _repo.InsertLicense(new LicenseRecord(id, HashKey(key), key[^5..], customerId, plan, maxDevices, now, null, fixedExpiry, null, null));
                created.Add(new CreatedLicense(id, key, plan, maxDevices, fixedExpiry, customerId));
            }
            _repo.Audit(actor, "license.created", $"count={quantity} plan={plan} maxDevices={maxDevices} customer={customerId ?? "-"}", now);
            return (IReadOnlyList<CreatedLicense>)created;
        });
    }

    public bool Revoke(string actor, string licenseId, string? reason)
    {
        DateTimeOffset now = _time.GetUtcNow();
        if (_repo.GetLicense(licenseId) is null) return false;
        _repo.SetRevoked(licenseId, now, string.IsNullOrWhiteSpace(reason) ? "Revoked by administrator" : reason.Trim());
        _repo.Audit(actor, "license.revoked", $"license={licenseId} reason={reason}", now);
        return true;
    }

    public bool Reinstate(string actor, string licenseId)
    {
        DateTimeOffset now = _time.GetUtcNow();
        if (_repo.GetLicense(licenseId) is null) return false;
        _repo.SetRevoked(licenseId, null, null);
        _repo.Audit(actor, "license.reinstated", $"license={licenseId}", now);
        return true;
    }

    public bool Update(string actor, string licenseId, DateTimeOffset? expiresAt, bool clearExpiry, int? maxDevices)
    {
        if (_repo.GetLicense(licenseId) is null) return false;
        if (maxDevices is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(maxDevices));
        _repo.UpdateLicense(licenseId, expiresAt, clearExpiry, maxDevices);
        _repo.Audit(actor, "license.updated", $"license={licenseId} expires={(clearExpiry ? "none" : expiresAt?.ToString("O") ?? "unchanged")} maxDevices={maxDevices?.ToString() ?? "unchanged"}", _time.GetUtcNow());
        return true;
    }

    public bool AdminDeactivate(string actor, long activationId)
    {
        ActivationRecord? a = _repo.GetActivation(activationId);
        if (a is null) return false;
        _repo.DeactivateActivation(activationId, _time.GetUtcNow());
        _repo.Audit(actor, "activation.deactivated_by_admin", $"license={a.LicenseId} device={a.DeviceId}", _time.GetUtcNow());
        return true;
    }
}
