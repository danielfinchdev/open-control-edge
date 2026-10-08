using System.IO;
using SQLitePCL;

namespace OpenControlEdge.Services;

/// Opens a SQLite database written by another program that runs as the plain user (Cursor's state.vscdb, OpenCode's
/// opencode.db). The installed copy never does this itself: it runs elevated, so SqliteHelper reads the file in a
/// short-lived copy of the executable started as the plain user, and only a copy that already runs unelevated (Debug,
/// portable) opens it in-process. Either way it is opened the way SQLite recommends for untrusted databases: read-only
/// and query-only, defensive mode, no functions or virtual tables run from the schema (trusted_schema off), cell size
/// checks, no memory mapping, and only a regular file (no link or reparse point) of a sane size.
internal sealed class UntrustedSqlite : IDisposable
{
    private const long MaxBytes = 2L * 1024 * 1024 * 1024;
    private const int MaxRows = 10_000;

    private const int SQLITE_DBCONFIG_DEFENSIVE = 1010;
    private const int SQLITE_DBCONFIG_TRUSTED_SCHEMA = 1017;

    private static readonly Lazy<bool> Initialized = new(() => { Batteries_V2.Init(); return true; });

    private readonly sqlite3 _db;

    private UntrustedSqlite(sqlite3 db) => _db = db;

    public static UntrustedSqlite Open(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > MaxBytes)
            throw new IOException("La base de datos no es un archivo normal.");

        _ = Initialized.Value;
        int rc = raw.sqlite3_open_v2(path, out sqlite3 db, raw.SQLITE_OPEN_READONLY | raw.SQLITE_OPEN_NOMUTEX, null);
        if (rc != raw.SQLITE_OK)
        {
            string error = db is null ? $"cÃ³digo {rc}" : raw.sqlite3_errmsg(db).utf8_to_string();
            db?.Dispose();
            throw new SqliteFailure(rc, error);
        }
        var connection = new UntrustedSqlite(db);
        try
        {
            raw.sqlite3_busy_timeout(db, 2000);
            raw.sqlite3_db_config(db, SQLITE_DBCONFIG_DEFENSIVE, 1, out _);
            raw.sqlite3_db_config(db, SQLITE_DBCONFIG_TRUSTED_SCHEMA, 0, out _);
            connection.Execute("PRAGMA query_only = ON; PRAGMA trusted_schema = OFF; PRAGMA cell_size_check = ON; PRAGMA mmap_size = 0;");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void Execute(string sql)
    {
        int rc = raw.sqlite3_exec(_db, sql);
        if (rc != raw.SQLITE_OK) throw new SqliteFailure(rc, raw.sqlite3_errmsg(_db).utf8_to_string());
    }

    /// Runs one statement and returns its rows as long, double, string or null values (at most MaxRows).
    public List<object?[]> Query(string sql)
    {
        int rc = raw.sqlite3_prepare_v2(_db, sql, out sqlite3_stmt statement);
        if (rc != raw.SQLITE_OK) throw new SqliteFailure(rc, raw.sqlite3_errmsg(_db).utf8_to_string());
        using (statement)
        {
            var rows = new List<object?[]>();
            while ((rc = raw.sqlite3_step(statement)) == raw.SQLITE_ROW)
            {
                if (rows.Count >= MaxRows) throw new InvalidDataException("La consulta devuelve demasiadas filas.");
                int columns = raw.sqlite3_column_count(statement);
                var row = new object?[columns];
                for (int i = 0; i < columns; i++)
                {
                    row[i] = raw.sqlite3_column_type(statement, i) switch
                    {
                        raw.SQLITE_INTEGER => raw.sqlite3_column_int64(statement, i),
                        raw.SQLITE_FLOAT => raw.sqlite3_column_double(statement, i),
                        raw.SQLITE_TEXT => raw.sqlite3_column_text(statement, i).utf8_to_string(),
                        _ => null,
                    };
                }
                rows.Add(row);
            }
            if (rc != raw.SQLITE_DONE) throw new SqliteFailure(rc, raw.sqlite3_errmsg(_db).utf8_to_string());
            return rows;
        }
    }

    public void Dispose() => _db.Dispose();

    /// Snapshots: writes a small database of our own (never one of another program) and closes it.
    internal static void CreateFixture(string path, string sql)
    {
        _ = Initialized.Value;
        int rc = raw.sqlite3_open(path, out sqlite3 db);
        using (db)
        {
            if (rc == raw.SQLITE_OK) rc = raw.sqlite3_exec(db, sql);
            if (rc != raw.SQLITE_OK) throw new SqliteFailure(rc, "fixture");
        }
    }

    /// A SQLite error code: SQLITE_BUSY / SQLITE_LOCKED mean the owning program is writing, so try again later.
    internal sealed class SqliteFailure(int code, string message) : IOException(message)
    {
        public int Code { get; } = code;
        public bool Busy => Code is raw.SQLITE_BUSY or raw.SQLITE_LOCKED;
    }
}
