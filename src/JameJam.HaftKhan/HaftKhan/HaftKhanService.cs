using System.Globalization;

using JameJam.Sync;

namespace JameJam.HaftKhan;

/// <summary>
/// Business logic for the Haft Khan to-do list — the advanced engine:
/// tags, projects, recurrence, dependencies (cycle-safe), undo history, natural-language
/// dates, streaks, focus picking, board/matrix views, and validated import/export.
/// Pure orchestration over <see cref="ITaskRepository"/> with an injected clock and
/// guard-validated <see cref="HaftKhanOptions"/> — no hardcoded limits anywhere.
/// </summary>
/// <param name="repository">Task storage.</param>
/// <param name="clock">Time source (UTC-based days).</param>
/// <param name="options">Customizable limits; defaults apply when null.</param>
public sealed class HaftKhanService(ITaskRepository repository, TimeProvider clock, HaftKhanOptions? options = null)
{
    private readonly ITaskRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly HaftKhanOptions _options = Validated(options ?? new HaftKhanOptions());

    /// <summary>The validated options in effect (surfaced for tests and diagnostics).</summary>
    public HaftKhanOptions Options => _options;

    /// <summary>Creates and stores a new task.</summary>
    /// <exception cref="ArgumentException">Invalid title, tags, dates, or a blocker id that does not exist.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A value over its configured cap.</exception>
    /// <exception cref="InvalidOperationException">A blocker would create a dependency cycle.</exception>
    public HaftKhanTask AddTask(
        string? title,
        string? notes = null,
        string? priorityName = null,
        string? dueDateText = null,
        string? project = null,
        string? tagsCsv = null,
        string? effortName = null,
        string? recurrenceName = null,
        string? intervalText = null,
        IReadOnlyList<long>? blockedBy = null)
    {
        var newTask = new NewTask(
            TaskGuard.CleanTitle(title, _options.MaxTitleLength),
            TaskGuard.CleanNotes(notes, _options.MaxNotesLength),
            TaskGuard.ParsePriority(priorityName),
            TaskGuard.ParseDueDate(dueDateText, Today()))
        {
            Project = TaskGuard.CleanProject(project, _options.MaxTitleLength),
            Tags = TaskGuard.CleanTags(tagsCsv, _options.MaxTagsPerTask, _options.MaxTagLength),
            Effort = TaskGuard.ParseEffort(effortName),
            Recurrence = TaskGuard.ParseRecurrenceKind(recurrenceName),
            RecurrenceInterval = TaskGuard.ParseRecurrenceInterval(intervalText, _options.MaxRecurrenceInterval),
            BlockedBy = blockedBy ?? [],
        };

        foreach (var blocker in newTask.BlockedBy)
        {
            _ = _repository.Find(blocker)
                ?? throw new ArgumentException($"Blocker task {blocker} does not exist.", nameof(blockedBy));
        }

        // A brand-new task has no incoming edges yet, so no cycle is possible here.
        var created = _repository.Add(newTask);
        PushUndo("add", [], createdIds: [created.Id]);
        return created;
    }

    /// <summary>Gets one task, or throws when it does not exist.</summary>
    /// <exception cref="ArgumentException">Invalid id.</exception>
    /// <exception cref="TaskNotFoundException">No task with that id.</exception>
    public HaftKhanTask Find(string? idText)
    {
        var id = TaskGuard.ParseId(idText);
        return _repository.Find(id) ?? throw new TaskNotFoundException(id);
    }

    /// <summary>Marks an open task as in progress (records the start time once).</summary>
    /// <exception cref="InvalidOperationException">Task is already done.</exception>
    public HaftKhanTask Start(string? idText)
    {
        var task = Find(idText);
        if (task.State == TaskState.Done)
            throw new InvalidOperationException($"Task {task.Id} is already done.");

        var updated = task with
        {
            State = TaskState.Doing,
            UpdatedAt = Now(),
            StartedAt = task.StartedAt ?? Now(),
        };
        PushUndo("start", [task]);
        _repository.Update(updated);
        return updated;
    }

