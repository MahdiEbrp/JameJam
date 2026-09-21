using System.Globalization;
using JameJam.HaftKhan;

namespace JameJam.Tests.HaftKhan;

/// <summary>
/// Feature tests for the advanced engine: tags, projects, recurrence, dependencies, undo,
/// search, filters, focus, board, matrix, streaks, and import/export.
/// </summary>
public sealed class AdvancedHaftKhanServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 10, 0, 0, TimeSpan.Zero); // a Saturday

    private readonly MemoryTaskRepository _repository = new();
    private readonly HaftKhanService _service;

    public AdvancedHaftKhanServiceTests() =>
        _service = new HaftKhanService(_repository, new FixedTimeProvider(Now));

    // ── Tags & projects ──

    [Fact]
    public void AddTask_StoresProjectTagsAndEffort()
    {
        var task = _service.AddTask("Fix the stable", null, "high", null, "home", "chores,quick", "m");

        Assert.Equal("home", task.Project);
        Assert.Equal(["chores", "quick"], task.Tags);
        Assert.Equal(TaskEffort.Medium, task.Effort);
        Assert.Equal(["chores", "quick"], _repository.GetTags(task.Id));
    }

    [Fact]
    public void ListFiltered_ByTagProjectAndPriority()
    {
        _ = _service.AddTask("a", null, "high", null, "home", "chores");
        _ = _service.AddTask("b", null, "low", null, "work", "email");
        _ = _service.AddTask("c", null, "critical", null, "home", "email");

        Assert.Equal(["a"], _service.ListFiltered(TaskView.Open, "chores", null, null).Select(task => task.Title));
        Assert.Equal(["c", "a"], _service.ListFiltered(TaskView.Open, null, "HOME", null).Select(task => task.Title)); // priority order
        Assert.Equal(["c"], _service.ListFiltered(TaskView.Open, "email", null, "critical").Select(task => task.Title));
        Assert.Equal(["c", "a", "b"], _service.ListFiltered(TaskView.Open, null, null, "low").Select(task => task.Title));
    }

    [Fact]
    public void Search_FindsTitlesNotesProjectsAndTags()
    {
        _ = _service.AddTask("Slay dragon", "needs a sword", null, null, "myth", "hero");
        _ = _service.AddTask("Buy bread", null);

        Assert.Equal(["Slay dragon"], _service.Search("dragon").Select(task => task.Title));
        Assert.Equal(["Slay dragon"], _service.Search("sword").Select(task => task.Title));
        Assert.Equal(["Slay dragon"], _service.Search("myth").Select(task => task.Title));
        Assert.Equal(["Slay dragon"], _service.Search("HERO").Select(task => task.Title));
        Assert.Empty(_service.Search("dragonfly"));
        Assert.Throws<ArgumentException>(() => _service.Search("   "));
    }

    // ── Recurrence ──

    [Fact]
    public void Complete_RecurringTask_SpawnsNextOccurrence()
    {
        var task = _service.AddTask(
            "Water the horse", notes: null, priorityName: "high", dueDateText: "2026-09-19",
            project: "stable", tagsCsv: "chor", recurrenceName: "daily");

        var result = _service.Complete(task.Id.ToString(CultureInfo.InvariantCulture));

        Assert.Equal(TaskState.Done, result.Completed.State);
        var next = result.Next!;
        Assert.NotEqual(task.Id, next.Id);
        Assert.Equal(TaskState.Todo, next.State);
        Assert.Equal(new DateOnly(2026, 9, 20), next.DueDate);
        Assert.Equal("high", next.Priority.ToString().ToLowerInvariant());
        Assert.Equal("stable", next.Project);
        Assert.Equal(["chor"], next.Tags);
        Assert.Equal(RecurrenceKind.Daily, next.Recurrence);
    }

    [Fact]
    public void Complete_OneOffTask_DoesNotSpawn()
    {
        var task = _service.AddTask("one-off");

        Assert.Null(_service.Complete(task.Id.ToString(CultureInfo.InvariantCulture)).Next);
    }

    // ── Dependencies ──

    [Fact]
    public void AddTask_WithBlockers_CreatesLinks()
    {
        var first = _service.AddTask("first");
        var second = _service.AddTask("second", blockedBy: [first.Id]);

        Assert.Contains(new TaskLink(second.Id, first.Id), _repository.ListDependencies());
    }

    [Fact]
    public void AddTask_WithUnknownBlocker_Throws() =>
        Assert.Throws<ArgumentException>(() => _service.AddTask("t", blockedBy: [999]));

    [Fact]
    public void Complete_BlockedTask_Throws_ForceWins()
    {
        var first = _service.AddTask("first");
        var second = _service.AddTask("second", blockedBy: [first.Id]);

        var blocked = Assert.Throws<InvalidOperationException>(
            () => _service.Complete(second.Id.ToString(CultureInfo.InvariantCulture)));
        Assert.Contains($"blocked by open task(s): #{first.Id}", blocked.Message, StringComparison.Ordinal);

        var result = _service.Complete(second.Id.ToString(CultureInfo.InvariantCulture), force: true);
        Assert.Equal(TaskState.Done, result.Completed.State);
    }

    [Fact]
    public void Complete_UnblockedAfterBlockerIsDone()
    {
        var first = _service.AddTask("first");
        var second = _service.AddTask("second", blockedBy: [first.Id]);
        _ = _service.Complete(first.Id.ToString(CultureInfo.InvariantCulture));

        Assert.Null(Record.Exception(() => _service.Complete(second.Id.ToString(CultureInfo.InvariantCulture))));
    }

    [Fact]
    public void AddLink_RejectsUnknown_Self_AndCycles()
    {
        var a = _service.AddTask("a");
        var b = _service.AddTask("b");
        _service.AddLink(b.Id.ToString(CultureInfo.InvariantCulture), a.Id.ToString(CultureInfo.InvariantCulture));

        Assert.Throws<TaskNotFoundException>(() => _service.AddLink("999", "1"));
        Assert.Throws<ArgumentException>(() => _service.AddLink(a.Id.ToString(CultureInfo.InvariantCulture), a.Id.ToString(CultureInfo.InvariantCulture)));
        Assert.Throws<InvalidOperationException>(() => _service.AddLink(a.Id.ToString(CultureInfo.InvariantCulture), b.Id.ToString(CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void RemoveLinks_ClearsBothDirections()
    {
        var a = _service.AddTask("a");
        var b = _service.AddTask("b");
        _service.AddLink(b.Id.ToString(CultureInfo.InvariantCulture), a.Id.ToString(CultureInfo.InvariantCulture));

        _service.RemoveLinks(a.Id.ToString(CultureInfo.InvariantCulture));

        Assert.Empty(_repository.ListDependencies());
    }

    [Fact]
    public void NextFocus_SkipsBlockedTasks()
    {
        var first = _service.AddTask("first", priorityName: "low");
        _ = _service.AddTask("second", blockedBy: [first.Id]);
        _ = _service.AddTask("third", priorityName: "high");

        // "second" is blocked; the first unblocked task in priority order is "third".
        Assert.Equal("third", _service.NextFocus()!.Title);

        _ = _service.Complete(first.Id.ToString(CultureInfo.InvariantCulture));
        Assert.Equal("third", _service.NextFocus()!.Title); // "third" still outranks "second"

        _ = _service.Complete("3");
        Assert.Equal("second", _service.NextFocus()!.Title); // unblocked once its blocker is done
    }

    [Fact]
    public void Remove_CleansUpLinks()
    {
        var a = _service.AddTask("a");
        var b = _service.AddTask("b");
        _service.AddLink(b.Id.ToString(CultureInfo.InvariantCulture), a.Id.ToString(CultureInfo.InvariantCulture));

        _service.Remove(a.Id.ToString(CultureInfo.InvariantCulture));

        Assert.Empty(_repository.ListDependencies());
    }

    // ── Undo ──

    [Fact]
    public void Undo_RevertsAdd_Remove_Start_Done_AndClearDone()
    {
        var task = _service.AddTask("labour", notes: "n", priorityName: "high");

        Assert.Equal("add", UndoAndOp());
        Assert.Null(_repository.Find(task.Id)); // add reverted

        var reloaded = _service.AddTask("labour2");
        _ = _service.Start(reloaded.Id.ToString(CultureInfo.InvariantCulture));
        _ = _service.Undo();
        Assert.Equal(TaskState.Todo, _repository.Find(reloaded.Id)!.State);

        _ = _service.Complete(reloaded.Id.ToString(CultureInfo.InvariantCulture));
        _ = _service.Undo();
        var restored = _repository.Find(reloaded.Id)!;
        Assert.Equal(TaskState.Todo, restored.State);
        Assert.Null(restored.CompletedAt);

        _ = _service.Complete(reloaded.Id.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(1, _service.ClearCompleted());
        _service.Undo();
        Assert.Equal(TaskState.Done, _repository.Find(reloaded.Id)!.State);
    }

    [Fact]
    public void Undo_RevertsRemoveWithTags()
    {
        var task = _service.AddTask("tagged", tagsCsv: "alpha,beta");
        _service.Remove(task.Id.ToString(CultureInfo.InvariantCulture));

        _service.Undo();

        var restored = _repository.Find(task.Id)!;
        Assert.Equal(["alpha", "beta"], restored.Tags);
    }

    [Fact]
    public void Undo_RevertsRespawnChain()
    {
        var task = _service.AddTask("daily", recurrenceName: "daily");

        _ = _service.Complete(task.Id.ToString(CultureInfo.InvariantCulture));
        _service.Undo(); // reverts respawn: the spawned occurrence is deleted
        Assert.Single(_repository.ListAll());
        _service.Undo(); // reverts done: the original is open again

        var original = _repository.Find(task.Id)!;
        Assert.Equal(TaskState.Todo, original.State);
        Assert.Single(_repository.ListAll());
    }

    [Fact]
    public void Undo_EmptyHistory_Throws() =>
        Assert.Throws<InvalidOperationException>(() => _service.Undo());

    [Fact]
    public void UndoDepth_IsConfigurable()
    {
        var repo = new MemoryTaskRepository(undoDepth: 1);
        var service = new HaftKhanService(repo, new FixedTimeProvider(Now), new HaftKhanOptions { MaxUndoDepth = 1 });
        _ = service.AddTask("one");
        _ = service.AddTask("two");
        _ = service.AddTask("three");

        service.Undo(); // reverts "three"
        Assert.Throws<InvalidOperationException>(() => service.Undo()); // "one" was trimmed
    }

    // ── Views ──

    [Fact]
    public void Board_SplitsColumns_AndCapsDone()
    {
        var options = new HaftKhanOptions { BoardTasksPerColumn = 2 };
        var repo = new MemoryTaskRepository();
        var service = new HaftKhanService(repo, new FixedTimeProvider(Now), options);
        var todo = service.AddTask("todo");
        var doing = service.AddTask("doing");
        _ = service.Start(doing.Id.ToString(CultureInfo.InvariantCulture));
        foreach (var index in Enumerable.Range(1, 3))
        {
            var done = service.AddTask($"done{index}");
            _ = service.Complete(done.Id.ToString(CultureInfo.InvariantCulture));
        }
        _ = todo;

        var columns = service.Board();

        Assert.Equal(["TODO", "DOING", "DONE"], columns.Select(column => column.Title));
        Assert.Equal(["todo"], columns[0].Tasks.Select(task => task.Title));
        Assert.Equal(["doing"], columns[1].Tasks.Select(task => task.Title));
        Assert.Equal(2, columns[2].Tasks.Count); // capped at the configured width
    }

    [Fact]
    public void Matrix_PutsTasksInEisenhowerQuadrants()
    {
        _ = _service.AddTask("crisis", priorityName: "critical", dueDateText: "2026-09-19");   // urgent + important
        _ = _service.AddTask("strategy", priorityName: "high");                                 // important
        _ = _service.AddTask("alarm", priorityName: "low", dueDateText: "2026-09-01");         // urgent
        _ = _service.AddTask("noise");                                                      // neither

        var quadrants = _service.Matrix();

        Assert.Equal(["crisis"], quadrants[0].Tasks.Select(task => task.Title));
        Assert.Equal(["strategy"], quadrants[1].Tasks.Select(task => task.Title));
        Assert.Equal(["alarm"], quadrants[2].Tasks.Select(task => task.Title));
        Assert.Equal(["noise"], quadrants[3].Tasks.Select(task => task.Title));
    }

    [Fact]
    public void Report_TracksCompletionsAndStreaks()
    {
        // Completed yesterday and today (2-day streak).
        var first = _repository.Add(new NewTask("yesterday", string.Empty, TaskPriority.Normal, null));
        _repository.Update(first with
        {
            State = TaskState.Done,
            CompletedAt = Now.AddDays(-1),
            UpdatedAt = Now,
        });
        var second = _repository.Add(new NewTask("today", string.Empty, TaskPriority.Normal, null));
        _repository.Update(second with { State = TaskState.Done, CompletedAt = Now, UpdatedAt = Now });
        _ = _service.AddTask("open");

        var report = _service.Report();

        Assert.Equal(1, report.DoneToday);
        Assert.Equal(2, report.DoneLast7Days);
        Assert.Equal(2, report.CurrentStreak);
        Assert.Equal(2, report.BestStreak);
        Assert.Single(report.Focus);
    }

    [Fact]
    public void Report_StreakBreaksCorrectly()
    {
        // Completed 3 days ago only → streak is 0 (yesterday had no completion).
        var old = _repository.Add(new NewTask("old", string.Empty, TaskPriority.Normal, null));
        _repository.Update(old with { State = TaskState.Done, CompletedAt = Now.AddDays(-3), UpdatedAt = Now });

        Assert.Equal(0, _service.Report().CurrentStreak);
        Assert.Equal(1, _service.Report().BestStreak);
    }

    // ── Import / export ──

    [Fact]
    public void ExportImport_RoundTripsTasksTagsAndLinks()
    {
        var source = new MemoryTaskRepository();
        var sourceService = new HaftKhanService(source, new FixedTimeProvider(Now));
        var first = sourceService.AddTask("first", notes: "n1", priorityName: "high", dueDateText: "2026-10-01", project: "p", tagsCsv: "x,y", effortName: "l", recurrenceName: "weekly");
        var second = sourceService.AddTask("second", blockedBy: [first.Id]);
        var (tasks, dependencies) = sourceService.ExportData();
        var backup = new Backup.BackupFile(
            Backup.CurrentVersion, "2026-09-19T00:00:00.0000000+00:00",
            [.. tasks.Select(Backup.ToDto)],
            [.. dependencies.Select(link => new Backup.DependencyDto(link.TaskId, link.DependsOnId))]);

        var (imported, links) = _service.Import(backup, replace: false);

        Assert.Equal(2, imported);
        Assert.Equal(1, links);
        var restoredFirst = _repository.Find(1)!; // fresh store: ids restart at 1
        Assert.Equal("first", restoredFirst.Title);
        Assert.Equal(["x", "y"], restoredFirst.Tags);
        Assert.Equal(new DateOnly(2026, 10, 1), restoredFirst.DueDate);
        Assert.Equal(TaskLinkKeys(_repository.ListDependencies()), TaskLinkKeys([new TaskLink(2, 1)]));
        _ = second;
    }

    [Fact]
    public void Import_Replace_ClearsExisting_AndIsUndoable()
    {
        _ = _service.AddTask("old");
        var backup = new Backup.BackupFile(
            Backup.CurrentVersion, "2026-09-19T00:00:00.0000000+00:00",
            [Backup.ToDto(MakeTask(1, "imported"))], []);

        var (imported, _) = _service.Import(backup, replace: true);

        Assert.Equal(1, imported);
        Assert.Equal(["imported"], _repository.ListAll().Select(task => task.Title));

        _service.Undo(); // removes the imported tasks
        Assert.Empty(_repository.ListAll());
        _service.Undo(); // restores what the replace had cleared
        Assert.Equal(["old"], _repository.ListAll().Select(task => task.Title));
    }

    [Fact]
    public void Import_RejectsTooManyTasks()
    {
        var service = new HaftKhanService(
            _repository, new FixedTimeProvider(Now), new HaftKhanOptions { MaxImportTasks = 1 });
        var backup = new Backup.BackupFile(
            Backup.CurrentVersion, "2026-09-19T00:00:00.0000000+00:00",
            [Backup.ToDto(MakeTask(1, "a")), Backup.ToDto(MakeTask(2, "b"))], []);

        Assert.Throws<ArgumentOutOfRangeException>(() => service.Import(backup, replace: false));
    }

    private static List<long> TaskLinkKeys(IEnumerable<TaskLink> links) =>
        [.. links.Select(link => link.TaskId)];

    private static HaftKhanTask MakeTask(long id, string title)
    {
        var now = DateTimeOffset.UtcNow;
        return new HaftKhanTask(id, title, string.Empty, TaskPriority.Normal, TaskState.Todo, null, now, now, null);
    }

    private string UndoAndOp()
    {
        var result = _service.Undo();
        return result.Operation;
    }
}
