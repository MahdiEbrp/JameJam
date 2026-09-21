namespace JameJam.Taqvim;

/// <summary>
/// Persistent storage for the Taqvim calendar: events plus an undo stack of JSON snapshots
/// (produced by the service), deletion tombstones, and delegated full-text search.
/// </summary>
public interface ITaqvimStore
{
    /// <summary>Inserts an event and returns it with its assigned id and sync identity.</summary>
    TaqvimEvent AddEvent(TaqvimEvent ev);

    /// <summary>Overwrites an existing event (same id).</summary>
    void UpdateEvent(TaqvimEvent ev);

    /// <summary>
    /// Removes an event, recording a tombstone (keyed by sync id) so other devices learn of
    /// the deletion. Returns true when it existed.
    /// </summary>
    bool RemoveEvent(long id, DateTimeOffset deletedAt);

    /// <summary>Gets an event by id, or null.</summary>
    TaqvimEvent? FindEvent(long id);

    /// <summary>Lists all events ordered by start.</summary>
    IReadOnlyList<TaqvimEvent> ListEvents();

    /// <summary>
    /// Full-text search over title, notes, and location; returns event ids best-first, at most
    /// <paramref name="limit"/>. Every term must match (AND semantics).
    /// </summary>
    IReadOnlyList<long> SearchIds(string query, int limit);

    /// <summary>Replaces every event with the given ones (undo restore); ids are preserved.</summary>
    void ReplaceEvents(IReadOnlyList<TaqvimEvent> events);

    /// <summary>Pushes an undo snapshot (a JSON payload produced by the service).</summary>
    void PushUndo(string payload);

    /// <summary>Pops the most recent undo snapshot, or null when the stack is empty.</summary>
    string? PopUndo();

    /// <summary>How many undo snapshots are currently stacked.</summary>
    int UndoCount { get; }

    /// <summary>
    /// Maximum undo snapshots kept; pushing beyond the depth drops the oldest. Defaults to
    /// <see cref="TaqvimDefaults.UndoDepth"/>; values below zero are rejected.
    /// </summary>
    int UndoDepth { get; set; }

    /// <summary>Lists live tombstones — deletions whose event does not exist locally.</summary>
    IReadOnlyList<TaqvimTombstone> GetTombstones();

    /// <summary>Records (or re-times) a tombstone so every device agrees on when a deletion happened.</summary>
    void UpsertTombstone(TaqvimTombstone tombstone);
}
