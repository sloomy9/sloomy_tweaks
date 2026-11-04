using System.Globalization;
using System.Text.Json;
using SloomyTweaks.Core.Benchmark;
using SloomyTweaks.Core.Engine;
using SloomyTweaks.Core.Optimizations;
using SloomyTweaks.Core.Settings;
using SloomyTweaks.Data.Sqlite;

namespace SloomyTweaks.Data;

/// <summary>
/// Local SQLite store for optimization backups, operation history, benchmark runs and settings.
/// Schema changes are applied through numbered migrations recorded in PRAGMA user_version.
/// </summary>
public sealed class AppDataStore : IBackupStore, IHistoryStore, IBenchmarkRepository, ISettingsStore, IDisposable
{
    private static readonly JsonSerializerOptions Json = new();
    private readonly SqliteDatabase _db;

    private static readonly string[] Migrations =
    {
        // 1: initial schema
        """
        CREATE TABLE backups (
            optimization_id TEXT PRIMARY KEY,
            created_at TEXT NOT NULL,
            values_json TEXT NOT NULL);
        CREATE TABLE history (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            optimization_id TEXT NOT NULL,
            action TEXT NOT NULL,
            ts TEXT NOT NULL,
            success INTEGER NOT NULL,
            message TEXT NOT NULL,
            before_json TEXT NOT NULL,
            after_json TEXT NOT NULL);
        CREATE INDEX ix_history_opt ON history(optimization_id, ts);
        CREATE TABLE benchmark_runs (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            label TEXT NOT NULL,
            created_at TEXT NOT NULL,
            application TEXT NOT NULL,
            source TEXT NOT NULL,
            stats_json TEXT NOT NULL,
            system_summary TEXT,
            notes TEXT);
        CREATE TABLE settings (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL);
        """,
    };

    public AppDataStore(string path)
    {
        _db = new SqliteDatabase(path);
        Migrate();
    }

    private void Migrate()
    {
        long version = _db.Query("PRAGMA user_version;", r => r.GetInt64(0))[0];
        for (long i = version; i < Migrations.Length; i++)
        {
            string migration = Migrations[i];
            _db.InTransaction(() =>
            {
                foreach (string statement in migration.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    _db.Execute(statement + ";");
                }
                _db.Execute($"PRAGMA user_version = {i + 1};");
                return 0;
            });
        }
    }

    // ---- IBackupStore ----
    public OptimizationBackup? Get(string optimizationId) =>
        _db.Query("SELECT optimization_id, created_at, values_json FROM backups WHERE optimization_id = @id", MapBackup, ("id", optimizationId)).FirstOrDefault();

    public void Save(OptimizationBackup backup) =>
        _db.Execute("INSERT INTO backups(optimization_id, created_at, values_json) VALUES(@id, @ts, @v) " +
                    "ON CONFLICT(optimization_id) DO UPDATE SET created_at = excluded.created_at, values_json = excluded.values_json",
            ("id", backup.OptimizationId), ("ts", backup.CreatedAt), ("v", JsonSerializer.Serialize(backup.OriginalValues, Json)));

    public void Delete(string optimizationId) =>
        _db.Execute("DELETE FROM backups WHERE optimization_id = @id", ("id", optimizationId));

    public IReadOnlyList<OptimizationBackup> List() =>
        _db.Query("SELECT optimization_id, created_at, values_json FROM backups ORDER BY created_at", MapBackup);

    private static OptimizationBackup MapBackup(SqliteRow r) => new(
        r.GetString(0),
        ParseTime(r.GetString(1)),
        JsonSerializer.Deserialize<Dictionary<string, SettingValue>>(r.GetString(2), Json) ?? new());

    // ---- IHistoryStore ----
    public void Append(OperationRecord record) =>
        _db.Execute("INSERT INTO history(optimization_id, action, ts, success, message, before_json, after_json) VALUES(@o, @a, @t, @s, @m, @b, @af)",
            ("o", record.OptimizationId), ("a", record.Action.ToString()), ("t", record.Timestamp), ("s", record.Success),
            ("m", record.Message), ("b", JsonSerializer.Serialize(record.Before, Json)), ("af", JsonSerializer.Serialize(record.After, Json)));

