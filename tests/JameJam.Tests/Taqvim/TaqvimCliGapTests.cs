using System.Text;

using JameJam.Anahita;
using JameJam.Sync;
using JameJam.Taqvim;

namespace JameJam.Tests.Taqvim;

/// <summary>CLI branch completion: integrations, error catch paths, sync flags, show variants.</summary>
public sealed class TaqvimCliGapTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly MemoryTaqvimStore _store = new();
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();

    public void Dispose()
    {
        _stdout.Clear();
        _stderr.Clear();
    }

    private string Out => _stdout.ToString();

    private string Err => _stderr.ToString();

    private TaqvimCommands Commands(
        Func<System.Collections.Generic.IReadOnlyList<string>>? dueTasks = null,
        Func<DateOnly, Task<string?>>? forecast = null,
        Func<DateOnly, string?>? journal = null,
        Func<SyncOptions, ISyncClient>? syncFactory = null,
        string? syncUrl = null,
        string? deviceId = null) => new(
        _store,
        new FixedTimeProvider(Now),
        new StringWriter(_stdout),
        new StringWriter(_stderr),
        syncClientFactory: syncFactory,
        syncUrlProvider: syncUrl is null ? null : () => syncUrl,
        deviceIdProvider: deviceId is null ? null : () => deviceId,
        deviceNameProvider: () => "GapBox",
        dueTasksProvider: dueTasks,
        forecastProvider: forecast,
        journalProvider: journal);

    private static int Run(TaqvimCommands commands, params string[] args) => commands.RunAsync(args).GetAwaiter().GetResult();

    // ── Keystone integrations ──

    [Fact]
    public void DayView_ShowsDueTasks_WeatherAndJournal()
    {
        _ = Run(Commands(), "add", "Hike", "--at", "2026-09-21 08:00", "--dur", "3h", "--tags", "outdoor");
        var commands = Commands(
            dueTasks: () => ["Submit the form", "Call the bank"],
            forecast: _ => Task.FromResult<string?>("☀ Clear, 18°C–29°C, 5% rain"),
            journal: day => $"journal entry for {day:yyyy-MM-dd} exists");
        Assert.Equal(0, Run(commands, "list", "2026-09-21"));
        Assert.Contains("due on Haft Khan: Submit the form · Call the bank", Out, StringComparison.Ordinal);
        Assert.Contains("Anahita: ☀ Clear", Out, StringComparison.Ordinal);
        Assert.Contains("journal entry for 2026-09-21 exists", Out, StringComparison.Ordinal);
    }

    [Fact]
    public void DayView_WeatherFailure_IsNotedAndSurvived()
    {
        _ = Run(Commands(), "add", "Hike", "--at", "2026-09-21 08:00", "--tags", "outdoor");
        var commands = Commands(forecast: _ => throw new AnahitaException("no network"));
        Assert.Equal(0, Run(commands, "list", "2026-09-21"));
        Assert.Contains("weather unavailable", Err, StringComparison.Ordinal);
    }

    [Fact]
    public void DayView_WeatherHttpFailure_IsSurvived()
    {
        _ = Run(Commands(), "add", "Hike", "--at", "2026-09-21 08:00", "--tags", "outdoor");
        var commands = Commands(forecast: _ => throw new System.Net.Http.HttpRequestException("boom"));
        Assert.Equal(0, Run(commands, "list", "2026-09-21"));
        Assert.Contains("weather unavailable", Err, StringComparison.Ordinal);
    }

    [Fact]
    public void DayView_ForecastNull_OrNoOutdoorTag_SkipsWeather()
    {
        _ = Run(Commands(), "add", "Indoor", "--at", "2026-09-21 08:00");
        var fetched = 0;
        var commands = Commands(forecast: _ =>
        {
            Interlocked.Increment(ref fetched);
            return Task.FromResult<string?>(null);
        });
        Assert.Equal(0, Run(commands, "list", "2026-09-21"));
        Assert.Equal(0, fetched); // no outdoor event → provider never called
        Assert.DoesNotContain("Anahita", Out, StringComparison.Ordinal);
    }

    [Fact]
    public void DayView_EmptyDueList_IsQuiet()
    {
        _ = Run(Commands(), "add", "Anything", "--at", "2026-09-21 08:00");
        var commands = Commands(dueTasks: () => [], journal: _ => null);
        Assert.Equal(0, Run(commands, "list", "2026-09-21"));
        Assert.DoesNotContain("due on Haft Khan", Out, StringComparison.Ordinal);
        Assert.DoesNotContain("journal", Out, StringComparison.Ordinal);
    }

    [Fact]
    public void TodayView_ShowsExtras()
    {
        _ = Run(Commands(), "add", "Today outdoor", "--at", "13:00", "--tags", "outdoor");
        var commands = Commands(
            dueTasks: () => ["One due"],
            forecast: _ => Task.FromResult<string?>("rain"));
        Assert.Equal(0, Run(commands, "today"));
        Assert.Contains("One due", Out, StringComparison.Ordinal);
        Assert.Contains("rain", Out, StringComparison.Ordinal);
    }

    // ── Catch paths ──

    [Fact]
    public void Export_ToAnUnwritablePath_FailsFriendly()
    {
        var commands = Commands();
        Assert.Equal(1, Run(commands, "export", "/nonexistent-dir-that-cannot-exist/x.ics"));
        Assert.NotEqual(string.Empty, Err);
    }

    [Fact]
    public async Task SyncException_FromTransport_FailsFriendly()
    {
        var commands = Commands(
            syncFactory: _ => new ThrowingClient(new SyncException("server on fire")),
            syncUrl: "http://127.0.0.1:9/v1/blobs/gap");
        Assert.Equal(1, await commands.RunAsync(["sync"]));
        Assert.Contains("server on fire", Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SoroushException_FromAi_FailsFriendly()
    {
        var commands = new TaqvimCommands(
            _store, new FixedTimeProvider(Now), new StringWriter(_stdout), new StringWriter(_stderr),
            aiCompletion: (_, _) => throw new JameJam.Soroush.SoroushException("provider down"));
        Assert.Equal(1, await commands.RunAsync(["ai", "brief"]));
        Assert.Contains("provider down", Err, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownListFlag_Fails()
    {
        var commands = Commands();
        Assert.Equal(1, Run(commands, "list", "--nope"));
        Assert.Contains("Unknown option", Err, StringComparison.Ordinal);
    }

    // ── Sync flags ──

    [Fact]
    public async Task Sync_WithForce_Runs()
    {
        var commands = Commands(
            syncFactory: _ => new FakeClient(),
            syncUrl: "http://127.0.0.1:9/v1/blobs/gap-force",
            deviceId: "gap-device");
        Assert.Equal(0, await commands.RunAsync(["sync", "merge", "--force"]));
        Assert.Contains("First sync", Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_PushMode_Runs()
    {
        var commands = Commands(
            syncFactory: _ => new FakeClient(),
            syncUrl: "http://127.0.0.1:9/v1/blobs/gap-push",
            deviceId: "gap-device");
        Assert.Equal(0, await commands.RunAsync(["sync", "push"]));
        Assert.Contains("Pushed", Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_MissingDeviceId_GeneratesOne()
    {
        var commands = Commands(
            syncFactory: _ => new FakeClient(),
            syncUrl: "http://127.0.0.1:9/v1/blobs/gap-nodevice");
        Assert.Equal(0, await commands.RunAsync(["sync"]));
        Assert.Contains("First sync", Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_FlagWithoutValue_Fails()
    {
        var commands = Commands(
            syncFactory: _ => new FakeClient(),
            syncUrl: "http://127.0.0.1:9/v1/blobs/gap-bad");
        Assert.Equal(1, await commands.RunAsync(["sync", "--url"]));
        Assert.Contains("Usage", Err, StringComparison.Ordinal);
    }

    // ── Show variants ──

    [Fact]
    public void Show_Usage_WhenNoArgs()
    {
        var commands = Commands();
        Assert.Equal(1, Run(commands, "show"));
        Assert.Contains("Usage: JameJam taqvim show", Err, StringComparison.Ordinal);
    }

    [Fact]
    public void Show_RecurringWithUntil_AndPastEvent()
    {
        var commands = Commands();
        _ = Run(commands, "add", "Anniversary", "--allday", "2026-01-01", "--every", "yearly", "--until", "2026-12-31", "--remind", "100");
        Assert.Equal(0, Run(commands, "show", "1"));
        Assert.Contains("every year until 2026-12-31", Out, StringComparison.Ordinal);
        Assert.Contains("Reminders: 100", Out, StringComparison.Ordinal);
        Assert.Contains("no upcoming occurrence", Out, StringComparison.Ordinal);
    }

    [Fact]
    public void Show_WithCount_AndMultilineNotes()
    {
        var commands = Commands();
        _ = Run(commands, "add", "Series", "--at", "2026-09-25 09:00", "--every", "weekly", "--count", "6");
        _ = Run(commands, "edit", "1", "--notes", "line one\nline two");
        Assert.Equal(0, Run(commands, "show", "1"));
        Assert.Contains("×6", Out, StringComparison.Ordinal);
        Assert.Contains("line one", Out, StringComparison.Ordinal);
        Assert.Contains("line two", Out, StringComparison.Ordinal);
    }

    // ── Edit / reschedule / add usage ──

    [Fact]
    public void Edit_AndReschedule_Usage_WithoutId()
    {
        var commands = Commands();
        Assert.Equal(1, Run(commands, "edit"));
        Assert.Contains("Usage: JameJam taqvim edit", Err, StringComparison.Ordinal);
        Assert.Equal(1, Run(commands, "reschedule"));
        Assert.Contains("Usage: JameJam taqvim reschedule", Err, StringComparison.Ordinal);
    }

    [Fact]
    public void Add_Usage_WithoutTitle()
    {
        var commands = Commands();
        Assert.Equal(1, Run(commands, "add"));
        Assert.Contains("Usage: JameJam taqvim add", Err, StringComparison.Ordinal);
    }

    [Fact]
    public void Repeat_Usage_TooFewArgs()
    {
        var commands = Commands();
        Assert.Equal(1, Run(commands, "repeat", "1"));
        Assert.Contains("Usage: JameJam taqvim repeat", Err, StringComparison.Ordinal);
    }

    [Fact]
    public void Add_RepeatAliases_AllWork()
    {
        var commands = Commands();
        _ = Run(commands, "add", "A", "--at", "2026-09-22 08:00", "--every", "day");
        _ = Run(commands, "add", "B", "--at", "2026-09-22 09:00", "--every", "week");
        _ = Run(commands, "add", "C", "--at", "2026-09-22 10:00", "--every", "month");
        _ = Run(commands, "add", "D", "--at", "2026-09-22 11:00", "--every", "annual");
        var kinds = _store.ListEvents().Select(e => e.Rule!.Kind).ToList();
        Assert.Equal(
            [RecurrenceKind.Daily, RecurrenceKind.Weekly, RecurrenceKind.Monthly, RecurrenceKind.Yearly],
            kinds);
    }

    [Fact]
    public void Add_WeekdayVariants_Parse()
    {
        var commands = Commands();
        Assert.Equal(0, Run(commands, "add", "Days", "--at", "2026-09-22 08:00", "--every", "weekly", "--on", "thursday,sat,sun"));
        Assert.Equal(3, Assert.Single(_store.ListEvents()).Rule!.Weekdays.Count);
    }

    [Fact]
    public void Add_Durations_BareHourAndDay()
    {
        var commands = Commands();
        Assert.Equal(0, Run(commands, "add", "Bare", "--at", "2026-09-22 08:00", "--dur", "45"));
        Assert.Equal(0, Run(commands, "add", "Hours", "--at", "2026-09-22 09:00", "--dur", "2h"));
        Assert.Equal(0, Run(commands, "add", "Days", "--at", "2026-09-22 10:00", "--dur", "1d"));
        var events = _store.ListEvents();
        Assert.Equal(TimeSpan.FromMinutes(45), events[0].End - events[0].Start);
        Assert.Equal(TimeSpan.FromHours(2), events[1].End - events[1].Start);
        Assert.Equal(TimeSpan.FromDays(1), events[2].End - events[2].Start);
    }

    [Fact]
    public void Add_BadUnitDuration_Fails()
    {
        var commands = Commands();
        Assert.Equal(1, Run(commands, "add", "X", "--at", "2026-09-22 08:00", "--dur", "2x"));
        Assert.Contains("Cannot read duration", Err, StringComparison.Ordinal);
    }

    [Fact]
    public void Add_NonNumericDuration_Fails()
    {
        var commands = Commands();
        Assert.Equal(1, Run(commands, "add", "X", "--at", "2026-09-22 08:00", "--dur", "xm"));
        Assert.Contains("Cannot read duration", Err, StringComparison.Ordinal);
    }

    // ── Render flags ──

    [Fact]
    public void List_ShowsReminderAndRepeatFlags_WithCalendarTagsAndPlace()
    {
        var commands = Commands();
        _ = Run(commands, "add", "Everything", "--at", "2026-09-22 08:00", "--dur", "30m", "--every", "daily",
            "--calendar", "Ops", "--tags", "a,b", "--location", "Room 5", "--remind", "10");
        Assert.Equal(0, Run(commands, "list", "2026-09-22"));
        Assert.Contains("↻", Out, StringComparison.Ordinal);
        Assert.Contains("⏰1", Out, StringComparison.Ordinal);
        Assert.Contains("[Ops]", Out, StringComparison.Ordinal);
        Assert.Contains("#a #b", Out, StringComparison.Ordinal);
        Assert.Contains("Room 5", Out, StringComparison.Ordinal);
    }

    // ── Capture with extras ──

    [Fact]
    public void Capture_ShowsLocationAndTags()
    {
        var commands = Commands();
        Assert.Equal(0, Run(commands, "capture", "lunch at Cafe Riviera tomorrow at noon #food #friends"));
        Assert.Contains("location: Cafe Riviera", Out, StringComparison.Ordinal);
        Assert.Contains("tags:     food, friends", Out, StringComparison.Ordinal);
    }

    // ── Import usage + event cap ──

    [Fact]
    public void Import_Usage_WithoutPath()
    {
        var commands = Commands();
        Assert.Equal(1, Run(commands, "import"));
        Assert.Contains("Usage: JameJam taqvim import", Err, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_OverTwoThousandEvents_IsCapped()
    {
        var path = Path.Combine(Path.GetTempPath(), $"taqvim-cap-{Guid.NewGuid():N}.ics");
        try
        {
            string[] header = ["BEGIN:VCALENDAR"];
            File.WriteAllLines(path, header
            .Concat(System.Linq.Enumerable.Range(0, TaqvimDefaults.MaxIcsEvents + 5).Select(i =>
                $"BEGIN:VEVENT\r\nSUMMARY:Event {i}\r\nDTSTART:20261001T090000\r\nDTEND:20261001T100000\r\nEND:VEVENT\r\n"))
            .Append("END:VCALENDAR"));

            var commands = Commands();
            Assert.Equal(0, Run(commands, "import", path));
            Assert.Contains($"Imported {TaqvimDefaults.MaxIcsEvents}", Out, StringComparison.Ordinal);
            Assert.Equal(TaqvimDefaults.MaxIcsEvents, _store.ListEvents().Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── AI plan with due tasks ──

    [Fact]
    public async Task AiPlan_IncludesOpenTasks()
    {
        var requests = new System.Collections.Generic.List<string>();
        var commands = new TaqvimCommands(
            _store, new FixedTimeProvider(Now), new StringWriter(_stdout), new StringWriter(_stderr),
            aiCompletion: (request, _) =>
            {
                requests.Add(request.Prompt);
                return Task.FromResult(new JameJam.Soroush.SoroushResult("plan ready", "p", "m", 1, TimeSpan.Zero));
            },
            dueTasksProvider: () => ["Write the report"]);
        Assert.Equal(0, await commands.RunAsync(["ai", "plan"]));
        Assert.Contains("open task: Write the report", requests[0], StringComparison.Ordinal);
    }

    private sealed class ThrowingClient(SyncException exception) : ISyncClient
    {
        public Task<string?> GetAsync(CancellationToken cancellationToken = default) => throw exception;

        public Task PutAsync(string json, CancellationToken cancellationToken = default) => throw exception;
    }

    private sealed class FakeClient : ISyncClient
    {
        public Task<string?> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task PutAsync(string json, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
