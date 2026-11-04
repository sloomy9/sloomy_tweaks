using System.Text.Json;

namespace SloomyTweaks.Core.Diagnostics;

public enum LogLevel
{
    Debug,
    Information,
    Warning,
    Error,
}

/// <summary>Structured logging abstraction. Properties are written as JSON fields.</summary>
public interface IAppLogger
{
    void Log(LogLevel level, string eventName, string message, IReadOnlyDictionary<string, object?>? properties = null, Exception? exception = null);
}

public static class AppLoggerExtensions
{
    public static void Info(this IAppLogger log, string eventName, string message, IReadOnlyDictionary<string, object?>? props = null) =>
        log.Log(LogLevel.Information, eventName, message, props);

    public static void Warn(this IAppLogger log, string eventName, string message, IReadOnlyDictionary<string, object?>? props = null) =>
        log.Log(LogLevel.Warning, eventName, message, props);

    public static void Error(this IAppLogger log, string eventName, string message, Exception? e = null, IReadOnlyDictionary<string, object?>? props = null) =>
        log.Log(LogLevel.Error, eventName, message, props, e);
}

public sealed class NullLogger : IAppLogger
{
    public static readonly NullLogger Instance = new();
    public void Log(LogLevel level, string eventName, string message, IReadOnlyDictionary<string, object?>? properties = null, Exception? exception = null) { }
}

/// <summary>
/// Writes one JSON object per line to daily files (sloomy-YYYYMMDD.jsonl) and deletes files
/// older than the retention period. Thread-safe. Never throws to callers.
/// </summary>
public sealed class JsonLinesFileLogger : IAppLogger
{
    private readonly string _directory;
    private readonly LogLevel _minimum;
    private readonly object _gate = new();

    public JsonLinesFileLogger(string directory, LogLevel minimum = LogLevel.Information, int retentionDays = 14)
    {
        _directory = directory;
        _minimum = minimum;
        try
        {
            Directory.CreateDirectory(directory);
            foreach (string file in Directory.EnumerateFiles(directory, "sloomy-*.jsonl"))
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-retentionDays)) File.Delete(file);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public string LogDirectory => _directory;

    public void Log(LogLevel level, string eventName, string message, IReadOnlyDictionary<string, object?>? properties = null, Exception? exception = null)
    {
        if (level < _minimum) return;
        var entry = new Dictionary<string, object?>
        {
            ["ts"] = DateTimeOffset.UtcNow.ToString("O"),
            ["level"] = level.ToString(),
            ["event"] = eventName,
            ["message"] = message,
        };
        if (properties is not null)
        {
            foreach (var (k, v) in properties) entry[k] = v;
        }
        if (exception is not null)
        {
            entry["exception"] = exception.GetType().FullName;
            entry["exceptionMessage"] = exception.Message;
            entry["hresult"] = exception.HResult;
        }

        try
        {
            string line = JsonSerializer.Serialize(entry);
            string path = Path.Combine(_directory, $"sloomy-{DateTime.UtcNow:yyyyMMdd}.jsonl");
            lock (_gate) File.AppendAllText(path, line + Environment.NewLine);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Logging must never break the application.
        }
    }
}
