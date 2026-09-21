namespace JameJam.Raz;

/// <summary>
/// Persistent storage for the Raz vault. The store only ever sees <em>ciphertext</em> for
/// sensitive fields (the service encrypts before calling) plus plaintext bookkeeping, and
/// keeps an undo stack of encrypted snapshots.
/// </summary>
public interface IVaultStore
{
    /// <summary>True when the vault has been initialized (meta rows exist).</summary>
    bool IsInitialized { get; }

    /// <summary>Sets the vault meta rows (salt, iterations, key check) — init only.</summary>
    void SetMeta(byte[] salt, int iterations, byte[] keyCheck);

    /// <summary>Reads the stored salt, or null when uninitialized.</summary>
    byte[]? GetSalt();

    /// <summary>Reads the stored iteration count, or 0 when uninitialized.</summary>
    int GetIterations();

    /// <summary>Reads the encrypted key-check payload, or null when uninitialized.</summary>
    byte[]? GetKeyCheck();

    /// <summary>Inserts an entry and returns it with its assigned id.</summary>
    RazEntry AddEntry(RazEntry entry);

    /// <summary>Overwrites an existing entry (same id).</summary>
    void UpdateEntry(RazEntry entry);

    /// <summary>Removes an entry. Returns true when it existed.</summary>
    bool RemoveEntry(long id);

    /// <summary>Gets an entry by id, or null.</summary>
    RazEntry? FindEntry(long id);

    /// <summary>Lists all entries ordered by id.</summary>
    IReadOnlyList<RazEntry> ListEntries();

    /// <summary>Replaces every entry with the given ones (undo restore); ids are preserved.</summary>
    void ReplaceEntries(IReadOnlyList<RazEntry> entries);

    /// <summary>How many entries are stored.</summary>
    int Count();

    /// <summary>Pushes an undo snapshot (an encrypted payload produced by the service).</summary>
    void PushUndo(byte[] payload);

    /// <summary>Pops the most recent undo snapshot, or null when the stack is empty.</summary>
    byte[]? PopUndo();

    /// <summary>How many undo snapshots are currently stacked.</summary>
    int UndoCount { get; }

    /// <summary>
    /// Maximum number of undo snapshots kept. Pushing beyond the depth drops the oldest
    /// snapshot; defaults to <see cref="RazDefaults.UndoDepth"/>. Values below zero are rejected.
    /// </summary>
    int UndoDepth { get; set; }
}
