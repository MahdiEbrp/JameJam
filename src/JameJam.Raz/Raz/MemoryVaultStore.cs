namespace JameJam.Raz;

/// <summary>In-memory <see cref="IVaultStore"/> for tests and non-persistent contexts.</summary>
public sealed class MemoryVaultStore : IVaultStore
{
    private readonly List<RazEntry> _entries = [];
    private readonly List<byte[]> _undo = [];
    private byte[]? _salt;
    private int _iterations;
    private byte[]? _keyCheck;
    private long _nextId = 1;

    /// <inheritdoc />
    public bool IsInitialized => _salt is not null;

    /// <inheritdoc />
    public void SetMeta(byte[] salt, int iterations, byte[] keyCheck)
    {
        _salt = [.. salt];
        _iterations = iterations;
        _keyCheck = [.. keyCheck];
    }

    /// <inheritdoc />
    public byte[]? GetSalt() => _salt is null ? null : [.. _salt];

    /// <inheritdoc />
    public int GetIterations() => _iterations;

    /// <inheritdoc />
    public byte[]? GetKeyCheck() => _keyCheck is null ? null : [.. _keyCheck];

    /// <inheritdoc />
    public RazEntry AddEntry(RazEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var withId = entry with { Id = _nextId++ };
        _entries.Add(withId);
        return withId;
    }

    /// <inheritdoc />
    public void UpdateEntry(RazEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var index = _entries.FindIndex(e => e.Id == entry.Id);
        if (index < 0)
        {
            throw new RazException($"No entry #{entry.Id}.");
        }

        _entries[index] = entry;
    }

    /// <inheritdoc />
    public bool RemoveEntry(long id) => _entries.RemoveAll(e => e.Id == id) > 0;

    /// <inheritdoc />
    public RazEntry? FindEntry(long id) => _entries.FirstOrDefault(e => e.Id == id);

    /// <inheritdoc />
    public IReadOnlyList<RazEntry> ListEntries() => [.. _entries.OrderBy(e => e.Id)];

    /// <inheritdoc />
    public void ReplaceEntries(IReadOnlyList<RazEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries.Clear();
        _entries.AddRange(entries.OrderBy(e => e.Id));
        _nextId = _entries.Count == 0 ? 1 : _entries.Max(e => e.Id) + 1;
    }

    /// <inheritdoc />
    public int Count() => _entries.Count;

    /// <inheritdoc />
    public void PushUndo(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        _undo.Add([.. payload]);
        while (_undo.Count > _undoDepth)
        {
            _undo.RemoveAt(0);
        }
    }

    /// <inheritdoc />
    public byte[]? PopUndo()
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

    private int _undoDepth = RazDefaults.UndoDepth;
}
