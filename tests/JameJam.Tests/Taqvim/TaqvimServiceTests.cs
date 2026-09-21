using System.Globalization;

using JameJam.Tests;
using JameJam.Taqvim;

namespace JameJam.Tests.Taqvim;

/// <summary>The service: rails, undo, windows, conflicts, free slots, stats, search.</summary>
public sealed class TaqvimServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero); // Sunday

    private readonly MemoryTaqvimStore _store = new();
    private readonly TaqvimService _service;

    public TaqvimServiceTests() => _service = new TaqvimService(_store, _clock);

    private readonly StepClock _clock = new();

    private TaqvimEvent Add(
        string title = "Lunch",
        string start = "2026-09-22T12:00:00+00:00",
        string end = "2026-09-22T13:00:00+00:00",
        bool allDay = false,
        string? calendar = null,
        string? location = null,
        string tags = "",
        Recurrence? rule = null,
        IReadOnlyList<int>? reminders = null,
        string notes = "") =>
        _service.AddEvent(title, DateTimeOffset.Parse(start, CultureInfo.InvariantCulture), DateTimeOffset.Parse(end, CultureInfo.InvariantCulture), allDay, calendar, location, notes, tags, rule, reminders);

    [Fact]
    public void Add_AssignsId_SyncId_AndDefaults()
    {
        var ev = Add(calendar: null, location: null);
        Assert.Equal(1, ev.Id);
        Assert.NotEqual(Guid.Empty, ev.SyncId);
        Assert.Equal(TaqvimDefaults.DefaultCalendar, ev.Calendar);
        Assert.Equal(Now, ev.CreatedAt);
        Assert.Equal(Now, ev.UpdatedAt);
        Assert.Empty(ev.Reminders);
    }

    [Fact]
    public void Add_TrimsTitle_AndCalendar()
    {
        var ev = _service.AddEvent("  Yoga  ", Now, Now.AddHours(1), calendar: " Health ");
        Assert.Equal("Yoga", ev.Title);
        Assert.Equal("Health", ev.Calendar);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Add_EmptyTitle_Throws(string title)
    {
        Assert.ThrowsAny<ArgumentException>(() => _service.AddEvent(title, Now, Now.AddHours(1)));
    }

    [Fact]
    public void Add_EndBeforeStart_Throws()
    {
        var ex = Assert.Throws<TaqvimException>(() => _service.AddEvent("Backwards", Now.AddHours(1), Now));
        Assert.Contains("end must be after", ex.Message);
    }

    [Fact]
    public void Add_AllDayMustEndOnLaterDay()
    {
        var day = new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero);
        Assert.Throws<TaqvimException>(() => _service.AddEvent("Same day", day, day, allDay: true));
    }

    [Fact]
    public void Add_SpanBeyondAgendaRail_Throws()
    {
        var start = new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero);
        Assert.Throws<TaqvimException>(() =>
            _service.AddEvent("Eon", start, start.AddDays(TaqvimDefaults.MaxAgendaDays + 1)));
    }

    [Fact]
    public void Add_TooFarAheadOrBehind_Throws()
    {
        var start = Now.AddYears(TaqvimDefaults.MaxScheduleHorizonYears).AddDays(1);
        Assert.Throws<TaqvimException>(() => _service.AddEvent("Far future", start, start.AddHours(1)));

        var past = Now.AddYears(-TaqvimDefaults.MaxScheduleHorizonYears).AddDays(-1);
        Assert.Throws<TaqvimException>(() => _service.AddEvent("Ancient", past, past.AddHours(1)));
    }

    [Fact]
    public void Add_PastYear_IsAllowed()
    {
        var start = Now.AddYears(-1);
        var ev = _service.AddEvent("Memory lane", start, start.AddHours(1));
        Assert.Equal("Memory lane", ev.Title);
    }

    [Fact]
    public void Add_EventCap_Throws()
    {
        var options = new TaqvimOptions { MaxEvents = 2 };
        var service = new TaqvimService(_store, new FixedTimeProvider(Now), options);
        _ = service.AddEvent("One", Now, Now.AddHours(1));
        _ = service.AddEvent("Two", Now.AddHours(1), Now.AddHours(2));
        Assert.Throws<TaqvimException>(() => service.AddEvent("Three", Now.AddHours(2), Now.AddHours(3)));
    }

    [Fact]
    public void Add_TagsAreCleaned()
    {
        var ev = Add(tags: "work, deep, work, , missing");
        Assert.Equal("work,deep,missing", ev.Tags); // "missing" is a legitimate tag word
    }

    [Fact]
    public void Add_RemindersAreSortedAndDeduplicated()
    {
        var ev = Add(reminders: [30, 10, 30, 5]);
        Assert.Equal([5, 10, 30], ev.Reminders);
    }

    [Fact]
    public void Add_ReminderOutsideRail_Throws()
    {
        Assert.Throws<TaqvimException>(() => Add(reminders: [-1]));
        Assert.Throws<TaqvimException>(() => Add(reminders: [TaqvimDefaults.MaxReminderMinutes + 1]));
    }

    [Fact]
    public void Add_TooManyReminders_Throws()
    {
        var options = new TaqvimOptions { MaxRemindersPerEvent = 2 };
        var service = new TaqvimService(_store, new FixedTimeProvider(Now), options);
        Assert.Throws<TaqvimException>(() =>
            service.AddEvent("Buzzing", Now, Now.AddHours(1), reminders: [5, 10, 15]));
    }

    [Fact]
    public void Rule_Rails()
    {
        Assert.Throws<TaqvimException>(() => Add(rule: new Recurrence(RecurrenceKind.Daily, 0)));
        Assert.Throws<TaqvimException>(() => Add(rule: new Recurrence(RecurrenceKind.Daily, TaqvimDefaults.MaxRecurrenceInterval + 1)));
        Assert.Throws<TaqvimException>(() => Add(rule: new Recurrence(RecurrenceKind.Weekly, 1, null, 0, null)));
        Assert.Throws<TaqvimException>(() => Add(rule: new Recurrence(RecurrenceKind.Weekly, 1, null, TaqvimDefaults.MaxRecurrenceCount + 1, null)));
        Assert.Throws<TaqvimException>(() => Add(rule: new Recurrence(
            RecurrenceKind.Weekly, 1,
            [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday],
            null, null)));
    }

    private sealed class StepClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = TaqvimServiceTests.Now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Edit_UpdatesOnlyGivenFields()
    {
        var ev = Add(location: "Old place", notes: "old", tags: "work");
        _clock.Now = Now.AddMinutes(5);
        var updated = _service.EditEvent(ev.Id, title: "New name", notes: "fresh notes");
        Assert.Equal("New name", updated.Title);
        Assert.Equal("Old place", updated.Location);
        Assert.Equal("fresh notes", updated.Notes);
        Assert.Equal("work", updated.Tags);
        Assert.True(updated.UpdatedAt > ev.UpdatedAt);
    }

    [Fact]
    public void Edit_StartMovesEnd_KeepingDuration_WhenEndOmitted()
    {
        var ev = Add();
        var moved = _service.EditEvent(ev.Id, start: DateTimeOffset.Parse("2026-09-23T15:00:00+00:00", CultureInfo.InvariantCulture));
        Assert.Equal(TimeSpan.FromHours(1), moved.End - moved.Start);
    }

    [Fact]
    public void Edit_EmptyCalendar_KeepsTheOldOne()
    {
        var ev = Add(calendar: "Work");
        var updated = _service.EditEvent(ev.Id, calendar: "   ");
        Assert.Equal("Work", updated.Calendar);
    }

    [Fact]
    public void Edit_UnknownId_Throws()
    {
        Assert.Throws<TaqvimException>(() => _service.EditEvent(999, title: "ghost"));
    }

    [Fact]
    public void Reschedule_KeepsDuration_AndCanSetEnd()
    {
        var ev = Add();
        var moved = _service.Reschedule(ev.Id, DateTimeOffset.Parse("2026-09-24T09:00:00+00:00", CultureInfo.InvariantCulture));
        Assert.Equal(TimeSpan.FromHours(1), moved.End - moved.Start);

        var stretched = _service.Reschedule(
            ev.Id,
            DateTimeOffset.Parse("2026-09-24T09:00:00+00:00", CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-09-24T12:00:00+00:00", CultureInfo.InvariantCulture));
        Assert.Equal(TimeSpan.FromHours(3), stretched.End - stretched.Start);
    }

    [Fact]
    public void SetRule_And_Clear()
    {
        var ev = Add();
        var weekly = _service.SetRule(ev.Id, new Recurrence(RecurrenceKind.Weekly));
        Assert.Equal(RecurrenceKind.Weekly, weekly.Rule!.Kind);

        var cleared = _service.SetRule(ev.Id, null);
        Assert.Null(cleared.Rule);
    }

    [Fact]
    public void SetReminders_And_Clear()
    {
        var ev = Add();
        var set = _service.SetReminders(ev.Id, [45, 15]);
        Assert.Equal([15, 45], set.Reminders);

        var cleared = _service.SetReminders(ev.Id, []);
        Assert.Empty(cleared.Reminders);
    }

    [Fact]
    public void Delete_Removes_AndUndoRestores()
    {
        var ev = Add();
        var deleted = _service.Delete(ev.Id);
        Assert.Equal(ev.Id, deleted.Id);
        Assert.Null(_service.Get(ev.Id));

        Assert.True(_service.Undo());
        var restored = _service.Get(ev.Id);
        Assert.NotNull(restored);
        Assert.Equal(ev.Title, restored.Title);
        Assert.Equal(ev.SyncId, restored.SyncId);
    }

    [Fact]
    public void Undo_EmptyStack_ReturnsFalse()
    {
        Assert.False(_service.Undo());
    }

    [Fact]
    public void Undo_CorruptSnapshot_Throws()
    {
        _store.PushUndo("{ this is not json");
        Assert.Throws<TaqvimException>(() => _service.Undo());
    }

    [Fact]
    public void Undo_NullSnapshot_ReturnsFalse()
    {
        _store.PushUndo("null");
        Assert.False(_service.Undo());
    }

    [Fact]
    public void PushUndoSnapshot_CountsAsAStep()
    {
        _service.PushUndoSnapshot();
        Assert.Equal(1, _store.UndoCount);
        Assert.True(_service.Undo());
    }

    [Fact]
    public void Occurrences_AreOrderedByStart()
    {
        _ = Add(title: "Late", start: "2026-09-22T18:00:00+00:00", end: "2026-09-22T19:00:00+00:00");
        _ = Add(title: "Early", start: "2026-09-22T08:00:00+00:00", end: "2026-09-22T09:00:00+00:00");
        var window = _service.Occurrences(
            DateTimeOffset.Parse("2026-09-22T00:00:00+00:00", CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-09-23T00:00:00+00:00", CultureInfo.InvariantCulture));
        Assert.Equal(2, window.Count);
        Assert.Equal("Early", window[0].Event.Title);
        Assert.Equal("Late", window[1].Event.Title);
    }

    [Fact]
    public void Occurrences_ReversedWindow_IsSwapped()
    {
        var window = _service.Occurrences(
            DateTimeOffset.Parse("2026-09-23T00:00:00+00:00", CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-09-22T00:00:00+00:00", CultureInfo.InvariantCulture));
        Assert.Empty(window);
    }

    [Fact]
    public void Occurrences_OverlongWindow_IsClampedToTheRail()
    {
        _ = Add(title: "Someday", start: "2028-06-01T10:00:00+00:00", end: "2028-06-01T11:00:00+00:00");
        var window = _service.Occurrences(Now, Now.AddDays(10_000));
        Assert.Empty(window); // clamped long before 2027 — nothing invented
    }

    [Fact]
    public void Day_Week_And_Month()
    {
        _ = Add(title: "Tuesday lunch", start: "2026-09-22T12:00:00+00:00", end: "2026-09-22T13:00:00+00:00");
        _ = Add(title: "Sunday coffee", start: "2026-09-20T10:00:00+00:00", end: "2026-09-20T11:00:00+00:00");

        Assert.Single(_service.Day(new DateOnly(2026, 9, 22)));
        Assert.Single(_service.Week(new DateOnly(2026, 9, 22))); // Mon 21 – Sun 27; Sunday 20 sits in the previous week
        Assert.Equal(2, _service.Month(2026, 9).Count);
    }

    [Fact]
    public void Week_MondayAnchored()
    {
        _ = Add(title: "Tuesday lunch", start: "2026-09-22T12:00:00+00:00", end: "2026-09-22T13:00:00+00:00");
        var week = _service.Week(new DateOnly(2026, 9, 22)); // Tuesday
        var single = Assert.Single(week);
        Assert.Equal("Tuesday lunch", single.Event.Title);
    }

    [Fact]
    public void Month_InvalidMonth_Throws()
    {
        Assert.Throws<TaqvimException>(() => _service.Month(2026, 0));
        Assert.Throws<TaqvimException>(() => _service.Month(2026, 13));
    }

    [Fact]
    public void Upcoming_FiltersByCalendarAndTag()
    {
        _ = Add(title: "Work thing", start: "2026-09-21T10:00:00+00:00", end: "2026-09-21T11:00:00+00:00", calendar: "Work", tags: "focus");
        _ = Add(title: "Home thing", start: "2026-09-21T12:00:00+00:00", end: "2026-09-21T13:00:00+00:00", calendar: "Home", tags: "family");

        Assert.Equal(2, _service.Upcoming(10).Count);
        Assert.Single(_service.Upcoming(10, calendar: "Work"));
        Assert.Single(_service.Upcoming(10, tag: "family"));

        var limited = _service.Upcoming(1);
        Assert.Single(limited);
        Assert.Equal("Work thing", limited[0].Event.Title);
    }

    [Fact]
    public void Search_FindsByTitleAndDistributesLimit()
    {
        _ = Add(title: "Dentist appointment", notes: "bring the x-rays");
        _ = Add(title: "Sprint planning", notes: "quarterly dentist chat");
        _ = Add(title: "Unrelated");

        Assert.Equal(2, _service.Search("dentist").Count);
        Assert.Single(_service.Search("dentist x-rays"));
        Assert.Single(_service.Search("unrelated"));
    }

    [Fact]
    public void Search_EmptyQuery_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => _service.Search("   "));
    }

    [Fact]
    public void Conflicts_DetectsOverlaps_IgnoringAllDayAndSelf()
    {
        _ = Add(title: "Deep work", start: "2026-09-22T14:00:00+00:00", end: "2026-09-22T16:00:00+00:00");
        _ = Add(title: "Gym", start: "2026-09-22T14:30:00+00:00", end: "2026-09-22T15:30:00+00:00");
        _ = Add(title: "All-day festival", start: "2026-09-22T00:00:00+00:00", end: "2026-09-23T00:00:00+00:00", allDay: true);

        var conflicts = _service.Conflicts(
            DateTimeOffset.Parse("2026-09-22T00:00:00+00:00", CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-09-23T00:00:00+00:00", CultureInfo.InvariantCulture));
        var conflict = Assert.Single(conflicts);
        Assert.Equal("Deep work", conflict.First.Event.Title);
        Assert.Equal("Gym", conflict.Second.Event.Title);
    }

    [Fact]
    public void FreeSlots_BetweenEvents_WithClipping()
    {
        _ = Add(title: "Morning", start: "2026-09-22T10:00:00+00:00", end: "2026-09-22T11:30:00+00:00");
        var slots = _service.FreeSlots(new DateOnly(2026, 9, 22), new TimeOnly(9, 0), new TimeOnly(14, 0), 60);
        Assert.Equal(2, slots.Count);
        Assert.Equal(new DateTimeOffset(2026, 9, 22, 9, 0, 0, TimeSpan.Zero), slots[0].Start);
        Assert.Equal(new DateTimeOffset(2026, 9, 22, 10, 0, 0, TimeSpan.Zero), slots[0].End);
        Assert.Equal(new DateTimeOffset(2026, 9, 22, 11, 30, 0, 0, TimeSpan.Zero), slots[1].Start);
        Assert.Equal(new DateTimeOffset(2026, 9, 22, 14, 0, 0, 0, TimeSpan.Zero), slots[1].End);
    }

    [Fact]
    public void FreeSlots_SkipsSlotsShorterThanTheMinimum()
    {
        _ = Add(title: "Blocker", start: "2026-09-22T10:00:00+00:00", end: "2026-09-22T10:30:00+00:00");
        var slots = _service.FreeSlots(new DateOnly(2026, 9, 22), new TimeOnly(9, 0), new TimeOnly(12, 0), 60);
        Assert.Equal(2, slots.Count); // 09:00–10:00 (exactly 60m) and 10:30–12:00 (90m) both survive
    }

    [Fact]
    public void FreeSlots_RailsAndErrors()
    {
        Assert.Throws<TaqvimException>(() => _service.FreeSlots(new DateOnly(2026, 9, 22), new TimeOnly(9, 0), new TimeOnly(17, 0), 0));
        Assert.Throws<TaqvimException>(() => _service.FreeSlots(new DateOnly(2026, 9, 22), new TimeOnly(9, 0), new TimeOnly(17, 0), TaqvimDefaults.MaxFreeSlotMinutes + 1));
        Assert.Throws<TaqvimException>(() => _service.FreeSlots(new DateOnly(2026, 9, 22), new TimeOnly(17, 0), new TimeOnly(9, 0), 30));
    }

    [Fact]
    public void FreeSlots_NothingScheduled_WIsTheWholeWindow()
    {
        var slots = _service.FreeSlots(new DateOnly(2026, 9, 22), new TimeOnly(9, 0), new TimeOnly(12, 0), 30);
        var slot = Assert.Single(slots);
        Assert.Equal(TimeSpan.FromHours(3), slot.Duration);
    }

    [Fact]
    public void Stats_Aggregates()
    {
        _ = Add(title: "Plain", start: "2026-09-21T10:00:00+00:00", end: "2026-09-21T11:00:00+00:00", tags: "work");
        _ = Add(title: "Weekly", start: "2026-09-21T12:00:00+00:00", end: "2026-09-21T13:00:00+00:00", rule: new Recurrence(RecurrenceKind.Weekly), reminders: [10, 30]);
        _ = Add(title: "Holiday", start: "2026-10-01T00:00:00+00:00", end: "2026-10-02T00:00:00+00:00", allDay: true);

        var stats = _service.Stats();
        Assert.Equal(3, stats.Events);
        Assert.Equal(1, stats.Recurring);
        Assert.Equal(1, stats.AllDay);
        Assert.Equal(1, stats.Tagged);
        Assert.Equal(2, stats.Reminders);
        Assert.InRange(stats.NextSevenDays, 2, 3);
        Assert.Equal(120, stats.BusyMinutesNextSevenDays);
    }

    [Fact]
    public void UndoStack_RespectsTheConfiguredDepth()
    {
        var options = new TaqvimOptions { UndoDepth = 2 };
        var service = new TaqvimService(_store, new FixedTimeProvider(Now), options);
        _ = service.AddEvent("One", Now, Now.AddHours(1));
        _ = service.AddEvent("Two", Now.AddHours(1), Now.AddHours(2));
        _ = service.AddEvent("Three", Now.AddHours(2), Now.AddHours(3));
        Assert.Equal(2, _store.UndoCount); // oldest snapshot trimmed

        // Undo 1 reverts the "Three" add (One + Two remain); undo 2 reverts the "Two" add (One remains).
        Assert.True(service.Undo());
        Assert.Equal(2, service.All().Count);
        Assert.True(service.Undo());
        Assert.Single(service.All());
    }

    [Fact]
    public void Edit_NullKeepSemantics_PreserveRuleAndReminders()
    {
        var ev = Add(rule: new Recurrence(RecurrenceKind.Daily), reminders: [20]);
        var updated = _service.EditEvent(ev.Id, title: "Renamed");
        Assert.Equal(RecurrenceKind.Daily, updated.Rule!.Kind);
        Assert.Equal([20], updated.Reminders);
    }

    [Fact]
    public void Edit_ClearRule_Semantics()
    {
        var ev = Add(rule: new Recurrence(RecurrenceKind.Daily));
        var updated = _service.EditEvent(ev.Id, clearRule: true);
        Assert.Null(updated.Rule);
    }

    [Fact]
    public void Edit_ClearReminders_Semantics()
    {
        var ev = Add(reminders: [20]);
        var updated = _service.EditEvent(ev.Id, clearReminders: true);
        Assert.Empty(updated.Reminders);
    }
}
