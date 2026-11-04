using System.Text.Json;
using System.Text.Json.Serialization;

namespace SloomyTweaks.Core.Licensing;

public enum LicensingEnvironment
{
    Production,
    Development,
}

/// <summary>
/// Client licensing configuration (licensing.json next to the executable). It contains only the
/// server URL and PUBLIC verification keys, never a secret. The file is treated as untrusted input.
///
/// Production requires an https:// URL on a non-loopback host. Development allows http://localhost
/// for testing; the app then shows a permanent "Development mode" badge so it can never pass for a
/// production build.
/// </summary>
public sealed class LicensingConfiguration
{
    public string? ServerUrl { get; set; }
    public LicensingEnvironment Environment { get; set; } = LicensingEnvironment.Production;
    public List<PublicKeyEntry> Keys { get; set; } = new();

    public sealed class PublicKeyEntry
    {
        public string Kid { get; set; } = string.Empty;
        public string PublicKeyPem { get; set; } = string.Empty;
    }

    [JsonIgnore] public bool IsDevelopment => Environment == LicensingEnvironment.Development;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Parses and validates. Returns null and an explanation when the configuration is unusable.</summary>
    public static LicensingConfiguration? Parse(string json, out string? problem)
    {
        LicensingConfiguration? cfg;
        try
        {
            cfg = JsonSerializer.Deserialize<LicensingConfiguration>(json, Json);
        }
        catch (JsonException e)
        {
            problem = "licensing.json is not valid JSON: " + e.Message;
            return null;
        }
        problem = cfg is null ? "licensing.json is empty." : Validate(cfg);
        return problem is null ? cfg : null;
    }

    public static LicensingConfiguration? Load(string path, out string? problem)
    {
        if (!File.Exists(path))
        {
            problem = "Licensing is not configured in this build (licensing.json is missing).";
            return null;
        }
        try
        {
            return Parse(File.ReadAllText(path), out problem);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problem = "licensing.json could not be read: " + e.Message;
            return null;
        }
    }

    private static string? Validate(LicensingConfiguration cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.ServerUrl) || !Uri.TryCreate(cfg.ServerUrl, UriKind.Absolute, out Uri? uri))
            return "licensing.json: serverUrl is missing or invalid.";
        if (cfg.Keys.Count == 0)
            return "licensing.json: no public keys are configured.";
        foreach (PublicKeyEntry k in cfg.Keys)
        {
            if (string.IsNullOrWhiteSpace(k.Kid)) return "licensing.json: a key has no kid.";
            if (!k.PublicKeyPem.Contains("BEGIN PUBLIC KEY", StringComparison.Ordinal)) return $"licensing.json: key '{k.Kid}' is not a PEM public key.";
            if (k.PublicKeyPem.Contains("PRIVATE KEY", StringComparison.Ordinal)) return $"licensing.json: key '{k.Kid}' contains a PRIVATE key. Never ship private keys.";
        }

        if (cfg.Environment == LicensingEnvironment.Production)
        {
            if (uri.Scheme != Uri.UriSchemeHttps) return "Production licensing requires an https:// server URL.";
            if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                return "Production licensing cannot use a localhost server.";
            if (uri.Host.EndsWith("example.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Contains("yourdomain", StringComparison.OrdinalIgnoreCase))
                return "licensing.json still contains the example server URL.";
        }
        else if (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
        {
            return "Development licensing allows plain http only for localhost.";
        }
        return null;
    }
}
