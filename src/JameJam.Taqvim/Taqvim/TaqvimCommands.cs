using System.Globalization;

using JameJam.Anahita;
using JameJam.Settings;
using JameJam.Soroush;
using JameJam.Sync;
using JameJam.Taqvim.Ai;
using JameJam.Taqvim.Sync;

namespace JameJam.Taqvim;

/// <summary>
/// The <c>taqvim</c> CLI: events with recurrence and reminders, agendas (day/week/month/next),
/// conflict detection, free-slot finding, ICS import/export, undo, stats, two-device sync,
/// and Soroush-powered AI (brief, plan, ask, capture).
/// </summary>
/// <param name="store">Calendar storage; null disables Taqvim (storage unavailable).</param>
/// <param name="clock">Time source for agendas and timestamps.</param>
/// <param name="output">Standard output.</param>
/// <param name="error">Error output.</param>
/// <param name="stdin">Piped input for notes; defaults to the console.</param>
/// <param name="aiCompletion">Routes a prompt through the Soroush safety layer; null when AI is unavailable.</param>
/// <param name="options">Customizable rails; defaults apply when null.</param>
/// <param name="syncClientFactory">Builds the sync transport for a URL; null disables sync.</param>
/// <param name="syncUrlProvider">Resolves a stored default sync URL (settings).</param>
/// <param name="deviceIdProvider">Resolves this device's stable sync identity (a GUID string).</param>
/// <param name="deviceNameProvider">Resolves this device's friendly name.</param>
/// <param name="dueTasksProvider">Due Haft Khan tasks shown on day views (null hides the section).</param>
/// <param name="forecastProvider">Anahita one-line forecast for a day (null hides weather).</param>
/// <param name="journalProvider">Divan journal hint for a day (null hides the section).</param>
public sealed class TaqvimCommands(
    ITaqvimStore? store,
    TimeProvider clock,
    TextWriter output,
    TextWriter error,
    TextReader? stdin = null,
    Func<AiRequest, CancellationToken, Task<SoroushResult>>? aiCompletion = null,
    TaqvimOptions? options = null,
    Func<SyncOptions, ISyncClient>? syncClientFactory = null,
    Func<string?>? syncUrlProvider = null,
    Func<string>? deviceIdProvider = null,
    Func<string>? deviceNameProvider = null,
    Func<IReadOnlyList<string>>? dueTasksProvider = null,
    Func<DateOnly, Task<string?>>? forecastProvider = null,
    Func<DateOnly, string?>? journalProvider = null)
{
    private const string NoStorageMessage = "Calendar storage is not available in this context.";

    private readonly ITaqvimStore? _store = store;
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly TextWriter _output = output ?? throw new ArgumentNullException(nameof(output));
    private readonly TextWriter _error = error ?? throw new ArgumentNullException(nameof(error));
    private readonly TextReader _stdin = stdin ?? Console.In;
    private readonly Func<AiRequest, CancellationToken, Task<SoroushResult>>? _aiCompletion = aiCompletion;
    private readonly TaqvimOptions _options = options ?? new TaqvimOptions();
    private readonly Func<SyncOptions, ISyncClient>? _syncClientFactory = syncClientFactory;
    private readonly Func<string?>? _syncUrlProvider = syncUrlProvider;
    private readonly Func<string>? _deviceIdProvider = deviceIdProvider;
    private readonly Func<string>? _deviceNameProvider = deviceNameProvider;
    private readonly Func<IReadOnlyList<string>>? _dueTasksProvider = dueTasksProvider;
    private readonly Func<DateOnly, Task<string?>>? _forecastProvider = forecastProvider;
    private readonly Func<DateOnly, string?>? _journalProvider = journalProvider;
    private readonly ScheduleAssistant _assistant = new(options);

    private TaqvimService? _service;
    private TaqvimService Service => _service ??= NewService();

    private TaqvimService NewService() =>
        _store is null
            ? throw new TaqvimException(NoStorageMessage)
            : new TaqvimService(_store, _clock, _options);

    // ── Routing ──

    /// <summary>Runs a <c>taqvim</c> command; returns the process exit code.</summary>
    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
        {
            return Help();
        }

        try
        {
            return args[0] switch
            {
                "help" => Help(),
                "add" => RunAdd(args[1..]),
                "list" => await RunListAsync(args[1..], cancellationToken).ConfigureAwait(false),
                "today" => await RunDayOffsetAsync(0, "Today").ConfigureAwait(false),
                "tomorrow" => await RunDayOffsetAsync(1, "Tomorrow").ConfigureAwait(false),
                "week" => RunWeek(),
                "month" => RunMonth(args[1..]),
                "show" => RunShow(args[1..]),
                "edit" => RunEdit(args[1..]),
                "reschedule" => RunReschedule(args[1..]),
                "repeat" => RunRepeat(args[1..]),
                "remind" => RunRemind(args[1..]),
                "delete" => RunDelete(args[1..]),
                "capture" => RunCapture(args[1..]),
                "free" => RunFree(args[1..]),
                "conflicts" => RunConflicts(args[1..]),
                "export" => RunExport(args[1..]),
                "import" => RunImport(args[1..]),
                "undo" => RunUndo(),
                "stats" => RunStats(),
                "sync" when args.Length >= 1 => await RunSyncAsync(args[1..], cancellationToken).ConfigureAwait(false),
                "sync" => await RunSyncAsync([], cancellationToken).ConfigureAwait(false),
                "ai" when args.Length >= 2 => await RunAiAsync(args[1..], cancellationToken).ConfigureAwait(false),
                "ai" => Fail("Usage: JameJam taqvim ai brief | plan | ask <question...> | capture <sentence...>"),
                _ => Fail($"Unknown taqvim command '{args[0]}'. Run 'JameJam taqvim help'."),
            };
        }
        catch (TaqvimException ex)
        {
            return Fail(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Fail(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Fail(ex.Message);
        }
        catch (SoroushException ex)
        {
            return Fail(ex.Message);
        }
        catch (SyncException ex)
        {
            return Fail(ex.Message);
        }
        catch (IOException ex)
        {
            return Fail(ex.Message);
        }
    }

    // ── Add ──

    private int RunAdd(string[] args)
    {
        if (args.Length < 1)
        {
            return Fail(
                "Usage: JameJam taqvim add <title> --at \"2026-09-21 14:00\" [--to \"...\"] [--dur 60m] "
                + "[--allday 2026-09-21] [--every daily|weekly|monthly|yearly] [--interval 2] [--on mon,wed] "
                + "[--count 10] [--until 2026-12-31] [--calendar Work] [--location ...] [--tags a,b] "
                + "[--remind 15,60] [--stdin]  (notes via stdin)");
        }

        var parsed = ParseFlags(args[1..], "--at", "--to", "--dur", "--allday", "--every", "--interval", "--on", "--count", "--until", "--calendar", "--location", "--tags", "--remind", "--stdin");
        var notes = parsed.Has("--stdin") ? ReadStdin("Event notes") : string.Empty;

        var allDayDay = parsed.Has("--allday") ? TaqvimText.ParseDay(parsed.Require("--allday", "an all-day date"), "all-day date") : (DateOnly?)null;
        DateTimeOffset start;
        DateTimeOffset end;
        string? capturedTitle = null;
        var capturedTags = string.Empty;
        string? capturedLocation = null;
        var allDay = false;
        if (allDayDay is { } day)
        {
            start = TaqvimText.ToLocalInstant(day.ToDateTime(TimeOnly.MinValue));
            end = parsed.Has("--to")
                ? TaqvimText.ToLocalInstant(TaqvimText.ParseDay(parsed.Require("--to", "an end date"), "end date").ToDateTime(TimeOnly.MinValue))
                : start.AddDays(1);
        }
        else if (parsed.Has("--at"))
        {
            start = TaqvimText.ParseWhen(parsed.Require("--at", "a start time"), _clock.GetUtcNow());
            end = parsed.Has("--to")
                ? TaqvimText.ParseWhen(parsed.Require("--to", "an end time"), start)
                : start + ParseDuration(parsed.Has("--dur") ? parsed.Require("--dur", "a duration") : $"{TaqvimDefaults.DefaultEventMinutes}m");
        }
        else
        {
            var captured = TaqvimCapture.TryParse(args[0], _clock.GetUtcNow());
            if (captured is null)
            {
                return Fail(
                    "When does it start? Add --at \"2026-09-21 14:00\", --allday 2026-09-21, or say it in words: "
                    + "taqvim add \"lunch with Sara next Tuesday at 1pm\" (try: taqvim capture \"...\")");
            }

            start = captured.Start;
            end = parsed.Has("--dur") ? captured.Start + ParseDuration(parsed.Require("--dur", "a duration")) : captured.End;
            allDay = captured.IsAllDay && !parsed.Has("--dur");
            capturedTitle = captured.Title;
            capturedTags = captured.Tags;
            capturedLocation = parsed.Has("--location") ? null : captured.Location;
        }

        var rule = parsed.Has("--every") ? BuildRule(parsed) : null;
        var ev = Service.AddEvent(
            capturedTitle ?? args[0],
            start,
            end,
            allDayDay is not null || allDay,
            parsed.Get("--calendar"),
            parsed.Get("--location") ?? capturedLocation,
            notes,
            parsed.Has("--tags") ? parsed.Require("--tags", "tags") : capturedTags,
            rule,
            parsed.Has("--remind") ? TaqvimText.CleanReminders(parsed.Require("--remind", "reminder minutes"), _options.MaxRemindersPerEvent, TaqvimDefaults.MaxReminderMinutes) : null);

        _output.WriteLine(FormattableString.Invariant(
            $"Added #{ev.Id} \"{ev.Title}\" — {TaqvimText.WhenLine(new Occurrence(ev, ev.Start, ev.End))}{(ev.Rule is { Kind: not RecurrenceKind.Once } ? $"  ({ev.Rule.Describe()})" : string.Empty)}"));
        if (ev.Reminders.Count > 0)
        {
            _output.WriteLine(FormattableString.Invariant($"  reminders: {string.Join(", ", ev.Reminders)} min before"));
        }

        return 0;
    }

    private static Recurrence BuildRule(FlagParser parsed)
    {
        var kind = parsed.Require("--every", "a repeat kind").Trim().ToLowerInvariant() switch
        {
            "daily" or "day" => RecurrenceKind.Daily,
            "weekly" or "week" => RecurrenceKind.Weekly,
            "monthly" or "month" => RecurrenceKind.Monthly,
            "yearly" or "annual" or "year" => RecurrenceKind.Yearly,
            var other => throw new TaqvimException(
                $"Unknown repeat kind '{other}' — use daily, weekly, monthly, or yearly."),
        };
        var interval = parsed.Has("--interval") ? TaqvimParse.PositiveInt(parsed.Require("--interval", "interval"), "Interval") : 1;
        List<DayOfWeek> weekdays = [];
        if (parsed.Has("--on"))
        {
            weekdays = [.. parsed.Require("--on", "weekdays").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(ParseWeekday)];
        }

        int? count = null;
        if (parsed.Has("--count"))
        {
            count = TaqvimParse.PositiveInt(parsed.Require("--count", "count"), "Count");
        }

        DateOnly? until = null;
        if (parsed.Has("--until"))
        {
            until = TaqvimText.ParseDay(parsed.Require("--until", "an until date"), "until date");
        }

        if (count is not null && until is not null)
        {
            throw new TaqvimException("Use --count or --until, not both.");
        }

        return new Recurrence(kind, interval, weekdays, count, until);
    }

    private static DayOfWeek ParseWeekday(string token) => token.Trim().ToLowerInvariant() switch
    {
        "mo" or "mon" or "monday" => DayOfWeek.Monday,
        "tu" or "tue" or "tuesday" => DayOfWeek.Tuesday,
        "we" or "wed" or "wednesday" => DayOfWeek.Wednesday,
        "th" or "thu" or "thursday" => DayOfWeek.Thursday,
        "fr" or "fri" or "friday" => DayOfWeek.Friday,
        "sa" or "sat" or "saturday" => DayOfWeek.Saturday,
        "su" or "sun" or "sunday" => DayOfWeek.Sunday,
        var other => throw new TaqvimException($"Unknown weekday '{other}' — use mo, tu, we, th, fr, sa, su."),
    };

    /// <summary>Parses durations like <c>45</c>, <c>90m</c>, <c>2h</c>, <c>1d</c>.</summary>
    private static TimeSpan ParseDuration(string text)
    {
        var trimmed = text.Trim().ToLowerInvariant();
        if (trimmed.Length == 0)
        {
            throw new TaqvimException("Duration cannot be empty — use 45m, 2h, or 1d.");
        }

        var (number, unit) = trimmed[^1] is >= '0' and <= '9'
            ? (trimmed, "m")
            : (trimmed[..^1], trimmed[^1..]);
        if (!int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
        {
            throw new TaqvimException($"Cannot read duration '{text}' — use 45m, 2h, or 1d.");
        }

        return unit switch
        {
            "m" => TimeSpan.FromMinutes(amount),
            "h" => TimeSpan.FromHours(amount),
            "d" => TimeSpan.FromDays(amount),
            _ => throw new TaqvimException($"Cannot read duration '{text}' — use 45m, 2h, or 1d."),
        };
    }

    // ── Lists ──

    private async Task<int> RunListAsync(string[] args, CancellationToken cancellationToken)
    {
        var scope = args.Length > 0 && !args[0].StartsWith('-') ? args[0].ToLowerInvariant() : "upcoming";
        var parsed = ParseFlags(args, "--at", "--calendar", "--tag", "--q", "--limit");
        var calendar = parsed.Get("--calendar");
        var tag = parsed.Get("--tag");
        var query = parsed.Get("--q");

        if (query is { Length: > 0 })
        {
            var matches = Service.Search(query);
            var filtered = matches
                .Where(e => calendar is null || e.Calendar.Equals(calendar, StringComparison.OrdinalIgnoreCase))
                .Where(e => tag is null || TaqvimText.TagsOf(e).Contains(tag, StringComparer.OrdinalIgnoreCase))
                .ToList();
            return RenderEvents($"Search: {query}", filtered);
        }

        var now = _clock.GetUtcNow();
        IReadOnlyList<Occurrence> window = scope switch
        {
            "today" => Service.Day(DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now.UtcDateTime, TimeZoneInfo.Local))),
            "tomorrow" => Service.Day(DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now.UtcDateTime, TimeZoneInfo.Local).AddDays(1))),
            "week" => Service.Week(DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now.UtcDateTime, TimeZoneInfo.Local))),
            "month" => Service.Month(now.ToLocalTime().Year, now.ToLocalTime().Month),
            "upcoming" => Service.Upcoming(
                parsed.Has("--limit") ? TaqvimParse.PositiveInt(parsed.Require("--limit", "limit"), "Limit") : 20,
                calendar, tag),
            _ when DateOnly.TryParseExact(scope, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var scopeDay) =>
                Service.Day(scopeDay),
            _ => throw new TaqvimException(
                $"Unknown list scope '{scope}' — use today, tomorrow, week, month, upcoming, or a date."),
        };

        if (scope is not ("upcoming"))
        {
            window = [.. window
                .Where(o => calendar is null || o.Event.Calendar.Equals(calendar, StringComparison.OrdinalIgnoreCase))
                .Where(o => tag is null || TaqvimText.TagsOf(o.Event).Contains(tag, StringComparer.OrdinalIgnoreCase))];
        }

        var code = RenderOccurrences(ScopeTitle(scope, parsed), window);
        var localToday2 = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(_clock.GetUtcNow().UtcDateTime, TimeZoneInfo.Local));
        DateOnly? extrasDay = scope switch
        {
            "today" => localToday2,
            "tomorrow" => localToday2.AddDays(1),
            _ => DateOnly.TryParseExact(scope, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var scopedDay)
                ? scopedDay
                : null,
        };
        if (extrasDay is { } day && window.Count > 0)
        {
            code = Math.Max(code, await RenderDayExtrasAsync(day, window, cancellationToken).ConfigureAwait(false));
        }

        return code;
    }

    /// <summary>Keystone integrations under a day view: Haft Khan due tasks, Anahita weather on outdoor events, Divan journal.</summary>
    private async Task<int> RenderDayExtrasAsync(DateOnly day, IReadOnlyList<Occurrence> occurrences, CancellationToken cancellationToken)
    {
        if (_dueTasksProvider is { } dueProvider)
        {
            var due = dueProvider();
            if (due.Count > 0)
            {
                _output.WriteLine(FormattableString.Invariant(
                    $"  ⛬ due on Haft Khan: {string.Join(" · ", due.Take(TaqvimDefaults.MaxDueTasksOnAgenda).Select(task => TaqvimText.Clip(task, 80)))}"));
            }
        }

        var outdoorTag = _options.OutdoorTag;
        if (_forecastProvider is { } forecastProvider && outdoorTag.Length > 0
            && occurrences.Any(o => TaqvimText.TagsOf(o.Event).Contains(outdoorTag, StringComparer.OrdinalIgnoreCase)))
        {
            try
            {
                var weather = await forecastProvider(day).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(weather))
                {
                    _output.WriteLine(FormattableString.Invariant($"  ☂ Anahita: {weather}"));
                }
            }
            catch (Exception ex) when (ex is AnahitaException or InvalidOperationException or System.Net.Http.HttpRequestException or TaskCanceledException)
            {
                _error.WriteLine(FormattableString.Invariant($"  (weather unavailable: {ex.Message})"));
            }
        }

        if (_journalProvider is { } journalProvider && journalProvider(day) is { Length: > 0 } hint)
        {
            _output.WriteLine(FormattableString.Invariant($"  📓 {hint}"));
        }

        return 0;
    }

    private static string ScopeTitle(string scope, FlagParser parsed) => scope switch
    {
        "today" => "Today",
        "tomorrow" => "Tomorrow",
        "week" => "This week",
        "month" => "This month",
        "upcoming" => "Upcoming",
        _ => parsed.Has("--at")
            ? FormattableString.Invariant($"On {parsed.Require("--at", "a date")}")
            : FormattableString.Invariant($"On {scope}"),
    };

    private async Task<int> RunDayOffsetAsync(int offsetDays, string title)
    {
        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(_clock.GetUtcNow().UtcDateTime, TimeZoneInfo.Local));
        var day = localToday.AddDays(offsetDays);
        var occurrences = Service.Day(day);
        var code = RenderOccurrences(title, occurrences);
        return Math.Max(code, await RenderDayExtrasAsync(day, occurrences, CancellationToken.None).ConfigureAwait(false));
    }

    private int RunWeek()
    {
        var localToday = TimeZoneInfo.ConvertTimeFromUtc(_clock.GetUtcNow().UtcDateTime, TimeZoneInfo.Local);
        return RenderOccurrences("This week", Service.Week(DateOnly.FromDateTime(localToday)));
    }

    private int RunMonth(string[] args)
    {
        var now = _clock.GetUtcNow().ToLocalTime();
        var (year, month) = args.Length > 0 && args[0].Length == 7 && args[0][4] == '-'
            ? (TaqvimParse.PositiveInt(args[0][..4], "Year"), TaqvimParse.PositiveInt(args[0][5..], "Month"))
            : (now.Year, now.Month);
        return RenderOccurrences($"Calendar {year:0000}-{month:00}", Service.Month(year, month));
    }

    private int RenderOccurrences(string title, IReadOnlyList<Occurrence> occurrences)
    {
        if (occurrences.Count == 0)
        {
            _output.WriteLine($"{title}: nothing scheduled.");
            return 0;
        }

        _output.WriteLine($"── {title} — {occurrences.Count} occurrence(s) ──");
        DateOnly? lastDay = null;
        foreach (var occurrence in occurrences)
        {
            var localStart = TimeZoneInfo.ConvertTimeFromUtc(occurrence.Start.UtcDateTime, TimeZoneInfo.Local);
            var day = DateOnly.FromDateTime(localStart);
            if (day != lastDay)
            {
                _output.WriteLine(FormattableString.Invariant($"  [{TaqvimText.JalaliDate(occurrence.Start)}]"));
                lastDay = day;
            }

            var ev = occurrence.Event;
            var flags = ev.Reminders.Count > 0 ? $" ⏰{ev.Reminders.Count.ToString(CultureInfo.InvariantCulture)}" : string.Empty;
            if (ev.Rule is { Kind: not RecurrenceKind.Once })
            {
                flags += " ↻";
            }

            var extras = ev.Calendar.Equals(TaqvimDefaults.DefaultCalendar, StringComparison.Ordinal)
                ? string.Empty
                : $" [{ev.Calendar}]";
            if (ev.Tags.Length > 0)
            {
                extras += " " + string.Join(' ', ev.Tags.Split(',').Select(part => "#" + part));
            }

            var where = ev.Location.Length > 0 ? $" · {ev.Location}" : string.Empty;
            _output.WriteLine(FormattableString.Invariant(
                $"  #{ev.Id,4}  {TaqvimText.WhenLine(occurrence)}  {ev.Title}{flags}{extras}{where}"));
        }

        return 0;
    }

    private int RenderEvents(string title, List<TaqvimEvent> events)
    {
        if (events.Count == 0)
        {
            _output.WriteLine($"{title}: no events match.");
            return 0;
        }

        _output.WriteLine(FormattableString.Invariant($"── {title} — {events.Count} event(s) ──"));
        foreach (var ev in events)
        {
            var repeat = ev.Rule is { Kind: not RecurrenceKind.Once } ? " ↻" : string.Empty;
            _output.WriteLine(FormattableString.Invariant(
                $"  #{ev.Id,4}  {ev.Title}  {TaqvimText.WhenLine(new Occurrence(ev, ev.Start, ev.End))}{repeat}"));
        }

        return 0;
    }

    // ── Show / edit / reschedule / repeat / remind / delete ──

    private int RunShow(string[] args)
    {
        if (args.Length < 1)
        {
            return Fail("Usage: JameJam taqvim show <id>");
        }

        var ev = Require(TaqvimParse.PositiveInt(args[0], "Event id"));
        _output.WriteLine(FormattableString.Invariant($"#{ev.Id} — {ev.Title}"));
        _output.WriteLine(FormattableString.Invariant(
            $"  Calendar: {ev.Calendar} · {TaqvimText.WhenLine(new Occurrence(ev, ev.Start, ev.End))}"));
        if (ev.Location.Length > 0)
        {
            _output.WriteLine(FormattableString.Invariant($"  Where: {ev.Location}"));
        }

        if (ev.Rule is { Kind: not RecurrenceKind.Once } rule)
        {
            var rail = rule.Until is { } until ? FormattableString.Invariant($" until {until:yyyy-MM-dd}") : rule.Count is { } count ? FormattableString.Invariant($" ×{count}") : string.Empty;
            _output.WriteLine(FormattableString.Invariant($"  Repeats: {rule.Describe()}{rail}"));
        }

        if (ev.Reminders.Count > 0)
        {
            _output.WriteLine(FormattableString.Invariant($"  Reminders: {string.Join(", ", ev.Reminders)} min before"));
        }

        if (ev.Tags.Length > 0)
        {
            _output.WriteLine(FormattableString.Invariant($"  Tags: {ev.Tags.Replace(",", ", ")}"));
        }

        if (ev.Notes.Length > 0)
        {
            _output.WriteLine($"  Notes:");
            foreach (var line in ev.Notes.Split('\n'))
            {
                _output.WriteLine($"    {line}");
            }
        }

        var now = _clock.GetUtcNow();
        var next = Service.Occurrences(now, now.AddDays(_options.MaxAgendaDays))
            .FirstOrDefault(o => o.Event.Id == ev.Id);
        _output.WriteLine(FormattableString.Invariant(
            $"  Next: {(next is null ? "no upcoming occurrence" : TaqvimText.WhenLine(next))} · Jalali: {TaqvimText.JalaliDate(ev.Start)}"));
        return 0;
    }

    private int RunEdit(string[] args)
    {
        if (args.Length < 1)
        {
            return Fail("Usage: JameJam taqvim edit <id> [--title t] [--location l] [--notes n] [--tags a,b] [--calendar c]");
        }

        var parsed = ParseFlags(args[1..], "--title", "--location", "--notes", "--tags", "--calendar");
        if (!parsed.Has("--title") && !parsed.Has("--location") && !parsed.Has("--notes")
            && !parsed.Has("--tags") && !parsed.Has("--calendar"))
        {
            return Fail("Nothing to change — pass --title, --location, --notes, --tags, or --calendar.");
        }

        var ev = Service.EditEvent(
            TaqvimParse.PositiveInt(args[0], "Event id"),
            title: parsed.Get("--title"),
            location: parsed.Get("--location"),
            notes: parsed.Get("--notes"),
            tags: parsed.Get("--tags"),
            calendar: parsed.Get("--calendar"));
        _output.WriteLine(FormattableString.Invariant($"Updated #{ev.Id} {ev.Title}."));
        return 0;
    }

    private int RunReschedule(string[] args)
    {
        if (args.Length < 1)
        {
            return Fail("Usage: JameJam taqvim reschedule <id> --at \"2026-09-22 09:00\" [--to \"...\"] [--dur 45m] [--day 2026-09-22]");
        }

        var parsed = ParseFlags(args[1..], "--at", "--to", "--dur", "--day");
        var ev = Require(TaqvimParse.PositiveInt(args[0], "Event id"));
        DateTimeOffset newStart;
        DateTimeOffset newEnd;
        if (parsed.Has("--day"))
        {
            var day = TaqvimText.ParseDay(parsed.Require("--day", "a date"), "day");
            newStart = TaqvimText.ToLocalInstant(day.ToDateTime(TimeOnly.MinValue));
            newEnd = parsed.Has("--to")
                ? TaqvimText.ToLocalInstant(TaqvimText.ParseDay(parsed.Require("--to", "an end date"), "end date").ToDateTime(TimeOnly.MinValue))
                : newStart.AddDays(1);
        }
        else
        {
            if (!parsed.Has("--at"))
            {
                return Fail("Move it where? Pass --at \"2026-09-22 09:00\" (or --day for all-day events).");
            }

            newStart = TaqvimText.ParseWhen(parsed.Require("--at", "a start time"), _clock.GetUtcNow());
            newEnd = parsed.Has("--to")
                ? TaqvimText.ParseWhen(parsed.Require("--to", "an end time"), newStart)
                : parsed.Has("--dur")
                    ? newStart + ParseDuration(parsed.Require("--dur", "a duration"))
                    : newStart + (ev.End - ev.Start);
        }

        var moved = ev.IsAllDay || parsed.Has("--day")
            ? Service.EditEvent(ev.Id, start: newStart, end: newEnd, allDay: true)
            : Service.Reschedule(ev.Id, newStart, newEnd);
        _output.WriteLine(FormattableString.Invariant(
            $"Moved #{moved.Id} {moved.Title} → {TaqvimText.WhenLine(new Occurrence(moved, moved.Start, moved.End))}."));
        return 0;
    }

    private int RunRepeat(string[] args)
    {
        if (args.Length < 2)
        {
            return Fail("Usage: JameJam taqvim repeat <id> none|daily|weekly|monthly|yearly [--interval 2] [--on mon,wed] [--count 10] [--until 2026-12-31]");
        }

        var parsed = ParseFlags(args[2..], "--interval", "--on", "--count", "--until");
        Recurrence? rule;
        if (args[1].Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            rule = null;
        }
        else
        {
            var full = new List<string> { "--every", args[1] };
            foreach (var flag in new[] { "--interval", "--on", "--count", "--until" })
            {
                if (parsed.Has(flag))
                {
                    full.Add(flag);
                    full.Add(parsed.Require(flag, $"a value for {flag}"));
                }
            }

            var ruleParser = new FlagParser([.. full], ["--every", "--interval", "--on", "--count", "--until"]);
            _ = ruleParser.Collect();
            rule = BuildRule(ruleParser);
        }

        var ev = Service.SetRule(TaqvimParse.PositiveInt(args[0], "Event id"), rule);
        _output.WriteLine(FormattableString.Invariant(
            $"#{ev.Id} {ev.Title} now repeats: {(rule is null ? "never (one-off)" : rule.Describe())}."));
        return 0;
    }

    private int RunRemind(string[] args)
    {
        if (args.Length < 2)
        {
            return Fail("Usage: JameJam taqvim remind <id> none|15,60   (minutes before)");
        }

        IReadOnlyList<int> reminders = args[1].Equals("none", StringComparison.OrdinalIgnoreCase)
            ? []
            : TaqvimText.CleanReminders(args[1], _options.MaxRemindersPerEvent, TaqvimDefaults.MaxReminderMinutes);
        var ev = Service.SetReminders(TaqvimParse.PositiveInt(args[0], "Event id"), reminders);
        _output.WriteLine(FormattableString.Invariant(
            $"#{ev.Id} reminders: {(ev.Reminders.Count == 0 ? "none" : FormattableString.Invariant($"{string.Join(", ", ev.Reminders)} min before"))}."));
        return 0;
    }

    private int RunDelete(string[] args)
    {
        if (args.Length < 1)
        {
            return Fail("Usage: JameJam taqvim delete <id>   (taqvim undo brings it back)");
        }

        var deleted = Service.Delete(TaqvimParse.PositiveInt(args[0], "Event id"));
        _output.WriteLine(FormattableString.Invariant($"Deleted #{deleted.Id} ({deleted.Title}). taqvim undo brings it back."));
        return 0;
    }

    /// <summary>Dry-run natural-language capture: prints what would be created, changes nothing.</summary>
    private int RunCapture(string[] args)
    {
        if (args.Length == 0)
        {
            return Fail("Usage: JameJam taqvim capture \"lunch with Sara next Tuesday at 1pm for 90m #friends\"");
        }

        var sentence = string.Join(' ', args);
        var captured = TaqvimCapture.TryParse(sentence, _clock.GetUtcNow());
        if (captured is null)
        {
            return Fail("No date or time found in that sentence — add words like 'tomorrow', 'next Friday', 'at 3pm', or '2026-09-25'.");
        }

        var preview = new TaqvimEvent(
            0, TaqvimDefaults.DefaultCalendar, captured.Title, captured.Location ?? string.Empty,
            string.Empty, captured.Tags, captured.Start, captured.End, captured.IsAllDay,
            null, [], captured.Start, captured.Start);
        _output.WriteLine("Would create:");
        _output.WriteLine(FormattableString.Invariant($"  title:    {captured.Title}"));
        _output.WriteLine(FormattableString.Invariant(
            $"  when:     {TaqvimText.WhenLine(new Occurrence(preview, captured.Start, captured.End))}"));
        _output.WriteLine(FormattableString.Invariant($"  jalali:   {TaqvimText.JalaliDate(captured.Start)}"));
        if (captured.Location is { } where)
        {
            _output.WriteLine(FormattableString.Invariant($"  location: {where}"));
        }

        if (captured.Tags.Length > 0)
        {
            _output.WriteLine(FormattableString.Invariant($"  tags:     {captured.Tags.Replace(",", ", ")}"));
        }

        _output.WriteLine("Run it with: JameJam taqvim add \"<the same sentence>\"");
        return 0;
    }

    // ── Free & conflicts ──

    private int RunFree(string[] args)
    {
        var parsed = ParseFlags(args, "--day", "--from", "--to", "--min", "--calendar");
        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(_clock.GetUtcNow().UtcDateTime, TimeZoneInfo.Local));
        var day = parsed.Has("--day") ? TaqvimText.ParseDay(parsed.Require("--day", "a date"), "day") : localToday;
        var from = TaqvimText.ParseClock(parsed.Get("--from") ?? TaqvimDefaults.WorkingDayStart, "window start");
        var to = TaqvimText.ParseClock(parsed.Get("--to") ?? TaqvimDefaults.WorkingDayEnd, "window end");
        var min = parsed.Has("--min") ? TaqvimParse.PositiveInt(parsed.Require("--min", "minimum"), "Minimum") : TaqvimDefaults.DefaultFreeSlotMinutes;

        var slots = Service.FreeSlots(day, from, to, min);
        if (slots.Count == 0)
        {
            _output.WriteLine(FormattableString.Invariant($"{day:yyyy-MM-dd}: no free window of {min}+ minutes between {from:HH:mm} and {to:HH:mm}."));
            return 0;
        }

        var jalali = TaqvimText.JalaliDate(TaqvimText.ToLocalInstant(day.ToDateTime(TimeOnly.MinValue)));
        _output.WriteLine(FormattableString.Invariant($"── Free on {day:yyyy-MM-dd} ({jalali}) ──"));
        foreach (var slot in slots)
        {
            var zone = TimeZoneInfo.Local;
            var start = TimeZoneInfo.ConvertTimeFromUtc(slot.Start.UtcDateTime, zone);
            var end = TimeZoneInfo.ConvertTimeFromUtc(slot.End.UtcDateTime, zone);
            _output.WriteLine(FormattableString.Invariant(
                $"  {start:HH:mm}–{end:HH:mm}  ({(int)slot.Duration.TotalMinutes} min)"));
        }

        return 0;
    }

    private int RunConflicts(string[] args)
    {
        var parsed = ParseFlags(args, "--days");
        var days = parsed.Has("--days") ? TaqvimParse.PositiveInt(parsed.Require("--days", "days"), "Days") : 7;
        var now = _clock.GetUtcNow();
        var conflicts = Service.Conflicts(now, now.AddDays(Math.Min(days, _options.MaxAgendaDays)));
        if (conflicts.Count == 0)
        {
            _output.WriteLine($"No conflicts in the next {days} day(s).");
            return 0;
        }

        _output.WriteLine($"── {conflicts.Count} conflict(s) ──");
        foreach (var conflict in conflicts)
        {
            _output.WriteLine(FormattableString.Invariant(
                $"  {TaqvimText.WhenLine(conflict.First)}: \"{conflict.First.Event.Title}\" overlaps \"{conflict.Second.Event.Title}\" ({TaqvimText.WhenLine(conflict.Second)})"));
        }

        return 0;
    }

    // ── ICS ──

    private int RunExport(string[] args)
    {
        if (args.Length < 1)
        {
            return Fail("Usage: JameJam taqvim export <file.ics>");
        }

        var events = Service.All();
        var path = args[0];
        File.WriteAllText(path, Ics.Export(events), System.Text.Encoding.UTF8);
        _output.WriteLine(FormattableString.Invariant($"Exported {events.Count} event(s) to {path}."));
        return 0;
    }

    private int RunImport(string[] args)
    {
        if (args.Length < 1)
        {
            return Fail("Usage: JameJam taqvim import <file.ics>   (taqvim undo reverts it)");
        }

        var path = args[0];
        if (!File.Exists(path))
        {
            return Fail($"No such file: {path}");
        }

        var info = new FileInfo(path);
        if (info.Length > TaqvimDefaults.MaxIcsBytes)
        {
            return Fail($"That file is {info.Length / 1024} KB — over the {TaqvimDefaults.MaxIcsBytes / 1024 / 1024} MB import limit.");
        }

        var parsedEvents = Ics.Parse(File.ReadAllText(path, System.Text.Encoding.UTF8));
        if (parsedEvents.Count > TaqvimDefaults.MaxIcsEvents)
        {
            parsedEvents = [.. parsedEvents.Take(TaqvimDefaults.MaxIcsEvents)];
        }

        Service.PushUndoSnapshot();
        var added = 0;
        foreach (var ics in parsedEvents)
        {
            _ = Service.AddEvent(
                ics.Title,
                ics.Start,
                ics.End,
                ics.IsAllDay,
                calendar: TaqvimDefaults.DefaultCalendar,
                location: ics.Location,
                notes: ics.Notes,
                tags: ics.Tags,
                rule: ics.Rule,
                reminders: ics.Reminders);
            added++;
        }

        _output.WriteLine(FormattableString.Invariant($"Imported {added} event(s). taqvim undo reverts it."));
        return 0;
    }

    // ── Undo & stats ──

    private int RunUndo()
    {
        if (Service.Undo())
        {
            _output.WriteLine("Undone — the calendar is back one step.");
            return 0;
        }

        _output.WriteLine("Nothing to undo.");
        return 0;
    }

    private int RunStats()
    {
        var stats = Service.Stats();
        _output.WriteLine("Taqvim — " + FormattableString.Invariant(
            $"{stats.Events} event(s), {stats.Recurring} recurring, {stats.AllDay} all-day, {stats.Tagged} tagged"));
        _output.WriteLine(FormattableString.Invariant(
            $"  {stats.Reminders} reminder(s) · next 7 days: {stats.NextSevenDays} occurrence(s), {stats.BusyMinutesNextSevenDays} scheduled min"));
        return 0;
    }

    // ── Sync ──

    private async Task<int> RunSyncAsync(string[] args, CancellationToken cancellationToken)
    {
        if (_store is null)
        {
            return Fail(NoStorageMessage);
        }

        if (_syncClientFactory is null)
        {
            return Fail("Sync is not available in this context.");
        }

        string? urlFlag = null;
        string? modeText = null;
        var force = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--url" && i + 1 < args.Length)
            {
                urlFlag = args[++i];
            }
            else if (args[i] is "--force")
            {
                force = true;
            }
            else if (modeText is null && !args[i].StartsWith('-'))
            {
                modeText = args[i];
            }
            else
            {
                return Fail("Usage: JameJam taqvim sync [merge|pull|push] [--url <url>] [--force]");
            }
        }

        var mode = modeText?.Trim().ToLowerInvariant() switch
        {
            null or "" or "merge" or "sync" => SyncMode.Merge,
            "pull" => SyncMode.Pull,
            "push" => SyncMode.Push,
            _ => (SyncMode?)null,
        };
        if (mode is null)
        {
            return Fail($"Unknown sync mode '{modeText}'. Use merge (default), pull, or push.");
        }

        var url = urlFlag
            ?? Environment.GetEnvironmentVariable(SyncDefaults.UrlEnvironmentVariable)
            ?? _syncUrlProvider?.Invoke();
        if (string.IsNullOrWhiteSpace(url))
        {
            return Fail(
                "No sync URL. Pass --url <url>, set JAMEJAM_SYNC_URL, or save it once: "
                + $"JameJam settings set {SettingKeys.TaqvimSyncUrl} <url>");
        }

        var syncOptions = new SyncOptions
        {
            Endpoint = url,
            BearerToken = Environment.GetEnvironmentVariable(SyncDefaults.TokenEnvironmentVariable),
        };
        try
        {
            syncOptions.Validate();
        }
        catch (SyncException ex)
        {
            return Fail(ex.Message);
        }

        var deviceId = _deviceIdProvider?.Invoke();
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            deviceId = Guid.CreateVersion7().ToString();
        }

        var deviceName = TaqvimText.Clip(_deviceNameProvider?.Invoke() ?? Environment.MachineName, TaqvimDefaults.MaxTitleLength);
        var adapter = new TaqvimSyncAdapter(Service, _store);
        try
        {
            var run = await SyncEngine.RunAsync(
                _syncClientFactory(syncOptions),
                adapter,
                deviceId,
                deviceName,
                mode.Value,
                force,
                _clock.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            _output.WriteLine(run.Describe());
            return 0;
        }
        catch (SyncException ex)
        {
            return Fail(ex.Message);
        }
    }

    // ── AI ──

    private async Task<int> RunAiAsync(string[] args, CancellationToken cancellationToken)
    {
        if (_aiCompletion is null)
        {
            return Fail("AI is not available in this context.");
        }

        switch (args[0])
        {
            case "brief":
            {
                var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(_clock.GetUtcNow().UtcDateTime, TimeZoneInfo.Local));
                var day = Service.Day(today);
                var reply = await CompleteAsync(_assistant.BuildBriefPrompt(day, _clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
                _output.WriteLine(reply);
                return 0;
            }

            case "plan":
            {
                var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(_clock.GetUtcNow().UtcDateTime, TimeZoneInfo.Local));
                var week = Service.Week(localToday);
                List<string> free = [];
                for (var offset = 0; offset < 7; offset++)
                {
                    var day = localToday.AddDays(offset);
                    foreach (var slot in Service.FreeSlots(day, TimeOnly.Parse(TaqvimDefaults.WorkingDayStart, CultureInfo.InvariantCulture), TimeOnly.Parse(TaqvimDefaults.WorkingDayEnd, CultureInfo.InvariantCulture), TaqvimDefaults.DefaultFreeSlotMinutes))
                    {
                        var zone = TimeZoneInfo.Local;
                        var start = TimeZoneInfo.ConvertTimeFromUtc(slot.Start.UtcDateTime, zone);
                        var end = TimeZoneInfo.ConvertTimeFromUtc(slot.End.UtcDateTime, zone);
                        free.Add(FormattableString.Invariant($"{day:ddd} {start:HH:mm}-{end:HH:mm} ({(int)slot.Duration.TotalMinutes} min free)"));
                    }
                }

                var due = _dueTasksProvider?.Invoke() ?? [];
                var openItems = due.Take(TaqvimDefaults.MaxDueTasksOnAgenda)
                    .Select(task => FormattableString.Invariant($"open task: {TaqvimText.Clip(task, 120)}"))
                    .ToList();
                var reply = await CompleteAsync(_assistant.BuildPlanPrompt(week, [.. free, .. openItems]), cancellationToken).ConfigureAwait(false);
                _output.WriteLine(reply);
                return 0;
            }

            case "ask":
            {
                var question = ParseQuestion(args[1..]);
                if (question.Length == 0)
                {
                    return Fail("Ask a question: JameJam taqvim ai ask \"when is my next free hour?\"");
                }

                var now = _clock.GetUtcNow();
                var context = Service.Occurrences(now, now.AddDays(7));
                var reply = await CompleteAsync(_assistant.BuildAskPrompt(question, context), cancellationToken).ConfigureAwait(false);
                _output.WriteLine(reply);
                return 0;
            }

            case "capture":
            {
                var sentence = ParseQuestion(args[1..]);
                if (sentence.Length == 0)
                {
                    return Fail("Give me a sentence: JameJam taqvim ai capture \"lunch with Sara next Tuesday at noon\"");
                }

                var reply = await CompleteAsync(ScheduleAssistant.BuildCapturePrompt(sentence, _clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
                var suggestion = ScheduleAssistant.ParseCapture(reply);
                if (suggestion is null)
                {
                    return Fail("The AI could not turn that into a command — try adding it with taqvim add.");
                }

                _output.WriteLine(FormattableString.Invariant($"Suggestion (copy, check, then run):\n  JameJam {suggestion}"));
                return 0;
            }

            default:
                return Fail($"Unknown taqvim ai command '{args[0]}' — use brief, plan, ask, or capture.");
        }
    }

    private async Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken)
    {
        var response = await _aiCompletion!(new AiRequest(prompt), cancellationToken).ConfigureAwait(false);
        return response.Content.Trim().Length == 0
            ? throw new TaqvimException("The AI returned an empty answer — try again.")
            : response.Content.Trim();
    }

    private static string ParseQuestion(string[] args)
    {
        List<string> parts = [];
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith('-'))
            {
                i++; // skip the flag's value; questions carry no flags today
                continue;
            }

            parts.Add(args[i]);
        }

        return string.Join(' ', parts);
    }

    // ── Help & helpers ──

    private int Help()
    {
        _output.WriteLine("Taqvim — the calendar: events, recurrence, reminders, agendas, free time, ICS, sync, AI");
        _output.WriteLine();
        _output.WriteLine("  taqvim add <title> --at \"2026-09-21 14:00\" [--dur 60m] [--allday 2026-09-21]");
        _output.WriteLine("            [--every daily|weekly|monthly|yearly] [--on mon,wed] [--count n] [--until d]");
        _output.WriteLine("            [--calendar c] [--location l] [--tags a,b] [--remind 15,60] [--stdin]");
        _output.WriteLine("  taqvim list [today|tomorrow|week|month|upcoming|YYYY-MM-DD] [--calendar] [--tag] [--q]");
        _output.WriteLine("  taqvim show|delete <id> · edit <id> [--title|--location|--notes|--tags|--calendar]");
        _output.WriteLine("  taqvim reschedule <id> --at \"...\" [--to] [--dur] [--day d] · repeat <id> none|daily|…");
        _output.WriteLine("  taqvim remind <id> none|15,60");
        _output.WriteLine("  taqvim capture \"lunch with Sara next Tuesday at 1pm\"   preview natural-language capture");
        _output.WriteLine("  taqvim free [--day d --from 09:00 --to 17:00 --min 30] · conflicts [--days 7]");
        _output.WriteLine("  taqvim export|import <file.ics> · undo · stats");
        _output.WriteLine("  taqvim sync [merge|pull|push] [--url <url>] [--force]   two-device sync (any server)");
        _output.WriteLine("  taqvim ai brief | plan | ask <q...> | capture \"lunch with Sara tuesday noon\"");
        _output.WriteLine();
        _output.WriteLine("Dates display in both Gregorian and Jalali (Persian) calendars. Alias: cal.");
        return 0;
    }

    private TaqvimEvent Require(long id) =>
        Service.Get(id) ?? throw new TaqvimException($"No event #{id}.");

    private int Fail(string message)
    {
        _error.WriteLine(message);
        return 1;
    }

    private string ReadStdin(string label)
    {
        var text = _stdin.ReadToEnd();
        return string.IsNullOrWhiteSpace(text)
            ? throw new TaqvimException($"No {label} arrived on stdin.")
            : text.TrimEnd();
    }

    private static FlagParser ParseFlags(string[] args, params string[] known)
    {
        var parser = new FlagParser(args, known);
        _ = parser.Collect();
        return parser;
    }

    /// <summary>Strict flag parser: unknown options fail, flags consume the next non-dash token.</summary>
    internal sealed class FlagParser(string[] args, string[] known)
    {
        private readonly Dictionary<string, string?> _values = [];

        public string? Get(string flag) => _values.GetValueOrDefault(flag);

        public bool Has(string flag) => _values.ContainsKey(flag);

        public string Require(string flag, string what) =>
            _values.TryGetValue(flag, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new TaqvimException($"The {flag} flag needs {what}.");

        public FlagParser Collect()
        {
            for (var i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith('-'))
                {
                    continue;
                }

                if (!known.Contains(args[i]))
                {
                    throw new TaqvimException($"Unknown option '{args[i]}'.");
                }

                if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                {
                    _values[args[i]] = args[i + 1];
                    i++;
                }
                else
                {
                    _values[args[i]] = null;
                }
            }

            return this;
        }
    }
}

