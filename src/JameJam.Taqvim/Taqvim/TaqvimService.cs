using System.Globalization;
using System.Text.Json;

namespace JameJam.Taqvim;

/// <summary>
/// Business logic for the Taqvim calendar: event CRUD with rails, occurrence expansion,
/// agendas, conflict detection, free-slot finding, ICS import/export, undo, and stats.
/// All mutations push an undo snapshot first — one <c>taqvim undo</c> reverts anything.
/// </summary>
/// <param name="store">Event storage.</param>
/// <param name="clock">Time source (injected for testability).</param>
/// <param name="options">Customizable rails; defaults apply when null.</param>
public sealed class TaqvimService(
    ITaqvimStore store,
    TimeProvider clock,
    TaqvimOptions? options = null)
{
    /// <summary>The validated options this service runs under.</summary>
    public TaqvimOptions Options { get; } = TaqvimOptions.CreateValidated(options);

    private readonly ITaqvimStore _store = Wire(store, options);
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>The culture used for snapshot serialization (invariant everywhere).</summary>
    private CultureInfo Culture { get; } = CultureInfo.InvariantCulture;

    // The undo depth is an option, but the trimming lives in the store — wire them once, here.
    private static ITaqvimStore Wire(ITaqvimStore store, TaqvimOptions? options)
    {
        ArgumentNullException.ThrowIfNull(store);
        store.UndoDepth = TaqvimOptions.CreateValidated(options).UndoDepth;
        return store;
    }

    // ── CRUD ──

    /// <summary>Creates an event. Reminders are cleaned; the rule is carried as given.</summary>
    public TaqvimEvent AddEvent(
        string title,
        DateTimeOffset start,
        DateTimeOffset end,
        bool allDay = false,
        string? calendar = null,
        string? location = null,
        string notes = "",
        string tags = "",
        Recurrence? rule = null,
        IReadOnlyList<int>? reminders = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (_store.ListEvents().Count >= Options.MaxEvents)
        {
            throw new TaqvimException($"At most {Options.MaxEvents} events are allowed.");
        }

        ValidateWindow(start, end, allDay);

        PushSnapshot();
        var now = _clock.GetUtcNow();
        return _store.AddEvent(new TaqvimEvent(
            0,
            calendar is { Length: > 0 } namedCalendar ? TaqvimText.Clip(namedCalendar, TaqvimDefaults.MaxLocationLength) : TaqvimDefaults.DefaultCalendar,
            TaqvimText.Clip(title, Options.MaxTitleLength),
            location is null ? string.Empty : TaqvimText.Clip(location, TaqvimDefaults.MaxLocationLength),
            TaqvimText.Clip(notes, Options.MaxNotesLength),
            TaqvimText.CleanTags(tags, TaqvimDefaults.MaxTagsPerEvent, TaqvimDefaults.MaxTagLength, TaqvimDefaults.MaxTagsLength),
            start,
            end,
            allDay,
            ValidateRule(rule),
            CleanReminders(reminders),
            now,
            now));
    }

    /// <summary>
    /// Edits the immutable parts of an event; null keeps the current value. Times move via
    /// <see cref="Reschedule"/> (or the start/end pair here, which also validates).
    /// </summary>
    public TaqvimEvent EditEvent(
        long id,
        string? title = null,
        string? location = null,
        string? notes = null,
        string? tags = null,
        string? calendar = null,
        DateTimeOffset? start = null,
        DateTimeOffset? end = null,
        Recurrence? rule = null,
        bool clearRule = false,
        IReadOnlyList<int>? reminders = null,
        bool clearReminders = false,
        bool? allDay = null)
    {
        var ev = Require(id);
        PushSnapshot();
        var nextStart = start ?? ev.Start;
        var nextEnd = end ?? (start is null ? ev.End : start.Value + (ev.End - ev.Start));
        var nextAllDay = allDay ?? ev.IsAllDay;
        ValidateWindow(nextStart, nextEnd, nextAllDay);
        var reminders2 = clearReminders ? [] : CleanReminders(reminders ?? ev.Reminders);
        var updated = ev with
        {
            Title = title is null ? ev.Title : TaqvimText.Clip(title, Options.MaxTitleLength),
            Location = location is null ? ev.Location : TaqvimText.Clip(location, TaqvimDefaults.MaxLocationLength),
            Notes = notes is null ? ev.Notes : TaqvimText.Clip(notes, Options.MaxNotesLength),
            Tags = tags is null ? ev.Tags : TaqvimText.CleanTags(tags, TaqvimDefaults.MaxTagsPerEvent, TaqvimDefaults.MaxTagLength, TaqvimDefaults.MaxTagsLength),
            Calendar = calendar is null
                ? ev.Calendar
                : TaqvimText.Clip(calendar, TaqvimDefaults.MaxLocationLength) is { Length: > 0 } named ? named : ev.Calendar,
            Start = nextStart,
            End = nextEnd,
            IsAllDay = nextAllDay,
            Rule = clearRule ? null : (rule is null ? ev.Rule : ValidateRule(rule)),
            Reminders = reminders2,
            UpdatedAt = _clock.GetUtcNow(),
        };
        _store.UpdateEvent(updated);
        return updated;
    }

    /// <summary>Moves an event to a new start (keeping its duration); all-day events land at day start.</summary>
    public TaqvimEvent Reschedule(long id, DateTimeOffset newStart, DateTimeOffset? newEnd = null)
    {
        var ev = Require(id);
        var duration = ev.End - ev.Start;
        var end = newEnd ?? newStart + duration;
        return EditEvent(id, start: newStart, end: end);
    }

    /// <summary>Sets (or clears, with null) the repeat rule of an event.</summary>
    public TaqvimEvent SetRule(long id, Recurrence? rule)
    {
        var ev = Require(id);
        PushSnapshot();
        var updated = ev with { Rule = rule is null ? null : ValidateRule(rule), UpdatedAt = _clock.GetUtcNow() };
        _store.UpdateEvent(updated);
        return updated;
    }

    /// <summary>Sets (or clears, with an empty list) the reminders of an event.</summary>
    public TaqvimEvent SetReminders(long id, IReadOnlyList<int> reminders)
    {
        var ev = Require(id);
        PushSnapshot();
        var updated = ev with { Reminders = CleanReminders(reminders), UpdatedAt = _clock.GetUtcNow() };
        _store.UpdateEvent(updated);
        return updated;
    }

    /// <summary>Deletes an event (a tombstone records the deletion; undo brings it back).</summary>
    public TaqvimEvent Delete(long id)
    {
        var ev = Require(id);
        PushSnapshot();
        _ = _store.RemoveEvent(id, _clock.GetUtcNow());
        return ev;
    }

    /// <summary>Gets one event, or null.</summary>
    public TaqvimEvent? Get(long id) => _store.FindEvent(id);

    /// <summary>Lists the stored masters (not occurrences).</summary>
    public IReadOnlyList<TaqvimEvent> All() => _store.ListEvents();

    // ── Agenda & windows ──

    /// <summary>Expands every event into occurrences that overlap the window (bounded by the day rail).</summary>
    public IReadOnlyList<Occurrence> Occurrences(DateTimeOffset windowStart, DateTimeOffset windowEnd)
    {
        if (windowEnd < windowStart)
        {
            (windowStart, windowEnd) = (windowEnd, windowStart);
        }

        if (windowEnd - windowStart > TimeSpan.FromDays(Options.MaxAgendaDays))
        {
            windowEnd = windowStart.AddDays(Options.MaxAgendaDays);
        }

        List<Occurrence> occurrences = [];
        foreach (var ev in _store.ListEvents())
        {
            occurrences.AddRange(Recurrences.Occurrences(ev, windowStart, windowEnd));
        }

        return [.. occurrences.OrderBy(o => o.Start).ThenBy(o => o.Event.Title, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Agenda for one calendar day (local time).</summary>
    public IReadOnlyList<Occurrence> Day(DateOnly day)
    {
        var start = TaqvimText.ToLocalInstant(day.ToDateTime(TimeOnly.MinValue));
        return Occurrences(start, start.AddDays(1));
    }

    /// <summary>Agenda for the local week (Monday-anchored) containing the given day.</summary>
    public IReadOnlyList<Occurrence> Week(DateOnly day)
    {
        var offset = ((int)day.DayOfWeek + 6) % 7; // Monday = 0
        var monday = day.AddDays(-offset);
        var start = TaqvimText.ToLocalInstant(monday.ToDateTime(TimeOnly.MinValue));
        return Occurrences(start, start.AddDays(7));
    }

    /// <summary>Agenda for one local month.</summary>
    public IReadOnlyList<Occurrence> Month(int year, int month)
    {
        if (month is < 1 or > 12)
        {
            throw new TaqvimException("Month must be between 1 and 12.");
        }

        var start = TaqvimText.ToLocalInstant(new DateTime(year, month, 1));
        return Occurrences(start, start.AddMonths(1));
    }

    /// <summary>The next events from now (across all events), at most <paramref name="take"/>.</summary>
    public IReadOnlyList<Occurrence> Upcoming(int take, string? calendar = null, string? tag = null)
    {
        take = Math.Clamp(take, 1, 100);
        var now = _clock.GetUtcNow();
        return [.. Occurrences(now, now.AddDays(Options.MaxAgendaDays))
            .Where(o => calendar is null || o.Event.Calendar.Equals(calendar, StringComparison.OrdinalIgnoreCase))
            .Where(o => tag is null || TaqvimText.TagsOf(o.Event).Contains(tag, StringComparer.OrdinalIgnoreCase))
            .Take(take)];
    }

    /// <summary>Events (masters) matching a full-text query.</summary>
    public IReadOnlyList<TaqvimEvent> Search(string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var ids = new HashSet<long>(_store.SearchIds(query, Options.SearchLimit));
        return [.. _store.ListEvents().Where(e => ids.Contains(e.Id))];
    }

    /// <summary>Finds conflicts — overlapping occurrences of different events — inside the window.</summary>
    public IReadOnlyList<Conflict> Conflicts(DateTimeOffset windowStart, DateTimeOffset windowEnd)
    {
        var busy = Occurrences(windowStart, windowEnd)
            .Where(o => !o.Event.IsAllDay)
            .OrderBy(o => o.Start)
            .ToList();
        List<Conflict> found = [];
        for (var i = 1; i < busy.Count; i++)
        {
            var previous = busy[i - 1];
            var current = busy[i];
            if (current.Start < previous.End && previous.Event.Id != current.Event.Id)
            {
                found.Add(new Conflict(previous, current));
            }
        }

        return found;
    }

    /// <summary>
    /// Finds free slots of at least <paramref name="minMinutes"/> inside the window
    /// (between 00:00 and 24:00 local on the given day).
    /// </summary>
    public IReadOnlyList<FreeSlot> FreeSlots(DateOnly day, TimeOnly from, TimeOnly to, int minMinutes)
    {
        if (minMinutes is < 1 or > TaqvimDefaults.MaxFreeSlotMinutes)
        {
            throw new TaqvimException(
                $"Minimum slot length must be between 1 and {TaqvimDefaults.MaxFreeSlotMinutes} minutes.");
        }

        if (to <= from)
        {
            throw new TaqvimException("The free-window end must be after its start.");
        }

        var windowStart = TaqvimText.ToLocalInstant(day.ToDateTime(TimeOnly.MinValue));
        var windowEnd = windowStart.AddDays(1);
        var clipStart = windowStart + from.ToTimeSpan();
        var clipEnd = windowStart + to.ToTimeSpan();

        List<(DateTimeOffset Start, DateTimeOffset End)> busy = [];
        foreach (var occurrence in Occurrences(windowStart, windowEnd))
        {
            if (occurrence.End <= clipStart || occurrence.Start >= clipEnd)
            {
                continue;
            }

            busy.Add((occurrence.Start > clipStart ? occurrence.Start : clipStart,
                occurrence.End < clipEnd ? occurrence.End : clipEnd));
        }

        busy.Sort((a, b) => a.Start.CompareTo(b.Start));

        List<FreeSlot> slots = [];
        var cursor = clipStart;
        foreach (var (start, end) in busy)
        {
            if (start > cursor && (start - cursor).TotalMinutes >= minMinutes)
            {
                slots.Add(new FreeSlot(cursor, start));
            }

            if (end > cursor)
            {
                cursor = end;
            }
        }

        if (clipEnd > cursor && (clipEnd - cursor).TotalMinutes >= minMinutes)
        {
            slots.Add(new FreeSlot(cursor, clipEnd));
        }

        return slots;
    }

    /// <summary>Aggregate numbers for the stats view.</summary>
    public TaqvimStats Stats()
    {
        var events = _store.ListEvents();
        var now = _clock.GetUtcNow();
        var soon = Occurrences(now, now.AddDays(7));
        return new TaqvimStats(
            events.Count,
            events.Count(e => e.Rule is { Kind: not RecurrenceKind.Once }),
            events.Count(e => e.IsAllDay),
            events.Count(e => e.Tags.Length > 0),
            events.Sum(e => e.Reminders.Count),
            soon.Count,
            (int)soon.Sum(o => o.Duration.TotalMinutes));
    }

    // ── Undo & snapshots ──

    /// <summary>Reverts the most recent mutation. Returns false when the stack is empty.</summary>
    public bool Undo()
    {
        var payload = _store.PopUndo();
        if (payload is null)
        {
            return false;
        }

        TaqvimSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<TaqvimSnapshot>(payload, TaqvimJson.Options);
        }
        catch (JsonException)
        {
            throw new TaqvimException("The undo snapshot is unreadable.");
        }

        if (snapshot is null)
        {
            return false;
        }

        _store.ReplaceEvents([.. snapshot.Events.Select(dto => TaqvimJson.FromDto(dto, Culture))]);
        return true;
    }

    /// <summary>Pushes a whole-calendar undo snapshot — sync calls this once before applying a merge.</summary>
    public void PushUndoSnapshot() => PushSnapshot();

    private void PushSnapshot()
    {
        var snapshot = new TaqvimSnapshot([.. _store.ListEvents().Select(e => TaqvimJson.ToDto(e, Culture))]);
        _store.PushUndo(JsonSerializer.Serialize(snapshot, TaqvimJson.Options));
    }

    // ── Internals ──

    private TaqvimEvent Require(long id) =>
        _store.FindEvent(id) ?? throw new TaqvimException($"No event #{id}.");

    private void ValidateWindow(DateTimeOffset start, DateTimeOffset end, bool allDay)
    {
        if (allDay)
        {
            var day = start;
            var nextDay = end;
            if (day.UtcDateTime.Date >= nextDay.UtcDateTime.Date)
            {
                throw new TaqvimException("An all-day event must end on a later day than it starts.");
            }

            return;
        }

        if (end <= start)
        {
            throw new TaqvimException("The end must be after the start.");
        }

        if (end - start > TimeSpan.FromDays(Options.MaxAgendaDays))
        {
            throw new TaqvimException($"An event may span at most {Options.MaxAgendaDays} days.");
        }

        var now = _clock.GetUtcNow();
        if (start > now.AddYears(Options.MaxScheduleHorizonYears))
        {
            throw new TaqvimException(
                $"Events may be scheduled at most {Options.MaxScheduleHorizonYears} years ahead.");
        }

        if (end < now.AddYears(-Options.MaxScheduleHorizonYears))
        {
            throw new TaqvimException(
                $"Events may not be scheduled more than {Options.MaxScheduleHorizonYears} years in the past.");
        }
    }

    private static Recurrence? ValidateRule(Recurrence? rule)
    {
        if (rule is null || rule.Kind == RecurrenceKind.Once)
        {
            return rule;
        }

        if (rule.Interval is < 1 or > TaqvimDefaults.MaxRecurrenceInterval)
        {
            throw new TaqvimException(
                $"Recurrence interval must be between 1 and {TaqvimDefaults.MaxRecurrenceInterval}.");
        }

        if (rule.Count is { } count && count is < 1 or > TaqvimDefaults.MaxRecurrenceCount)
        {
            throw new TaqvimException(
                $"Recurrence count must be between 1 and {TaqvimDefaults.MaxRecurrenceCount}.");
        }

        if (rule.Weekdays.Count > 6 && rule.Kind == RecurrenceKind.Weekly)
        {
            throw new TaqvimException("A weekly rule needs at most six distinct weekdays (all seven means daily).");
        }

        return rule;
    }

    private IReadOnlyList<int> CleanReminders(IReadOnlyList<int>? reminders)
    {
        if (reminders is null || reminders.Count == 0)
        {
            return [];
        }

        SortedSet<int> kept = [];
        foreach (var minutes in reminders)
        {
            if (minutes is < 0 || minutes > TaqvimDefaults.MaxReminderMinutes)
            {
                throw new TaqvimException(
                    $"Reminders must be between 0 and {TaqvimDefaults.MaxReminderMinutes} minutes before the event.");
            }

            _ = kept.Add(minutes);
            if (kept.Count > Options.MaxRemindersPerEvent)
            {
                throw new TaqvimException($"At most {Options.MaxRemindersPerEvent} reminders per event.");
            }
        }

        return [.. kept];
    }
}
