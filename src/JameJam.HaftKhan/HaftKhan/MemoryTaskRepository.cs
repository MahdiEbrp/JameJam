using System.Text.Json;

namespace JameJam.HaftKhan;

/// <summary>Non-persistent in-memory task repository (for tests, dry runs, and previews).</summary>
public sealed class MemoryTaskRepository : ITaskRepository
{
    private sealed record SnapshotDto(
        string Operation,
        List<Backup.TaskDto> Tasks,
        Dictionary<long, List<string>> Tags,
        List<Backup.DependencyDto> Dependencies,
        List<long> CreatedTaskIds,
        bool ReplaceAll);

    private readonly object _gate = new();
    private readonly Dictionary<long, HaftKhanTask> _tasks = new();
    private readonly Dictionary<long, List<string>> _tags = new();
    private readonly List<TaskLink> _links = [];
    private readonly List<SnapshotDto> _undo = [];
    private readonly int _undoDepth;
    private long _nextId = 1;

    /// <summary>Initializes the repository.</summary>
    /// <param name="undoDepth">How many undo snapshots to keep (defaults to the named rail).</param>
    public MemoryTaskRepository(int undoDepth = HaftKhanOptions.DefaultMaxUndoDepth) =>
        _undoDepth = undoDepth;

    /// <inheritdoc />
    public HaftKhanTask Add(NewTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var stored = new HaftKhanTask(
                _nextId++, task.Title, task.Notes, task.Priority, TaskState.Todo,
                task.DueDate, now, now, CompletedAt: null)
            {
                Project = task.Project,
                Tags = [.. task.Tags],
                Effort = task.Effort,
                Recurrence = task.Recurrence,
                RecurrenceInterval = task.RecurrenceInterval,
                Uid = string.IsNullOrEmpty(task.Uid) ? Backup.NewUid() : task.Uid,
            };
            _tasks[stored.Id] = stored;
            _tags[stored.Id] = [.. task.Tags];
            foreach (var blocker in task.BlockedBy)
            {
                AddDependency(stored.Id, blocker);
            }

            return stored;
        }
    }

    /// <inheritdoc />
    public HaftKhanTask? Find(long id)
    {
        lock (_gate)
        {
            return _tasks.TryGetValue(id, out var task) ? task : null;
        }
    }

    /// <inheritdoc />
    public void Update(HaftKhanTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (_gate)
        {
            if (!_tasks.ContainsKey(task.Id))
                throw new TaskNotFoundException(task.Id);

            _tasks[task.Id] = task;
            _tags[task.Id] = [.. task.Tags];
        }
    }

    /// <inheritdoc />
    public bool Remove(long id)
    {
        lock (_gate)
        {
            RemoveDependenciesFor(id);
            _tags.Remove(id);
            return _tasks.Remove(id);
        }
    }

    /// <inheritdoc />
    public int RemoveCompleted()
    {
        lock (_gate)
        {
            var doneIds = _tasks.Values.Where(task => task.State == TaskState.Done).Select(task => task.Id).ToList();
            foreach (var id in doneIds)
            {
                Remove(id);
            }

            return doneIds.Count;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<HaftKhanTask> ListOpen()
    {
        lock (_gate)
        {
            return [.. _tasks.Values
                .Where(task => task.State != TaskState.Done)
                .OrderByDescending(task => task.Priority)
                .ThenBy(task => task.DueDate ?? DateOnly.MaxValue)
                .ThenBy(task => task.Id)];
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<HaftKhanTask> ListAll()
    {
        lock (_gate)
        {
            return [.. _tasks.Values
                .OrderBy(task => task.State)
                .ThenByDescending(task => task.Priority)
                .ThenBy(task => task.DueDate ?? DateOnly.MaxValue)
                .ThenBy(task => task.Id)];
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<HaftKhanTask> ListDueOnOrBefore(DateOnly dueDate)
    {
        lock (_gate)
        {
            return [.. _tasks.Values
                .Where(task => task.State != TaskState.Done
                    && task.DueDate.HasValue
                    && task.DueDate.Value <= dueDate)
                .OrderBy(task => task.DueDate)
                .ThenByDescending(task => task.Priority)
                .ThenBy(task => task.Id)];
        }
    }

    /// <inheritdoc />
    public TaskCounts CountByState(DateOnly today)
    {
        lock (_gate)
        {
            var todo = 0;
            var doing = 0;
            var done = 0;
            var overdue = 0;
            foreach (var task in _tasks.Values)
            {
                switch (task.State)
                {
                    case TaskState.Todo:
                        todo++;
                        break;
                    case TaskState.Doing:
                        doing++;
                        break;
                    default:
                        done++;
                        break;
                }

                if (task.State != TaskState.Done && task.DueDate.HasValue && task.DueDate.Value < today)
                    overdue++;
            }

            return new TaskCounts(todo, doing, done, overdue);
        }
    }

    /// <inheritdoc />
    public HaftKhanTask? FindByUid(string uid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uid);
        lock (_gate)
        {
            return _tasks.Values.FirstOrDefault(task => task.Uid == uid);
        }
    }

    /// <inheritdoc />
    public HaftKhanTask Upsert(HaftKhanTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (_gate)
        {
            var uid = string.IsNullOrEmpty(task.Uid) ? Backup.NewUid() : task.Uid;
            var existing = _tasks.Values.FirstOrDefault(value => value.Uid == uid);
            var id = existing?.Id ?? _nextId++;
            if (id >= _nextId)
                _nextId = id + 1;

            var stored = task with { Id = id, Uid = uid };
            _tasks[id] = stored;
            _tags[id] = [.. task.Tags];
            return stored;
        }
    }

    /// <inheritdoc />
    public void SetDependencies(IReadOnlyList<TaskLink> links)
    {
        ArgumentNullException.ThrowIfNull(links);
        lock (_gate)
        {
            _links.Clear();
            _links.AddRange(links.Distinct());
        }
    }

    /// <inheritdoc />
    public void AddDependency(long taskId, long dependsOnId)
    {
        if (taskId == dependsOnId)
            throw new ArgumentException("A task cannot block itself.", nameof(dependsOnId));

        lock (_gate)
        {
            if (!_tasks.ContainsKey(taskId) || !_tasks.ContainsKey(dependsOnId))
                throw new ArgumentException($"Both tasks must exist to link {taskId} → {dependsOnId}.");

            var link = new TaskLink(taskId, dependsOnId);
            if (!_links.Contains(link))
                _links.Add(link);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<TaskLink> ListDependencies()
    {
        lock (_gate)
        {
            return [.. _links];
        }
    }

    /// <inheritdoc />
    public void RemoveDependenciesFor(long taskId)
    {
        lock (_gate)
        {
            _links.RemoveAll(link => link.TaskId == taskId || link.DependsOnId == taskId);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetTags(long taskId)
    {
        lock (_gate)
        {
            return _tags.TryGetValue(taskId, out var tags) ? [.. tags] : [];
        }
    }

    /// <inheritdoc />
    public void PushUndo(UndoSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            _undo.Add(ToDto(snapshot));
            if (_undo.Count > _undoDepth)
                _undo.RemoveRange(0, _undo.Count - _undoDepth);
        }
    }

    /// <inheritdoc />
    public UndoResult Undo()
    {
        lock (_gate)
        {
            if (_undo.Count == 0)
                throw new InvalidOperationException("Nothing to undo.");

            var snapshot = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            Apply(snapshot);
            return new UndoResult(snapshot.Operation, snapshot.Tasks.Count + snapshot.CreatedTaskIds.Count);
        }
    }

    private void Apply(SnapshotDto snapshot)
    {
        if (snapshot.ReplaceAll)
        {
            foreach (var id in _tasks.Keys.ToList())
            {
                Remove(id);
            }
        }

        foreach (var createdId in snapshot.CreatedTaskIds)
        {
            Remove(createdId);
        }

        foreach (var dto in snapshot.Tasks)
        {
            var task = Backup.FromDto(dto);
            Remove(task.Id);
            _tasks[task.Id] = task;
            _tags[task.Id] = [.. dto.Tags];
            if (task.Id >= _nextId)
                _nextId = task.Id + 1;
        }

        var touchedIds = snapshot.Tasks.Select(dto => Backup.FromDto(dto).Id)
            .Concat(snapshot.CreatedTaskIds)
            .ToHashSet();
        _links.RemoveAll(link => touchedIds.Contains(link.TaskId) || touchedIds.Contains(link.DependsOnId));
        foreach (var dependency in snapshot.Dependencies)
        {
            var link = new TaskLink(dependency.TaskId, dependency.DependsOnId);
            if (!_links.Contains(link))
                _links.Add(link);
        }
    }

    private static SnapshotDto ToDto(UndoSnapshot snapshot) => new(
        snapshot.Operation,
        [.. snapshot.Tasks.Select(Backup.ToDto)],
        snapshot.Tags.ToDictionary(pair => pair.Key, pair => pair.Value.ToList()),
        [.. snapshot.Dependencies.Select(link => new Backup.DependencyDto(link.TaskId, link.DependsOnId))],
        [.. snapshot.CreatedTaskIds],
        snapshot.ReplaceAll);
}