    /// <summary>
    /// Completes a task (records the completion time). Recurring tasks spawn their next
    /// occurrence automatically. Open blockers refuse completion unless <paramref name="force"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Task is already done, or is blocked and <paramref name="force"/> is false.</exception>
    public CompleteResult Complete(string? idText, bool force = false)
    {
        var task = Find(idText);
        if (task.State == TaskState.Done)
            throw new InvalidOperationException($"Task {task.Id} is already done.");

        var blockers = OpenBlockersOf(task.Id);
        if (blockers.Count > 0 && !force)
        {
            throw new InvalidOperationException(
                $"Task {task.Id} is blocked by open task(s): {Describe(blockers)}. Use --force to conquer anyway.");
        }

        var now = Now();
        PushUndo("done", [task]);
        _repository.Update(task with { State = TaskState.Done, UpdatedAt = now, CompletedAt = now });

        if (task.Recurrence == RecurrenceKind.None)
            return new CompleteResult(Find(idText), null);

        var nextDue = Recurrence.NextDue(
            task.Recurrence, task.RecurrenceInterval, task.DueDate, DateOnly.FromDateTime(now.UtcDateTime));
        var spawned = _repository.Add(new NewTask(
            task.Title, task.Notes, task.Priority, nextDue)
        {
            Project = task.Project,
            Tags = [.. task.Tags],
            Effort = task.Effort,
            Recurrence = task.Recurrence,
            RecurrenceInterval = task.RecurrenceInterval,
        });
        PushUndo("respawn", [], createdIds: [spawned.Id]);
        return new CompleteResult(Find(idText), spawned);
    }

    /// <summary>Removes a task (undoable). Throws when it does not exist.</summary>
    public void Remove(string? idText)
    {
        var task = Find(idText);
        PushUndo("remove", [task]);
        _ = _repository.Remove(task.Id);
    }

    /// <summary>Removes all completed tasks (undoable) and returns how many were removed.</summary>
    public int ClearCompleted()
    {
        var done = _repository.ListAll().Where(task => task.State == TaskState.Done).ToList();
        PushUndo("clear-done", done);
        return _repository.RemoveCompleted();
    }

    /// <summary>Lists the requested view.</summary>
    public IReadOnlyList<HaftKhanTask> List(TaskView view) => view switch
    {
        TaskView.Open => _repository.ListOpen(),
        TaskView.All => _repository.ListAll(),
        TaskView.Done => [.. _repository.ListAll().Where(task => task.State == TaskState.Done)],
        TaskView.Today => [.. _repository.ListDueOnOrBefore(Today()).Where(task => task.DueDate == Today())],
        TaskView.Overdue => [.. _repository.ListDueOnOrBefore(Today()).Where(task => task.DueDate < Today())],
        _ => throw new ArgumentOutOfRangeException(nameof(view), view, "Unknown view."),
    };

    /// <summary>
    /// Lists a view with filters: a tag (case-insensitive), a project (case-insensitive),
    /// and/or a minimum priority.
    /// </summary>
    public IReadOnlyList<HaftKhanTask> ListFiltered(TaskView view, string? tag, string? project, string? priorityName)
    {
        var tasks = List(view);
        if (priorityName is not null)
        {
            var minPriority = TaskGuard.ParsePriority(priorityName);
            tasks = [.. tasks.Where(task => task.Priority >= minPriority)];
        }

        if (!string.IsNullOrWhiteSpace(tag))
        {
            tasks = [.. tasks.Where(task => task.Tags.Any(
                existing => existing.Equals(tag.Trim(), StringComparison.OrdinalIgnoreCase)))];
        }

        if (!string.IsNullOrWhiteSpace(project))
        {
            tasks = [.. tasks.Where(task => task.Project.Equals(project.Trim(), StringComparison.OrdinalIgnoreCase))];
        }

        return tasks;
    }

    /// <summary>Searches title, notes, project, and tags for a case-insensitive substring.</summary>
    /// <exception cref="ArgumentException">Empty query.</exception>
    public IReadOnlyList<HaftKhanTask> Search(string? query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var needle = query.Trim();
        return [.. _repository.ListAll().Where(task =>
            task.Title.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || task.Notes.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || task.Project.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || task.Tags.Any(tag => tag.Contains(needle, StringComparison.OrdinalIgnoreCase)))];
    }

