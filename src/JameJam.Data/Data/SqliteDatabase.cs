using Microsoft.Data.Sqlite;

namespace JameJam.Data;

/// <summary>
/// Shared, safety-hardened access to one local SQLite database file:
/// parameterized connections (callers still parameterize every statement), WAL journaling,
/// versioned schema creation exactly once per lifetime (double-checked lock), and
/// owner-only file permissions on Unix for the database and its created directory.
/// The schema script is produced by a factory that may inspect the live connection
/// (e.g. to migrate from an older <c>PRAGMA user_version</c>).
/// </summary>
/// <param name="databasePath">Path of the SQLite database file (created on first use).</param>
public sealed class SqliteDatabase(string databasePath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = false,
    }.ToString();

    private readonly object _initializationLock = new();
    private bool _initialized;

    /// <summary>Path of the SQLite database file.</summary>
    public string DatabasePath { get; } = databasePath;

    /// <summary>
    /// Creates the directory, WAL mode, and schema once per store lifetime,
    /// then locks the file down to the current user on Unix.
    /// </summary>
    /// <param name="schemaFactory">
    /// Produces the idempotent schema/migration script; receives the open connection so it
    /// can inspect the current schema version first.
    /// </param>
    public void Initialize(Func<SqliteConnection, string> schemaFactory)
    {
        if (_initialized)
            return;

        lock (_initializationLock)
        {
            if (_initialized)
                return;

            var directory = Path.GetDirectoryName(Path.GetFullPath(DatabasePath));
            if (!string.IsNullOrEmpty(directory))
            {
                var createdDirectory = !Directory.Exists(directory);
                Directory.CreateDirectory(directory);
                if (createdDirectory)
                    RestrictPermissions(directory, isDirectory: true);
            }

            using (var connection = Open())
            {
                using var journal = connection.CreateCommand();
                journal.CommandText = "PRAGMA journal_mode = WAL";
                journal.ExecuteNonQuery();

                using var schema = connection.CreateCommand();
                schema.CommandText = schemaFactory(connection);
                schema.ExecuteNonQuery();
            }

            RestrictPermissions(Path.GetFullPath(DatabasePath), isDirectory: false);
            _initialized = true;
        }
    }

    /// <summary>Opens a short-lived connection to the database.</summary>
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    /// <summary>Grants only the owner access (best-effort; a no-op on Windows).</summary>
    private static void RestrictPermissions(string path, bool isDirectory)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var ownerOnly = isDirectory
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite;
            File.SetUnixFileMode(path, ownerOnly);
        }
    }
}
