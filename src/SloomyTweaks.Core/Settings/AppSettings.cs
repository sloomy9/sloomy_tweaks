namespace SloomyTweaks.Core.Settings;

/// <summary>User-configurable application settings, stored locally.</summary>
public sealed record AppSettings
{
    public string Theme { get; init; } = "Dark";
    public bool StartWithWindows { get; init; }
    public bool ShowNotifications { get; init; } = true;
    public int BenchmarkHistoryLimit { get; init; } = 200;
    public int LogRetentionDays { get; init; } = 14;
    public bool CreateRestorePoints { get; init; } = true;
    public bool CheckForUpdates { get; init; } = true;
}

public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}

public sealed class InMemorySettingsStore : ISettingsStore
{
    private AppSettings _settings = new();
    public AppSettings Load() => _settings;
    public void Save(AppSettings settings) => _settings = settings;
}
