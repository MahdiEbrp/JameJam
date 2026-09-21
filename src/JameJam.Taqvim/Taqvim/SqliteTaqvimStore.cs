using System.Globalization;
using System.Text;
using System.Text.Json;

using JameJam.Data;

using Microsoft.Data.Sqlite;

namespace JameJam.Taqvim;

/// <summary>
/// SQLite-backed <see cref="ITaqvimStore"/>: FTS5 search (LIKE fallback on engines without
/// it), deletion tombstones, and an undo log — one database file, owner-only permissions.
/// </summary>
/// <param name="databasePath">Path of the calendar database (created on first use).</param>
public sealed class SqliteTaqvimStore : ITaqvimStore
{
    private readonly SqliteDatabase _database;
    private readonly object _ftsGate = new();
    private bool _fts = true;
    private int _undoDepth = TaqvimDefaults.UndoDepth;

    /// <summary>Initializes the database (WAL, schema, owner-only permissions).</summary>
    public SqliteTaqvimStore(string databasePath)
    {
        _database = new SqliteDatabase(databasePath);
        _database.Initialize(Schema);
    }

    /// <inheritdoc />
    public int UndoDepth
    {
        get => _undoDepth;
        set => _undoDepth = value >= 0 ? value : throw new TaqvimException("UndoDepth must not be negative.");
    }

