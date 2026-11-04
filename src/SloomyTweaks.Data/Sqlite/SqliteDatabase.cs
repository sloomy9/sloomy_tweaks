using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace SloomyTweaks.Data.Sqlite;

public sealed class SqliteException : Exception
{
    public SqliteException(int code, string message) : base($"SQLite error {code}: {message}") => Code = code;
    public SqliteException() { }
    public SqliteException(string message) : base(message) { }
    public SqliteException(string message, Exception inner) : base(message, inner) { }
    public int Code { get; }
}

/// <summary>Read-only view of the current result row.</summary>
public readonly ref struct SqliteRow
{
    private readonly IntPtr _stmt;
    internal SqliteRow(IntPtr stmt) => _stmt = stmt;

    public bool IsNull(int i) => Native.sqlite3_column_type(_stmt, i) == Native.SQLITE_NULL;
    public long GetInt64(int i) => Native.sqlite3_column_int64(_stmt, i);
    public int GetInt32(int i) => checked((int)Native.sqlite3_column_int64(_stmt, i));
    public double GetDouble(int i) => Native.sqlite3_column_double(_stmt, i);
    public bool GetBoolean(int i) => Native.sqlite3_column_int64(_stmt, i) != 0;

    public unsafe string GetString(int i)
    {
        byte* text = Native.sqlite3_column_text(_stmt, i);
        if (text == null) return string.Empty;
        int len = Native.sqlite3_column_bytes(_stmt, i);
        return Encoding.UTF8.GetString(text, len);
    }

    public string? GetStringOrNull(int i) => IsNull(i) ? null : GetString(i);
}

/// <summary>
/// Minimal, dependency-free SQLite access. One connection, serialized by a lock; parameters are
/// always bound (never concatenated into SQL).
/// </summary>
public sealed class SqliteDatabase : IDisposable
{
    private readonly object _gate = new();
    private IntPtr _db;

    static SqliteDatabase()
    {
        NativeLibrary.SetDllImportResolver(typeof(SqliteDatabase).Assembly, Resolve);
    }

    public SqliteDatabase(string path)
    {
        if (path != ":memory:")
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
        int rc = OpenRaw(path, out _db);
        if (rc != Native.SQLITE_OK)
        {
            string msg = _db != IntPtr.Zero ? ErrorMessage() : "unable to open database";
            if (_db != IntPtr.Zero) _ = Native.sqlite3_close_v2(_db);
            throw new SqliteException(rc, msg);
        }
        _ = Native.sqlite3_busy_timeout(_db, 5000);
        Execute("PRAGMA foreign_keys = ON;");
        if (path != ":memory:") Execute("PRAGMA journal_mode = WAL;");
    }

    public long LastInsertRowId
    {
        get { lock (_gate) return Native.sqlite3_last_insert_rowid(_db); }
    }

