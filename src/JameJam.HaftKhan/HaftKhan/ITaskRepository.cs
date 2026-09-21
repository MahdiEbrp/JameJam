namespace JameJam.HaftKhan;

/// <summary>Persistent storage for Haft Khan tasks (including tags, dependencies, and undo history).</summary>
public interface ITaskRepository
{
    /// <summary>Stores a new task (with its tags and dependency links) and returns it with its assigned id.</summary>
    HaftKhanTask Add(NewTask task);

    /// <summary>Gets the task with <paramref name="id"/>, or null when absent.</summary>
    HaftKhanTask? Find(long id);

    /// <summary>Overwrites an existing task (matched by id, tags replaced).</summary>
    void Update(HaftKhanTask task);

    /// <summary>Removes the task with <paramref name="id"/> (and its tags/dependency links). Returns true when it existed.</summary>
    bool Remove(long id);

    /// <summary>Removes every completed task. Returns the number of removed tasks.</summary>
    int RemoveCompleted();

    /// <summary>Lists open tasks (to-do and doing): priority high→low, then due date, then id.</summary>
    IReadOnlyList<HaftKhanTask> ListOpen();

    /// <summary>Lists every task grouped by state, then priority, then due date.</summary>
    IReadOnlyList<HaftKhanTask> ListAll();

    /// <summary>
    /// Lists open tasks due on or before <paramref name="dueDate"/> (the range rides an index),
    /// ordered by due date, then priority.
    /// </summary>
    IReadOnlyList<HaftKhanTask> ListDueOnOrBefore(DateOnly dueDate);

    /// <summary>Counts tasks per state, plus open tasks overdue relative to <paramref name="today"/>.</summary>
    TaskCounts CountByState(DateOnly today);

    /// <summary>Finds a task by its stable sync identity, or null when absent.</summary>
    HaftKhanTask? FindByUid(string uid);

    /// <summary>
    /// Inserts or updates a task matched by its stable uid (sync support): the local id is
    /// preserved when the task exists; content, timestamps, and uid come from
    /// <paramref name="task"/> verbatim. Returns the stored task with its local id.
    /// </summary>
    HaftKhanTask Upsert(HaftKhanTask task);

    /// <summary>Replaces every dependency edge with <paramref name="links"/> (used by sync merge).</summary>
    void SetDependencies(IReadOnlyList<TaskLink> links);

    /// <summary>Adds a dependency edge: <paramref name="taskId"/> is blocked until <paramref name="dependsOnId"/> is done.
    /// Idempotent (duplicate links are ignored). Both tasks must exist; self-links are rejected.</summary>
    void AddDependency(long taskId, long dependsOnId);

    /// <summary>Lists every dependency edge.</summary>
    IReadOnlyList<TaskLink> ListDependencies();

    /// <summary>Removes dependency edges in both directions for <paramref name="taskId"/>.</summary>
    void RemoveDependenciesFor(long taskId);

    /// <summary>Gets the tags stored for <paramref name="taskId"/> (empty when it has none).</summary>
    IReadOnlyList<string> GetTags(long taskId);

    /// <summary>Pushes an undo snapshot onto the history stack (oldest entries are dropped beyond the depth limit).</summary>
    void PushUndo(UndoSnapshot snapshot);

    /// <summary>
    /// Pops the newest snapshot and restores the prior state it describes.
    /// Returns the reverted operation's name and how many tasks were restored.
    /// </summary>
    /// <exception cref="InvalidOperationException">The undo history is empty.</exception>
    UndoResult Undo();
}