    /// <inheritdoc />
    public TaqvimEvent AddEvent(TaqvimEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);
        var identity = ev.SyncId == Guid.Empty ? Guid.CreateVersion7() : ev.SyncId;
        var id = WithWrite(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO events (calendar, title, location, notes, tags, start_at, end_at, is_allday, rule, reminders, created_at, updated_at, sync_id)
                VALUES ($calendar, $title, $location, $notes, $tags, $start, $end, $allday, $rule, $reminders, $created, $updated, $sync);
                SELECT last_insert_rowid();
                """;
            Bind(command, ev, identity);
            return (long)(command.ExecuteScalar() ?? 0L);
        });
        return ev with { Id = id, SyncId = identity };
    }

    /// <inheritdoc />
    public void UpdateEvent(TaqvimEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);
        var rows = WithWrite(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE events SET calendar=$calendar, title=$title, location=$location, notes=$notes, tags=$tags,
                       start_at=$start, end_at=$end, is_allday=$allday, rule=$rule, reminders=$reminders,
                       created_at=$created, updated_at=$updated, sync_id=$sync
                WHERE id=$id
                """;
            Bind(command, ev, ev.SyncId);
            AddParams(command, Param("$id", ev.Id));
            return command.ExecuteNonQuery();
        });
        if (rows == 0)
        {
            throw new TaqvimException($"No event #{ev.Id}.");
        }
    }

    /// <inheritdoc />
    public bool RemoveEvent(long id, DateTimeOffset deletedAt)
    {
        return WithWrite(connection =>
        {
            _ = Exec(connection, """
                INSERT INTO event_tombstones (sync_id, deleted_at)
                SELECT sync_id, $at FROM events WHERE id=$id AND sync_id != ''
                ON CONFLICT (sync_id) DO UPDATE SET deleted_at = excluded.deleted_at
                """, Param("$at", Text(deletedAt)), Param("$id", id));
            return Exec(connection, "DELETE FROM events WHERE id=$id", Param("$id", id)) > 0;
        });
    }

    /// <inheritdoc />
    public TaqvimEvent? FindEvent(long id) =>
        WithRead(connection => QueryOne(connection, SelectEvents + " WHERE id=$id", MapEvent, Param("$id", id)));

    /// <inheritdoc />
    public IReadOnlyList<TaqvimEvent> ListEvents() =>
        WithRead(connection => QueryList(connection, SelectEvents + " ORDER BY start_at, id", MapEvent));

    /// <inheritdoc />
    public IReadOnlyList<long> SearchIds(string query, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return WithRead(connection =>
        {
            lock (_ftsGate)
            {
                if (_fts)
                {
                    try
                    {
                        return QueryList(connection, """
                            SELECT rowid FROM event_fts WHERE event_fts MATCH $q ORDER BY rank LIMIT $limit
                            """, reader => reader.GetInt64(0), Param("$q", FtsQuery(query)), Param("$limit", limit));
                    }
                    catch (SqliteException)
                    {
                        _fts = false; // engine lost FTS mid-flight — fall through to LIKE
                    }
                }
            }

            // LIKE fallback: every term must appear in title/notes/location (AND semantics).
            var conditions = new StringBuilder();
            var parameters = new List<SqliteParameter> { Param("$limit", limit) };
            for (var i = 0; i < terms.Length; i++)
            {
                _ = conditions.Append(i == 0 ? "WHERE " : " AND ");
                _ = conditions.Append(
                    FormattableString.Invariant(
                        $"(title LIKE $t{i} ESCAPE '[' OR notes LIKE $t{i} ESCAPE '[' OR location LIKE $t{i} ESCAPE '[')"));
                parameters.Add(Param($"$t{i}", $"%{EscapeLike(terms[i])}%"));
            }

            return QueryList(
                connection,
                FormattableString.Invariant($"SELECT id FROM events {conditions} ORDER BY updated_at DESC LIMIT $limit"),
                reader => reader.GetInt64(0),
                [.. parameters]);
        });
    }

    /// <inheritdoc />
    public void ReplaceEvents(IReadOnlyList<TaqvimEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        WithWrite(connection =>
        {
            _ = Exec(connection, "DELETE FROM events");
            _ = Exec(connection, "INSERT INTO event_fts(event_fts) VALUES('rebuild')");
            foreach (var ev in events)
            {
                InsertDirect(connection, ev);
            }

            // Restored events retract their tombstones.
            _ = Exec(connection, "DELETE FROM event_tombstones WHERE sync_id IN (SELECT sync_id FROM events WHERE sync_id != '')");
        });
    }

    /// <inheritdoc />
    public void PushUndo(string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        WithWrite(connection =>
        {
            _ = Exec(connection, "INSERT INTO undo_log (created_at, payload) VALUES ($at, $payload)",
                Param("$at", Text(DateTimeOffset.UtcNow)), Param("$payload", payload));
            if (_undoDepth == 0)
            {
                _ = Exec(connection, "DELETE FROM undo_log");
                return;
            }

            _ = Exec(connection, """
                DELETE FROM undo_log WHERE id < (
                    SELECT MIN(id) FROM (SELECT id FROM undo_log ORDER BY id DESC LIMIT $depth))
                """, Param("$depth", _undoDepth));
        });
    }

    /// <inheritdoc />
    public string? PopUndo() =>
        WithRead(connection => QueryOne(connection, "SELECT payload FROM undo_log ORDER BY id DESC LIMIT 1", r => r.GetString(0)))
        is { } payload
            ? StripOldest() ?? payload
            : null;

    /// <inheritdoc />
    public int UndoCount =>
        WithRead(connection => QueryOne(connection, "SELECT COUNT(*) FROM undo_log", r => Convert.ToInt32(r.GetInt64(0), CultureInfo.InvariantCulture)));

    /// <inheritdoc />
    public IReadOnlyList<TaqvimTombstone> GetTombstones() =>
        WithRead(connection => QueryList(connection, """
            SELECT t.sync_id, t.deleted_at FROM event_tombstones t
            WHERE t.sync_id != '' AND NOT EXISTS (SELECT 1 FROM events e WHERE e.sync_id = t.sync_id)
            ORDER BY t.deleted_at
            """, MapTombstone));

    /// <inheritdoc />
    public void UpsertTombstone(TaqvimTombstone tombstone)
    {
        ArgumentNullException.ThrowIfNull(tombstone);
        if (tombstone.SyncId == Guid.Empty)
        {
            return;
        }

        _ = WithWrite(connection => Exec(connection, """
            INSERT INTO event_tombstones (sync_id, deleted_at) VALUES ($id, $at)
            ON CONFLICT (sync_id) DO UPDATE SET deleted_at = excluded.deleted_at
            """, Param("$id", tombstone.SyncId.ToString()), Param("$at", Text(tombstone.DeletedAt))));
    }

    private string? StripOldest()
    {
        return WithWrite(connection =>
        {
            using var select = connection.CreateCommand();
            select.CommandText = "SELECT payload FROM undo_log ORDER BY id DESC LIMIT 1";
            var payload = select.ExecuteScalar() as string;
            _ = Exec(connection, "DELETE FROM undo_log WHERE id = (SELECT MAX(id) FROM undo_log)");
            return payload;
        });
    }

    // ── Plumbing ──

    private const string SelectEvents = """
        SELECT id, calendar, title, location, notes, tags, start_at, end_at, is_allday, rule, reminders, created_at, updated_at, sync_id FROM events
        """;

    private T WithRead<T>(Func<SqliteConnection, T> action)
    {
        using var connection = _database.Open();
        return action(connection);
    }

    private T WithWrite<T>(Func<SqliteConnection, T> action)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        var result = action(connection);
        transaction.Commit();
        return result;
    }

    private void WithWrite(Action<SqliteConnection> action)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        action(connection);
        transaction.Commit();
    }

    private static int Exec(SqliteConnection connection, string sql, params SqliteParameter[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParams(command, parameters);
        return command.ExecuteNonQuery();
    }

    private static T? QueryOne<T>(SqliteConnection connection, string sql, Func<SqliteDataReader, T> map, params SqliteParameter[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParams(command, parameters);
        using var reader = command.ExecuteReader();
        return reader.Read() ? map(reader) : default;
    }

    private static List<T> QueryList<T>(SqliteConnection connection, string sql, Func<SqliteDataReader, T> map, params SqliteParameter[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParams(command, parameters);
        using var reader = command.ExecuteReader();
        List<T> rows = [];
        while (reader.Read())
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    private static void AddParams(SqliteCommand command, params SqliteParameter[] parameters)
    {
        foreach (var parameter in parameters)
        {
            _ = command.Parameters.Add(parameter);
        }
    }

    private static SqliteParameter Param(string name, object? value) => new(name, value ?? DBNull.Value);

    private static string Text(DateTimeOffset stamp) => stamp.ToString("O", CultureInfo.InvariantCulture);

    private static string FtsQuery(string query)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(' ', terms.Select(t => $"\"{t.Replace("\"", "\"\"")}\""));
    }

    private static string EscapeLike(string text) => text.Replace("[", "[[", StringComparison.Ordinal).Replace("%", "[%", StringComparison.Ordinal).Replace("_", "[_", StringComparison.Ordinal);

    // ── Mapping ──

    private static TaqvimEvent MapEvent(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        reader.GetInt64(8) != 0,
        ParseRule(reader.IsDBNull(9) ? null : reader.GetString(9)),
        TaqvimText.RemindersFromCsv(reader.GetString(10)),
        DateTimeOffset.Parse(reader.GetString(11), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        DateTimeOffset.Parse(reader.GetString(12), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        Guid.Parse(reader.GetString(13)));

    private static TaqvimTombstone MapTombstone(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    private static readonly System.Text.Json.JsonSerializerOptions RuleReader = new() { PropertyNameCaseInsensitive = true };

    private static readonly System.Text.Json.JsonSerializerOptions RuleWriter = new();

    private static Recurrence? ParseRule(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? null
            : System.Text.Json.JsonSerializer.Deserialize<Recurrence>(json, RuleReader);

    private static string? RuleToJson(Recurrence? rule) =>
        rule is null || rule.Kind == RecurrenceKind.Once
            ? null
            : System.Text.Json.JsonSerializer.Serialize(rule, RuleWriter);

    private static void Bind(SqliteCommand command, TaqvimEvent ev, Guid syncId)
    {
        AddParams(command,
            Param("$calendar", ev.Calendar),
            Param("$title", ev.Title),
            Param("$location", ev.Location),
            Param("$notes", ev.Notes),
            Param("$tags", ev.Tags),
            Param("$start", Text(ev.Start)),
            Param("$end", Text(ev.End)),
            Param("$allday", ev.IsAllDay ? 1 : 0),
            Param("$rule", RuleToJson(ev.Rule)),
            Param("$reminders", TaqvimText.RemindersToCsv(ev.Reminders)),
            Param("$created", Text(ev.CreatedAt)),
            Param("$updated", Text(ev.UpdatedAt)),
            Param("$sync", syncId.ToString()));
    }

    private static void InsertDirect(SqliteConnection connection, TaqvimEvent ev)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO events (id, calendar, title, location, notes, tags, start_at, end_at, is_allday, rule, reminders, created_at, updated_at, sync_id)
            VALUES ($id, $calendar, $title, $location, $notes, $tags, $start, $end, $allday, $rule, $reminders, $created, $updated, $sync)
            """;
        AddParams(command, Param("$id", ev.Id));
        Bind(command, ev, ev.SyncId == Guid.Empty ? Guid.CreateVersion7() : ev.SyncId);
        _ = command.ExecuteNonQuery(); // the events_ai trigger updates the FTS index
    }

    // ── Schema ──

    private string Schema(SqliteConnection connection)
    {
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA user_version";
            if (Convert.ToInt32(pragma.ExecuteScalar(), CultureInfo.InvariantCulture) >= 1)
            {
                _fts = ProbeFts(connection);
                return string.Empty;
            }
        }

        // 1) Base tables first.
        _ = Exec(connection, """
            CREATE TABLE IF NOT EXISTS events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                calendar TEXT NOT NULL DEFAULT 'Personal',
                title TEXT NOT NULL,
                location TEXT NOT NULL DEFAULT '',
                notes TEXT NOT NULL DEFAULT '',
                tags TEXT NOT NULL DEFAULT '',
                start_at TEXT NOT NULL,
                end_at TEXT NOT NULL,
                is_allday INTEGER NOT NULL DEFAULT 0,
                rule TEXT,
                reminders TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                sync_id TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS idx_events_start ON events (start_at);
            CREATE TABLE IF NOT EXISTS event_tombstones (
                sync_id TEXT PRIMARY KEY,
                deleted_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS undo_log (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                created_at TEXT NOT NULL,
                payload TEXT NOT NULL
            );
            PRAGMA user_version = 1;
            """);

        // 2) FTS5 index + triggers in their own batch (they reference `events`).
        try
        {
            _ = Exec(connection, """
                CREATE VIRTUAL TABLE IF NOT EXISTS event_fts USING fts5(title, notes, location, content='events', content_rowid='id');
                CREATE TRIGGER IF NOT EXISTS events_ai AFTER INSERT ON events BEGIN
                    INSERT INTO event_fts(rowid, title, notes, location) VALUES (new.id, new.title, new.notes, new.location);
                END;
                CREATE TRIGGER IF NOT EXISTS events_ad AFTER DELETE ON events BEGIN
                    INSERT INTO event_fts(event_fts, rowid, title, notes, location) VALUES ('delete', old.id, old.title, old.notes, old.location);
                END;
                CREATE TRIGGER IF NOT EXISTS events_au AFTER UPDATE ON events BEGIN
                    INSERT INTO event_fts(event_fts, rowid, title, notes, location) VALUES ('delete', old.id, old.title, old.notes, old.location);
                    INSERT INTO event_fts(rowid, title, notes, location) VALUES (new.id, new.title, new.notes, new.location);
                END;
                """);
            _fts = ProbeFts(connection);
        }
        catch (SqliteException)
        {
            _fts = false;
        }

        return string.Empty;
    }

    private static bool ProbeFts(SqliteConnection connection)
    {
        try
        {
            _ = QueryList(connection, "SELECT rowid FROM event_fts LIMIT 1", reader => reader.GetInt64(0));
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }
}
