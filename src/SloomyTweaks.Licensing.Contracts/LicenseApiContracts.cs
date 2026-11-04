using System.Text.Json;
using System.Text.Json.Serialization;

namespace SloomyTweaks.Licensing;

/// <summary>JSON settings shared by client and server: camelCase names, enums as strings.</summary>
public static class LicensingJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }
}

// Wire contracts for the public client API (/api/v1/licenses/*).
// Administrative contracts live only in the server project: the client never sees them.

public sealed record ActivationRequest(string LicenseKey, string DeviceId, string DeviceName, string AppVersion);

/// <summary>Refresh/deactivate are authenticated by a token previously issued to this device.</summary>
public sealed record TokenRequest(string Token, string DeviceId);

public sealed record LicenseResponse(
    string Token,
    LicensePlan Plan,
    DateTimeOffset? LicenseExpiresAt,
    DateTimeOffset TokenExpiresAt,
    int ActiveDevices,
    int MaxDevices);

public sealed record ApiError(string Code, string Message);

public static class ApiErrorCodes
{
    public const string InvalidRequest = "invalid_request";
    public const string InvalidKey = "invalid_key";
    public const string Revoked = "revoked";
    public const string Expired = "expired";
    public const string DeviceLimit = "device_limit";
    public const string InvalidToken = "invalid_token";
    public const string NotActivated = "not_activated";
    public const string RateLimited = "rate_limited";
}
