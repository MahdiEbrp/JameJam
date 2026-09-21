using System.Globalization;
using System.Text.Json;
using JameJam.Data;
using Microsoft.Data.Sqlite;

namespace JameJam.HaftKhan;

/// <summary>
/// SQLite-backed task repository (schema version 2: tags, dependencies, recurrence, undo history).
/// Safety: parameterized SQL only, transactional writes, owner-only file permissions,
/// single-init versioned schema with migration from version 1 (shared <see cref="SqliteDatabase"/>).
/// </summary>
/// <param name="databasePath">Path of the SQLite database file (created on first use).</param>
/// <param name="undoDepth">How many undo snapshots to keep (defaults to the named rail).</param>
public sealed class SqliteTaskRepository(string databasePath, int undoDepth = HaftKhanOptions.DefaultMaxUndoDepth) : ITaskRepository
{
    private const int CurrentSchemaVersion = 3;

    private const string SelectColumns = """
        id, title, notes, priority, state, due_date, created_at, updated_at, completed_at,
        project, effort, recurrence_kind, recurrence_interval, started_at, uid
        """;

    private const string CreateTasksTableSql = """
        CREATE TABLE IF NOT EXISTS tasks (
            id                  INTEGER PRIMARY KEY,
            title               TEXT NOT NULL,
            notes               TEXT NOT NULL,
            priority            INTEGER NOT NULL,
            state               INTEGER NOT NULL,
            due_date            TEXT,
            created_at          TEXT NOT NULL,
            updated_at          TEXT NOT NULL,
            completed_at        TEXT,
            project             TEXT NOT NULL DEFAULT '',
            effort              INTEGER NOT NULL DEFAULT 0,
            recurrence_kind     INTEGER NOT NULL DEFAULT 0,
            recurrence_interval INTEGER NOT NULL DEFAULT 1,
            started_at          TEXT,
            uid                 TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_tasks_state_due ON tasks(state, due_date);
        """;

    private const string CreateAuxTablesSql = """
        CREATE TABLE IF NOT EXISTS task_tags (
            task_id INTEGER NOT NULL,
            tag     TEXT NOT NULL,
            UNIQUE(task_id, tag)
        );
        CREATE INDEX IF NOT EXISTS ix_task_tags_tag ON task_tags(tag);
        CREATE TABLE IF NOT EXISTS task_dependencies (
            task_id     INTEGER NOT NULL,
            depends_on  INTEGER NOT NULL,
            PRIMARY KEY (task_id, depends_on)
        );
        CREATE TABLE IF NOT EXISTS undo_log (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            created_at TEXT NOT NULL,
            payload    TEXT NOT NULL
        );
        """;

    private static readonly JsonSerializerOptions SnapshotJsonOptions = new();

    private readonly SqliteDatabase _database = new(databasePath);
    private readonly int _undoDepth = undoDepth;

    /// <summary>Path of the SQLite database file.</summary>
    public string DatabasePath => _database.DatabasePath;

    /// <inheritdoc />
    public HaftKhanTask Add(NewTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        ValidateEnum(task.Priority, nameof(task.Priority));
        ValidateEnum(task.Effort, nameof(task.Effort));
        ValidateEnum(task.Recurrence, nameof(task.Recurrence));
        _database.Initialize(SchemaFactory);

        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        long id;
        string createdUid = string.Empty;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            var uid = string.IsNullOrEmpty(task.Uid) ? Backup.NewUid() : task.Uid;
            command.CommandText = """
                INSERT INTO tasks(title, notes, priority, state, due_date, created_at, updated_at, completed_at,
                                  project, effort, recurrence_kind, recurrence_interval, started_at, uid)
                VALUES($title, $notes, $priority, $state, $due, $now, $now, NULL,
                        $project, $effort, $recKind, $recInterval, NULL, $uid)
                """;
            command.Parameters.AddWithValue("$title", task.Title);
            command.Parameters.AddWithValue("$notes", task.Notes);
            command.Parameters.AddWithValue("$priority", (int)task.Priority);
            command.Parameters.AddWithValue("$state", (int)TaskState.Todo);
            AddOptionalDate(command, "$due", task.DueDate);
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$project", task.Project);
            command.Parameters.AddWithValue("$effort", (int)task.Effort);
            command.Parameters.AddWithValue("$recKind", (int)task.Recurrence);
            command.Parameters.AddWithValue("$recInterval", task.RecurrenceInterval);
            command.Parameters.AddWithValue("$uid", uid);
            command.ExecuteNonQuery();
            createdUid = uid;
        }