    IReadOnlyList<OperationRecord> IHistoryStore.List(int limit) =>
        _db.Query("SELECT id, optimization_id, action, ts, success, message, before_json, after_json FROM history ORDER BY ts DESC, id DESC LIMIT @n", MapHistory, ("n", limit));

    public OperationRecord? LatestSuccessful(string optimizationId) =>
        _db.Query("SELECT id, optimization_id, action, ts, success, message, before_json, after_json FROM history WHERE optimization_id = @o AND success = 1 ORDER BY ts DESC, id DESC LIMIT 1",
            MapHistory, ("o", optimizationId)).FirstOrDefault();

    private static OperationRecord MapHistory(SqliteRow r) => new(
        r.GetInt64(0), r.GetString(1), Enum.Parse<OperationAction>(r.GetString(2)), ParseTime(r.GetString(3)), r.GetBoolean(4), r.GetString(5),
        JsonSerializer.Deserialize<Dictionary<string, SettingValue>>(r.GetString(6), Json) ?? new(),
        JsonSerializer.Deserialize<Dictionary<string, SettingValue>>(r.GetString(7), Json) ?? new());

    // ---- IBenchmarkRepository ----
    BenchmarkRun IBenchmarkRepository.Save(BenchmarkRun run)
    {
        return _db.InTransaction(() =>
        {
            _db.Execute("INSERT INTO benchmark_runs(label, created_at, application, source, stats_json, system_summary, notes) VALUES(@l, @c, @a, @s, @j, @sys, @n)",
                ("l", run.ConfigurationLabel), ("c", run.CreatedAt), ("a", run.Application), ("s", run.Source),
                ("j", JsonSerializer.Serialize(run.Stats, Json)), ("sys", run.SystemSummary), ("n", run.Notes));
            return run with { Id = _db.LastInsertRowId };
        });
    }

    IReadOnlyList<BenchmarkRun> IBenchmarkRepository.List() =>
        _db.Query("SELECT id, label, created_at, application, source, stats_json, system_summary, notes FROM benchmark_runs ORDER BY created_at DESC, id DESC",
            r => new BenchmarkRun
            {
                Id = r.GetInt64(0),
                ConfigurationLabel = r.GetString(1),
                CreatedAt = ParseTime(r.GetString(2)),
                Application = r.GetString(3),
                Source = r.GetString(4),
                Stats = JsonSerializer.Deserialize<FrameTimeStats>(r.GetString(5), Json)!,
                SystemSummary = r.GetStringOrNull(6),
                Notes = r.GetStringOrNull(7),
            });

    void IBenchmarkRepository.Delete(long id) => _db.Execute("DELETE FROM benchmark_runs WHERE id = @id", ("id", id));

    /// <summary>Keeps only the newest <paramref name="keep"/> benchmark runs.</summary>
    public int PruneBenchmarks(int keep) =>
        _db.Execute("DELETE FROM benchmark_runs WHERE id NOT IN (SELECT id FROM benchmark_runs ORDER BY created_at DESC, id DESC LIMIT @k)", ("k", keep));

    // ---- ISettingsStore ----
    public AppSettings Load()
    {
        string? json = _db.Query("SELECT value FROM settings WHERE key = 'app'", r => r.GetString(0)).FirstOrDefault();
        if (json is null) return new AppSettings();
        try { return JsonSerializer.Deserialize<AppSettings>(json, Json) ?? new AppSettings(); }
        catch (JsonException) { return new AppSettings(); }
    }

    public void Save(AppSettings settings) =>
        _db.Execute("INSERT INTO settings(key, value) VALUES('app', @v) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            ("v", JsonSerializer.Serialize(settings, Json)));

    public void Dispose() => _db.Dispose();

    private static DateTimeOffset ParseTime(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
