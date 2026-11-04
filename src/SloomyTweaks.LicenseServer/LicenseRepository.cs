using System.Globalization;
using SloomyTweaks.Data.Sqlite;
using SloomyTweaks.Licensing;

namespace SloomyTweaks.LicenseServer;

public sealed record Customer(string Id, string Email, string? Name, DateTimeOffset CreatedAt, string? Notes);

public sealed record LicenseRecord(
    string Id,
    string KeyHash,
    string KeyHint,
    string? CustomerId,
    LicensePlan Plan,
    int MaxDevices,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ActivatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    string? RevokeReason);

public sealed record ActivationRecord(
    long Id,
    string LicenseId,
    string DeviceId,
    string DeviceName,
    string AppVersion,
    DateTimeOffset ActivatedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? DeactivatedAt);

public sealed record AuditRecord(long Id, DateTimeOffset Timestamp, string Actor, string Action, string Details);

public sealed class LicenseRepository : IDisposable
{
    private readonly SqliteDatabase _db;

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS customers (
            id TEXT PRIMARY KEY,
            email TEXT NOT NULL UNIQUE,
            name TEXT,
            created_at TEXT NOT NULL,
            notes TEXT);
        CREATE TABLE IF NOT EXISTS licenses (
            id TEXT PRIMARY KEY,
            key_hash TEXT NOT NULL UNIQUE,
            key_hint TEXT NOT NULL,
            customer_id TEXT REFERENCES customers(id),
            plan INTEGER NOT NULL,
            max_devices INTEGER NOT NULL,
            created_at TEXT NOT NULL,
            activated_at TEXT,
            expires_at TEXT,
            revoked_at TEXT,
            revoke_reason TEXT);
        CREATE TABLE IF NOT EXISTS activations (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            license_id TEXT NOT NULL REFERENCES licenses(id),
            device_id TEXT NOT NULL,
            device_name TEXT NOT NULL,
            app_version TEXT NOT NULL,
            activated_at TEXT NOT NULL,
            last_seen_at TEXT NOT NULL,
            deactivated_at TEXT);
        CREATE INDEX IF NOT EXISTS ix_activations_license ON activations(license_id, device_id);
        CREATE TABLE IF NOT EXISTS audit (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            ts TEXT NOT NULL,
            actor TEXT NOT NULL,
            action TEXT NOT NULL,
            details TEXT NOT NULL)
        """;

    public LicenseRepository(string path)
    {
        _db = new SqliteDatabase(path);
        foreach (string stmt in Schema.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            _db.Execute(stmt + ";");
    }

    public T InTransaction<T>(Func<T> work) => _db.InTransaction(work);

    // ---- customers ----
    public Customer CreateCustomer(string email, string? name, string? notes, DateTimeOffset now)
    {
        var c = new Customer("cus_" + Guid.NewGuid().ToString("N")[..16], email.Trim().ToLowerInvariant(), name, now, notes);
        _db.Execute("INSERT INTO customers(id, email, name, created_at, notes) VALUES(@id, @e, @n, @c, @no)",
            ("id", c.Id), ("e", c.Email), ("n", c.Name), ("c", c.CreatedAt), ("no", c.Notes));
        return c;
    }

    public Customer? FindCustomerByEmail(string email) =>
        _db.Query("SELECT id, email, name, created_at, notes FROM customers WHERE email = @e", MapCustomer, ("e", email.Trim().ToLowerInvariant())).FirstOrDefault();

    public Customer? GetCustomer(string id) =>
        _db.Query("SELECT id, email, name, created_at, notes FROM customers WHERE id = @id", MapCustomer, ("id", id)).FirstOrDefault();

    public IReadOnlyList<Customer> ListCustomers() =>
        _db.Query("SELECT id, email, name, created_at, notes FROM customers ORDER BY created_at DESC", MapCustomer);

    private static Customer MapCustomer(SqliteRow r) =>
        new(r.GetString(0), r.GetString(1), r.GetStringOrNull(2), Time(r.GetString(3)), r.GetStringOrNull(4));

    // ---- licenses ----
    private const string LicenseColumns = "id, key_hash, key_hint, customer_id, plan, max_devices, created_at, activated_at, expires_at, revoked_at, revoke_reason";

    public void InsertLicense(LicenseRecord l) =>
        _db.Execute($"INSERT INTO licenses({LicenseColumns}) VALUES(@id, @h, @hint, @c, @p, @m, @cr, @a, @e, @r, @rr)",
            ("id", l.Id), ("h", l.KeyHash), ("hint", l.KeyHint), ("c", l.CustomerId), ("p", (int)l.Plan), ("m", l.MaxDevices),
            ("cr", l.CreatedAt), ("a", l.ActivatedAt), ("e", l.ExpiresAt), ("r", l.RevokedAt), ("rr", l.RevokeReason));

    public LicenseRecord? FindLicenseByHash(string keyHash) =>
        _db.Query($"SELECT {LicenseColumns} FROM licenses WHERE key_hash = @h", MapLicense, ("h", keyHash)).FirstOrDefault();

    public LicenseRecord? GetLicense(string id) =>
        _db.Query($"SELECT {LicenseColumns} FROM licenses WHERE id = @id", MapLicense, ("id", id)).FirstOrDefault();

    public IReadOnlyList<LicenseRecord> ListLicenses(string? customerId = null) => customerId is null
        ? _db.Query($"SELECT {LicenseColumns} FROM licenses ORDER BY created_at DESC", MapLicense)
        : _db.Query($"SELECT {LicenseColumns} FROM licenses WHERE customer_id = @c ORDER BY created_at DESC", MapLicense, ("c", customerId));

    public void MarkActivated(string id, DateTimeOffset activatedAt, DateTimeOffset? expiresAt) =>
        _db.Execute("UPDATE licenses SET activated_at = @a, expires_at = COALESCE(expires_at, @e) WHERE id = @id AND activated_at IS NULL",
            ("id", id), ("a", activatedAt), ("e", expiresAt));

    public void UpdateLicense(string id, DateTimeOffset? expiresAt, bool clearExpiry, int? maxDevices)
    {
        if (clearExpiry) _db.Execute("UPDATE licenses SET expires_at = NULL WHERE id = @id", ("id", id));
        else if (expiresAt is not null) _db.Execute("UPDATE licenses SET expires_at = @e WHERE id = @id", ("id", id), ("e", expiresAt));
        if (maxDevices is not null) _db.Execute("UPDATE licenses SET max_devices = @m WHERE id = @id", ("id", id), ("m", maxDevices.Value));
    }

    public void SetRevoked(string id, DateTimeOffset? revokedAt, string? reason) =>
        _db.Execute("UPDATE licenses SET revoked_at = @r, revoke_reason = @rr WHERE id = @id", ("id", id), ("r", revokedAt), ("rr", reason));

    private static LicenseRecord MapLicense(SqliteRow r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetStringOrNull(3), (LicensePlan)r.GetInt32(4), r.GetInt32(5),
        Time(r.GetString(6)), TimeOrNull(r.GetStringOrNull(7)), TimeOrNull(r.GetStringOrNull(8)), TimeOrNull(r.GetStringOrNull(9)), r.GetStringOrNull(10));

    // ---- activations ----
    private const string ActivationColumns = "id, license_id, device_id, device_name, app_version, activated_at, last_seen_at, deactivated_at";

    public IReadOnlyList<ActivationRecord> ListActivations(string licenseId, bool activeOnly) =>
        _db.Query($"SELECT {ActivationColumns} FROM activations WHERE license_id = @l {(activeOnly ? "AND deactivated_at IS NULL" : "")} ORDER BY activated_at DESC",
            MapActivation, ("l", licenseId));

    public ActivationRecord? FindActiveActivation(string licenseId, string deviceId) =>
        _db.Query($"SELECT {ActivationColumns} FROM activations WHERE license_id = @l AND device_id = @d AND deactivated_at IS NULL", MapActivation,
            ("l", licenseId), ("d", deviceId)).FirstOrDefault();

    public ActivationRecord? GetActivation(long id) =>
        _db.Query($"SELECT {ActivationColumns} FROM activations WHERE id = @id", MapActivation, ("id", id)).FirstOrDefault();

    public void InsertActivation(string licenseId, string deviceId, string deviceName, string appVersion, DateTimeOffset now) =>
        _db.Execute("INSERT INTO activations(license_id, device_id, device_name, app_version, activated_at, last_seen_at) VALUES(@l, @d, @n, @v, @t, @t)",
            ("l", licenseId), ("d", deviceId), ("n", deviceName), ("v", appVersion), ("t", now));

    public void TouchActivation(long id, DateTimeOffset now, string? appVersion = null)
    {
        if (appVersion is null) _db.Execute("UPDATE activations SET last_seen_at = @t WHERE id = @id", ("id", id), ("t", now));
        else _db.Execute("UPDATE activations SET last_seen_at = @t, app_version = @v WHERE id = @id", ("id", id), ("t", now), ("v", appVersion));
    }

    public void DeactivateActivation(long id, DateTimeOffset now) =>
        _db.Execute("UPDATE activations SET deactivated_at = @t WHERE id = @id AND deactivated_at IS NULL", ("id", id), ("t", now));

    private static ActivationRecord MapActivation(SqliteRow r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), Time(r.GetString(5)), Time(r.GetString(6)), TimeOrNull(r.GetStringOrNull(7)));

    // ---- audit ----
    public void Audit(string actor, string action, string details, DateTimeOffset now) =>
        _db.Execute("INSERT INTO audit(ts, actor, action, details) VALUES(@t, @a, @ac, @d)", ("t", now), ("a", actor), ("ac", action), ("d", details));

    public IReadOnlyList<AuditRecord> ListAudit(int limit) =>
        _db.Query("SELECT id, ts, actor, action, details FROM audit ORDER BY id DESC LIMIT @n",
            r => new AuditRecord(r.GetInt64(0), Time(r.GetString(1)), r.GetString(2), r.GetString(3), r.GetString(4)), ("n", limit));

    public void Dispose() => _db.Dispose();

    private static DateTimeOffset Time(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static DateTimeOffset? TimeOrNull(string? s) => s is null ? null : Time(s);
}
