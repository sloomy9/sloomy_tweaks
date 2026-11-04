using SloomyTweaks.Core.Optimizations;

namespace SloomyTweaks.Core.Engine;

/// <summary>Original values captured before Sloomy Tweaks changed anything.</summary>
public sealed record OptimizationBackup(
    string OptimizationId,
    DateTimeOffset CreatedAt,
    IReadOnlyDictionary<string, SettingValue> OriginalValues);

public interface IBackupStore
{
    OptimizationBackup? Get(string optimizationId);
    void Save(OptimizationBackup backup);
    void Delete(string optimizationId);
    IReadOnlyList<OptimizationBackup> List();
}

public enum OperationAction
{
    Apply,
    Restore,
}

public sealed record OperationRecord(
    long Id,
    string OptimizationId,
    OperationAction Action,
    DateTimeOffset Timestamp,
    bool Success,
    string Message,
    IReadOnlyDictionary<string, SettingValue> Before,
    IReadOnlyDictionary<string, SettingValue> After);

public interface IHistoryStore
{
    void Append(OperationRecord record);
    IReadOnlyList<OperationRecord> List(int limit = 500);
    OperationRecord? LatestSuccessful(string optimizationId);
}

public sealed class InMemoryBackupStore : IBackupStore
{
    private readonly Dictionary<string, OptimizationBackup> _items = new();
    private readonly object _gate = new();

    public OptimizationBackup? Get(string optimizationId)
    {
        lock (_gate) return _items.GetValueOrDefault(optimizationId);
    }

    public void Save(OptimizationBackup backup)
    {
        lock (_gate) _items[backup.OptimizationId] = backup;
    }

    public void Delete(string optimizationId)
    {
        lock (_gate) _items.Remove(optimizationId);
    }

    public IReadOnlyList<OptimizationBackup> List()
    {
        lock (_gate) return _items.Values.OrderBy(b => b.CreatedAt).ToList();
    }
}

public sealed class InMemoryHistoryStore : IHistoryStore
{
    private readonly List<OperationRecord> _items = new();
    private readonly object _gate = new();

    public void Append(OperationRecord record)
    {
        lock (_gate) _items.Add(record with { Id = _items.Count + 1 });
    }

    public IReadOnlyList<OperationRecord> List(int limit = 500)
    {
        lock (_gate) return _items.OrderByDescending(r => r.Timestamp).ThenByDescending(r => r.Id).Take(limit).ToList();
    }

    public OperationRecord? LatestSuccessful(string optimizationId)
    {
        lock (_gate)
            return _items.Where(r => r.OptimizationId == optimizationId && r.Success)
                .OrderByDescending(r => r.Timestamp).ThenByDescending(r => r.Id).FirstOrDefault();
    }
}
