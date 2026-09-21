using System.Text;

using JameJam.Settings;
using JameJam.Soroush;
using JameJam.Sync;
using JameJam.Taqvim;

namespace JameJam.Tests.Taqvim;

/// <summary>The <c>taqvim</c> CLI end to end: routing, flags, rails, sync wiring, AI hooks.</summary>
public sealed class TaqvimCommandsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero); // Sunday

    private readonly MemoryTaqvimStore _store = new();
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();
    private readonly TaqvimCommands _commands;
    private readonly List<AiRequest> _aiCalls = [];
    private string? _aiReply = "taqvim add Gym --at 2026-09-22 18:00";
    private string? _syncUrl;

    public TaqvimCommandsTests()
    {
        var clock = new FixedTimeProvider(Now);
        _commands = new TaqvimCommands(
            _store,
            clock,
            new StringWriter(_stdout),
            new StringWriter(_stderr),
            stdin: new StringReader("piped notes\n"),
            aiCompletion: (request, _) =>
            {
                _aiCalls.Add(request);
                return Task.FromResult(new SoroushResult(_aiReply ?? string.Empty, "test-provider", "test-model", 1, TimeSpan.Zero));
            },
            syncClientFactory: _ => new FakeBlobClient(),
            syncUrlProvider: () => _syncUrl,
            deviceIdProvider: () => "11111111-2222-3333-4444-555555555555",
            deviceNameProvider: () => "TestBox");
    }

    public void Dispose()
    {
        _stdout.Clear();
        _stderr.Clear();
    }

    private int Run(params string[] args)
    {
        _stdout.Clear();
        _stderr.Clear();
        return _commands.RunAsync(args).GetAwaiter().GetResult();
    }

    private string Out => _stdout.ToString();

    private string Err => _stderr.ToString();

    // ── Routing & help ──

    [Fact]
    public void NoArgs_ShowsHelp()
    {
        Assert.Equal(0, Run());
        Assert.Contains("Taqvim", Out, StringComparison.Ordinal);
    }

    [Fact]
    public void Help_ShowsHelp()
    {
        Assert.Equal(0, Run("help"));
        Assert.Contains("taqvim add", Out, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownVerb_Fails()
    {
        Assert.Equal(1, Run("meditate"));
        Assert.Contains("Unknown taqvim command", Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoStorage_Fails()
    {
        var commands = new TaqvimCommands(null, new FixedTimeProvider(Now), new StringWriter(_stdout), new StringWriter(_stderr));
        Assert.Equal(1, await commands.RunAsync(["stats"]));
        Assert.Contains("storage is not available", Err, StringComparison.Ordinal);
    }

    // ── Add ──

    [Fact]
    public void Add_ExplicitTimes()
    {
        Assert.Equal(0, Run("add", "Lunch", "--at", "2026-09-22 12:00", "--dur", "90m", "--location", "Cafe", "--tags", "food", "--remind", "15,30"));
        Assert.Contains("Added #1", Out, StringComparison.Ordinal);
        var ev = Assert.Single(_store.ListEvents());
        Assert.Equal("Lunch", ev.Title);
        Assert.Equal([15, 30], ev.Reminders);
        Assert.Equal("food", ev.Tags);
    }

    [Fact]
    public void Add_NoTimeSignal_FailsWithHint()
    {
        Assert.Equal(1, Run("add", "Vague plans"));
        Assert.Contains("When does it start", Err, StringComparison.Ordinal);
    }

    [Fact]
    public void Add_NaturalLanguage()
    {
        Assert.Equal(0, Run("add", "lunch with Sara next Tuesday at 1pm for 90m #friends"));
        var ev = Assert.Single(_store.ListEvents());
        Assert.Equal("lunch with Sara", ev.Title);
        Assert.Equal(13, ev.Start.Hour);
        Assert.Equal(TimeSpan.FromMinutes(90), ev.End - ev.Start);
        Assert.Equal("friends", ev.Tags);
    }

    [Fact]
    public void Add_AllDay()
    {
        Assert.Equal(0, Run("add", "Nowruz", "--allday", "2027-03-21"));
        var ev = Assert.Single(_store.ListEvents());
        Assert.True(ev.IsAllDay);
        Assert.Equal(TimeSpan.FromDays(1), ev.End - ev.Start);
    }

    [Fact]
    public void Add_Recurring()
    {
        Assert.Equal(0, Run("add", "Standup", "--at", "2026-09-21 09:30", "--dur", "15m", "--every", "weekly", "--on", "mo,we", "--count", "10"));
        var ev = Assert.Single(_store.ListEvents());
        Assert.NotNull(ev.Rule);
        Assert.Equal(RecurrenceKind.Weekly, ev.Rule.Kind);
        Assert.Equal(2, ev.Rule.Weekdays.Count);
        Assert.Equal(10, ev.Rule.Count);
        Assert.Contains("every week on Mo, We", Out, StringComparison.Ordinal);
    }

    [Fact]
    public void Add_Until_AndCountConflict()
    {
        Assert.Equal(1, Run("add", "Both", "--at", "2026-09-21 09:30", "--every", "daily", "--count", "3", "--until", "2026-12-31"));
        Assert.Contains("Use --count or --until", Err, StringComparison.Ordinal);
    }

    [Fact]
    public void Add_BadRepeatKind()
    {
        Assert.Equal(1, Run("add", "Weird", "--at", "2026-09-21 09:30", "--every", "hourly"));
        Assert.Contains("Unknown repeat kind", Err, StringComparison.Ordinal);
    }

    [Fact]
    public void Add_BadWeekday()
    {
        Assert.Equal(1, Run("add", "Weird", "--at", "2026-09-21 09:30", "--every", "weekly", "--on", "someday"));
        Assert.Contains("Unknown weekday", Err, StringComparison.Ordinal);
    }

    [Fact]
    public void Add_BadDuration()
    {
        Assert.Equal(1, Run("add", "Weird", "--at", "2026-09-21 09:30", "--dur", "soon"));
        Assert.Contains("Cannot read duration", Err, StringComparison.Ordinal);
    }

    [Fact]
    public void Add_UnknownFlag()
    {
        Assert.Equal(1, Run("add", "Weird", "--vibes", "good"));
        Assert.Contains("Unknown option", Err, StringComparison.Ordinal);
    }

    [Fact]
    public void Add_StdinNotes()
    {
        Assert.Equal(0, Run("add", "With notes", "--at", "2026-09-22 09:00", "--stdin"));
        Assert.Contains("piped notes", Assert.Single(_store.ListEvents()).Notes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_StdinEmpty_Fails()
    {
        var commands = new TaqvimCommands(
            _store, new FixedTimeProvider(Now), new StringWriter(_stdout), new StringWriter(_stderr),
            stdin: new StringReader("   \n"));
        Assert.Equal(1, await commands.RunAsync(["add", "Empty", "--at", "2026-09-22 09:00", "--stdin"]));
        Assert.Contains("No Event notes", Err, StringComparison.Ordinal);
    }

    // ── Lists ──

    [Fact]
    public void List_TodayTomorrowWeekMonthAndDate()
    {
        _ = Run("add", "Today thing", "--at", "12:30");
        _ = Run("add", "Morning thing tomorrow at 09:00"); // natural-language date
        Assert.Equal(0, Run("list", "today"));
        Assert.Contains("Today thing", Out, StringComparison.Ordinal);
        Assert.Equal(0, Run("list", "tomorrow"));
        Assert.Contains("Morning thing", Out, StringComparison.Ordinal);
        Assert.Equal(0, Run("list", "week"));
        Assert.Equal(0, Run("list", "month"));
        Assert.Equal(0, Run("list", "2026-09-21"));
        Assert.Equal(0, Run("today"));
        Assert.Equal(0, Run("tomorrow"));
        Assert.Equal(0, Run("week"));
    }

    [Fact]
    public void List_Empty_IsFriendly()
    {
        Assert.Equal(0, Run("list", "today"));
        Assert.Contains("nothing scheduled", Out, StringComparison.Ordinal);
    }

    [Fact]
    public void List_UnknownScope_Fails()
    {
        Assert.Equal(1, Run("list", "whenever"));
        Assert.Contains("Unknown list scope", Err, StringComparison.Ordinal);
    }

    [Fact]
    public void List_Upcoming_WithLimitAndFilters()
    {
        _ = Run("add", "Work thing", "--at", "2026-09-21 10:00", "--calendar", "Work", "--tags", "focus");
        _ = Run("add", "Home thing", "--at", "2026-09-21 15:00", "--calendar", "Home");
        Assert.Equal(0, Run("list", "upcoming", "--limit", "1"));
        Assert.Contains("Work thing", Out, StringComparison.Ordinal);
        Assert.DoesNotContain("Home thing", Out, StringComparison.Ordinal);
        Assert.Equal(0, Run("list", "upcoming", "--calendar", "Home"));
        Assert.Contains("Home thing", Out, StringComparison.Ordinal);
        Assert.Equal(0, Run("list", "upcoming", "--tag", "focus"));
    }

    [Fact]
    public void List_SearchByQuery()
    {
        _ = Run("add", "Dentist appointment", "--at", "2026-09-21 10:00");
        Assert.Equal(0, Run("list", "--q", "dentist"));
        Assert.Contains("Dentist", Out, StringComparison.Ordinal);
        Assert.Equal(0, Run("list", "--q", "nothing-matches-this"));
        Assert.Contains("no events match", Out, StringComparison.Ordinal);
    }

    [Fact]
    public void List_MonthWithExplicitYearMonth()
    {
        Assert.Equal(0, Run("month", "2026-10"));
        Assert.Contains("Calendar 2026-10", Out, StringComparison.Ordinal);
        Assert.Equal(0, Run("month"));
    }

    // ── Show / edit / reschedule / repeat / remind / delete ──

    [Fact]
    public void Show_RendersTheEvent()
    {
        _ = Run("add", "Full event", "--at", "2026-09-22 12:00", "--dur", "60m", "--location", "Room 9", "--tags", "work,focus", "--remind", "10", "--calendar", "Ops");
        Assert.Equal(0, Run("show", "1"));
        Assert.Contains("Full event", Out, StringComparison.Ordinal);
        Assert.Contains("Room 9", Out, StringComparison.Ordinal);
        Assert.Contains("work, focus", Out, StringComparison.Ordinal);
        Assert.Contains("Reminders: 10", Out, StringComparison.Ordinal);
        Assert.Contains("Jalali:", Out, StringComparison.Ordinal);
        Assert.Contains("1405", Out, StringComparison.Ordinal);
    }

    [Fact]
    public void Show_UnknownId_Fails()
    {
        Assert.Equal(1, Run("show", "77"));
        Assert.Contains("No event #77", Err, StringComparison.Ordinal);
    }

    [Fact]
    public void Edit_RequiresSomethingToChange()
    {
        _ = Run("add", "Static", "--at", "2026-09-22 12:00");
        Assert.Equal(1, Run("edit", "1"));
        Assert.Contains("Nothing to change", Err, StringComparison.Ordinal);
        Assert.Equal(0, Run("edit", "1", "--title", "Dynamic", "--location", "Room 2", "--notes", "fresh", "--tags", "a,b", "--calendar", "New"));
        var ev = Assert.Single(_store.ListEvents());
        Assert.Equal(("Dynamic", "Room 2", "fresh", "a,b", "New"), (ev.Title, ev.Location, ev.Notes, ev.Tags, ev.Calendar));
    }

    [Fact]
    public void Reschedule_WithAtOrDay()
    {
        _ = Run("add", "Movable", "--at", "2026-09-22 12:00", "--dur", "60m");
        Assert.Equal(0, Run("reschedule", "1", "--at", "2026-09-23 09:00"));
        Assert.Equal(0, Run("reschedule", "1", "--at", "2026-09-23 10:00", "--dur", "45m"));
        Assert.Equal(0, Run("reschedule", "1", "--day", "2026-09-25"));
        var ev = Assert.Single(_store.ListEvents());
        Assert.True(ev.IsAllDay);
        Assert.Equal(1, Run("reschedule", "1"));
        Assert.Contains("Move it where", Err, StringComparison.Ordinal);
    }

    [Fact]
    public void Repeat_NoneAndKinds()
    {
        _ = Run("add", "Flexible", "--at", "2026-09-22 12:00");
        Assert.Equal(1, Run("repeat"));
        Assert.Equal(0, Run("repeat", "1", "daily", "--interval", "2", "--count", "5"));
        var ev = Assert.Single(_store.ListEvents());
        Assert.Equal(RecurrenceKind.Daily, ev.Rule!.Kind);
        Assert.Equal(2, ev.Rule.Interval);

        Assert.Equal(0, Run("repeat", "1", "none"));
        Assert.Null(Assert.Single(_store.ListEvents()).Rule);
    }

    [Fact]
    public void Remind_SetAndClear()
    {
        _ = Run("add", "Buzzed", "--at", "2026-09-22 12:00");
        Assert.Equal(1, Run("remind"));
        Assert.Equal(0, Run("remind", "1", "15,60"));
        Assert.Equal([15, 60], Assert.Single(_store.ListEvents()).Reminders);
        Assert.Equal(0, Run("remind", "1", "none"));
        Assert.Empty(Assert.Single(_store.ListEvents()).Reminders);
    }

    [Fact]
    public void Delete_ThenUndo()
    {
        _ = Run("add", "Temporary", "--at", "2026-09-22 12:00");
        Assert.Equal(1, Run("delete"));
        Assert.Equal(0, Run("delete", "1"));
        Assert.Empty(_store.ListEvents());
        Assert.Equal(0, Run("undo"));
        Assert.Single(_store.ListEvents());
        Assert.Equal(0, Run("undo")); // reverts the add itself
        Assert.Empty(_store.ListEvents());
        Assert.Equal(0, Run("undo")); // stack exhausted but friendly
        Assert.Contains("Nothing to undo", Out, StringComparison.Ordinal);
    }

    // ── Free & conflicts ──

    [Fact]
    public void Free_RendersWindows()
    {
        _ = Run("add", "Blocker", "--at", "2026-09-22 10:00", "--dur", "90m");
        Assert.Equal(0, Run("free", "--day", "2026-09-22", "--from", "09:00", "--to", "12:00", "--min", "30"));
        Assert.Contains("09:00–10:00", Out, StringComparison.Ordinal);
        Assert.Contains("11:30–12:00", Out, StringComparison.Ordinal);
    }

    [Fact]
    public void Free_NoWindows_IsFriendly()
    {
        Assert.Equal(0, Run("free", "--day", "2026-09-22", "--min", "700"));
        Assert.Contains("no free window", Out, StringComparison.Ordinal);
    }

    [Fact]
    public void Free_RailErrors()
    {
        Assert.Equal(1, Run("free", "--min", "0"));
        Assert.Equal(1, Run("free", "--from", "18:00", "--to", "09:00"));
    }

    [Fact]
    public void Conflicts_FoundAndNone()
    {
        _ = Run("add", "First", "--at", "2026-09-22 10:00", "--dur", "60m");
        _ = Run("add", "Second", "--at", "2026-09-22 10:30", "--dur", "60m");
        Assert.Equal(0, Run("conflicts", "--days", "7"));
        Assert.Contains("1 conflict", Out, StringComparison.Ordinal);
        Assert.Contains("overlaps", Out, StringComparison.Ordinal);

        _ = Run("delete", "2");
        Assert.Equal(0, Run("conflicts", "--days", "7"));
        Assert.Contains("No conflicts", Out, StringComparison.Ordinal);
    }

    [Fact]
    public void Conflicts_BadDays_Throws()
    {
        Assert.Equal(1, Run("conflicts", "--days", "zero"));
        Assert.Contains("must be a positive number", Err, StringComparison.Ordinal);
    }

    // ── ICS ──

    [Fact]
    public void ExportImport_RoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"taqvim-cli-{Guid.NewGuid():N}.ics");
        try
        {
            _ = Run("add", "Round trip", "--at", "2026-09-22 12:00", "--dur", "60m", "--location", "Cafe");
            Assert.Equal(1, Run("export"));
            Assert.Equal(0, Run("export", path));
            Assert.Contains("Exported 1", Out, StringComparison.Ordinal);
            Assert.Equal(0, Run("delete", "1"));
            Assert.Equal(0, Run("import", path));
            Assert.Contains("Imported 1", Out, StringComparison.Ordinal);
            Assert.Single(_store.ListEvents());
            Assert.Equal(0, Run("undo")); // import is undoable
            Assert.Empty(_store.ListEvents());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Import_MissingFile_Fails()
    {
        Assert.Equal(1, Run("import", "/nonexistent/path.ics"));
        Assert.Contains("No such file", Err, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_OversizedFile_Fails()
    {
        var path = Path.Combine(Path.GetTempPath(), $"taqvim-big-{Guid.NewGuid():N}.ics");
        try
        {
            File.WriteAllText(path, new string('x', (int)TaqvimDefaults.MaxIcsBytes + 1));
            Assert.Equal(1, Run("import", path));
            Assert.Contains("import limit", Err, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── Stats ──

    [Fact]
    public void Stats_RendersNumbers()
    {
        _ = Run("add", "Counted", "--at", "2026-09-21 12:00", "--tags", "work");
        Assert.Equal(0, Run("stats"));
        Assert.Contains("1 event(s)", Out, StringComparison.Ordinal);
        Assert.Contains("next 7 days: 1 occurrence(s)", Out, StringComparison.Ordinal);
    }

    // ── Capture ──

    [Fact]
    public void Capture_Previews()
    {
        Assert.Equal(0, Run("capture", "lunch with Sara next Tuesday at 1pm"));
        Assert.Contains("Would create:", Out, StringComparison.Ordinal);
        Assert.Contains("lunch with Sara", Out, StringComparison.Ordinal);
        Assert.Contains("jalali:", Out, StringComparison.Ordinal);
    }

    [Fact]
    public void Capture_NoSignal_Fails()
    {
        Assert.Equal(1, Run("capture", "write the report"));
        Assert.Contains("No date or time found", Err, StringComparison.Ordinal);
    }

    [Fact]
    public void Capture_Empty_FailsWithUsage()
    {
        Assert.Equal(1, Run("capture"));
        Assert.Contains("Usage", Err, StringComparison.Ordinal);
    }

    // ── AI ──

    [Fact]
    public async Task Ai_Unavailable_Fails()
    {
        var commands = new TaqvimCommands(
            _store, new FixedTimeProvider(Now), new StringWriter(_stdout), new StringWriter(_stderr));
        Assert.Equal(1, await commands.RunAsync(["ai", "brief"]));
        Assert.Contains("AI is not available", Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ai_Brief_Plans_Asks()
    {
        _ = Run("add", "Briefed", "--at", "2026-09-21 09:00");
        Assert.Equal(0, await _commands.RunAsync(["ai", "brief"]));
        Assert.Contains("Briefed", Out, StringComparison.Ordinal);
        Assert.Single(_aiCalls);

        Assert.Equal(0, await _commands.RunAsync(["ai", "plan"]));
        Assert.Equal(0, await _commands.RunAsync(["ai", "ask", "when", "is", "my", "briefing?"]));
        Assert.True(_aiCalls.Count >= 3);
    }

    [Fact]
    public async Task Ai_Capture_SuggestsACommand()
    {
        Assert.Equal(0, await _commands.RunAsync(["ai", "capture", "gym tomorrow at 6pm"]));
        Assert.Contains("Suggestion", Out, StringComparison.Ordinal);
        Assert.Contains("taqvim add Gym --at 2026-09-22 18:00", Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ai_Capture_NonSenseReply_Fails()
    {
        _aiReply = "I am sorry, I cannot do that.";
        Assert.Equal(1, await _commands.RunAsync(["ai", "capture", "gym tomorrow at 6pm"]));
        Assert.Contains("could not turn that into a command", Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ai_EmptyReply_Fails()
    {
        _aiReply = "   ";
        Assert.Equal(1, await _commands.RunAsync(["ai", "brief"]));
        Assert.Contains("empty answer", Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ai_EmptyQuestion_Fails()
    {
        Assert.Equal(1, await _commands.RunAsync(["ai", "ask"]));
        Assert.Contains("Ask a question", Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ai_EmptyCaptureSentence_Fails()
    {
        Assert.Equal(1, await _commands.RunAsync(["ai", "capture"]));
        Assert.Contains("Give me a sentence", Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ai_UnknownVerb_Fails()
    {
        Assert.Equal(1, await _commands.RunAsync(["ai", "meditate"]));
        Assert.Contains("Unknown taqvim ai command", Err, StringComparison.Ordinal);
    }

    // ── Sync ──

    [Fact]
    public async Task Sync_NoUrl_FailsWithSettingHint()
    {
        Assert.Equal(1, await _commands.RunAsync(["sync"]));
        Assert.Contains(SettingKeys.TaqvimSyncUrl, Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_BadMode_Fails()
    {
        _syncUrl = "http://127.0.0.1:9/v1/blobs/taqvim-test";
        Assert.Equal(1, await _commands.RunAsync(["sync", "sideways"]));
        Assert.Contains("Unknown sync mode", Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_BadUrl_Fails()
    {
        _syncUrl = "not-a-url";
        Assert.Equal(1, await _commands.RunAsync(["sync"]));
        Assert.Contains("endpoint", Err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sync_Merge_RunsThroughTheSharedEngine()
    {
        _syncUrl = "http://127.0.0.1:9/v1/blobs/taqvim-cli";
        Assert.Equal(0, await _commands.RunAsync(["sync"])); // empty remote → first sync
        Assert.Contains("First sync", Out, StringComparison.Ordinal);
        Assert.Equal(0, await _commands.RunAsync(["sync", "pull"]));
        Assert.Contains("Pulled", Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_ExtraArg_Fails()
    {
        _syncUrl = "http://127.0.0.1:9/v1/blobs/taqvim-x";
        Assert.Equal(1, await _commands.RunAsync(["sync", "merge", "--wat"]));
        Assert.Contains("Usage: JameJam taqvim sync", Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_NoStorage_Fails()
    {
        var commands = new TaqvimCommands(
            null, new FixedTimeProvider(Now), new StringWriter(_stdout), new StringWriter(_stderr),
            syncClientFactory: _ => new FakeBlobClient());
        Assert.Equal(1, await commands.RunAsync(["sync"]));
        Assert.Contains("storage is not available", Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_NoFactory_Fails()
    {
        var commands = new TaqvimCommands(
            _store, new FixedTimeProvider(Now), new StringWriter(_stdout), new StringWriter(_stderr));
        Assert.Equal(1, await commands.RunAsync(["sync"]));
        Assert.Contains("Sync is not available", Err, StringComparison.Ordinal);
    }

    // ── Argument edge cases ──

    [Fact]
    public void BadNumbers_FailFriendly()
    {
        _ = Run("add", "N", "--at", "2026-09-22 12:00");
        Assert.Equal(1, Run("show", "abc"));
        Assert.Equal(1, Run("delete", "-3"));
        Assert.Equal(1, Run("month", "20x6-10"));
        Assert.Equal(1, Run("list", "upcoming", "--limit", "zero"));
    }

    [Fact]
    public void Remind_GarbageMinutes_KeepsWhatItCanRead()
    {
        _ = Run("add", "N", "--at", "2026-09-22 12:00");
        Assert.Equal(0, Run("remind", "1", "15,x,30"));
        Assert.Equal([15, 30], Assert.Single(_store.ListEvents()).Reminders);
    }

    [Fact]
    public void Reschedule_MissingId_Fails()
    {
        Assert.Equal(1, Run("reschedule", "9", "--at", "2026-09-22 09:00"));
        Assert.Contains("No event #9", Err, StringComparison.Ordinal);
    }

    private sealed class FakeBlobClient : ISyncClient
    {
        public Task<string?> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task PutAsync(string json, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