        using (var identity = connection.CreateCommand())
        {
            identity.Transaction = transaction;
            identity.CommandText = "SELECT last_insert_rowid()";
            id = (long)identity.ExecuteScalar()!;
        }

        InsertTags(connection, transaction, id, task.Tags);
        transaction.Commit();

        foreach (var blocker in task.BlockedBy)
        {
            AddDependency(id, blocker);
        }

        return new HaftKhanTask(
            id, task.Title, task.Notes, task.Priority, TaskState.Todo,
            task.DueDate, DateTimeOffset.Parse(now, CultureInfo.InvariantCulture), DateTimeOffset.Parse(now, CultureInfo.InvariantCulture), null)
        {
            Project = task.Project,
            Tags = [.. task.Tags],
            Effort = task.Effort,
            Recurrence = task.Recurrence,
            RecurrenceInterval = task.RecurrenceInterval,
            Uid = createdUid,
        };
    }

    /// <inheritdoc />
    public HaftKhanTask? Find(long id)
    {
        _database.Initialize(SchemaFactory);

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM tasks WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        var task = MapRow(reader);
        task = task with { Tags = LoadTags(connection, id) };
        return task;
    }

    /// <inheritdoc />
    public void Update(HaftKhanTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        ValidateEnum(task.Priority, nameof(task.Priority));
        ValidateEnum(task.State, nameof(task.State));
        ValidateEnum(task.Effort, nameof(task.Effort));
        ValidateEnum(task.Recurrence, nameof(task.Recurrence));
        _database.Initialize(SchemaFactory);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE tasks
            SET title = $title, notes = $notes, priority = $priority, state = $state,
                due_date = $due, updated_at = $updatedAt, completed_at = $completedAt,
                project = $project, effort = $effort, recurrence_kind = $recKind,
                recurrence_interval = $recInterval, started_at = $startedAt
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$title", task.Title);
        command.Parameters.AddWithValue("$notes", task.Notes);
        command.Parameters.AddWithValue("$priority", (int)task.Priority);
        command.Parameters.AddWithValue("$state", (int)task.State);
        AddOptionalDate(command, "$due", task.DueDate);
        command.Parameters.AddWithValue("$updatedAt", task.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        AddOptionalInstant(command, "$completedAt", task.CompletedAt);
        command.Parameters.AddWithValue("$project", task.Project);
        command.Parameters.AddWithValue("$effort", (int)task.Effort);
        command.Parameters.AddWithValue("$recKind", (int)task.Recurrence);
        command.Parameters.AddWithValue("$recInterval", task.RecurrenceInterval);
        AddOptionalInstant(command, "$startedAt", task.StartedAt);
        command.Parameters.AddWithValue("$id", task.Id);

        if (command.ExecuteNonQuery() == 0)
            throw new TaskNotFoundException(task.Id);

        DeleteTags(connection, transaction, task.Id);
        InsertTags(connection, transaction, task.Id, task.Tags);
        transaction.Commit();
    }

    /// <inheritdoc />
    public bool Remove(long id)
    {
        _database.Initialize(SchemaFactory);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        var removed = Exists(connection, id);
        if (removed)
            DeleteTask(connection, transaction, id); // links + tags + row, all on this connection

        transaction.Commit();
        return removed;
    }

    /// <inheritdoc />
    public int RemoveCompleted()
    {
        _database.Initialize(SchemaFactory);

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM tasks WHERE state = $done";
        command.Parameters.AddWithValue("$done", (int)TaskState.Done);

        return command.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public IReadOnlyList<HaftKhanTask> ListOpen()
    {
        _database.Initialize(SchemaFactory);

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns} FROM tasks
            WHERE state != $done
            ORDER BY priority DESC, (due_date IS NULL), due_date, id
            """;
        command.Parameters.AddWithValue("$done", (int)TaskState.Done);

        return ReadAll(connection, command);
    }

    /// <inheritdoc />
    public IReadOnlyList<HaftKhanTask> ListAll()
    {
        _database.Initialize(SchemaFactory);

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns} FROM tasks
            ORDER BY state, priority DESC, (due_date IS NULL), due_date, id
            """;

        return ReadAll(connection, command);
    }

    /// <inheritdoc />
    public IReadOnlyList<HaftKhanTask> ListDueOnOrBefore(DateOnly dueDate)
    {
        _database.Initialize(SchemaFactory);

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns} FROM tasks
            WHERE state != $done AND due_date IS NOT NULL AND due_date <= $d
            ORDER BY due_date, priority DESC, id
            """;
        command.Parameters.AddWithValue("$done", (int)TaskState.Done);
        command.Parameters.AddWithValue("$d", ToDateString(dueDate));

        return ReadAll(connection, command);
    }

    /// <inheritdoc />
    public TaskCounts CountByState(DateOnly today)
    {
        _database.Initialize(SchemaFactory);

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT state,
                   COUNT(*),
                   SUM(CASE WHEN due_date IS NOT NULL AND due_date < $today THEN 1 ELSE 0 END)
            FROM tasks
            WHERE state != $done
            GROUP BY state
            """;
        command.Parameters.AddWithValue("$today", ToDateString(today));
        command.Parameters.AddWithValue("$done", (int)TaskState.Done);

        var todo = 0;
        var doing = 0;
        var overdue = 0;
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var count = reader.GetInt64(1);
                var overdueCount = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                switch ((TaskState)reader.GetInt64(0))
                {
                    case TaskState.Todo:
                        todo = (int)count;
                        overdue += (int)overdueCount;
                        break;
                    case TaskState.Doing:
                        doing = (int)count;
                        overdue += (int)overdueCount;
                        break;
                }
            }
        }

        using var doneCommand = connection.CreateCommand();
        doneCommand.CommandText = "SELECT COUNT(*) FROM tasks WHERE state = $done";
        doneCommand.Parameters.AddWithValue("$done", (int)TaskState.Done);
        var done = (long)doneCommand.ExecuteScalar()!;

        return new TaskCounts(todo, doing, (int)done, overdue);
    }

    /// <inheritdoc />
    public void AddDependency(long taskId, long dependsOnId)
    {
        if (taskId == dependsOnId)
            throw new ArgumentException("A task cannot block itself.", nameof(dependsOnId));

        _database.Initialize(SchemaFactory);

        using var connection = _database.Open();
        if (!Exists(connection, taskId) || !Exists(connection, dependsOnId))
            throw new ArgumentException($"Both tasks must exist to link {taskId} → {dependsOnId}.");

        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO task_dependencies(task_id, depends_on) VALUES($a, $b)";
        command.Parameters.AddWithValue("$a", taskId);
        command.Parameters.AddWithValue("$b", dependsOnId);
        command.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public IReadOnlyList<TaskLink> ListDependencies()
    {
        _database.Initialize(SchemaFactory);

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT task_id, depends_on FROM task_dependencies ORDER BY task_id, depends_on";
        using var reader = command.ExecuteReader();
        List<TaskLink> links = [];
        while (reader.Read())
        {
            links.Add(new TaskLink(reader.GetInt64(0), reader.GetInt64(1)));
        }

        return links;
    }

    /// <inheritdoc />
    public void RemoveDependenciesFor(long taskId)
    {
        _database.Initialize(SchemaFactory);

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM task_dependencies WHERE task_id = $id OR depends_on = $id";
        command.Parameters.AddWithValue("$id", taskId);
        command.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetTags(long taskId)
    {
        _database.Initialize(SchemaFactory);

        using var connection = _database.Open();
        return LoadTags(connection, taskId);
    }

    /// <inheritdoc />
    public void PushUndo(UndoSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _database.Initialize(SchemaFactory);

        var payload = JsonSerializer.Serialize(ToSnapshotDto(snapshot), SnapshotJsonOptions);
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO undo_log(created_at, payload) VALUES($now, $payload)";
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$payload", payload);
            command.ExecuteNonQuery();
        }

        using (var trim = connection.CreateCommand())
        {
            trim.Transaction = transaction;
            trim.CommandText = """
                DELETE FROM undo_log WHERE id NOT IN (
                    SELECT id FROM undo_log ORDER BY id DESC LIMIT $depth)
                """;
            trim.Parameters.AddWithValue("$depth", _undoDepth);
            trim.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <inheritdoc />
    public UndoResult Undo()
    {
        _database.Initialize(SchemaFactory);

        using var connection = _database.Open();
        long undoId;
        SnapshotDto snapshot;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, payload FROM undo_log ORDER BY id DESC LIMIT 1
                """;
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                throw new InvalidOperationException("Nothing to undo.");

            undoId = reader.GetInt64(0);
            snapshot = JsonSerializer.Deserialize<SnapshotDto>(reader.GetString(1), SnapshotJsonOptions)
                ?? throw new InvalidOperationException("The undo snapshot could not be read.");
        }

        using var transaction = connection.BeginTransaction();
        foreach (var createdId in snapshot.CreatedTaskIds)
        {
            DeleteTask(connection, transaction, createdId);
        }

        if (snapshot.ReplaceAll)
        {
            using var wipe = connection.CreateCommand();
            wipe.Transaction = transaction;
            wipe.CommandText = "DELETE FROM tasks";
            wipe.ExecuteNonQuery();
        }

        var touchedIds = new HashSet<long>();
        foreach (var dto in snapshot.Tasks)
        {
            var task = Backup.FromDto(dto);
            touchedIds.Add(task.Id);
            DeleteTask(connection, transaction, task.Id);
            InsertTask(connection, task, dto.Tags);
        }

        foreach (var createdId in snapshot.CreatedTaskIds)
        {
            touchedIds.Add(createdId);
        }

        foreach (var touchedId in touchedIds)
        {
            using var clearLinks = connection.CreateCommand();
            clearLinks.Transaction = transaction;
            clearLinks.CommandText = "DELETE FROM task_dependencies WHERE task_id = $id OR depends_on = $id";
            clearLinks.Parameters.AddWithValue("$id", touchedId);
            clearLinks.ExecuteNonQuery();
        }

        foreach (var dependency in snapshot.Dependencies)
        {
            using var link = connection.CreateCommand();
            link.Transaction = transaction;
            link.CommandText = "INSERT OR IGNORE INTO task_dependencies(task_id, depends_on) VALUES($a, $b)";
            link.Parameters.AddWithValue("$a", dependency.TaskId);
            link.Parameters.AddWithValue("$b", dependency.DependsOnId);
            link.ExecuteNonQuery();
        }

        using (var deleteUndo = connection.CreateCommand())
        {
            deleteUndo.Transaction = transaction;
            deleteUndo.CommandText = "DELETE FROM undo_log WHERE id = $id";
            deleteUndo.Parameters.AddWithValue("$id", undoId);
            deleteUndo.ExecuteNonQuery();
        }

        transaction.Commit();
        return new UndoResult(snapshot.Operation, snapshot.Tasks.Count + snapshot.CreatedTaskIds.Count);
    }

    /// <summary>Builds the schema/migration script based on the current <c>PRAGMA user_version</c>.</summary>
    private string SchemaFactory(SqliteConnection connection)
    {
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        var current = Convert.ToInt64(version.ExecuteScalar()!, CultureInfo.InvariantCulture);

        if (current > CurrentSchemaVersion)
            throw new InvalidOperationException($"The database schema (v{current}) is newer than this build supports (v{CurrentSchemaVersion}).");

        var script = CreateTasksTableSql + "\n" + CreateAuxTablesSql;
        if (current == 1)
        {
            // Version 1 lacked tags/links/undo and the new task columns — ALTER them in.
            script += """
                ALTER TABLE tasks ADD COLUMN project TEXT NOT NULL DEFAULT '';
                ALTER TABLE tasks ADD COLUMN effort INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE tasks ADD COLUMN recurrence_kind INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE tasks ADD COLUMN recurrence_interval INTEGER NOT NULL DEFAULT 1;
                ALTER TABLE tasks ADD COLUMN started_at TEXT;
                ALTER TABLE tasks ADD COLUMN uid TEXT;
                """;
        }
        else if (current == 2)
        {
            // Version 2 predates sync identities — add the uid column.
            script += "ALTER TABLE tasks ADD COLUMN uid TEXT;\n";
        }

        if (current is >= 1 and < CurrentSchemaVersion)
        {
            // Backfill sync identities for rows that predate them, then enforce uniqueness.
            script += """
                UPDATE tasks SET uid = lower(hex(randomblob(16))) WHERE uid IS NULL OR uid = '';
                CREATE UNIQUE INDEX IF NOT EXISTS ix_tasks_uid ON tasks(uid);
                """;
        }

        script += $"PRAGMA user_version = {CurrentSchemaVersion};";
        return script;
    }

    private static void InsertTask(SqliteConnection connection, HaftKhanTask task, IReadOnlyList<string> tags)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tasks(id, title, notes, priority, state, due_date, created_at, updated_at, completed_at,
                              project, effort, recurrence_kind, recurrence_interval, started_at, uid)
            VALUES($id, $title, $notes, $priority, $state, $due, $createdAt, $updatedAt, $completedAt,
                   $project, $effort, $recKind, $recInterval, $startedAt, $uid)
            """;
        command.Parameters.AddWithValue("$id", task.Id);
        command.Parameters.AddWithValue("$title", task.Title);
        command.Parameters.AddWithValue("$notes", task.Notes);
        command.Parameters.AddWithValue("$priority", (int)task.Priority);
        command.Parameters.AddWithValue("$state", (int)task.State);
        AddOptionalDate(command, "$due", task.DueDate);
        command.Parameters.AddWithValue("$createdAt", task.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updatedAt", task.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        AddOptionalInstant(command, "$completedAt", task.CompletedAt);
        command.Parameters.AddWithValue("$project", task.Project);
        command.Parameters.AddWithValue("$effort", (int)task.Effort);
        command.Parameters.AddWithValue("$recKind", (int)task.Recurrence);
        command.Parameters.AddWithValue("$recInterval", task.RecurrenceInterval);
        AddOptionalInstant(command, "$startedAt", task.StartedAt);
        command.Parameters.AddWithValue("$uid", string.IsNullOrEmpty(task.Uid) ? Backup.NewUid() : task.Uid);
        command.ExecuteNonQuery();

        InsertTags(connection, null, task.Id, tags);
    }

    private static void DeleteTask(SqliteConnection connection, SqliteTransaction? transaction, long id)
    {
        using var tags = connection.CreateCommand();
        tags.Transaction = transaction;
        tags.CommandText = "DELETE FROM task_tags WHERE task_id = $id";
        tags.Parameters.AddWithValue("$id", id);
        tags.ExecuteNonQuery();

        using var links = connection.CreateCommand();
        links.Transaction = transaction;
        links.CommandText = "DELETE FROM task_dependencies WHERE task_id = $id OR depends_on = $id";
        links.Parameters.AddWithValue("$id", id);
        links.ExecuteNonQuery();

        using var row = connection.CreateCommand();
        row.Transaction = transaction;
        row.CommandText = "DELETE FROM tasks WHERE id = $id";
        row.Parameters.AddWithValue("$id", id);
        row.ExecuteNonQuery();
    }

    private static void InsertTags(
        SqliteConnection connection, SqliteTransaction? transaction, long taskId, IReadOnlyList<string> tags)
    {
        foreach (var tag in tags)
        {
            using var command = connection.CreateCommand();
            if (transaction is not null)
                command.Transaction = transaction;

            command.CommandText = "INSERT OR IGNORE INTO task_tags(task_id, tag) VALUES($id, $tag)";
            command.Parameters.AddWithValue("$id", taskId);
            command.Parameters.AddWithValue("$tag", tag);
            command.ExecuteNonQuery();
        }
    }

    private static void DeleteTags(SqliteConnection connection, SqliteTransaction? transaction, long taskId)
    {
        using var command = connection.CreateCommand();
        if (transaction is not null)
            command.Transaction = transaction;

        command.CommandText = "DELETE FROM task_tags WHERE task_id = $id";
        command.Parameters.AddWithValue("$id", taskId);
        command.ExecuteNonQuery();
    }

    private static List<string> LoadTags(SqliteConnection connection, long taskId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT tag FROM task_tags WHERE task_id = $id ORDER BY tag";
        command.Parameters.AddWithValue("$id", taskId);
        using var reader = command.ExecuteReader();
        List<string> tags = [];
        while (reader.Read())
        {
            tags.Add(reader.GetString(0));
        }

        return tags;
    }

    private static bool Exists(SqliteConnection connection, long id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM tasks WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return (long)command.ExecuteScalar()! > 0;
    }

    private static List<HaftKhanTask> ReadAll(SqliteConnection connection, SqliteCommand command)
    {
        List<HaftKhanTask> tasks = [];
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                tasks.Add(MapRow(reader));
            }
        }

        if (tasks.Count == 0)
            return tasks;

        // Records are immutable — rebuild with their tags attached.
        var withTags = new List<HaftKhanTask>(tasks.Count);
        foreach (var task in tasks)
        {
            withTags.Add(task with { Tags = LoadTags(connection, task.Id) });
        }

        return withTags;
    }

    private static HaftKhanTask MapRow(SqliteDataReader reader)
    {
        var createdAt = DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var updatedAt = DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var completedAt = reader.IsDBNull(8)
            ? (DateTimeOffset?)null
            : DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var startedAt = reader.IsDBNull(13)
            ? (DateTimeOffset?)null
            : DateTimeOffset.Parse(reader.GetString(13), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        return new HaftKhanTask(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            (TaskPriority)reader.GetInt64(3),
            (TaskState)reader.GetInt64(4),
            reader.IsDBNull(5) ? (DateOnly?)null : DateOnly.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
            createdAt,
            updatedAt,
            completedAt)
        {
            Project = reader.GetString(9),
            Tags = [],
            Effort = (TaskEffort)reader.GetInt64(10),
            Recurrence = (RecurrenceKind)reader.GetInt64(11),
            RecurrenceInterval = (int)reader.GetInt64(12),
            StartedAt = startedAt,
            Uid = reader.IsDBNull(14) ? string.Empty : reader.GetString(14),
        };
    }

    private static string ToDateString(DateOnly date) => date.ToString("O", CultureInfo.InvariantCulture)[..10];

    private static void AddOptionalDate(SqliteCommand command, string parameterName, DateOnly? value)
    {
        command.Parameters.AddWithValue(
            parameterName, value.HasValue ? ToDateString(value.Value) : DBNull.Value);
    }

    private static void AddOptionalInstant(SqliteCommand command, string parameterName, DateTimeOffset? value)
    {
        command.Parameters.AddWithValue(
            parameterName, value.HasValue ? value.Value.ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
    }

    private static void ValidateEnum<T>(T value, string paramName)
        where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
            throw new ArgumentOutOfRangeException(paramName, value, "Value is not a defined enum member.");
    }

    /// <inheritdoc />
    public HaftKhanTask? FindByUid(string uid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uid);
        _database.Initialize(SchemaFactory);

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM tasks WHERE uid = $uid";
        command.Parameters.AddWithValue("$uid", uid);
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapRow(reader) with { Tags = LoadTags(connection, reader.GetInt64(0)) } : null;
    }

    /// <inheritdoc />
    public HaftKhanTask Upsert(HaftKhanTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        ValidateEnum(task.Priority, nameof(task.Priority));
        ValidateEnum(task.State, nameof(task.State));
        ValidateEnum(task.Effort, nameof(task.Effort));
        ValidateEnum(task.Recurrence, nameof(task.Recurrence));
        _database.Initialize(SchemaFactory);

        var uid = string.IsNullOrEmpty(task.Uid) ? Backup.NewUid() : task.Uid;
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        var existing = FindByUidWithin(connection, uid);
        if (existing is null)
        {
            using var identity = connection.CreateCommand();
            identity.Transaction = transaction;
            identity.CommandText = "SELECT COALESCE(MAX(id), 0) + 1 FROM tasks";
            var newId = (long)identity.ExecuteScalar()!;
            InsertTaskWithId(connection, transaction, task with { Id = newId, Uid = uid }, task.Tags);
            transaction.Commit();
            return task with { Id = newId, Uid = uid };
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE tasks
                SET title = $title, notes = $notes, priority = $priority, state = $state,
                    due_date = $due, created_at = $createdAt, updated_at = $updatedAt, completed_at = $completedAt,
                    project = $project, effort = $effort, recurrence_kind = $recKind,
                    recurrence_interval = $recInterval, started_at = $startedAt
                WHERE uid = $uid
                """;
            FillTaskUpdate(command, task);
            command.Parameters.AddWithValue("$uid", uid);
            command.ExecuteNonQuery();
        }

        DeleteTags(connection, transaction, existing.Id);
        InsertTags(connection, transaction, existing.Id, task.Tags);
        transaction.Commit();
        return task with { Id = existing.Id, Uid = uid };
    }

    /// <inheritdoc />
    public void SetDependencies(IReadOnlyList<TaskLink> links)
    {
        ArgumentNullException.ThrowIfNull(links);
        _database.Initialize(SchemaFactory);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM task_dependencies";
            clear.ExecuteNonQuery();
        }

        foreach (var link in links.Distinct())
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT OR IGNORE INTO task_dependencies(task_id, depends_on) VALUES($a, $b)";
            command.Parameters.AddWithValue("$a", link.TaskId);
            command.Parameters.AddWithValue("$b", link.DependsOnId);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static void FillTaskUpdate(SqliteCommand command, HaftKhanTask task)
    {
        command.Parameters.AddWithValue("$title", task.Title);
        command.Parameters.AddWithValue("$notes", task.Notes);
        command.Parameters.AddWithValue("$priority", (int)task.Priority);
        command.Parameters.AddWithValue("$state", (int)task.State);
        AddOptionalDate(command, "$due", task.DueDate);
        command.Parameters.AddWithValue("$createdAt", task.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updatedAt", task.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        AddOptionalInstant(command, "$completedAt", task.CompletedAt);
        command.Parameters.AddWithValue("$project", task.Project);
        command.Parameters.AddWithValue("$effort", (int)task.Effort);
        command.Parameters.AddWithValue("$recKind", (int)task.Recurrence);
        command.Parameters.AddWithValue("$recInterval", task.RecurrenceInterval);
        AddOptionalInstant(command, "$startedAt", task.StartedAt);
    }

    private static HaftKhanTask? FindByUidWithin(SqliteConnection connection, string uid)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM tasks WHERE uid = $uid";
        command.Parameters.AddWithValue("$uid", uid);
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapRow(reader) : null;
    }

    private static void InsertTaskWithId(
        SqliteConnection connection, SqliteTransaction transaction, HaftKhanTask task, IReadOnlyList<string> tags)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO tasks(id, title, notes, priority, state, due_date, created_at, updated_at, completed_at,
                              project, effort, recurrence_kind, recurrence_interval, started_at, uid)
            VALUES($id, $title, $notes, $priority, $state, $due, $createdAt, $updatedAt, $completedAt,
                   $project, $effort, $recKind, $recInterval, $startedAt, $uid)
            """;
        command.Parameters.AddWithValue("$id", task.Id);
        FillTaskUpdate(command, task);
        command.Parameters.AddWithValue("$uid", task.Uid);
        command.ExecuteNonQuery();

        InsertTags(connection, transaction, task.Id, tags);
    }

    private static SnapshotDto ToSnapshotDto(UndoSnapshot snapshot) => new(
        snapshot.Operation,
        [.. snapshot.Tasks.Select(Backup.ToDto)],
        snapshot.Tags.ToDictionary(pair => pair.Key, pair => pair.Value.ToList()),
        [.. snapshot.Dependencies.Select(link => new Backup.DependencyDto(link.TaskId, link.DependsOnId))],
        [.. snapshot.CreatedTaskIds],
        snapshot.ReplaceAll);

    private sealed record SnapshotDto(
        string Operation,
        List<Backup.TaskDto> Tasks,
        Dictionary<long, List<string>> Tags,
        List<Backup.DependencyDto> Dependencies,
        List<long> CreatedTaskIds,
        bool ReplaceAll);
}
