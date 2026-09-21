using System.Globalization;
using System.Text.Json;

namespace JameJam.Taqvim;

/// <summary>In-memory <see cref="ITaqvimStore"/> for tests and non-persistent contexts.</summary>
public sealed class MemoryTaqvimStore : ITaqvimStore
{
    private readonly List<TaqvimEvent> _events = [];
    private readonly List<string> _undo = [];
    private readonly Dictionary<Guid, DateTimeOffset> _tombstones = [];
    private long _nextEventId = 1;

    /// <inheritdoc />
    public int UndoDepth
    {
        get => _undoDepth;
        set => _undoDepth = value >= 0 ? value : throw new TaqvimException("UndoDepth must not be negative.");
    }

    private int _undoDepth = TaqvimDefaults.UndoDepth;

    /// <inheritdoc />
    public TaqvimEvent AddEvent(TaqvimEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);
        var withIdentity = ev with
        {
            Id = _nextEventId++,
            SyncId = ev.SyncId == Guid.Empty ? Guid.CreateVersion7() : ev.SyncId,
        };
        _events.Add(withIdentity);
        return withIdentity;
    }

    /// <inheritdoc />
    public void UpdateEvent(TaqvimEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);
        var index = _events.FindIndex(e => e.Id == ev.Id);
        if (index < 0)
        {
            throw new TaqvimException($"No event #{ev.Id}.");
        }

        _events[index] = ev;
    }

    /// <inheritdoc />
    public bool RemoveEvent(long id, DateTimeOffset deletedAt)
    {
        var ev = _events.FirstOrDefault(e => e.Id == id);
        if (ev is null)
        {
            return false;
        }

        if (ev.SyncId != Guid.Empty)
        {
            _tombstones[ev.SyncId] = deletedAt;
        }

        return _events.RemoveAll(e => e.Id == id) > 0;
    }

    /// <inheritdoc />
    public TaqvimEvent? FindEvent(long id) => _events.FirstOrDefault(e => e.Id == id);

    /// <inheritdoc />
    public IReadOnlyList<TaqvimEvent> ListEvents() => [.. _events.OrderBy(e => e.Start).ThenBy(e => e.Id)];

    /// <inheritdoc />
    public IReadOnlyList<long> SearchIds(string query, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<(long Id, int Score)> scored = [];
        foreach (var ev in _events)
        {
            var score = 0;
            var all = true;
            foreach (var term in terms)
            {
                var inTitle = ev.Title.Contains(term, StringComparison.OrdinalIgnoreCase);
                var inBody = ev.Notes.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || ev.Location.Contains(term, StringComparison.OrdinalIgnoreCase);
                if (!inTitle && !inBody)
                {
                    all = false;
                    break;
                }

                score += inTitle ? 2 : 1;
            }

            if (all)
            {
                scored.Add((ev.Id, score));
            }
        }

        return [.. scored
            .OrderByDescending(s => s.Score)
            .ThenByDescending(s => _events.First(e => e.Id == s.Id).UpdatedAt)
            .Take(limit)
            .Select(s => s.Id)];
    }

    /// <inheritdoc />
    public void ReplaceEvents(IReadOnlyList<TaqvimEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        _events.Clear();
        _events.AddRange(events.OrderBy(e => e.Start).ThenBy(e => e.Id));
        _nextEventId = _events.Count == 0 ? 1 : _events.Max(e => e.Id) + 1;
        foreach (var syncId in _events.Select(e => e.SyncId).Where(id => id != Guid.Empty))
        {
            _ = _tombstones.Remove(syncId); // restored events retract their tombstones
        }
    }

    /// <inheritdoc />
    public void PushUndo(string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        _undo.Add(payload);
        while (_undo.Count > Math.Max(UndoDepth, 0))
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
    public IReadOnlyList<TaqvimTombstone> GetTombstones() =>
        [.. _tombstones
            .Where(t => _events.All(e => e.SyncId != t.Key))
            .OrderBy(t => t.Key)
            .Select(t => new TaqvimTombstone(t.Key, t.Value))];

    /// <inheritdoc />
    public void UpsertTombstone(TaqvimTombstone tombstone)
    {
        ArgumentNullException.ThrowIfNull(tombstone);
        if (tombstone.SyncId == Guid.Empty)
        {
            return;
        }

        _tombstones[tombstone.SyncId] = tombstone.DeletedAt;
    }
}

/// <summary>Wire/undo DTO for one event.</summary>
/// <param name="Id">Local id.</param>
/// <param name="Calendar">Grouping calendar.</param>
/// <param name="Title">Title.</param>
/// <param name="Location">Location.</param>
/// <param name="Notes">Notes.</param>
/// <param name="Tags">Tags.</param>
/// <param name="Start">Start (round-trip string).</param>
/// <param name="End">End (round-trip string).</param>
/// <param name="IsAllDay">All-day flag.</param>
/// <param name="Rule">The rule (null for one-offs).</param>
/// <param name="Reminders">Reminder offsets.</param>
/// <param name="CreatedAt">Created (round-trip string).</param>
/// <param name="UpdatedAt">Updated (round-trip string).</param>
/// <param name="SyncId">Cross-device identity.</param>
internal sealed record EventDto(
    long Id,
    string Calendar,
    string Title,
    string Location,
    string Notes,
    string Tags,
    string Start,
    string End,
    bool IsAllDay,
    Recurrence? Rule,
    IReadOnlyList<int> Reminders,
    string CreatedAt,
    string UpdatedAt,
    Guid SyncId = default);

/// <summary>Undo snapshot: the whole event list.</summary>
/// <param name="Events">All events at snapshot time.</param>
internal sealed record TaqvimSnapshot(IReadOnlyList<EventDto> Events);

/// <summary>JSON helpers shared by the undo stack (service-owned snapshots).</summary>
internal static class TaqvimJson
{
    /// <summary>The shared serializer options (cached per CA1869).</summary>
    public static readonly JsonSerializerOptions Options = new();

    /// <summary>Converts an event to its snapshot DTO.</summary>
    public static EventDto ToDto(TaqvimEvent ev, CultureInfo culture) => new(
        ev.Id, ev.Calendar, ev.Title, ev.Location, ev.Notes, ev.Tags,
        ev.Start.ToString("O", culture), ev.End.ToString("O", culture), ev.IsAllDay,
        ev.Rule, ev.Reminders,
        ev.CreatedAt.ToString("O", culture), ev.UpdatedAt.ToString("O", culture), ev.SyncId);

    /// <summary>Converts a snapshot DTO back into an event.</summary>
    public static TaqvimEvent FromDto(EventDto dto, CultureInfo culture) => new(
        dto.Id, dto.Calendar, dto.Title, dto.Location, dto.Notes, dto.Tags,
        DateTimeOffset.Parse(dto.Start, culture, DateTimeStyles.RoundtripKind),
        DateTimeOffset.Parse(dto.End, culture, DateTimeStyles.RoundtripKind),
        dto.IsAllDay, dto.Rule, dto.Reminders,
        DateTimeOffset.Parse(dto.CreatedAt, culture, DateTimeStyles.RoundtripKind),
        DateTimeOffset.Parse(dto.UpdatedAt, culture, DateTimeStyles.RoundtripKind),
        dto.SyncId);
}
