namespace JameJam.HaftKhan;

/// <summary>A dependency edge: <see cref="TaskId"/> is blocked until <see cref="DependsOnId"/> is done.</summary>
/// <param name="TaskId">The blocked task.</param>
/// <param name="DependsOnId">The blocker.</param>
public sealed record TaskLink(long TaskId, long DependsOnId);

/// <summary>A point-in-time snapshot used by <c>undo</c>: the full prior state of every affected task.</summary>
/// <param name="Operation">Name of the operation that is about to run (e.g. "done", "remove").</param>
/// <param name="Tasks">Prior-state tasks (empty when the operation introduced brand-new tasks only).</param>
/// <param name="Tags">Prior tags per task id.</param>
/// <param name="Dependencies">Prior dependency edges touching the affected tasks.</param>
/// <param name="CreatedTaskIds">Ids created by the operation — undo deletes these.</param>
/// <param name="ReplaceAll">When true, undo first wipes the store, then restores the snapshot.</param>
public sealed record UndoSnapshot(
    string Operation,
    IReadOnlyList<HaftKhanTask> Tasks,
    IReadOnlyDictionary<long, IReadOnlyList<string>> Tags,
    IReadOnlyList<TaskLink> Dependencies,
    IReadOnlyList<long> CreatedTaskIds,
    bool ReplaceAll)
{
}

/// <summary>Outcome of applying an undo.</summary>
/// <param name="Operation">The reverted operation.</param>
/// <param name="RestoredTasks">How many tasks were restored to their prior state.</param>
public sealed record UndoResult(string Operation, int RestoredTasks);

/// <summary>Result of completing a task (recurrence may spawn the next occurrence).</summary>
/// <param name="Completed">The completed task.</param>
/// <param name="Next">The freshly spawned next occurrence, or null for one-off tasks.</param>
public sealed record CompleteResult(HaftKhanTask Completed, HaftKhanTask? Next);

/// <summary>Rich productivity report (stats, streaks, focus picks).</summary>
/// <param name="Todo">Tasks not started.</param>
/// <param name="Doing">Tasks in progress.</param>
/// <param name="Done">Completed tasks.</param>
/// <param name="Overdue">Open tasks past their due date.</param>
/// <param name="DoneToday">Completed today.</param>
/// <param name="DoneLast7Days">Completed in the last 7 days (including today).</param>
/// <param name="CurrentStreak">Consecutive days with at least one completion, ending today or yesterday.</param>
/// <param name="BestStreak">Longest run of consecutive completion days ever.</param>
/// <param name="Focus">Top unblocked open tasks to tackle next.</param>
public sealed record ProductivityReport(
    int Todo,
    int Doing,
    int Done,
    int Overdue,
    int DoneToday,
    int DoneLast7Days,
    int CurrentStreak,
    int BestStreak,
    IReadOnlyList<HaftKhanTask> Focus);

/// <summary>One column of the kanban board view.</summary>
/// <param name="Title">Column heading (e.g. "TODO").</param>
/// <param name="Tasks">Tasks in the column (already capped for display).</param>
public sealed record BoardColumn(string Title, IReadOnlyList<HaftKhanTask> Tasks);

/// <summary>One quadrant of the Eisenhower matrix view.</summary>
/// <param name="Title">Quadrant heading (e.g. "DO NOW — urgent + important").</param>
/// <param name="Tasks">Tasks in the quadrant.</param>
public sealed record MatrixQuadrant(string Title, IReadOnlyList<HaftKhanTask> Tasks);
