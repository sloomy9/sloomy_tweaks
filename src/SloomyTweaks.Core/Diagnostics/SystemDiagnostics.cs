namespace SloomyTweaks.Core.Diagnostics;

// Read-only diagnostics. These are information, not optimizations: they never change the system.

public sealed record DiagnosticRow(string Label, string Value, DiagnosticSeverity Severity = DiagnosticSeverity.Info);

public enum DiagnosticSeverity
{
    Info,
    Good,
    Warning,
}

public sealed record DisplayInfo(string Name, int Width, int Height, int RefreshRateHz);

public interface ISystemDiagnostics
{
    /// <summary>Processor power policy of the active plan (AC values).</summary>
    IReadOnlyList<DiagnosticRow> ProcessorPowerPolicy();
    /// <summary>Commit charge and page-file configuration.</summary>
    IReadOnlyList<DiagnosticRow> Memory();
    IReadOnlyList<DisplayInfo> Displays();
    /// <summary>Windows pointer speed (1–20) or null.</summary>
    int? PointerSpeed();
    /// <summary>Contents of Fortnite's GameUserSettings.ini, or null when not found.</summary>
    string? ReadFortniteSettings();
    /// <summary>NTFS TRIM status text.</summary>
    DiagnosticRow TrimStatus();
}

/// <summary>Extracts the settings relevant to latency and frame pacing from Fortnite's GameUserSettings.ini (read-only).</summary>
public static class FortniteSettingsReader
{
    /// <summary>Keys shown when present. Unknown or missing keys are simply not displayed.</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> Keys = new[]
    {
        ("FrameRateLimit", "Frame-rate limit"),
        ("bUseVSync", "V-Sync"),
        ("PreferredRHI", "Rendering API (preferred)"),
        ("FullscreenMode", "Window mode"),
        ("ResolutionSizeX", "Resolution width"),
        ("ResolutionSizeY", "Resolution height"),
        ("bUseDynamicResolution", "Dynamic resolution"),
    };

    public static IReadOnlyList<DiagnosticRow> Parse(string? ini)
    {
        if (ini is null) return new[] { new DiagnosticRow("Fortnite settings", "GameUserSettings.ini was not found for this Windows user.") };
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in ini.Split('\n'))
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=');
            if (eq <= 0 || line.StartsWith(';') || line.StartsWith('[')) continue;
            values.TryAdd(line[..eq].Trim(), line[(eq + 1)..].Trim());
        }
        var rows = new List<DiagnosticRow>();
        foreach (var (key, label) in Keys)
        {
            if (!values.TryGetValue(key, out string? v)) continue;
            rows.Add(new DiagnosticRow(label, Describe(key, v)));
        }
        if (rows.Count == 0) rows.Add(new DiagnosticRow("Fortnite settings", "The file was found but contains none of the known settings."));
        return rows;
    }

    private static string Describe(string key, string value) => key switch
    {
        "FrameRateLimit" when double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double fps)
            => fps <= 0 ? "Unlimited" : $"{fps:0} FPS",
        "bUseVSync" or "bUseDynamicResolution" => value.Equals("True", StringComparison.OrdinalIgnoreCase) ? "On" : "Off",
        "FullscreenMode" => value switch { "0" => "Fullscreen", "1" => "Windowed fullscreen", "2" => "Windowed", _ => value },
        _ => value,
    };
}