    public int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        lock (_gate)
        {
            IntPtr stmt = Prepare(sql);
            try
            {
                Bind(stmt, parameters);
                int rc;
                while ((rc = Native.sqlite3_step(stmt)) == Native.SQLITE_ROW) { }
                if (rc != Native.SQLITE_DONE) throw new SqliteException(rc, ErrorMessage());
                return Native.sqlite3_changes(_db);
            }
            finally
            {
                _ = Native.sqlite3_finalize(stmt);
            }
        }
    }

    public delegate T RowMapper<out T>(SqliteRow row);

    public List<T> Query<T>(string sql, RowMapper<T> map, params (string Name, object? Value)[] parameters)
    {
        lock (_gate)
        {
            IntPtr stmt = Prepare(sql);
            try
            {
                Bind(stmt, parameters);
                var results = new List<T>();
                int rc;
                while ((rc = Native.sqlite3_step(stmt)) == Native.SQLITE_ROW)
                {
                    results.Add(map(new SqliteRow(stmt)));
                }
                if (rc != Native.SQLITE_DONE) throw new SqliteException(rc, ErrorMessage());
                return results;
            }
            finally
            {
                _ = Native.sqlite3_finalize(stmt);
            }
        }
    }

    /// <summary>Runs <paramref name="work"/> in a transaction; rolls back on exception.</summary>
    public T InTransaction<T>(Func<T> work)
    {
        lock (_gate)
        {
            Execute("BEGIN IMMEDIATE;");
            try
            {
                T result = work();
                Execute("COMMIT;");
                return result;
            }
            catch
            {
                Execute("ROLLBACK;");
                throw;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_db != IntPtr.Zero)
            {
                _ = Native.sqlite3_close_v2(_db);
                _db = IntPtr.Zero;
            }
        }
    }

    private unsafe int OpenRaw(string path, out IntPtr db)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(path + "\0");
        fixed (byte* p = utf8)
        {
            return Native.sqlite3_open_v2(p, out db,
                Native.SQLITE_OPEN_READWRITE | Native.SQLITE_OPEN_CREATE | Native.SQLITE_OPEN_FULLMUTEX, IntPtr.Zero);
        }
    }

    private unsafe IntPtr Prepare(string sql)
    {
        ObjectDisposedException.ThrowIf(_db == IntPtr.Zero, this);
        byte[] utf8 = Encoding.UTF8.GetBytes(sql);
        fixed (byte* p = utf8)
        {
            int rc = Native.sqlite3_prepare_v2(_db, p, utf8.Length, out IntPtr stmt, IntPtr.Zero);
            if (rc != Native.SQLITE_OK) throw new SqliteException(rc, ErrorMessage() + " — in: " + sql);
            return stmt;
        }
    }

    private unsafe void Bind(IntPtr stmt, (string Name, object? Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
        {
            byte[] nameUtf8 = Encoding.UTF8.GetBytes((name.StartsWith('@') ? name : "@" + name) + "\0");
            int index;
            fixed (byte* n = nameUtf8) index = Native.sqlite3_bind_parameter_index(stmt, n);
            if (index == 0) throw new ArgumentException($"SQL parameter '{name}' is not used by the statement.");

            int rc = value switch
            {
                null => Native.sqlite3_bind_null(stmt, index),
                bool b => Native.sqlite3_bind_int64(stmt, index, b ? 1 : 0),
                int i => Native.sqlite3_bind_int64(stmt, index, i),
                long l => Native.sqlite3_bind_int64(stmt, index, l),
                double d => Native.sqlite3_bind_double(stmt, index, d),
                string s => BindText(stmt, index, s),
                DateTimeOffset dto => BindText(stmt, index, dto.UtcDateTime.ToString("O")),
                _ => throw new ArgumentException($"Unsupported parameter type {value.GetType().Name} for '{name}'."),
            };
            if (rc != Native.SQLITE_OK) throw new SqliteException(rc, ErrorMessage());
        }
    }

    private static unsafe int BindText(IntPtr stmt, int index, string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        fixed (byte* p = utf8)
        {
            // SQLITE_TRANSIENT (-1): SQLite copies the buffer before returning.
            return Native.sqlite3_bind_text(stmt, index, p, utf8.Length, new IntPtr(-1));
        }
    }

    private unsafe string ErrorMessage()
    {
        byte* msg = Native.sqlite3_errmsg(_db);
        return msg == null ? "unknown error" : Marshal.PtrToStringUTF8((IntPtr)msg) ?? "unknown error";
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != Native.Library) return IntPtr.Zero;
        string[] candidates = OperatingSystem.IsWindows()
            ? new[] { "winsqlite3.dll" }                 // ships with Windows 10/11 in System32
            : OperatingSystem.IsMacOS()
                ? new[] { "libsqlite3.dylib" }
                : new[] { "libsqlite3.so.0", "libsqlite3.so" };
        foreach (string candidate in candidates)
        {
            if (NativeLibrary.TryLoad(candidate, assembly, DllImportSearchPath.System32 | DllImportSearchPath.SafeDirectories, out IntPtr handle))
                return handle;
        }
        throw new DllNotFoundException("SQLite engine not found. Tried: " + string.Join(", ", candidates));
    }
}

internal static unsafe class Native
{
    public const string Library = "sloomy_sqlite3";
    public const int SQLITE_OK = 0, SQLITE_ROW = 100, SQLITE_DONE = 101, SQLITE_NULL = 5;
    public const int SQLITE_OPEN_READWRITE = 0x2, SQLITE_OPEN_CREATE = 0x4, SQLITE_OPEN_FULLMUTEX = 0x10000;

    [DllImport(Library)] public static extern int sqlite3_open_v2(byte* filename, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Library)] public static extern int sqlite3_close_v2(IntPtr db);
    [DllImport(Library)] public static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport(Library)] public static extern byte* sqlite3_errmsg(IntPtr db);
    [DllImport(Library)] public static extern int sqlite3_prepare_v2(IntPtr db, byte* sql, int nByte, out IntPtr stmt, IntPtr tail);
    [DllImport(Library)] public static extern int sqlite3_step(IntPtr stmt);
    [DllImport(Library)] public static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport(Library)] public static extern int sqlite3_bind_parameter_index(IntPtr stmt, byte* name);
    [DllImport(Library)] public static extern int sqlite3_bind_null(IntPtr stmt, int index);
    [DllImport(Library)] public static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);
    [DllImport(Library)] public static extern int sqlite3_bind_double(IntPtr stmt, int index, double value);
    [DllImport(Library)] public static extern int sqlite3_bind_text(IntPtr stmt, int index, byte* text, int n, IntPtr destructor);
    [DllImport(Library)] public static extern int sqlite3_column_type(IntPtr stmt, int col);
    [DllImport(Library)] public static extern long sqlite3_column_int64(IntPtr stmt, int col);
    [DllImport(Library)] public static extern double sqlite3_column_double(IntPtr stmt, int col);
    [DllImport(Library)] public static extern byte* sqlite3_column_text(IntPtr stmt, int col);
    [DllImport(Library)] public static extern int sqlite3_column_bytes(IntPtr stmt, int col);
    [DllImport(Library)] public static extern long sqlite3_last_insert_rowid(IntPtr db);
    [DllImport(Library)] public static extern int sqlite3_changes(IntPtr db);
}
