using System.IO;
using Microsoft.Data.Sqlite;

namespace OpenControlEdge.Services;

/// Opens a SQLite database written by another program that runs as the plain user (Cursor's state.vscdb, OpenCode's
/// opencode.db) from this process, which is elevated when installed. Any program of the user can craft that file, so
/// it is opened the way SQLite recommends for untrusted databases: read-only and query-only, defensive mode, no
/// functions or virtual tables run from the schema (trusted_schema off), cell size checks, no memory mapping, and only
/// a regular file (no link or reparse point) of a sane size. SQLite still parses the file inside this process; that
/// remaining risk is described in the README ("Seguridad").
internal static class UntrustedSqlite
{
    private const long MaxBytes = 2L * 1024 * 1024 * 1024;

    private const int SQLITE_DBCONFIG_DEFENSIVE = 1010;
    private const int SQLITE_DBCONFIG_TRUSTED_SCHEMA = 1017;

    public static SqliteConnection Open(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > MaxBytes)
            throw new IOException("La base de datos no es un archivo normal.");

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 2,
        };
        var connection = new SqliteConnection(builder.ToString());
        try
        {
            connection.Open();
            SQLitePCL.raw.sqlite3_db_config(connection.Handle, SQLITE_DBCONFIG_DEFENSIVE, 1, out _);
            SQLitePCL.raw.sqlite3_db_config(connection.Handle, SQLITE_DBCONFIG_TRUSTED_SCHEMA, 0, out _);
            using SqliteCommand pragmas = connection.CreateCommand();
            pragmas.CommandTimeout = 2;
            pragmas.CommandText = "PRAGMA query_only = ON; PRAGMA trusted_schema = OFF; PRAGMA cell_size_check = ON; PRAGMA mmap_size = 0;";
            pragmas.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