    /// <summary>The kanban board: to-do, doing, and recently-conquered columns.</summary>
    public IReadOnlyList<BoardColumn> Board()
    {
        var open = _repository.ListOpen();
        var done = _repository.ListAll()
            .Where(task => task.State == TaskState.Done)
            .OrderByDescending(task => task.CompletedAt)
            .Take(_options.BoardTasksPerColumn)
            .ToList();

        return
        [
            new BoardColumn("TODO", [.. open.Where(task => task.State == TaskState.Todo).Take(_options.BoardTasksPerColumn)]),
            new BoardColumn("DOING", [.. open.Where(task => task.State == TaskState.Doing).Take(_options.BoardTasksPerColumn)]),
            new BoardColumn("DONE", done),
        ];
    }

    /// <summary>The Eisenhower matrix over open tasks (urgency = overdue/today, importance = high/critical).</summary>
    public IReadOnlyList<MatrixQuadrant> Matrix()
    {
        var today = Today();
        var open = _repository.ListOpen();
        bool IsUrgent(HaftKhanTask task) => task.DueDate.HasValue && task.DueDate.Value <= today;
        bool IsImportant(HaftKhanTask task) => task.Priority is TaskPriority.High or TaskPriority.Critical;

        return
        [
            new MatrixQuadrant("DO NOW — urgent + important", [.. open.Where(task => IsUrgent(task) && IsImportant(task))]),
            new MatrixQuadrant("SCHEDULE — important, not urgent", [.. open.Where(task => !IsUrgent(task) && IsImportant(task))]),
            new MatrixQuadrant("DELEGATE — urgent, not important", [.. open.Where(task => IsUrgent(task) && !IsImportant(task))]),
            new MatrixQuadrant("LATER — neither", [.. open.Where(task => !IsUrgent(task) && !IsImportant(task))]),
        ];
    }

    /// <summary>The single next best labour: first unblocked open task in priority order, or null.</summary>
    public HaftKhanTask? NextFocus()
    {
        var blockedIds = BlockedIds();
        return _repository.ListOpen().FirstOrDefault(task => !blockedIds.Contains(task.Id));
    }

    /// <summary>Rich productivity report: counts, overdue, today/7-day completions, streaks, focus picks.</summary>
    public ProductivityReport Report()
    {
        var today = Today();
        var counts = _repository.CountByState(today);
        var completions = _repository.ListAll()
            .Where(task => task.State == TaskState.Done && task.CompletedAt.HasValue)
            .Select(task => DateOnly.FromDateTime(task.CompletedAt!.Value.UtcDateTime))
            .Distinct()
            .OrderByDescending(date => date)
            .ToList();

        var doneToday = _repository.ListAll().Count(task =>
            task.State == TaskState.Done && task.CompletedAt.HasValue
            && DateOnly.FromDateTime(task.CompletedAt.Value.UtcDateTime) == today);
        var doneLast7Days = _repository.ListAll().Count(task =>
            task.State == TaskState.Done && task.CompletedAt.HasValue
            && DateOnly.FromDateTime(task.CompletedAt.Value.UtcDateTime) > today.AddDays(-7));

        var blockedIds = BlockedIds();
        return new ProductivityReport(
            counts.Todo,
            counts.Doing,
            counts.Done,
            counts.Overdue,
            doneToday,
            doneLast7Days,
            CurrentStreak(completions, today),
            BestStreak(completions),
            [.. _repository.ListOpen().Where(task => !blockedIds.Contains(task.Id)).Take(_options.ReviewFocusCount)]);
    }

    /// <summary>Reverts the last mutating operation.</summary>
    /// <exception cref="InvalidOperationException">The undo history is empty.</exception>
    public UndoResult Undo() => _repository.Undo();

