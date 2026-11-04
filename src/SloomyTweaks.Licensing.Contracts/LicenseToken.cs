using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SloomyTweaks.Licensing;

/// <summary>Claims carried by a signed Sloomy Tweaks license token.</summary>
public sealed record LicenseTokenPayload
{
    [JsonPropertyName("v")] public int Version { get; init; } = 1;
    [JsonPropertyName("kid")] public required string KeyId { get; init; }
    [JsonPropertyName("lic")] public required string LicenseId { get; init; }
    [JsonPropertyName("dev")] public required string DeviceId { get; init; }
    [JsonPropertyName("plan")] public required LicensePlan Plan { get; init; }
    [JsonPropertyName("iat")] public required long IssuedAtUnix { get; init; }
    /// <summary>When this token stops being valid offline. Clients must refresh before this.</summary>
    [JsonPropertyName("exp")] public required long TokenExpiresAtUnix { get; init; }
    /// <summary>When the license itself ends; null for lifetime licenses.</summary>
    [JsonPropertyName("lexp")] public long? LicenseExpiresAtUnix { get; init; }

    [JsonIgnore] public DateTimeOffset IssuedAt => DateTimeOffset.FromUnixTimeSeconds(IssuedAtUnix);
    [JsonIgnore] public DateTimeOffset TokenExpiresAt => DateTimeOffset.FromUnixTimeSeconds(TokenExpiresAtUnix);
    [JsonIgnore] public DateTimeOffset? LicenseExpiresAt =>
        LicenseExpiresAtUnix is { } v ? DateTimeOffset.FromUnixTimeSeconds(v) : null;
}

public enum TokenValidationStatus
{
    Valid,
    Malformed,
    BadSignature,
    UnknownKey,
    WrongDevice,
    TokenExpired,
    LicenseExpired,
}

public sealed record TokenValidationResult(TokenValidationStatus Status, LicenseTokenPayload? Payload)
{
    public bool IsValid => Status == TokenValidationStatus.Valid;
}

/// <summary>
/// Compact signed token: <c>ST1.base64url(payload-json).base64url(signature)</c>.
/// Signature: ECDSA P-256 / SHA-256 (IEEE P1363 encoding) over the ASCII bytes of <c>ST1.payload</c>.
/// Only the server holds the private key; clients embed public keys and can verify but never mint.
/// </summary>
public static class LicenseTokenCodec
{
    public const string Header = "ST1";

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Sign(LicenseTokenPayload payload, ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(privateKey);
        string body = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(payload, Json));
        string signingInput = $"{Header}.{body}";
        byte[] signature = privateKey.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256);
        return $"{signingInput}.{Base64Url.Encode(signature)}";
    }

    /// <summary>Parses claims WITHOUT verifying the signature. Never use the result for authorization.</summary>
    public static LicenseTokenPayload? ReadUnverified(string? token)
    {
        if (!TrySplit(token, out _, out string body, out _)) return null;
        try
        {
            return JsonSerializer.Deserialize<LicenseTokenPayload>(Base64Url.Decode(body), Json);
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            return null;
        }
    }

    /// <param name="publicKeys">Trusted public keys by key id (supports key rotation).</param>
    /// <param name="expectedDeviceId">Device the token must be bound to, or null to skip the check (server side).</param>
    /// <param name="allowExpiredToken">Server-side refresh accepts expired-but-authentic tokens.</param>
    public static TokenValidationResult Validate(
        string? token,
        IReadOnlyDictionary<string, ECDsa> publicKeys,
        string? expectedDeviceId,
        DateTimeOffset now,
        bool allowExpiredToken = false)
    {
        if (!TrySplit(token, out string signingInput, out string body, out string sig))
            return new(TokenValidationStatus.Malformed, null);

        LicenseTokenPayload? payload;
        byte[] signature;
        try
        {
            payload = JsonSerializer.Deserialize<LicenseTokenPayload>(Base64Url.Decode(body), Json);
            signature = Base64Url.Decode(sig);
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            return new(TokenValidationStatus.Malformed, null);
        }
        if (payload is null || payload.Version != 1) return new(TokenValidationStatus.Malformed, null);

        if (!publicKeys.TryGetValue(payload.KeyId, out ECDsa? key))
            return new(TokenValidationStatus.UnknownKey, null);

        if (!key.VerifyData(Encoding.ASCII.GetBytes(signingInput), signature, HashAlgorithmName.SHA256))
            return new(TokenValidationStatus.BadSignature, null);

        // From here on the claims are authentic.
        if (expectedDeviceId is not null &&
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(payload.DeviceId), Encoding.UTF8.GetBytes(expectedDeviceId)))
            return new(TokenValidationStatus.WrongDevice, payload);

        if (payload.LicenseExpiresAt is { } lexp && now >= lexp)
            return new(TokenValidationStatus.LicenseExpired, payload);

        if (!allowExpiredToken && now >= payload.TokenExpiresAt)
            return new(TokenValidationStatus.TokenExpired, payload);

        return new(TokenValidationStatus.Valid, payload);
    }

    private static bool TrySplit(string? token, out string signingInput, out string body, out string sig)
    {
        signingInput = body = sig = string.Empty;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096) return false;
        string[] parts = token.Split('.');
        if (parts.Length != 3 || parts[0] != Header || parts[1].Length == 0 || parts[2].Length == 0) return false;
        body = parts[1];
        sig = parts[2];
        signingInput = $"{parts[0]}.{parts[1]}";
        return true;
    }
}

public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string text)
    {
        string s = text.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
            case 1: throw new FormatException("Invalid base64url length.");
        }
        return Convert.FromBase64String(s);
    }
}
