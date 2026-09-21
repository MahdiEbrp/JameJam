namespace JameJam.Divan;

/// <summary>In-memory <see cref="IDivanStore"/> for tests and non-persistent contexts.</summary>
public sealed class MemoryDivanStore : IDivanStore
{
    private readonly List<Notebook> _notebooks = [];
    private readonly List<Note> _notes = [];
    private readonly List<string> _undo = [];
    private readonly Dictionary<Guid, DateTimeOffset> _tombstones = [];
    private long _nextNotebookId = 1;
    private long _nextNoteId = 1;

    /// <inheritdoc />
    public Notebook AddNotebook(Notebook notebook)
    {
        ArgumentNullException.ThrowIfNull(notebook);
        var withId = notebook with { Id = _nextNotebookId++ };
        _notebooks.Add(withId);
        return withId;
    }

    /// <inheritdoc />
    public void UpdateNotebook(Notebook notebook)
    {
        ArgumentNullException.ThrowIfNull(notebook);
        var index = _notebooks.FindIndex(n => n.Id == notebook.Id);
        if (index < 0)
        {
            throw new DivanException($"No notebook #{notebook.Id}.");
        }

        _notebooks[index] = notebook;
    }

    /// <inheritdoc />
    public bool RemoveNotebook(long id) => _notebooks.RemoveAll(n => n.Id == id) > 0;

    /// <inheritdoc />
    public Notebook? FindNotebook(long id) => _notebooks.FirstOrDefault(n => n.Id == id);

    /// <inheritdoc />
    public Notebook? FindNotebookByName(string name) =>
        _notebooks.FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public IReadOnlyList<Notebook> ListNotebooks() => [.. _notebooks.OrderBy(n => n.Id)];

    /// <inheritdoc />
    public Note AddNote(Note note)
    {
        ArgumentNullException.ThrowIfNull(note);
        var withId = note with
        {
            Id = _nextNoteId++,
            SyncId = note.SyncId == Guid.Empty ? Guid.CreateVersion7() : note.SyncId,
        };
        _notes.Add(withId);
        return withId;
    }

    /// <inheritdoc />
    public void UpdateNote(Note note)
    {
        ArgumentNullException.ThrowIfNull(note);
        var index = _notes.FindIndex(n => n.Id == note.Id);
        if (index < 0)
        {
            throw new DivanException($"No note #{note.Id}.");
        }

        _notes[index] = note;
    }

    /// <inheritdoc />
    public bool RemoveNote(long id, DateTimeOffset deletedAt)
    {
        var note = _notes.FirstOrDefault(n => n.Id == id);
        if (note is null)
        {
            return false;
        }

        if (note.SyncId != Guid.Empty)
        {
            _tombstones[note.SyncId] = deletedAt; // live deletions are visible to other devices
        }

        return _notes.RemoveAll(n => n.Id == id) > 0;
    }

    /// <inheritdoc />
    public void UpsertTombstone(DivanTombstone tombstone)
    {
        ArgumentNullException.ThrowIfNull(tombstone);
        if (tombstone.SyncId == Guid.Empty)
        {
            return;
        }

        _tombstones[tombstone.SyncId] = tombstone.DeletedAt;
    }

    /// <inheritdoc />
    public IReadOnlyList<DivanTombstone> GetTombstones() =>
        [.. _tombstones
            .Where(t => _notes.All(n => n.SyncId != t.Key))
            .OrderBy(t => t.Value)
            .Select(t => new DivanTombstone(t.Key, t.Value))];

    /// <inheritdoc />
    public Note? FindNote(long id) => _notes.FirstOrDefault(n => n.Id == id);

    /// <inheritdoc />
    public IReadOnlyList<Note> ListNotes() => [.. _notes.OrderBy(n => n.Id)];

    /// <inheritdoc />
    public IReadOnlyList<long> SearchIds(string query, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<(long Id, int Score)> scored = [];
        foreach (var note in _notes)
        {
            var score = 0;
            var all = true;
            foreach (var term in terms)
            {
                var inTitle = note.Title.Contains(term, StringComparison.OrdinalIgnoreCase);
                var inBody = note.Body.Contains(term, StringComparison.OrdinalIgnoreCase);
                if (!inTitle && !inBody)
                {
                    all = false;
                    break;
                }

                score += inTitle ? 2 : 1;
            }

            if (all)
            {
                scored.Add((note.Id, score));
            }
        }

        return [.. scored
            .OrderByDescending(s => s.Score)
            .ThenByDescending(s => _notes.First(n => n.Id == s.Id).UpdatedAt)
            .Take(limit)
            .Select(s => s.Id)];
    }

    /// <inheritdoc />
    public void ReplaceNotes(IReadOnlyList<Note> notes)
    {
        ArgumentNullException.ThrowIfNull(notes);
        _notes.Clear();
        _notes.AddRange(notes.OrderBy(n => n.Id));
        _nextNoteId = _notes.Count == 0 ? 1 : _notes.Max(n => n.Id) + 1;
        foreach (var id in _notes.Select(n => n.SyncId).Where(id => id != Guid.Empty))
        {
            _ = _tombstones.Remove(id); // restored notes retract their tombstones
        }
    }

    /// <inheritdoc />
    public void ReplaceNotebooks(IReadOnlyList<Notebook> notebooks)
    {
        ArgumentNullException.ThrowIfNull(notebooks);
        _notebooks.Clear();
        _notebooks.AddRange(notebooks.OrderBy(n => n.Id));
        _nextNotebookId = _notebooks.Count == 0 ? 1 : _notebooks.Max(n => n.Id) + 1;
    }

    /// <inheritdoc />
    public void PushUndo(string payload)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        _undo.Add(payload);
        while (_undo.Count > _undoDepth)
        {
            _undo.RemoveAt(0);
        }
    }

    /// <inheritdoc />
    public string? PopUndo()
    {
        if (_undo.Count == 0)
        {
            return null;
        }

        var last = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        return last;
    }

    /// <inheritdoc />
    public int UndoCount => _undo.Count;

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
}
