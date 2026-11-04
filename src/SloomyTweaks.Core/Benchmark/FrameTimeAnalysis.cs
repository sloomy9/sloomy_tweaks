using System.Globalization;

namespace SloomyTweaks.Core.Benchmark;

/// <summary>Summary metrics for one capture. All values are derived from real frame times.</summary>
public sealed record FrameTimeStats
{
    public required int FrameCount { get; init; }
    public required double DurationSeconds { get; init; }
    /// <summary>Frames divided by capture duration.</summary>
    public required double AverageFps { get; init; }
    /// <summary>Average FPS of the slowest 1% of frames (by frame time).</summary>
    public required double OnePercentLowFps { get; init; }
    /// <summary>Average FPS of the slowest 0.1% of frames.</summary>
    public required double PointOnePercentLowFps { get; init; }
    public required double MedianFrameTimeMs { get; init; }
    public required double P95FrameTimeMs { get; init; }
    public required double P99FrameTimeMs { get; init; }
    public required double MaxFrameTimeMs { get; init; }
    public required double FrameTimeStdDevMs { get; init; }
    /// <summary>Histogram of frame times in 1 ms buckets up to 50 ms; the last bucket collects everything above.</summary>
    public required IReadOnlyList<int> Distribution { get; init; }
}

public static class FrameTimeAnalyzer
{
    public const int MinimumFrames = 100;

    /// <summary>
    /// Computes statistics from per-frame times in milliseconds (PresentMon MsBetweenPresents or equivalent).
    /// Definitions used:
    /// - 1% low = mean FPS of the slowest 1% of frames (worst frames by time).
    /// - Percentiles use linear interpolation between closest ranks.
    /// </summary>
    public static FrameTimeStats Analyze(IReadOnlyList<double> frameTimesMs)
    {
        ArgumentNullException.ThrowIfNull(frameTimesMs);
        var valid = frameTimesMs.Where(t => t > 0 && double.IsFinite(t)).ToArray();
        if (valid.Length < MinimumFrames)
            throw new ArgumentException($"At least {MinimumFrames} valid frames are required for meaningful statistics (got {valid.Length}).");

        double totalMs = valid.Sum();
        double[] sorted = valid.OrderBy(t => t).ToArray();
        double mean = totalMs / valid.Length;
        double variance = valid.Sum(t => (t - mean) * (t - mean)) / (valid.Length - 1);

        var buckets = new int[51];
        foreach (double t in valid) buckets[Math.Min(50, (int)Math.Floor(t))]++;

        return new FrameTimeStats
        {
            FrameCount = valid.Length,
            DurationSeconds = totalMs / 1000.0,
            AverageFps = valid.Length / (totalMs / 1000.0),
            OnePercentLowFps = LowFps(sorted, 0.01),
            PointOnePercentLowFps = LowFps(sorted, 0.001),
            MedianFrameTimeMs = Percentile(sorted, 50),
            P95FrameTimeMs = Percentile(sorted, 95),
            P99FrameTimeMs = Percentile(sorted, 99),
            MaxFrameTimeMs = sorted[^1],
            FrameTimeStdDevMs = Math.Sqrt(variance),
            Distribution = buckets,
        };
    }

    /// <summary>Percentile with linear interpolation; <paramref name="sortedAscending"/> must be sorted.</summary>
    public static double Percentile(IReadOnlyList<double> sortedAscending, double percentile)
    {
        if (sortedAscending.Count == 0) throw new ArgumentException("No data.");
        if (percentile is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(percentile));
        double rank = percentile / 100.0 * (sortedAscending.Count - 1);
        int lo = (int)Math.Floor(rank);
        int hi = (int)Math.Ceiling(rank);
        double frac = rank - lo;
        return sortedAscending[lo] + (sortedAscending[hi] - sortedAscending[lo]) * frac;
    }

    private static double LowFps(double[] sortedAscending, double fraction)
    {
        int count = Math.Max(1, (int)Math.Ceiling(sortedAscending.Length * fraction));
        double worstMeanMs = sortedAscending.Skip(sortedAscending.Length - count).Average();
        return 1000.0 / worstMeanMs;
    }
}

public sealed record FrameCapture(string Application, IReadOnlyList<double> FrameTimesMs, string SourceColumn, int SkippedRows);

/// <summary>
/// Imports PresentMon CSV captures. Supports PresentMon 1.x ("MsBetweenPresents") and 2.x
/// ("FrameTime" / "MsBetweenPresents") column layouts. Sloomy Tweaks does not bundle PresentMon;
/// users capture with the official tool and import the CSV, so measurements stay traceable.
/// </summary>
public static class PresentMonCsvParser
{
    private static readonly string[] FrameTimeColumns = { "MsBetweenPresents", "msBetweenPresents", "FrameTime", "MsBetweenDisplayChange" };

    /// <param name="applicationFilter">Keep only rows for this process. When null and several
    /// processes were captured, only the process with the most frames is kept so statistics are never mixed.</param>
    public static FrameCapture Parse(TextReader reader, string? applicationFilter = null)
    {
        string? header = reader.ReadLine() ?? throw new FormatException("The file is empty.");
        string[] columns = SplitCsv(header);
        int frameIdx = -1;
        string? usedColumn = null;
        foreach (string candidate in FrameTimeColumns)
        {
            frameIdx = Array.FindIndex(columns, c => string.Equals(c.Trim(), candidate, StringComparison.Ordinal));
            if (frameIdx >= 0) { usedColumn = candidate; break; }
        }
        if (frameIdx < 0)
            throw new FormatException("No frame-time column found. Expected one of: " + string.Join(", ", FrameTimeColumns) + ".");

        int appIdx = Array.FindIndex(columns, c => c.Trim() is "Application" or "ProcessName");
        var rows = new List<(string App, double Ms)>();
        int skipped = 0;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            string[] fields = SplitCsv(line);
            if (fields.Length <= frameIdx) { skipped++; continue; }
            string app = appIdx >= 0 && fields.Length > appIdx ? fields[appIdx] : "Unknown";
            if (double.TryParse(fields[frameIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out double ms) && ms > 0 && double.IsFinite(ms))
                rows.Add((app, ms));
            else
                skipped++;
        }

        string? target = applicationFilter ?? rows.GroupBy(r => r.App, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        var times = rows.Where(r => string.Equals(r.App, target, StringComparison.OrdinalIgnoreCase)).Select(r => r.Ms).ToList();
        if (times.Count == 0) throw new FormatException("The capture contains no valid frames" + (applicationFilter is null ? "." : $" for '{applicationFilter}'."));
        return new FrameCapture(target ?? "Unknown", times, usedColumn!, skipped);
    }

    public static FrameCapture ParseFile(string path, string? applicationFilter = null)
    {
        using var reader = new StreamReader(path);
        return Parse(reader, applicationFilter);
    }

    internal static string[] SplitCsv(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else current.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        fields.Add(current.ToString());
        return fields.ToArray();
    }
}
