using System.Globalization;
using JameJam.Data;

namespace JameJam.Settings;

/// <summary>
/// SQLite-backed settings store — the toolbox's safe persistent settings layer.
/// Safety: parameterized SQL only (no string-built queries), transactional upserts,
/// WAL journaling, user-only file permissions on Unix, versioned schema creation
/// exactly once per store lifetime (shared <see cref="SqliteDatabase"/>), and refusal
/// to store secret-looking keys. All limits come from <see cref="SettingsOptions"/> —
/// validated by the guard, never hardcoded.
/// </summary>
/// <param name="databasePath">Path of the SQLite database file (created on first use).</param>
/// <param name="options">Custom limits; defaults apply when null.</param>
public sealed class SqliteSettingsStore : ISettingsStore
{
    private const string CreateSchemaSql = """
        CREATE TABLE IF NOT EXISTS settings (
            key        TEXT PRIMARY KEY,
            value      TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        PRAGMA user_version = 1;
        """;

    private readonly SqliteDatabase _database;
    private readonly SettingsOptions _options;

    /// <summary>Initializes the store with optional custom limits (validated on construction).</summary>
    public SqliteSettingsStore(string databasePath, SettingsOptions? options = null)
    {
        _options = options ?? new SettingsOptions();
        _options.Validate();
        _database = new SqliteDatabase(databasePath);
    }

    /// <summary>Path of the SQLite database file.</summary>
    public string DatabasePath => _database.DatabasePath;

    /// <inheritdoc />
    public string? GetValue(string key)
    {
        SettingGuard.ValidateKey(key, _options.MaxKeyLength);
        _database.Initialize(_ => CreateSchemaSql);

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);

        return command.ExecuteScalar() as string;
    }

    /// <inheritdoc />
    public IReadOnlyList<SettingsEntry> GetAll()
    {
        _database.Initialize(_ => CreateSchemaSql);

        var entries = new List<SettingsEntry>();
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        // ORDER BY walks the primary-key index — sorted output without a separate sort step.
        command.CommandText = "SELECT key, value, updated_at FROM settings ORDER BY key";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(new SettingsEntry(
                reader.GetString(0),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }

        return entries;
    }

    /// <inheritdoc />
    public void SetValue(string key, string? value)
    {
        SettingGuard.ValidateKey(key, _options.MaxKeyLength);
        SettingGuard.EnsureNotSecretKey(key, _options.SecretKeyNeedles);
        var safeValue = SettingGuard.ValidateValue(value, _options.MaxValueLength);
        _database.Initialize(_ => CreateSchemaSql);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO settings(key, value, updated_at)
            VALUES($key, $value, $now)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", safeValue);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <inheritdoc />
    public bool Remove(string key)
    {
        SettingGuard.ValidateKey(key, _options.MaxKeyLength);
        _database.Initialize(_ => CreateSchemaSql);

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM settings WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);

        return command.ExecuteNonQuery() > 0;
    }

    /// <inheritdoc />
    public int Clear()
    {
        _database.Initialize(_ => CreateSchemaSql);

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM settings";

        return command.ExecuteNonQuery();
    }
}