    /// <summary>
    /// Imports tasks from a parsed backup (ids are reassigned; dependency edges are remapped).
    /// When <paramref name="replace"/> is true the current list is cleared first (undoable).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">More tasks than <see cref="HaftKhanOptions.MaxImportTasks"/>.</exception>
    public (int Tasks, int Links) Import(Backup.BackupFile backup, bool replace)
    {
        ArgumentNullException.ThrowIfNull(backup);
        if (backup.Tasks.Count > _options.MaxImportTasks)
            throw new ArgumentOutOfRangeException(nameof(backup), $"Import is capped at {_options.MaxImportTasks} tasks.");

        if (replace)
        {
            PushUndo("import-replace", _repository.ListAll(), replaceAll: true);
            foreach (var task in _repository.ListAll().Where(task => task.State != TaskState.Done).ToList())
            {
                _ = _repository.Remove(task.Id);
            }

            _ = _repository.RemoveCompleted();
        }

        var today = Today();
        Dictionary<long, long> idMap = [];
        Dictionary<string, long> uidMap = [];
        List<long> createdIds = [];
        foreach (var dto in backup.Tasks)
        {
            var created = _repository.Add(new NewTask(
                TaskGuard.CleanTitle(dto.Title, _options.MaxTitleLength),
                TaskGuard.CleanNotes(dto.Notes, _options.MaxNotesLength),
                TaskGuard.ParsePriority(((TaskPriority)dto.Priority).ToString()),
                ParseImportedDue(dto.DueDate, today))
            {
                Project = TaskGuard.CleanProject(dto.Project, _options.MaxTitleLength),
                Tags = TaskGuard.CleanTags(
                    string.Join(',', dto.Tags), _options.MaxTagsPerTask, _options.MaxTagLength),
                Effort = ValidateImportedEnum<TaskEffort>(dto.Effort),
                Recurrence = ValidateImportedEnum<RecurrenceKind>(dto.Recurrence),
                RecurrenceInterval = TaskGuard.ParseRecurrenceInterval(
                    dto.RecurrenceInterval.ToString(CultureInfo.InvariantCulture), _options.MaxRecurrenceInterval),
                Uid = dto.Uid,
            });
            idMap[dto.Id] = created.Id;
            if (!string.IsNullOrEmpty(created.Uid))
                uidMap[created.Uid] = created.Id;
            createdIds.Add(created.Id);
        }

        var links = 0;
        foreach (var dependency in backup.Dependencies)
        {
            long? taskId = null;
            long? dependsOnId = null;
            if (!string.IsNullOrEmpty(dependency.TaskUid) && !string.IsNullOrEmpty(dependency.DependsOnUid))
            {
                taskId = uidMap.TryGetValue(dependency.TaskUid, out var a) ? a : null;
                dependsOnId = uidMap.TryGetValue(dependency.DependsOnUid, out var b) ? b : null;
            }

            taskId ??= idMap.TryGetValue(dependency.TaskId, out var c) ? c : null;
            dependsOnId ??= idMap.TryGetValue(dependency.DependsOnId, out var d) ? d : null;

            if (taskId.HasValue && dependsOnId.HasValue)
            {
                _repository.AddDependency(taskId.Value, dependsOnId.Value);
                links++;
            }
        }

        PushUndo("import", [], createdIds: createdIds);
        return (createdIds.Count, links);
    }

    /// <summary>Every task plus every dependency edge — the export payload.</summary>
    public (IReadOnlyList<HaftKhanTask> Tasks, IReadOnlyList<TaskLink> Dependencies) ExportData() =>
        (_repository.ListAll(), _repository.ListDependencies());

    /// <summary>
    /// Syncs with a remote URL: <see cref="SyncMode.Merge"/> pulls, merges by stable uid
    /// (last-write-wins on <c>UpdatedAt</c>, ties keep local), unions dependencies, and pushes
    /// the merged result so both sides converge; <see cref="SyncMode.Pull"/> only merges locally;
    /// <see cref="SyncMode.Push"/> replaces the remote (refused while the remote is non-empty
    /// unless <paramref name="force"/>). The whole merge is undoable.
    /// </summary>
    /// <exception cref="SyncException">Transport, protocol, or push-safety failure.</exception>
    public async Task<SyncReport> SyncAsync(
        ISyncClient client,
        SyncMode mode = SyncMode.Merge,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var localTasks = _repository.ListAll();
        var remote = ParseRemote(await client.GetAsync(cancellationToken).ConfigureAwait(false));
        var remoteTasks = remote?.Tasks.Select(Backup.FromDto).ToList() ?? [];

        if (mode == SyncMode.Push)
        {
            if (remoteTasks.Count > 0 && !force)
            {
                throw new SyncException(
                    $"The remote already holds {remoteTasks.Count} task(s); pushing replaces them. Use --force to confirm.");
            }

            var pushed = await PushAsync(client, cancellationToken).ConfigureAwait(false);
            return new SyncReport(SyncMode.Push, 0, pushed, localTasks.Count);
        }

        // Snapshot the whole store first — `undo` reverts the entire merge.
        PushUndo("sync", localTasks, replaceAll: true);

        var pulled = 0;
        foreach (var remoteTask in remoteTasks.OrderBy(task => task.UpdatedAt))
        {
            var existing = _repository.FindByUid(remoteTask.Uid);
            if (existing is not null && existing.UpdatedAt >= remoteTask.UpdatedAt)
                continue; // local wins (last-write-wins; ties keep local)

            _repository.Upsert(remoteTask);
            pulled++;
        }

        MergeDependencies(remote?.Dependencies ?? []);
        var total = _repository.ListAll().Count;

        if (mode == SyncMode.Pull)
            return new SyncReport(SyncMode.Pull, pulled, 0, total);

        var pushedCount = await PushAsync(client, cancellationToken).ConfigureAwait(false);
        return new SyncReport(SyncMode.Merge, pulled, pushedCount, total);
    }

