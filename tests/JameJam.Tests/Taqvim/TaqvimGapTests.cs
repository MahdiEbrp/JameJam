using System.Globalization;
using System.Text.Json;

using JameJam.Sync;
using JameJam.Taqvim;
using JameJam.Taqvim.Ai;
using JameJam.Taqvim.Sync;

namespace JameJam.Tests.Taqvim;

/// <summary>Branch-completion tests: every remaining path in the Taqvim package.</summary>
public sealed class TaqvimGapTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions Wire = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // ── TaqvimText ──

    [Fact]
    public void ParseWhen_Failure_IsFriendly()
    {
        Assert.Throws<TaqvimException>(() => TaqvimText.ParseWhen("gibberish", Now));
    }

    [Fact]
    public void ParseWhen_DateWithSlash_AndSeconds()
    {
        var slash = TaqvimText.ParseWhen("2026/09/22", Now);
        Assert.Equal(22, slash.Day);
        var seconds = TaqvimText.ParseWhen("2026-09-22 14:30:15", Now);
        Assert.Equal(new TimeSpan(14, 30, 15), seconds.TimeOfDay);
    }

    [Fact]
    public void ParseClock_Failure_IsFriendly()
    {
        var ex = Assert.Throws<TaqvimException>(() => TaqvimText.ParseClock("25:99", "window start"));
        Assert.Contains("window start", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseDay_Failure_IsFriendly()
    {
        Assert.Throws<TaqvimException>(() => TaqvimText.ParseDay("soon", "day"));
    }

    [Fact]
    public void CleanTags_StopsAtTheTotalLengthRail()
    {
        var tags = TaqvimText.CleanTags("aaaaaaaaaaaaaaaaaaaa,bbbbbbbbbbbbbbbbbbbb,cccccccccccccccccccc,dddddddddddddddddddd",
            maxTags: 12, maxTagLength: 30, maxLength: 40);
        Assert.True(tags.Length <= 40);
        Assert.DoesNotContain("dddd", tags, StringComparison.Ordinal); // the tail was dropped
    }

    [Fact]
    public void CleanReminders_SkipsJunk_BreaksAtMax()
    {
        Assert.Equal([15, 30], TaqvimText.CleanReminders("15, x, 30", 8, TaqvimDefaults.MaxReminderMinutes));
        Assert.Equal([5, 10, 15], TaqvimText.CleanReminders("5,10,15,20,25,30,35,40", 3, TaqvimDefaults.MaxReminderMinutes));
        Assert.Empty(TaqvimText.CleanReminders(null, 8, 100));
        Assert.Empty(TaqvimText.CleanReminders("  ", 8, 100));
    }

    [Fact]
    public void RemindersFromCsv_SkipsJunk()
    {
        Assert.Equal([15, 30], TaqvimText.RemindersFromCsv("15,x,30"));
        Assert.Empty(TaqvimText.RemindersFromCsv(string.Empty));
    }

    [Fact]
    public void IcsEscape_HandlesEverySpecial()
    {
        Assert.Equal("a\\nb", TaqvimText.IcsEscape("a\nb"));
        Assert.Equal("ab", TaqvimText.IcsEscape("a\rb"));
        Assert.Equal("c\\,d\\;e\\\\f", TaqvimText.IcsEscape("c,d;e\\f"));
    }

    [Fact]
    public void Clip_Trims_AndClips()
    {
        Assert.Equal("trimmed", TaqvimText.Clip("  trimmed  ", 50));
        Assert.Equal("0123456789", TaqvimText.Clip("0123456789xxx", 10));
        Assert.Throws<ArgumentNullException>(() => TaqvimText.Clip(null!, 10));
    }

    [Fact]
    public void CleanTags_NullOrEmpty_IsEmpty()
    {
        Assert.Empty(TaqvimText.CleanTags(null, 12, 30, 400));
        Assert.Empty(TaqvimText.CleanTags(",,, ;;;", 12, 30, 400));
    }

    [Fact]
    public void TagsOf_Splits()
    {
        var ev = new TaqvimEvent(1, "C", "T", string.Empty, string.Empty, "a,b,c",
            Now, Now, false, null, [], Now, Now);
        Assert.Equal(["a", "b", "c"], TaqvimText.TagsOf(ev));
        Assert.Empty(TaqvimText.TagsOf(ev with { Tags = string.Empty }));
    }

    // ── Recurrence branches ──

    [Fact]
    public void Describe_UnknownKind_FallsBackToOnce()
    {
        Assert.Equal("once", new Recurrence((RecurrenceKind)99).Describe());
    }

    [Fact]
    public void ShortNames_CoverEveryDay()
    {
        Assert.Equal("Th", Recurrence.ShortName(DayOfWeek.Thursday));
        Assert.Equal("Fr", Recurrence.ShortName(DayOfWeek.Friday));
        Assert.Equal("Sa", Recurrence.ShortName(DayOfWeek.Saturday));
    }

    [Fact]
    public void ToRrule_AllWeekdays_RoundTrip()
    {
        var all = new[] { DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday };
        var text = Recurrences.ToRrule(new Recurrence(RecurrenceKind.Weekly, 1, all, null, null));
        Assert.Contains("BYDAY=SU,MO,TU,WE,TH,FR,SA", text, StringComparison.Ordinal);
        Assert.NotNull(text);
        var parsed = Recurrences.FromRrule(text);
        Assert.NotNull(parsed);
        Assert.Equal(7, parsed.Weekdays.Count);
    }

    [Fact]
    public void FromRrule_UntilWithTime_UsesTheDatePrefix()
    {
        var rule = Recurrences.FromRrule("FREQ=DAILY;UNTIL=20261231T235959Z");
        Assert.NotNull(rule);
        Assert.Equal(new DateOnly(2026, 12, 31), rule.Until);
    }

    [Fact]
    public void Yearly_StoppedByUntil()
    {
        var first = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
        var rule = new Recurrence(RecurrenceKind.Yearly, 1, null, null, new DateOnly(2027, 12, 31));
        var starts = Recurrences.Starts(rule, first, new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)).ToList();
        Assert.Equal(2, starts.Count); // 2026 and 2027 only
    }

    [Fact]
    public void Daily_StartsBeyondTheWindowEnd_YieldBreak()
    {
        var first = new DateTimeOffset(2026, 9, 25, 9, 0, 0, TimeSpan.Zero);
        var rule = new Recurrence(RecurrenceKind.Daily, 1, null, null, null);
        var starts = Recurrences.Starts(rule, first, new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero)).ToList();
        Assert.Empty(starts);
    }

    // ── Service: free-slot cursor edge ──

    [Fact]
    public void FreeSlots_OverlappingEvents_ExtendTheBusyCursor()
    {
        var store = new MemoryTaqvimStore();
        var service = new TaqvimService(store, new FixedTimeProvider(Now));
        _ = service.AddEvent("First", DateTimeOffset.Parse("2026-09-22T10:00:00+00:00", CultureInfo.InvariantCulture), DateTimeOffset.Parse("2026-09-22T11:00:00+00:00", CultureInfo.InvariantCulture));
        _ = service.AddEvent("Overlap", DateTimeOffset.Parse("2026-09-22T10:30:00+00:00", CultureInfo.InvariantCulture), DateTimeOffset.Parse("2026-09-22T12:30:00+00:00", CultureInfo.InvariantCulture));

        var slots = service.FreeSlots(new DateOnly(2026, 9, 22), new TimeOnly(9, 0), new TimeOnly(14, 0), 30);
        Assert.Equal(2, slots.Count);
        Assert.Equal(new DateTimeOffset(2026, 9, 22, 9, 0, 0, TimeSpan.Zero), slots[0].Start);
        Assert.Equal(new DateTimeOffset(2026, 9, 22, 10, 0, 0, TimeSpan.Zero), slots[0].End);
        Assert.Equal(new DateTimeOffset(2026, 9, 22, 12, 30, 0, TimeSpan.Zero), slots[1].Start);
    }

    // ── Options: parameterless shortcut ──

    [Fact]
    public void CreateValidated_Parameterless()
    {
        _ = TaqvimOptions.CreateValidated();
    }

    // ── ICS deeper branches ──

    [Fact]
    public void Ics_DurationVariants()
    {
        const string days = "BEGIN:VEVENT\r\nSUMMARY:Retreat\r\nDTSTART;VALUE=DATE:20261001\r\nDURATION:P2D\r\nEND:VEVENT\r\n";
        var retreat = Assert.Single(Ics.Parse(days));
        Assert.Equal(TimeSpan.FromDays(2), retreat.End - retreat.Start);

        const string hours = "BEGIN:VEVENT\r\nSUMMARY:Seminar\r\nDTSTART:20261001T090000\r\nDURATION:P1DT2H30M\r\nEND:VEVENT\r\n";
        var seminar = Assert.Single(Ics.Parse(hours));
        Assert.Equal(new TimeSpan(26, 30, 0), seminar.End - seminar.Start);
    }

    [Fact]
    public void Ics_MalformedDurations_FallBackToDefaults()
    {
        const string noUnit = "BEGIN:VEVENT\r\nSUMMARY:A\r\nDTSTART:20261001T090000\r\nDURATION:P9X\r\nEND:VEVENT\r\n";
        Assert.Equal(TimeSpan.FromMinutes(TaqvimDefaults.DefaultEventMinutes), Assert.Single(Ics.Parse(noUnit)).End - Assert.Single(Ics.Parse(noUnit)).Start);

        const string empty = "BEGIN:VEVENT\r\nSUMMARY:B\r\nDTSTART:20261001T090000\r\nDURATION:PT\r\nEND:VEVENT\r\n";
        Assert.Equal(TimeSpan.FromMinutes(TaqvimDefaults.DefaultEventMinutes), Assert.Single(Ics.Parse(empty)).End - Assert.Single(Ics.Parse(empty)).Start);

        const string wrongStart = "BEGIN:VEVENT\r\nSUMMARY:C\r\nDTSTART:20261001T090000\r\nDURATION:XD1\r\nEND:VEVENT\r\n";
        Assert.Equal(TimeSpan.FromMinutes(TaqvimDefaults.DefaultEventMinutes), Assert.Single(Ics.Parse(wrongStart)).End - Assert.Single(Ics.Parse(wrongStart)).Start);
    }

    [Fact]
    public void Ics_UtcFullFormat_AndLooseFormats()
    {
        const string zSuffix = "BEGIN:VEVENT\r\nSUMMARY:Z\r\nDTSTART:20261001T090000Z\r\nEND:VEVENT\r\n";
        var withZ = Assert.Single(Ics.Parse(zSuffix));
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero), withZ.Start);

        const string loose = "BEGIN:VEVENT\r\nSUMMARY:L\r\nDTSTART:2026-10-01 09:00\r\nEND:VEVENT\r\n";
        var looseParsed = Assert.Single(Ics.Parse(loose));
        Assert.Equal(9, looseParsed.Start.Hour);
    }

    [Fact]
    public void Ics_BadTrigger_IsIgnored()
    {
        const string ics = "BEGIN:VEVENT\r\nSUMMARY:T\r\nDTSTART:20261001T090000\r\nDTEND:20261001T100000\r\nBEGIN:VALARM\r\nTRIGGER:-PTbroken\r\nEND:VALARM\r\nEND:VEVENT\r\n";
        Assert.Empty(Assert.Single(Ics.Parse(ics)).Reminders);
    }

    [Fact]
    public void Ics_NoColonLines_AreSkipped()
    {
        Assert.Empty(Ics.Parse("junk line without colon"));
        var ok = Assert.Single(Ics.Parse("junk line\nBEGIN:VEVENT\r\nSUMMARY:Ok\r\nDTSTART:20261001T090000\r\nEND:VEVENT\r\n"));
        Assert.Equal("Ok", ok.Title);
    }

    [Fact]
    public void Ics_FoldAtTheVeryStart_IsIgnored()
    {
        const string ics = " folded\r\nBEGIN:VEVENT\r\nSUMMARY:S\r\nDTSTART:20261001T090000\r\nEND:VEVENT\r\n";
        var ev = Assert.Single(Ics.Parse(ics));
        Assert.Equal("S", ev.Title);
    }

    // ── Adapter: empty sync ids inside payloads ──

    [Fact]
    public async Task Merge_IgnoresEmptySyncIds_InsidePayloads()
    {
        var payload = JsonSerializer.Serialize(
            new TaqvimSyncAdapterTests.FakePayload(
                [new TaqvimSyncAdapterTests.FakeEvent(Guid.Empty, "Ghost", Now)],
                [new TaqvimSyncAdapterTests.FakeTombstone(Guid.Empty, Now)]),
            Wire);
        var merged = await new TaqvimSyncAdapter(
            new TaqvimService(new MemoryTaqvimStore(), new FixedTimeProvider(Now)),
            new MemoryTaqvimStore()).MergeAsync(payload, payload, "a", "b");
        Assert.Contains("\"events\":[]", merged.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    // ── ScheduleAssistant: location/calendar/notes lines ──

    [Fact]
    public void BriefPrompt_IncludesLocationCalendarAndNotes()
    {
        var ev = new TaqvimEvent(
            1, "Ops", "Review", "Room 9", "bring slides", string.Empty,
            Now, Now.AddHours(1), false, null, [], Now, Now);
        var prompt = new ScheduleAssistant().BuildBriefPrompt([new Occurrence(ev, Now, Now.AddHours(1))], Now);
        Assert.Contains("Room 9", prompt, StringComparison.Ordinal);
        Assert.Contains("calendar: Ops", prompt, StringComparison.Ordinal);
        Assert.Contains("bring slides", prompt, StringComparison.Ordinal);
    }

    // ── SQLite store branches ──

    [Fact]
    public void Sqlite_UndoDepth_Negative_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), $"taqvim-gap-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteTaqvimStore(path);
            Assert.Throws<TaqvimException>(() => store.UndoDepth = -1);
            Assert.Equal(0, new SqliteTaqvimStore(path) { UndoDepth = 0 }.UndoDepth);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
    }

    [Fact]
    public void Sqlite_PushUndo_WithDepthZero_ClearsTheLog()
    {
        var path = Path.Combine(Path.GetTempPath(), $"taqvim-gap-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteTaqvimStore(path) { UndoDepth = 0 };
            store.PushUndo("one");
            Assert.Equal(0, store.UndoCount);
            Assert.Null(store.PopUndo());
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
    }

    [Fact]
    public void Sqlite_UpsertTombstone_EmptySyncId_IsNoOp()
    {
        var path = Path.Combine(Path.GetTempPath(), $"taqvim-gap-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteTaqvimStore(path);
            store.UpsertTombstone(new TaqvimTombstone(Guid.Empty, Now));
            Assert.Empty(store.GetTombstones());
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
    }

    [Fact]
    public void Sqlite_LikeEscapes_BracketPercentUnderscore()
    {
        var path = Path.Combine(Path.GetTempPath(), $"taqvim-gap-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteTaqvimStore(path);
            _ = store.AddEvent(new TaqvimEvent(
                0, "Work", "C++ [vol 2] 100%", string.Empty, string.Empty, string.Empty,
                Now, Now.AddHours(1), false, null, [], Now, Now));
            _ = store.AddEvent(new TaqvimEvent(
                0, "Work", "Plain title", string.Empty, string.Empty, string.Empty,
                Now, Now.AddHours(2), false, null, [], Now, Now));

            // LIKE-wildcard-free pattern via the FTS engine: % _ and [ must match literally.
            Assert.Single(store.SearchIds("[vol", 10));
            Assert.Single(store.SearchIds("100%", 10));
            Assert.Empty(store.SearchIds("vol 1", 10));
            Assert.Single(store.SearchIds("Plain title", 10));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
    }

    [Fact]
    public void Sqlite_ReopenWithDroppedFts_FallsBackToLike()
    {
        var path = Path.Combine(Path.GetTempPath(), $"taqvim-gap-{Guid.NewGuid():N}.db");
        try
        {
            var first = new SqliteTaqvimStore(path);
            _ = first.AddEvent(new TaqvimEvent(
                0, "Work", "Needle event", string.Empty, "haystack words", string.Empty,
                Now, Now.AddHours(1), false, null, [], Now, Now));

            // Simulate an engine without FTS5: drop the index behind its back.
            using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearPool(raw);
                raw.Open();
                using var drop = raw.CreateCommand();
                drop.CommandText = "DROP TABLE IF EXISTS event_fts; DROP TRIGGER IF EXISTS events_ai; DROP TRIGGER IF EXISTS events_ad; DROP TRIGGER IF EXISTS events_au;";
                _ = drop.ExecuteNonQuery();
            }

            var second = new SqliteTaqvimStore(path); // schema probe fails → LIKE fallback
            Assert.Single(second.SearchIds("needle", 10));
            Assert.Single(second.SearchIds("haystack", 10));
            Assert.Empty(second.SearchIds("absent", 10));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
    }
}
