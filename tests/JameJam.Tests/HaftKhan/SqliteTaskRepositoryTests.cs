using JameJam.HaftKhan;

namespace JameJam.Tests.HaftKhan;

/// <summary>Tests for the SQLite task repository (real database in a temp file, parameterized + safe).</summary>
public sealed class SqliteTaskRepositoryTests : IDisposable
{
    private readonly TempDatabase _database = new();
    private readonly SqliteTaskRepository _repository;

    public SqliteTaskRepositoryTests() => _repository = new SqliteTaskRepository(_database.DbPath);

    public void Dispose() => _database.Dispose();

    [Fact]
    public void Add_AssignsSequentialIds_AndStoresFields()
    {
        var first = _repository.Add(new NewTask("first", "notes", TaskPriority.High, new DateOnly(2026, 9, 25)));
        var second = _repository.Add(new NewTask("second", string.Empty, TaskPriority.Low, null));

        Assert.Equal(1, first.Id);
        Assert.Equal(2, second.Id);

        var stored = _repository.Find(first.Id)!;
        Assert.Equal("first", stored.Title);
        Assert.Equal("notes", stored.Notes);
        Assert.Equal(TaskPriority.High, stored.Priority);
        Assert.Equal(TaskState.Todo, stored.State);
        Assert.Equal(new DateOnly(2026, 9, 25), stored.DueDate);
        Assert.Null(stored.CompletedAt);
        Assert.True(stored.CreatedAt <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Find_Missing_ReturnsNull() =>
        Assert.Null(_repository.Find(123));

    [Fact]
    public void Update_PersistsEveryField()
    {
        var added = _repository.Add(new NewTask("before", string.Empty, TaskPriority.Normal, null));
        var completedAt = DateTimeOffset.UtcNow;
        var updated = added with
        {
            Title = "after",
            Notes = "now with notes",
            Priority = TaskPriority.Critical,
            State = TaskState.Done,
            DueDate = new DateOnly(2026, 12, 1),
            UpdatedAt = completedAt,
            CompletedAt = completedAt,
        };

        _repository.Update(updated);
        var stored = _repository.Find(added.Id)!;

        Assert.Equal("after", stored.Title);
        Assert.Equal("now with notes", stored.Notes);
        Assert.Equal(TaskPriority.Critical, stored.Priority);
        Assert.Equal(TaskState.Done, stored.State);
        Assert.Equal(new DateOnly(2026, 12, 1), stored.DueDate);
        Assert.Equal(completedAt, stored.CompletedAt);
    }

    [Fact]
    public void Update_MissingTask_ThrowsNotFound() =>
        Assert.Throws<TaskNotFoundException>(() => _repository.Update(MakeTask(id: 555)));

    [Fact]
    public void Remove_ReportsExistence()
    {
        var task = _repository.Add(new NewTask("t", string.Empty, TaskPriority.Normal, null));

        Assert.True(_repository.Remove(task.Id));
        Assert.False(_repository.Remove(task.Id));
        Assert.Null(_repository.Find(task.Id));
    }

    [Fact]
    public void RemoveCompleted_RemovesOnlyDoneTasks()
    {
        var open = _repository.Add(new NewTask("open", string.Empty, TaskPriority.Normal, null));
        var done = _repository.Add(new NewTask("done", string.Empty, TaskPriority.Normal, null));
        _repository.Update(done with { State = TaskState.Done, CompletedAt = DateTimeOffset.UtcNow });

        Assert.Equal(1, _repository.RemoveCompleted());
        Assert.NotNull(_repository.Find(open.Id));
        Assert.Null(_repository.Find(done.Id));
    }

    [Fact]
    public void ListOpen_SortsByPriorityThenDue()
    {
        var low = _repository.Add(new NewTask("low", string.Empty, TaskPriority.Low, null));
        var criticalLater = _repository.Add(new NewTask("crit-later", string.Empty, TaskPriority.Critical, new DateOnly(2026, 10, 1)));
        var criticalSooner = _repository.Add(new NewTask("crit-sooner", string.Empty, TaskPriority.Critical, new DateOnly(2026, 9, 20)));

        var open = _repository.ListOpen();

        Assert.Equal([criticalSooner.Id, criticalLater.Id, low.Id], open.Select(task => task.Id));
    }

    [Fact]
    public void ListOpen_ExcludesDone()
    {
        var open = _repository.Add(new NewTask("open", string.Empty, TaskPriority.Normal, null));
        var done = _repository.Add(new NewTask("done", string.Empty, TaskPriority.Normal, null));
        _repository.Update(done with { State = TaskState.Done, CompletedAt = DateTimeOffset.UtcNow });

        Assert.Equal([open.Id], _repository.ListOpen().Select(task => task.Id));
    }

    [Fact]
    public void ListDueOnOrBefore_RangeQuery_IsSortedAndFiltered()
    {
        var overdue = _repository.Add(new NewTask("overdue", string.Empty, TaskPriority.Normal, new DateOnly(2026, 9, 1)));
        var boundary = _repository.Add(new NewTask("boundary", string.Empty, TaskPriority.High, new DateOnly(2026, 9, 19)));
        _ = _repository.Add(new NewTask("future", string.Empty, TaskPriority.Critical, new DateOnly(2026, 9, 30)));
        var donePast = _repository.Add(new NewTask("done-past", string.Empty, TaskPriority.Normal, new DateOnly(2026, 8, 1)));
        _repository.Update(donePast with { State = TaskState.Done, CompletedAt = DateTimeOffset.UtcNow });

        var due = _repository.ListDueOnOrBefore(new DateOnly(2026, 9, 19));

        Assert.Equal([overdue.Id, boundary.Id], due.Select(task => task.Id));
    }

    [Fact]
    public void CountByState_GroupsStates_CountsOverdue()
    {
        _ = _repository.Add(new NewTask("todo", string.Empty, TaskPriority.Normal, new DateOnly(2026, 8, 1)));   // overdue
        var doing = _repository.Add(new NewTask("doing", string.Empty, TaskPriority.Normal, new DateOnly(2026, 8, 2))); // overdue
        _repository.Update(doing with { State = TaskState.Doing, UpdatedAt = DateTimeOffset.UtcNow });
        _ = _repository.Add(new NewTask("open", string.Empty, TaskPriority.Normal, new DateOnly(2026, 12, 1)));  // not overdue
        var done = _repository.Add(new NewTask("done", string.Empty, TaskPriority.Normal, new DateOnly(2026, 8, 3)));
        _repository.Update(done with { State = TaskState.Done, CompletedAt = DateTimeOffset.UtcNow });

        var counts = _repository.CountByState(new DateOnly(2026, 9, 19));

        Assert.Equal((2, 1, 1, 2), (counts.Todo, counts.Doing, counts.Done, counts.Overdue));
        Assert.Equal(4, counts.Total);
    }

    [Fact]
    public void Data_PersistsAcrossRepositoryInstances()
    {
        var task = _repository.Add(new NewTask("durable", string.Empty, TaskPriority.High, null));

        var reopened = new SqliteTaskRepository(_database.DbPath);

        Assert.Equal("durable", reopened.Find(task.Id)!.Title);
    }

    [Fact]
    public void InvalidEnumValues_AreRejected_BeforeReachingSql()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => _repository.Add(new NewTask("t", string.Empty, (TaskPriority)99, null)));

        var task = _repository.Add(new NewTask("t", string.Empty, TaskPriority.Normal, null));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => _repository.Update(task with { State = (TaskState)42 }));
    }

    [Fact]
    public void DatabaseFile_IsOwnerOnly_OnUnix()
    {
        _repository.Add(new NewTask("touch", string.Empty, TaskPriority.Normal, null)); // forces file creation

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return; // Unix-only hardening check

        var forbidden = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

        Assert.True(
            (File.GetUnixFileMode(Path.GetFullPath(_database.DbPath)) & forbidden) == 0,
            "The task database must be readable/writable by the owner only.");
    }

    private static HaftKhanTask MakeTask(long id)
    {
        var now = DateTimeOffset.UtcNow;
        return new HaftKhanTask(id, "t", string.Empty, TaskPriority.Normal, TaskState.Todo, null, now, now, null);
    }

    [Fact]
    public void Tags_Dependencies_AndUndo_PersistAcrossInstances()
    {
        var first = _repository.Add(new NewTask("tagged", string.Empty, TaskPriority.High, null)
        {
            Tags = ["one", "two"],
            Project = "home",
            Effort = TaskEffort.Medium,
            Recurrence = RecurrenceKind.Weekly,
            RecurrenceInterval = 3,
        });
        var second = _repository.Add(new NewTask("blocked", string.Empty, TaskPriority.Normal, null));
        _repository.AddDependency(second.Id, first.Id);

        var reopened = new SqliteTaskRepository(_database.DbPath);
        var stored = reopened.Find(first.Id)!;
        Assert.Equal(["one", "two"], stored.Tags);
        Assert.Equal("home", stored.Project);
        Assert.Equal(TaskEffort.Medium, stored.Effort);
        Assert.Equal(RecurrenceKind.Weekly, stored.Recurrence);
        Assert.Equal(3, stored.RecurrenceInterval);
        Assert.Contains(new TaskLink(second.Id, first.Id), reopened.ListDependencies());
        Assert.Equal(["one", "two"], reopened.GetTags(first.Id));

        // Undo persists too: push a removal snapshot, reopen, undo → task restored.
        var snapshotTasks = new List<HaftKhanTask> { stored };
        reopened.PushUndo(new UndoSnapshot(
            "remove", snapshotTasks,
            new Dictionary<long, IReadOnlyList<string>> { [first.Id] = ["one", "two"] },
            [new TaskLink(second.Id, first.Id)], [], ReplaceAll: false));
        Assert.True(reopened.Remove(first.Id));

        var reuser = new SqliteTaskRepository(_database.DbPath);
        var result = reuser.Undo();
        Assert.Equal("remove", result.Operation);
        Assert.NotNull(reuser.Find(first.Id));
        Assert.Equal(["one", "two"], reuser.GetTags(first.Id));
        Assert.Contains(new TaskLink(second.Id, first.Id), reuser.ListDependencies());
    }

    [Fact]
    public void RecurrenceFields_RoundTrip()
    {
        var added = _repository.Add(new NewTask("daily", string.Empty, TaskPriority.Normal, null)
        {
            Recurrence = RecurrenceKind.Daily,
            RecurrenceInterval = 4,
        });
        var completedAt = DateTimeOffset.UtcNow;
        _repository.Update(added with
        {
            State = TaskState.Doing,
            StartedAt = completedAt,
            UpdatedAt = completedAt,
        });

        var stored = _repository.Find(added.Id)!;
        Assert.Equal(RecurrenceKind.Daily, stored.Recurrence);
        Assert.Equal(4, stored.RecurrenceInterval);
        Assert.NotNull(stored.StartedAt);
    }

    [Fact]
    public void AddDependency_ValidatesExistenceAndSelf()
    {
        var task = _repository.Add(new NewTask("t", string.Empty, TaskPriority.Normal, null));

        Assert.Throws<ArgumentException>(() => _repository.AddDependency(task.Id, 999));
        Assert.Throws<ArgumentException>(() => _repository.AddDependency(999, task.Id));
        Assert.Throws<ArgumentException>(() => _repository.AddDependency(task.Id, task.Id));
    }

    [Fact]
    public void Undo_EmptyHistory_Throws() =>
        Assert.Throws<InvalidOperationException>(() => _repository.Undo());

    /// <summary>A unique temp SQLite file that cleans up after itself (including WAL sidecars).</summary>
    private sealed class TempDatabase : IDisposable
    {
        public string DbPath { get; } = Path.Combine(
            Path.GetTempPath(), $"jamejam-haftkhan-tests-{Guid.NewGuid():N}.db");

        public void Dispose()
        {
            foreach (var path in new[] { DbPath, DbPath + "-wal", DbPath + "-shm" })
            {
                try
                {
                    File.Delete(path);
                }
                catch (FileNotFoundException)
                {
                    // Already gone — nothing to clean up.
                }
            }
        }
    }
}
