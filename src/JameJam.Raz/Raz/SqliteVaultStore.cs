using System.Globalization;

using JameJam.Data;
using JameJam.Raz.Crypto;
using Microsoft.Data.Sqlite;

namespace JameJam.Raz;

/// <summary>
/// SQLite-backed <see cref="IVaultStore"/>. Sensitive columns hold ciphertext BLOBs
/// (the service encrypts); the database file is created 0600 in a 0700 directory.
/// </summary>
public sealed class SqliteVaultStore : IVaultStore
{
    private readonly SqliteDatabase _database;

    /// <summary>Opens (and initializes) the vault database at <paramref name="databasePath"/>.</summary>
    public SqliteVaultStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _database = new SqliteDatabase(databasePath);
        _database.Initialize(connection => Schema(connection));
    }

    /// <summary>Path of the SQLite database file.</summary>
    public string DatabasePath => _database.DatabasePath;

    /// <inheritdoc />
    public bool IsInitialized => GetSalt() is not null;

    /// <inheritdoc />
    public void SetMeta(byte[] salt, int iterations, byte[] keyCheck)
    {
        ArgumentNullException.ThrowIfNull(salt);
        ArgumentNullException.ThrowIfNull(keyCheck);
        WithWrite(connection =>
        {
            _ = Exec(connection, "INSERT OR REPLACE INTO meta (key, value) VALUES ('salt', $salt)",
                Param("$salt", salt));
            _ = Exec(connection, "INSERT OR REPLACE INTO meta (key, value) VALUES ('iterations', $it)",
                Param("$it", iterations.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            _ = Exec(connection, "INSERT OR REPLACE INTO meta (key, value) VALUES ('key_check', $check)",
                Param("$check", keyCheck));
        });
    }

    /// <inheritdoc />
    public byte[]? GetSalt() =>
        QueryMetaBlob("salt");

    /// <inheritdoc />
    public int GetIterations()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = 'iterations'";
        var value = command.ExecuteScalar() as string;
        return int.TryParse(value, out var iterations) ? iterations : 0;
    }

    /// <inheritdoc />
    public byte[]? GetKeyCheck() => QueryMetaBlob("key_check");

    /// <inheritdoc />
    public RazEntry AddEntry(RazEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return WithWrite(connection =>
        {
            var now = Text(entry.UpdatedAt);
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO entries (title, secret, username, url, notes, tags, totp_seed, totp_algo,
                                     totp_digits, totp_period, expires_on, favorite, created_at, updated_at)
                VALUES ($title, $secret, $username, $url, $notes, $tags, $seed, $algo,
                        $digits, $period, $expires, $favorite, $created, $updated);
                SELECT last_insert_rowid();
                """;
            BindEntry(command, entry, now);
            var id = (long)(command.ExecuteScalar() ?? 0L);
            return entry with { Id = id };
        });
    }

    /// <inheritdoc />
    public void UpdateEntry(RazEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        WithWrite(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE entries SET title=$title, secret=$secret, username=$username, url=$url, notes=$notes,
                       tags=$tags, totp_seed=$seed, totp_algo=$algo, totp_digits=$digits, totp_period=$period,
                       expires_on=$expires, favorite=$favorite, created_at=$created, updated_at=$updated
                WHERE id=$id
                """;
            BindEntry(command, entry, Text(entry.UpdatedAt));
            AddParams(command, Param("$id", entry.Id));
            _ = command.ExecuteNonQuery();
        });
    }

    /// <inheritdoc />
    public bool RemoveEntry(long id)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        var affected = Exec(connection, "DELETE FROM entries WHERE id=$id", Param("$id", id));
        transaction.Commit();
        return affected > 0;
    }

    /// <inheritdoc />
    public RazEntry? FindEntry(long id) =>
        WithRead(connection => QueryOne(connection, "SELECT * FROM entries WHERE id=$id", MapEntry, Param("$id", id)));

    /// <inheritdoc />
    public IReadOnlyList<RazEntry> ListEntries() =>
        WithRead(connection => QueryList(connection, "SELECT * FROM entries ORDER BY id", MapEntry));

    /// <inheritdoc />
    public void ReplaceEntries(IReadOnlyList<RazEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        WithWrite(connection =>
        {
            _ = Exec(connection, "DELETE FROM entries");
            foreach (var entry in entries)
            {
                InsertDirect(connection, entry);
            }
        });
    }

    /// <inheritdoc />
    public int Count() =>
        WithRead(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM entries";
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        });

    /// <inheritdoc />
    public void PushUndo(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        WithWrite(connection =>
        {
            _ = Exec(connection,
                "INSERT INTO undo_log (created_at, payload) VALUES ($created, $payload)",
                Param("$created", Text(DateTimeOffset.UtcNow)), Param("$payload", payload));
            TrimUndo(connection);
        });
    }

    /// <inheritdoc />
    public byte[]? PopUndo()
    {
        var payload = WithRead(connection =>
            QueryOne(connection, "SELECT payload FROM undo_log ORDER BY id DESC LIMIT 1", ReadBlob));
        if (payload is null)
        {
            return null;
        }

        WithWrite(connection => _ = Exec(
            connection, "DELETE FROM undo_log WHERE id = (SELECT MAX(id) FROM undo_log)"));
        return payload;
    }

    /// <inheritdoc />
    public int UndoCount =>
        WithRead(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM undo_log";
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        });

    /// <inheritdoc />
    public int UndoDepth
    {
        get => _undoDepth;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _undoDepth = value;
        }
    }

    private int _undoDepth = RazDefaults.UndoDepth;

    // ── Schema ──

    private static string Schema(SqliteConnection connection)
    {
        var version = 0;
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA user_version";
            version = Convert.ToInt32(pragma.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        if (version >= RazDefaults.BackupVersion)
        {
            return string.Empty;
        }

        return """
            CREATE TABLE IF NOT EXISTS meta (
                key TEXT PRIMARY KEY,
                value BLOB NOT NULL
            );
            CREATE TABLE IF NOT EXISTS entries (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                title BLOB NOT NULL,
                secret BLOB NOT NULL,
                username BLOB NOT NULL,
                url BLOB NOT NULL,
                notes BLOB NOT NULL,
                tags BLOB NOT NULL,
                totp_seed BLOB NOT NULL,
                totp_algo INTEGER NOT NULL,
                totp_digits INTEGER NOT NULL,
                totp_period INTEGER NOT NULL,
                expires_on TEXT,
                favorite INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_entries_expires ON entries (expires_on);
            CREATE TABLE IF NOT EXISTS undo_log (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                created_at TEXT NOT NULL,
                payload BLOB NOT NULL
            );
            PRAGMA user_version = 1;
            """;
    }

    // ── Helpers ──

    private byte[]? QueryMetaBlob(string key) =>
        WithRead(connection => QueryOne(connection, "SELECT value FROM meta WHERE key=$key", ReadBlob, Param("$key", key)));

    private static byte[] ReadBlob(SqliteDataReader reader) => (byte[])reader.GetValue(0);

    private static void BindEntry(SqliteCommand command, RazEntry entry, string updated)
    {
        AddParams(command, Param("$title", VaultCrypto.FromText(entry.Title)));
        AddParams(command, Param("$secret", VaultCrypto.FromText(entry.Secret)));
        AddParams(command, Param("$username", VaultCrypto.FromText(entry.Username)));
        AddParams(command, Param("$url", VaultCrypto.FromText(entry.Url)));
        AddParams(command, Param("$notes", VaultCrypto.FromText(entry.Notes)));
        AddParams(command, Param("$tags", VaultCrypto.FromText(entry.Tags)));
        AddParams(command, Param("$seed", VaultCrypto.FromText(entry.TotpSeed)));
        AddParams(command, Param("$algo", (int)entry.TotpAlgorithm));
        AddParams(command, Param("$digits", entry.TotpDigits));
        AddParams(command, Param("$period", entry.TotpPeriodSeconds));
        AddParams(command, Param("$expires",
            entry.ExpiresOn is { } day ? day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : null));
        AddParams(command, Param("$favorite", entry.Favorite ? 1 : 0));
        AddParams(command, Param("$created", Text(entry.CreatedAt)));
        AddParams(command, Param("$updated", updated));
    }

    private static void InsertDirect(SqliteConnection connection, RazEntry entry)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO entries (id, title, secret, username, url, notes, tags, totp_seed, totp_algo,
                                 totp_digits, totp_period, expires_on, favorite, created_at, updated_at)
            VALUES ($id, $title, $secret, $username, $url, $notes, $tags, $seed, $algo,
                    $digits, $period, $expires, $favorite, $created, $updated)
            """;
        AddParams(command, Param("$id", entry.Id));
        BindEntry(command, entry, Text(entry.UpdatedAt));
        _ = command.ExecuteNonQuery();
    }

    private static RazEntry MapEntry(SqliteDataReader reader)
    {
        var expires = reader.IsDBNull(reader.GetOrdinal("expires_on"))
            ? null
            : (DateOnly?)DateOnly.ParseExact(reader.GetString(reader.GetOrdinal("expires_on")), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return new RazEntry(
            reader.GetInt64(reader.GetOrdinal("id")),
            VaultCrypto.ToText(ReadBlobAt(reader, "title")),
            VaultCrypto.ToText(ReadBlobAt(reader, "secret")),
            VaultCrypto.ToText(ReadBlobAt(reader, "username")),
            VaultCrypto.ToText(ReadBlobAt(reader, "url")),
            VaultCrypto.ToText(ReadBlobAt(reader, "notes")),
            VaultCrypto.ToText(ReadBlobAt(reader, "tags")),
            VaultCrypto.ToText(ReadBlobAt(reader, "totp_seed")),
            (TotpAlgorithm)reader.GetInt32(reader.GetOrdinal("totp_algo")),
            reader.GetInt32(reader.GetOrdinal("totp_digits")),
            reader.GetInt32(reader.GetOrdinal("totp_period")),
            expires,
            reader.GetInt64(reader.GetOrdinal("favorite")) != 0,
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at")), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at")), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind));
    }

    private static byte[] ReadBlobAt(SqliteDataReader reader, string column) => (byte[])reader.GetValue(reader.GetOrdinal(column));

    private void TrimUndo(SqliteConnection connection)
    {
        if (_undoDepth == 0)
        {
            _ = Exec(connection, "DELETE FROM undo_log");
            return;
        }

        _ = Exec(
            connection,
            "DELETE FROM undo_log WHERE id < " +
            "(SELECT MIN(id) FROM (SELECT id FROM undo_log ORDER BY id DESC LIMIT $depth))",
            Param("$depth", _undoDepth));
    }

    // ── Shared SQLite plumbing (same shape as the other stores) ──

    private void WithWrite(Action<SqliteConnection> action)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        action(connection);
        transaction.Commit();
    }

    private T WithWrite<T>(Func<SqliteConnection, T> func)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        var result = func(connection);
        transaction.Commit();
        return result;
    }

    private T WithRead<T>(Func<SqliteConnection, T> func)
    {
        using var connection = _database.Open();
        return func(connection);
    }

    private static int Exec(SqliteConnection connection, string text, params SqliteParameter[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = text;
        foreach (var parameter in parameters)
        {
            _ = command.Parameters.Add(parameter);
        }

        return command.ExecuteNonQuery();
    }

    private static T? QueryOne<T>(SqliteConnection connection, string text, Func<SqliteDataReader, T> map, params SqliteParameter[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = text;
        foreach (var parameter in parameters)
        {
            _ = command.Parameters.Add(parameter);
        }

        using var reader = command.ExecuteReader();
        return reader.Read() ? map(reader) : default;
    }

    private static List<T> QueryList<T>(SqliteConnection connection, string text, Func<SqliteDataReader, T> map, params SqliteParameter[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = text;
        foreach (var parameter in parameters)
        {
            _ = command.Parameters.Add(parameter);
        }

        using var reader = command.ExecuteReader();
        List<T> rows = [];
        while (reader.Read())
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    private static SqliteParameter Param(string name, object? value) =>
        new(name, value ?? DBNull.Value);

    private static void AddParams(SqliteCommand command, params SqliteParameter[] parameters)
    {
        foreach (var parameter in parameters)
        {
            _ = command.Parameters.Add(parameter);
        }
    }

    private static string Text(DateTimeOffset stamp) =>
        stamp.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
}
