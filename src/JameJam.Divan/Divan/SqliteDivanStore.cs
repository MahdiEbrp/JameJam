using System.Globalization;
using JameJam.Data;
using Microsoft.Data.Sqlite;

namespace JameJam.Divan;

/// <summary>
/// SQLite-backed <see cref="IDivanStore"/> with an FTS5 index (when the bundled engine
/// ships it) kept in sync by triggers; the search falls back to LIKE otherwise.
/// The database file is created 0600 in a 0700 directory.
/// </summary>
public sealed class SqliteDivanStore : IDivanStore
{
    private readonly SqliteDatabase _database;
    private bool _fts;

    /// <summary>Opens (and initializes) the pad database at <paramref name="databasePath"/>.</summary>
    public SqliteDivanStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _database = new SqliteDatabase(databasePath);
        _database.Initialize(connection => Schema(connection));
        _fts = ProbeFts();
    }

    /// <summary>Path of the SQLite database file.</summary>
    public string DatabasePath => _database.DatabasePath;

    /// <summary>True when the FTS5 index is available.</summary>
    public bool UsesFts => _fts;

    /// <inheritdoc />
    public Notebook AddNotebook(Notebook notebook)
    {
        ArgumentNullException.ThrowIfNull(notebook);
        return WithWrite(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO notebooks (name, created_at, is_archived, updated_at) VALUES ($name, $created, $archived, $updated);
                SELECT last_insert_rowid();
                """;
            AddParams(command,
                Param("$name", notebook.Name),
                Param("$created", Text(notebook.CreatedAt)),
                Param("$archived", notebook.IsArchived ? 1 : 0),
                Param("$updated", notebook.UpdatedAt == default ? null : Text(notebook.UpdatedAt)));
            var id = (long)(command.ExecuteScalar() ?? 0L);
            return notebook with { Id = id };
        });
    }

    /// <inheritdoc />
    public void UpdateNotebook(Notebook notebook)
    {
        ArgumentNullException.ThrowIfNull(notebook);
        var rows = WithWrite(connection => Exec(connection,
            "UPDATE notebooks SET name=$name, created_at=$created, is_archived=$archived, updated_at=$updated WHERE id=$id",
            Param("$name", notebook.Name), Param("$created", Text(notebook.CreatedAt)),
            Param("$archived", notebook.IsArchived ? 1 : 0),
            Param("$updated", notebook.UpdatedAt == default ? null : Text(notebook.UpdatedAt)),
            Param("$id", notebook.Id)));
        if (rows == 0)
        {
            throw new DivanException($"No notebook #{notebook.Id}.");
        }
    }

    /// <inheritdoc />
    public bool RemoveNotebook(long id) =>
        WithWrite(connection => Exec(connection, "DELETE FROM notebooks WHERE id=$id", Param("$id", id)) > 0);

    /// <inheritdoc />
    public Notebook? FindNotebook(long id) =>
        WithRead(connection => QueryOne(connection, "SELECT id, name, created_at, is_archived, updated_at FROM notebooks WHERE id=$id", MapNotebook, Param("$id", id)));

    /// <inheritdoc />
    public Notebook? FindNotebookByName(string name) =>
        WithRead(connection => QueryOne(connection, "SELECT id, name, created_at, is_archived, updated_at FROM notebooks WHERE name=$name COLLATE NOCASE", MapNotebook, Param("$name", name)));

    /// <inheritdoc />
    public IReadOnlyList<Notebook> ListNotebooks() =>
        WithRead(connection => QueryList(connection, "SELECT id, name, created_at, is_archived, updated_at FROM notebooks ORDER BY id", MapNotebook));

    /// <inheritdoc />
    public Note AddNote(Note note)
    {
        ArgumentNullException.ThrowIfNull(note);
        return WithWrite(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO notes (notebook_id, title, body, tags, pinned, archived, created_at, updated_at, sync_id)
                VALUES ($notebook, $title, $body, $tags, $pinned, $archived, $created, $updated, $sync);
                SELECT last_insert_rowid();
                """;
            var identity = note.SyncId == Guid.Empty ? Guid.CreateVersion7() : note.SyncId;
            BindNote(command, note, identity);
            var id = (long)(command.ExecuteScalar() ?? 0L);
            return note with { Id = id, SyncId = identity };
        });
    }

    /// <inheritdoc />
    public void UpdateNote(Note note)
    {
        ArgumentNullException.ThrowIfNull(note);
        var rows = WithWrite(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE notes SET notebook_id=$notebook, title=$title, body=$body, tags=$tags,
                       pinned=$pinned, archived=$archived, created_at=$created, updated_at=$updated,
                       sync_id=$sync
                WHERE id=$id
                """;
            BindNote(command, note, note.SyncId);
            AddParams(command, Param("$id", note.Id));
            return command.ExecuteNonQuery();
        });
        if (rows == 0)
        {
            throw new DivanException($"No note #{note.Id}.");
        }
    }

    /// <inheritdoc />
    public bool RemoveNote(long id, DateTimeOffset deletedAt)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        // Record the tombstone and delete the row atomically; the notes_ad trigger keeps the
        // FTS index in sync — no manual index writes here.
        _ = Exec(connection, """
            INSERT INTO note_tombstones (sync_id, deleted_at)
            SELECT sync_id, $at FROM notes WHERE id=$id AND sync_id != ''
            ON CONFLICT (sync_id) DO UPDATE SET deleted_at = excluded.deleted_at
            """, Param("$at", Text(deletedAt)), Param("$id", id));
        var affected = Exec(connection, "DELETE FROM notes WHERE id=$id", Param("$id", id));
        transaction.Commit();
        return affected > 0;
    }

    /// <inheritdoc />
    public void UpsertTombstone(DivanTombstone tombstone)
    {
        ArgumentNullException.ThrowIfNull(tombstone);
        if (tombstone.SyncId == Guid.Empty)
        {
            return;
        }

        _ = WithWrite(connection => Exec(connection, """
            INSERT INTO note_tombstones (sync_id, deleted_at) VALUES ($id, $at)
            ON CONFLICT (sync_id) DO UPDATE SET deleted_at = excluded.deleted_at
            """, Param("$id", tombstone.SyncId.ToString()), Param("$at", Text(tombstone.DeletedAt))));
    }

    /// <inheritdoc />
    public IReadOnlyList<DivanTombstone> GetTombstones() =>
        WithRead(connection => QueryList(connection, """
            SELECT t.sync_id, t.deleted_at FROM note_tombstones t
            WHERE t.sync_id != '' AND NOT EXISTS (SELECT 1 FROM notes n WHERE n.sync_id = t.sync_id)
            ORDER BY t.deleted_at
            """, MapTombstone));

    /// <inheritdoc />
    public Note? FindNote(long id) =>
        WithRead(connection => QueryOne(connection, SelectNotes + " WHERE id=$id", MapNote, Param("$id", id)));

    /// <inheritdoc />
    public IReadOnlyList<Note> ListNotes() =>
        WithRead(connection => QueryList(connection, SelectNotes + " ORDER BY id", MapNote));

    /// <inheritdoc />
    public IReadOnlyList<long> SearchIds(string query, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (_fts)
        {
            try
            {
                return WithRead(connection =>
                    QueryList(connection,
                        "SELECT rowid FROM notes_fts WHERE notes_fts MATCH $q ORDER BY rank LIMIT $limit",
                        reader => reader.GetInt64(0),
                        Param("$q", FtsQuery(query)), Param("$limit", limit)));
            }
            catch (SqliteException)
            {
                // fall through to the LIKE path (e.g. unusual tokenization edge cases)
            }
        }

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var conditions = string.Join(" AND ", terms.Select((_, i) => $"(title LIKE $t{i} ESCAPE '[' OR body LIKE $t{i} ESCAPE '[')"));
        var sql = $"{SelectNotes} WHERE {conditions} ORDER BY id LIMIT $limit";
        return WithRead(connection =>
            QueryList(connection, sql, reader => reader.GetInt64(0),
                terms.Select((t, i) => Param($"$t{i}", $"%{EscapeLike(t)}%")).Concat([Param("$limit", limit)]).ToArray()));
    }

    /// <inheritdoc />
    public void ReplaceNotes(IReadOnlyList<Note> notes)
    {
        ArgumentNullException.ThrowIfNull(notes);
        WithWrite(connection =>
        {
            _ = Exec(connection, "DELETE FROM notes");
            _ = Exec(connection, "INSERT INTO notes_fts(notes_fts) VALUES('rebuild')");
            foreach (var note in notes)
            {
                InsertNoteDirect(connection, note);
            }

            // Restored notes retract their tombstones.
            _ = Exec(connection, "DELETE FROM note_tombstones WHERE sync_id IN (SELECT sync_id FROM notes WHERE sync_id != '')");
        });
    }

    /// <inheritdoc />
    public void ReplaceNotebooks(IReadOnlyList<Notebook> notebooks)
    {
        ArgumentNullException.ThrowIfNull(notebooks);
        WithWrite(connection =>
        {
            _ = Exec(connection, "DELETE FROM notebooks");
            foreach (var notebook in notebooks)
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO notebooks (id, name, created_at, is_archived) VALUES ($id, $name, $created, $archived)
                    """;
                AddParams(command, Param("$id", notebook.Id), Param("$name", notebook.Name),
                    Param("$created", Text(notebook.CreatedAt)), Param("$archived", notebook.IsArchived ? 1 : 0));
                _ = command.ExecuteNonQuery();
            }
        });
    }

    /// <inheritdoc />
    public void PushUndo(string payload)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        WithWrite(connection =>
        {
            _ = Exec(connection,
                "INSERT INTO undo_log (created_at, payload) VALUES ($created, $payload)",
                Param("$created", Text(DateTimeOffset.UtcNow)), Param("$payload", payload));
            TrimUndo(connection);
        });
    }

    /// <inheritdoc />
    public string? PopUndo()
    {
        var payload = WithRead(connection =>
            QueryOne(connection, "SELECT payload FROM undo_log ORDER BY id DESC LIMIT 1", reader => reader.GetString(0)));
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

    private int _undoDepth = DivanDefaults.UndoDepth;

    // ── Schema ──

    private string Schema(SqliteConnection connection)
    {
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA user_version";
            var version = Convert.ToInt32(pragma.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (version >= 2)
            {
                _fts = ProbeFts();
                return string.Empty;
            }

            if (version == 1)
            {
                MigrateToV2(connection);
            }
        }

        // 1) Base tables first (schema v2: sync identity + tombstones + notebook timestamps).
        _ = Exec(connection, """
            CREATE TABLE IF NOT EXISTS notebooks (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                created_at TEXT NOT NULL,
                is_archived INTEGER NOT NULL DEFAULT 0,
                updated_at TEXT
            );
            CREATE TABLE IF NOT EXISTS notes (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                notebook_id INTEGER NOT NULL,
                title TEXT NOT NULL,
                body TEXT NOT NULL,
                tags TEXT NOT NULL,
                pinned INTEGER NOT NULL DEFAULT 0,
                archived INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                sync_id TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS idx_notes_notebook ON notes (notebook_id);
            CREATE TABLE IF NOT EXISTS note_tombstones (
                sync_id TEXT PRIMARY KEY,
                deleted_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS undo_log (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                created_at TEXT NOT NULL,
                payload TEXT NOT NULL
            );
            PRAGMA user_version = 2;
            """);

        // 2) Then the FTS5 index and its sync triggers (they reference `notes`).
        try
        {
            _ = Exec(connection, """
                CREATE VIRTUAL TABLE IF NOT EXISTS notes_fts USING fts5(title, body, content='notes', content_rowid='id');
                CREATE TRIGGER IF NOT EXISTS notes_ai AFTER INSERT ON notes BEGIN
                    INSERT INTO notes_fts(rowid, title, body) VALUES (new.id, new.title, new.body);
                END;
                CREATE TRIGGER IF NOT EXISTS notes_ad AFTER DELETE ON notes BEGIN
                    INSERT INTO notes_fts(notes_fts, rowid, title, body) VALUES ('delete', old.id, old.title, old.body);
                END;
                CREATE TRIGGER IF NOT EXISTS notes_au AFTER UPDATE ON notes BEGIN
                    INSERT INTO notes_fts(notes_fts, rowid, title, body) VALUES ('delete', old.id, old.title, old.body);
                    INSERT INTO notes_fts(rowid, title, body) VALUES (new.id, new.title, new.body);
                END;
                """);
            _fts = ProbeFts();
        }
        catch (SqliteException)
        {
            _fts = false; // engine without FTS5: LIKE fallback
        }

        return string.Empty;
    }

    /// <summary>v1 → v2: per-note sync identity, deletion tombstones, notebook timestamps.</summary>
    private void MigrateToV2(SqliteConnection connection)
    {
        _ = Exec(connection, """
            ALTER TABLE notes ADD COLUMN sync_id TEXT NOT NULL DEFAULT '';
            UPDATE notes SET sync_id = lower(hex(randomblob(16))) WHERE sync_id = '';
            ALTER TABLE notebooks ADD COLUMN updated_at TEXT;
            UPDATE notebooks SET updated_at = created_at WHERE updated_at IS NULL;
            CREATE TABLE IF NOT EXISTS note_tombstones (
                sync_id TEXT PRIMARY KEY,
                deleted_at TEXT NOT NULL
            );
            PRAGMA user_version = 2;
            """);
        _fts = ProbeFts();
    }

    private bool ProbeFts()
    {
        try
        {
            _ = WithRead(connection =>
                QueryList(connection, "SELECT rowid FROM notes_fts LIMIT 1", reader => reader.GetInt64(0)));
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static string FtsQuery(string query)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(' ', terms.Select(t => $"\"{t.Replace("\"", "\"\"")}\""));
    }

    private static string EscapeLike(string text) => text.Replace("[", "[[", StringComparison.Ordinal).Replace("%", "[%", StringComparison.Ordinal).Replace("_", "[_", StringComparison.Ordinal);

    private const string SelectNotes =
        "SELECT id, notebook_id, title, body, tags, pinned, archived, created_at, updated_at, sync_id FROM notes";

    // ── Mapping ──

    private static Notebook MapNotebook(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        reader.GetInt64(3) != 0,
        reader.IsDBNull(4)
            ? default
            : DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    private static Note MapNote(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetInt64(5) != 0,
        reader.GetInt64(6) != 0,
        DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        Guid.Parse(reader.GetString(9)));

    private static DivanTombstone MapTombstone(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    private static void BindNote(SqliteCommand command, Note note, Guid syncId)
    {
        AddParams(command,
            Param("$notebook", note.NotebookId),
            Param("$title", note.Title),
            Param("$body", note.Body),
            Param("$tags", note.Tags),
            Param("$pinned", note.Pinned ? 1 : 0),
            Param("$archived", note.Archived ? 1 : 0),
            Param("$created", Text(note.CreatedAt)),
            Param("$updated", Text(note.UpdatedAt)),
            Param("$sync", syncId.ToString()));
    }

    private static void InsertNoteDirect(SqliteConnection connection, Note note)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO notes (id, notebook_id, title, body, tags, pinned, archived, created_at, updated_at, sync_id)
            VALUES ($id, $notebook, $title, $body, $tags, $pinned, $archived, $created, $updated, $sync)
            """;
        AddParams(command, Param("$id", note.Id));
        BindNote(command, note, note.SyncId == Guid.Empty ? Guid.CreateVersion7() : note.SyncId);
        _ = command.ExecuteNonQuery(); // the notes_ai trigger updates the FTS index
    }

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

    // ── Shared SQLite plumbing ──

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
        AddParams(command, parameters);
        return command.ExecuteNonQuery();
    }

    private static T? QueryOne<T>(SqliteConnection connection, string text, Func<SqliteDataReader, T> map, params SqliteParameter[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = text;
        AddParams(command, parameters);
        using var reader = command.ExecuteReader();
        return reader.Read() ? map(reader) : default;
    }

    private static List<T> QueryList<T>(SqliteConnection connection, string text, Func<SqliteDataReader, T> map, params SqliteParameter[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = text;
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

    private static SqliteParameter Param(string name, object? value) =>
        new(name, value ?? DBNull.Value);

    private static string Text(DateTimeOffset stamp) =>
        stamp.ToString("O", CultureInfo.InvariantCulture);
}
