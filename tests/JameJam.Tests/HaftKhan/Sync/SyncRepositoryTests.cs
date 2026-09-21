using JameJam.HaftKhan;

namespace JameJam.Tests.HaftKhan.Sync;

/// <summary>Tests for the sync surface of both repositories (uid upserts, dependency replacement).</summary>
public sealed class SyncRepositoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"jamejam-sync-tests-{Guid.NewGuid():N}.db");
    private readonly MemoryTaskRepository _memory = new();

    public void Dispose()
    {
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { File.Delete(path); } catch (FileNotFoundException) { /* already gone */ }
        }
    }

    [Fact]
    public void Upsert_InsertsThenUpdatesByUid_KeepingStableIds()
    {
        var task = _memory.Upsert(Make("uid-a", "first"));
        var reloaded = _memory.FindByUid("uid-a")!;
        Assert.Equal(task.Id, reloaded.Id);

        var again = _memory.Upsert(Make("uid-a", "renamed"));

        Assert.Equal(task.Id, again.Id);
        Assert.Equal("renamed", _memory.Find(again.Id)!.Title);
        Assert.Single(_memory.ListAll());
    }

    [Fact]
    public void Upsert_GeneratesUidWhenMissing() =>
        Assert.False(string.IsNullOrEmpty(_memory.Upsert(Make(string.Empty, "no-uid")).Uid));

    [Fact]
    public void SetDependencies_ReplacesEverything()
    {
        var a = _memory.Add(new NewTask("a", string.Empty, TaskPriority.Normal, null));
        var b = _memory.Add(new NewTask("b", string.Empty, TaskPriority.Normal, null));
        var c = _memory.Add(new NewTask("c", string.Empty, TaskPriority.Normal, null));
        _memory.AddDependency(b.Id, a.Id);

        _memory.SetDependencies([new TaskLink(c.Id, a.Id)]);

        var links = _memory.ListDependencies();
        Assert.Equal([new TaskLink(c.Id, a.Id)], links);
    }

    [Fact]
    public async Task Sqlite_UpsertAndFindByUid_WorkAcrossConnections()
    {
        var repo = new SqliteTaskRepository(_dbPath);
        var inserted = repo.Upsert(Make("uid-a", "synced"));

        var reopened = new SqliteTaskRepository(_dbPath);
        var found = reopened.FindByUid("uid-a")!;
        Assert.Equal(inserted.Id, found.Id);
        Assert.Equal("synced", found.Title);

        var updated = reopened.Upsert(Make("uid-a", "renamed"));
        Assert.Equal(inserted.Id, updated.Id);
        Assert.Equal("renamed", reopened.Find(inserted.Id)!.Title);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Sqlite_MigratesV2Database_AddingBackfilledUids()
    {
        // Simulate a v2 database (pre-sync): tasks table without the uid column.
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate }.ToString());
        await connection.OpenAsync();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE tasks (
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
                    started_at          TEXT
                );
                INSERT INTO tasks(title, notes, priority, state, due_date, created_at, updated_at, completed_at)
                VALUES('legacy', '', 1, 0, NULL, '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00', NULL);
                PRAGMA user_version = 2;
                """;
            create.ExecuteNonQuery();
        }

        await connection.CloseAsync();

        // Opening through the repository migrates to v3 and backfills sync identities.
        var repo = new SqliteTaskRepository(_dbPath);
        var legacy = repo.ListAll().Single();
        Assert.Equal("legacy", legacy.Title);
        Assert.False(string.IsNullOrEmpty(legacy.Uid), "Migration must backfill stable uids.");

        // The uid is unique and usable for sync.
        Assert.NotNull(repo.FindByUid(legacy.Uid));
    }

    private static HaftKhanTask Make(string uid, string title)
    {
        var now = DateTimeOffset.UtcNow;
        return new HaftKhanTask(0, title, string.Empty, TaskPriority.Normal, TaskState.Todo, null, now, now, null)
        {
            Uid = uid,
        };
    }
}
