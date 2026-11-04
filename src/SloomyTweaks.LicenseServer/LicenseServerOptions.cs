namespace SloomyTweaks.LicenseServer;

/// <summary>
/// Server configuration (section "Sloomy" in appsettings.json or SLOOMY__* environment variables).
/// Secrets (signing key file, admin key hash, key pepper) must come from a secret store or
/// environment, never from source control.
/// </summary>
public sealed class LicenseServerOptions
{
    public string DatabasePath { get; set; } = "data/licenses.db";
    /// <summary>Path to the ECDSA P-256 private key (PKCS#8 PEM) used to sign license tokens.</summary>
    public string SigningKeyPath { get; set; } = "secrets/signing-key.pem";
    public string SigningKeyId { get; set; } = "k1";
    /// <summary>Lowercase hex SHA-256 of the admin API key. The key itself is never stored.</summary>
    public string AdminKeySha256 { get; set; } = string.Empty;
    /// <summary>Secret mixed into license-key hashes (HMAC-SHA256) so a database leak does not reveal keys.</summary>
    public string KeyPepper { get; set; } = string.Empty;
    /// <summary>How long a client may work offline after its last successful contact.</summary>
    public int TokenLifetimeHours { get; set; } = 72;
    /// <summary>Expired-but-authentic tokens older than this cannot be refreshed; the user must re-enter the key.</summary>
    public int MaxRefreshAgeDays { get; set; } = 30;
    public int ActivationRequestsPerMinute { get; set; } = 10;
    public int DefaultMaxDevices { get; set; } = 2;
    /// <summary>Set to true only when the server runs behind a trusted reverse proxy (Caddy, nginx) that sets X-Forwarded-For/Proto.</summary>
    public bool TrustForwardedHeaders { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(KeyPepper) || KeyPepper.Length < 32)
            throw new InvalidOperationException("Sloomy:KeyPepper must be set to a random secret of at least 32 characters (use `generate-pepper`).");
        if (AdminKeySha256.Length != 64)
            throw new InvalidOperationException("Sloomy:AdminKeySha256 must be the 64-character hex SHA-256 of the admin key (use `hash-admin-key`).");
        if (!File.Exists(SigningKeyPath))
            throw new InvalidOperationException($"Signing key not found at '{SigningKeyPath}' (use `generate-signing-key`).");
        if (TokenLifetimeHours is < 1 or > 24 * 30)
            throw new InvalidOperationException("Sloomy:TokenLifetimeHours must be between 1 and 720.");
    }
}
