using System.Globalization;
using JameJam.HaftKhan;

namespace JameJam.Tests.HaftKhan;

/// <summary>Tests for the Haft Khan business logic (in-memory repo + fixed clock: fully deterministic).</summary>
public sealed class HaftKhanServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 10, 0, 0, TimeSpan.Zero);

    private readonly MemoryTaskRepository _repository = new();
    private readonly HaftKhanService _service;

    public HaftKhanServiceTests() => _service = new HaftKhanService(_repository, new FixedTimeProvider(Now));

    [Fact]
    public void AddTask_RoundTrips_AllFields()
    {
        var task = _service.AddTask("  Slay the dragon ", " bring a sword\u0002", "critical", "2026-09-25");

        var stored = _service.Find(task.Id.ToString(CultureInfo.InvariantCulture));

        Assert.Equal("Slay the dragon", stored.Title);
        Assert.Equal("bring a sword", stored.Notes); // trimmed, control chars stripped
        Assert.Equal(TaskPriority.Critical, stored.Priority);
        Assert.Equal(new DateOnly(2026, 9, 25), stored.DueDate);
        Assert.Equal(TaskState.Todo, stored.State);
        Assert.Null(stored.CompletedAt);
        Assert.True(stored.CreatedAt <= DateTimeOffset.UtcNow); // repo stamps real time, service owns the clock
    }

    [Fact]
    public void AddTask_ControlCharsAreStripped()
    {
        var task = _service.AddTask("a\u0007b", null, null, null);

        Assert.Equal("ab", task.Title);
    }

    [Fact]
    public void AddTask_EmptyTitle_Throws() =>
        Assert.Throws<ArgumentException>(() => _service.AddTask("   ", null, null, null));

    [Fact]
    public void AddTask_TooLongTitle_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => _service.AddTask(new string('t', 201), null, null, null));

    [Fact]
    public void AddTask_InvalidPriority_Throws() =>
        Assert.Throws<ArgumentException>(() => _service.AddTask("t", null, "urgent", null));

    [Fact]
    public void AddTask_InvalidDate_Throws() =>
        Assert.Throws<ArgumentException>(() => _service.AddTask("t", null, null, "not-a-date"));

    [Fact]
    public void AddTask_NaturalLanguageDate_Parses()
    {
        var task = _service.AddTask("t", null, null, "tomorrow");

        Assert.Equal(new DateOnly(2026, 9, 20), task.DueDate);
    }

    [Fact]
    public void Start_SetsDoingState()
    {
        var task = _service.AddTask("t", null, null, null);

        var started = _service.Start(task.Id.ToString(CultureInfo.InvariantCulture));

        Assert.Equal(TaskState.Doing, started.State);
        Assert.Equal(Now, started.UpdatedAt);
        Assert.Equal(TaskState.Doing, _service.Find(task.Id.ToString(CultureInfo.InvariantCulture)).State);
    }

    [Fact]
    public void Start_DoneTask_Throws()
    {
        var task = _service.AddTask("t", null, null, null);
        _ = _service.Complete(task.Id.ToString(CultureInfo.InvariantCulture));

        var exception = Assert.Throws<InvalidOperationException>(() => _service.Start(task.Id.ToString(CultureInfo.InvariantCulture)));

        Assert.Contains("already done", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Complete_SetsDone_AndRecordsCompletionTime()
    {
        var task = _service.AddTask("t", null, null, null);

        var result = _service.Complete(task.Id.ToString(CultureInfo.InvariantCulture));

        Assert.Equal(TaskState.Done, result.Completed.State);
        Assert.Equal(Now, result.Completed.CompletedAt);
        Assert.Equal(Now, result.Completed.UpdatedAt);
        Assert.Null(result.Next);
    }

    [Fact]
    public void Complete_AlreadyDone_Throws()
    {
        var task = _service.AddTask("t", null, null, null);
        _ = _service.Complete(task.Id.ToString(CultureInfo.InvariantCulture));

        Assert.Throws<InvalidOperationException>(() => _service.Complete(task.Id.ToString(CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Remove_RemovesExistingTask()
    {
        var task = _service.AddTask("t", null, null, null);

        _service.Remove(task.Id.ToString(CultureInfo.InvariantCulture));

        Assert.Throws<TaskNotFoundException>(() => _service.Find(task.Id.ToString(CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Remove_MissingTask_ThrowsNotFound()
    {
        var exception = Assert.Throws<TaskNotFoundException>(() => _service.Remove("99"));

        Assert.Equal(99, exception.Id);
        Assert.Contains("Task 99 was not found.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ClearCompleted_RemovesOnlyDone()
    {
        var done = _service.AddTask("done", null, null, null);
        var open = _service.AddTask("open", null, null, null);
        _ = _service.Complete(done.Id.ToString(CultureInfo.InvariantCulture));

        Assert.Equal(1, _service.ClearCompleted());
        Assert.Equal("open", _service.Find(open.Id.ToString(CultureInfo.InvariantCulture)).Title);
        Assert.Equal(0, _service.ClearCompleted());
    }

    [Fact]
    public void List_Open_SortsByPriorityThenDueThenId()
    {
        var low = _service.AddTask("low", null, "low", null);
        var criticalDueLater = _service.AddTask("crit-later", null, "critical", "2026-09-30");
        var criticalDueSooner = _service.AddTask("crit-sooner", null, "critical", "2026-09-20");
        var normal = _service.AddTask("normal", null, null, null);

        var open = _service.List(TaskView.Open);

        Assert.Equal(
            [criticalDueSooner.Id, criticalDueLater.Id, normal.Id, low.Id],
            open.Select(task => task.Id));
    }

    [Fact]
    public void List_Done_OnlyCompleted()
    {
        var openTask = _service.AddTask("open", null, null, null);
        var doneTask = _service.AddTask("done", null, null, null);
        _ = _service.Complete(doneTask.Id.ToString(CultureInfo.InvariantCulture));

        var done = _service.List(TaskView.Done);

        Assert.Equal([doneTask.Id], done.Select(task => task.Id));
        Assert.Contains(openTask.Id, _service.List(TaskView.Open).Select(task => task.Id));
    }

    [Fact]
    public void List_Today_OnlyTasksDueToday()
    {
        _ = _service.AddTask("past", null, null, "2026-09-18");
        var todayTask = _service.AddTask("today", null, null, "2026-09-19");
        _ = _service.AddTask("future", null, null, "2026-09-20");

        var today = _service.List(TaskView.Today);

        Assert.Equal([todayTask.Id], today.Select(task => task.Id));
    }

    [Fact]
    public void List_Overdue_OnlyPastDueOpenTasks()
    {
        var yesterday = _service.AddTask("past", null, null, "2026-09-18");
        _ = _service.AddTask("today", null, null, "2026-09-19");

        var overdue = _service.List(TaskView.Overdue);

        Assert.Equal([yesterday.Id], overdue.Select(task => task.Id));
    }

    [Fact]
    public void Stats_CountsStatesAndOverdue()
    {
        _ = _service.AddTask("todo", null, null, null);
        var doing = _service.AddTask("doing", null, null, null);
        _ = _service.Start(doing.Id.ToString(CultureInfo.InvariantCulture));
        var doneTask = _service.AddTask("done", null, null, null);
        _ = _service.Complete(doneTask.Id.ToString(CultureInfo.InvariantCulture));
        _ = _service.AddTask("overdue", null, null, "2026-09-01");

        var report = _service.Report();

        Assert.Equal(4, report.Todo + report.Doing + report.Done);
        Assert.Equal(2, report.Todo);
        Assert.Equal(1, report.Doing);
        Assert.Equal(1, report.Done);
        Assert.Equal(1, report.Overdue);
    }

    [Fact]
    public void List_UnknownView_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => _service.List((TaskView)99));

    [Fact]
    public void Options_AreValidatedAtConstruction() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new HaftKhanService(_repository, new FixedTimeProvider(Now), new HaftKhanOptions { MaxTitleLength = 0 }));

    [Fact]
    public void Find_InvalidIdText_Throws()
    {
        Assert.Throws<ArgumentException>(() => _service.Find("zero"));
        Assert.Throws<TaskNotFoundException>(() => _service.Find("7"));
    }
}
