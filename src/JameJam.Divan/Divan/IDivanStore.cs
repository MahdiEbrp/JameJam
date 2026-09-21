namespace JameJam.Divan;

/// <summary>
/// Persistent storage for the Divan pad: notebooks + notes plus an undo stack of
/// JSON snapshots (produced by the service). Full-text search is delegated to the
/// store so each implementation can use its best index.
/// </summary>
public interface IDivanStore
{
    /// <summary>Inserts a notebook and returns it with its assigned id.</summary>
    Notebook AddNotebook(Notebook notebook);

    /// <summary>Overwrites an existing notebook (same id).</summary>
    void UpdateNotebook(Notebook notebook);

    /// <summary>Removes a notebook (and, via the service, its notes). Returns true when it existed.</summary>
    bool RemoveNotebook(long id);

    /// <summary>Gets a notebook by id, or null.</summary>
    Notebook? FindNotebook(long id);

    /// <summary>Gets a notebook by name (case-insensitive), or null.</summary>
    Notebook? FindNotebookByName(string name);

    /// <summary>Lists all notebooks ordered by id.</summary>
    IReadOnlyList<Notebook> ListNotebooks();

    /// <summary>Inserts a note and returns it with its assigned id.</summary>
    Note AddNote(Note note);

    /// <summary>Overwrites an existing note (same id) and refreshes search indexes.</summary>
    void UpdateNote(Note note);

    /// <summary>
    /// Removes a note and its search index row, recording a tombstone (keyed by the note's
    /// sync id) so other devices learn about the deletion. Returns true when it existed.
    /// </summary>
    bool RemoveNote(long id, DateTimeOffset deletedAt);

    /// <summary>Lists live tombstones — deletions whose note does not exist locally.</summary>
    IReadOnlyList<DivanTombstone> GetTombstones();

    /// <summary>Records (or re-times) a tombstone so every device agrees on when a deletion happened.</summary>
    void UpsertTombstone(DivanTombstone tombstone);

    /// <summary>Gets a note by id, or null.</summary>
    Note? FindNote(long id);

    /// <summary>Lists all notes ordered by id.</summary>
    IReadOnlyList<Note> ListNotes();

    /// <summary>
    /// Full-text search over title and body; returns note ids best-first, at most
    /// <paramref name="limit"/>. Every term must match (AND semantics).
    /// </summary>
    IReadOnlyList<long> SearchIds(string query, int limit);

    /// <summary>Replaces every note with the given ones (undo restore); ids are preserved.</summary>
    void ReplaceNotes(IReadOnlyList<Note> notes);

    /// <summary>Replaces every notebook with the given ones (undo restore); ids are preserved.</summary>
    void ReplaceNotebooks(IReadOnlyList<Notebook> notebooks);

    /// <summary>Pushes an undo snapshot (a JSON payload produced by the service).</summary>
    void PushUndo(string payload);

    /// <summary>Pops the most recent undo snapshot, or null when the stack is empty.</summary>
    string? PopUndo();

    /// <summary>How many undo snapshots are currently stacked.</summary>
    int UndoCount { get; }

    /// <summary>
    /// Maximum number of undo snapshots kept. Pushing beyond the depth drops the oldest
    /// snapshot; defaults to <see cref="DivanDefaults.UndoDepth"/>. Values below zero are rejected.
    /// </summary>
    int UndoDepth { get; set; }
}