    /// <summary>Parses the remote payload; null when the remote does not exist yet.</summary>
    private static Backup.BackupFile? ParseRemote(string? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            return Backup.FromJson(json);
        }
        catch (ArgumentException ex)
        {
            throw new SyncException("The remote did not return a valid Haft Khan backup.", innerException: ex);
        }
    }

    /// <summary>Uploads the full local store as a version-2 backup.</summary>
    private async Task<int> PushAsync(ISyncClient client, CancellationToken cancellationToken)
    {
        var (tasks, dependencies) = ExportData();
        var backup = new Backup.BackupFile(
            Backup.CurrentVersion,
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            [.. tasks.Select(Backup.ToDto)],
            [.. dependencies.Select(link => new Backup.DependencyDto(
                link.TaskId,
                link.DependsOnId,
                _repository.Find(link.TaskId)?.Uid,
                _repository.Find(link.DependsOnId)?.Uid))]);
        await client.PutAsync(Backup.ToJson(backup), cancellationToken).ConfigureAwait(false);
        return tasks.Count;
    }

    /// <summary>Unions local and remote dependency edges (matched by stable uid), then applies the result.</summary>
    private void MergeDependencies(IReadOnlyList<Backup.DependencyDto> remoteDependencies)
    {
        var tasks = _repository.ListAll();
        var idToUid = tasks.ToDictionary(task => task.Id, task => task.Uid);
        var uidToId = tasks.ToDictionary(task => task.Uid, task => task.Id);

        HashSet<(string TaskUid, string DependsOnUid)> pairs = [];
        foreach (var link in _repository.ListDependencies())
        {
            if (idToUid.TryGetValue(link.TaskId, out var a) && idToUid.TryGetValue(link.DependsOnId, out var b))
                pairs.Add((a, b));
        }

        foreach (var dependency in remoteDependencies)
        {
            if (!string.IsNullOrEmpty(dependency.TaskUid) && !string.IsNullOrEmpty(dependency.DependsOnUid))
                pairs.Add((dependency.TaskUid, dependency.DependsOnUid));
        }

        List<TaskLink> merged = [];
        foreach (var (taskUid, dependsOnUid) in pairs)
        {
            if (uidToId.TryGetValue(taskUid, out var taskId) && uidToId.TryGetValue(dependsOnUid, out var dependsOnId))
                merged.Add(new TaskLink(taskId, dependsOnId));
        }

        _repository.SetDependencies([.. merged.OrderBy(link => link.TaskId).ThenBy(link => link.DependsOnId)]);
    }

    private static HaftKhanOptions Validated(HaftKhanOptions value)
    {
        value.Validate(); // fail fast: bad limits never reach business logic
        return value;
    }

    /// <summary>
    /// Adds a dependency edge between existing tasks: <paramref name="idText"/> becomes blocked
    /// until every id in <paramref name="blockedByCsv"/> is done. Cycle-safe.
    /// </summary>
    /// <exception cref="ArgumentException">Unknown id or self-link.</exception>
    /// <exception cref="InvalidOperationException">The link would close a dependency cycle.</exception>
    public void AddLink(string? idText, string? blockedByCsv)
    {
        var task = Find(idText);
        foreach (var blocker in TaskGuard.ParseIdList(blockedByCsv))
        {
            _ = _repository.Find(blocker)
                ?? throw new ArgumentException($"Blocker task {blocker} does not exist.");

            if (blocker == task.Id)
                throw new ArgumentException("A task cannot block itself.");

            if (Reaches(blocker, task.Id))
                throw new InvalidOperationException(
                    $"Dependency rejected: task {task.Id} would depend on itself through a cycle.");

            _repository.AddDependency(task.Id, blocker);
        }
    }

    /// <summary>Removes every dependency edge of a task (both directions).</summary>
    public void RemoveLinks(string? idText)
    {
        var task = Find(idText);
        PushUndo("unlink", [task]);
        _repository.RemoveDependenciesFor(task.Id);
    }

    /// <summary>Walks blocker edges upward from <paramref name="from"/> looking for <paramref name="target"/>.</summary>
    private bool Reaches(long from, long target)
    {
        var adjacency = _repository.ListDependencies()
            .GroupBy(link => link.TaskId)
            .ToDictionary(group => group.Key, group => group.Select(link => link.DependsOnId).ToList());

        HashSet<long> visited = [];
        Stack<long> stack = [];
        stack.Push(from);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current == target)
                return true;

            if (!visited.Add(current))
                continue;

            if (adjacency.TryGetValue(current, out var blockers))
            {
                foreach (var blocker in blockers)
                    stack.Push(blocker);
            }
        }

        return false;
    }

    private List<long> OpenBlockersOf(long taskId)
    {
        var blockers = _repository.ListDependencies()
            .Where(link => link.TaskId == taskId)
            .Select(link => link.DependsOnId)
            .Distinct();
        List<long> open = [];
        foreach (var blocker in blockers)
        {
            var task = _repository.Find(blocker);
            if (task is not null && task.State != TaskState.Done)
                open.Add(blocker);
        }

        return open;
    }

    private HashSet<long> BlockedIds()
    {
        var openIds = _repository.ListOpen().Select(task => task.Id).ToHashSet();
        HashSet<long> blocked = [];
        foreach (var link in _repository.ListDependencies())
        {
            if (openIds.Contains(link.TaskId)
                && _repository.Find(link.DependsOnId) is { } blocker
                && blocker.State != TaskState.Done)
            {
                blocked.Add(link.TaskId);
            }
        }

        return blocked;
    }

    private void PushUndo(
        string operation,
        IReadOnlyList<HaftKhanTask> affected,
        bool replaceAll = false,
        IReadOnlyList<long>? createdIds = null)
    {
        Dictionary<long, IReadOnlyList<string>> tags = [];
        List<TaskLink> dependencies = [];
        var ids = affected.Select(task => task.Id).ToHashSet();

        foreach (var task in affected)
        {
            tags[task.Id] = _repository.GetTags(task.Id);
        }

        if (ids.Count > 0)
        {
            dependencies = [.. _repository.ListDependencies().Where(link => ids.Contains(link.TaskId) || ids.Contains(link.DependsOnId))];
        }

        _repository.PushUndo(new UndoSnapshot(
            operation, affected, tags, dependencies, createdIds ?? [], replaceAll));
    }

    private static string Describe(IEnumerable<long> ids) =>
        string.Join(", ", ids.Select(id => $"#{id}"));

    private static int CurrentStreak(List<DateOnly> completionsDescending, DateOnly today)
    {
        if (completionsDescending.Count == 0)
            return 0;

        var expected = completionsDescending[0] == today ? today : today.AddDays(-1);
        if (completionsDescending[0] != expected)
            return 0;

        var streak = 0;
        foreach (var date in completionsDescending)
        {
            if (date != expected)
                break;

            streak++;
            expected = expected.AddDays(-1);
        }

        return streak;
    }

    private static int BestStreak(List<DateOnly> completionsDescending)
    {
        if (completionsDescending.Count == 0)
            return 0;

        var best = 1;
        var run = 1;
        for (var i = 1; i < completionsDescending.Count; i++)
        {
            run = completionsDescending[i] == completionsDescending[i - 1].AddDays(-1) ? run + 1 : 1;
            if (run > best)
                best = run;
        }

        return best;
    }

    private static DateOnly? ParseImportedDue(string? dueDate, DateOnly today) =>
        string.IsNullOrEmpty(dueDate)
            ? null
            : TaskGuard.ParseDueDate(dueDate, today);

    private static T ValidateImportedEnum<T>(int value)
        where T : struct, Enum
    {
        var parsed = (T)(object)value;
        return Enum.IsDefined(parsed)
            ? parsed
            : throw new ArgumentException($"Imported value {value} is not a valid {typeof(T).Name}.");
    }

    private DateTimeOffset Now() => _clock.GetUtcNow();

    private DateOnly Today() => DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
}
