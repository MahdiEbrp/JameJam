using System.Globalization;
using System.Text;

namespace JameJam.Taqvim;

/// <summary>How an event repeats.</summary>
public enum RecurrenceKind
{
    /// <summary>A single occurrence.</summary>
    Once = 0,

    /// <summary>Repeats every <c>Interval</c> days.</summary>
    Daily = 1,

    /// <summary>Repeats on the chosen weekdays of every <c>Interval</c>-th week.</summary>
    Weekly = 2,

    /// <summary>Repeats on the same day of every <c>Interval</c>-th month (clamped to month length).</summary>
    Monthly = 3,

    /// <summary>Repeats on the same date of every <c>Interval</c>-th year (Feb 29 clamps to Feb 28).</summary>
    Yearly = 4,
}

/// <summary>
/// A recurrence rule. <see cref="Count"/> (occurrences including the first) and
/// <see cref="Until"/> (inclusive last day) are mutually exclusive rails — when both are set
/// the earlier one wins.
/// </summary>
/// <param name="Kind">The repeat pattern.</param>
/// <param name="Interval">Every Nth day/week/month/year (≥ 1).</param>
/// <param name="OnWeekdays">Weekly only: the weekdays to fire on; empty means the event's own weekday.</param>
/// <param name="Count">Total occurrences including the first; null means unbounded (until horizon).</param>
/// <param name="Until">Inclusive last day (UTC); null means unbounded.</param>
public sealed record Recurrence(
    RecurrenceKind Kind,
    int Interval = 1,
    IReadOnlyList<DayOfWeek>? OnWeekdays = null,
    int? Count = null,
    DateOnly? Until = null)
{
    /// <summary>The chosen weekdays (weekly rules); empty means the event's own weekday.</summary>
    public IReadOnlyList<DayOfWeek> Weekdays => OnWeekdays ?? [];

    /// <summary>Short human description, e.g. "every 2 weeks on Mon, Wed" (culture-invariant).</summary>
    public string Describe() => Kind switch
    {
        RecurrenceKind.Once => "once",
        RecurrenceKind.Daily => Interval == 1 ? "every day" : $"every {Interval} days",
        RecurrenceKind.Weekly => WeeklyText(),
        RecurrenceKind.Monthly => Interval == 1 ? "every month" : $"every {Interval} months",
        RecurrenceKind.Yearly => Interval == 1 ? "every year" : $"every {Interval} years",
        _ => "once",
    };

    private string WeeklyText()
    {
        var days = Weekdays.Count == 0
            ? string.Empty
            : " on " + string.Join(", ", Weekdays.OrderBy(d => ((int)d + 6) % 7).Select(ShortName));
        var every = Interval == 1 ? "week" : $"{Interval} weeks";
        return FormattableString.Invariant($"every {every}{days}");
    }

    /// <summary>Three-letter weekday name (Mo, Tu, …) — culture-invariant.</summary>
    public static string ShortName(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "Mo",
        DayOfWeek.Tuesday => "Tu",
        DayOfWeek.Wednesday => "We",
        DayOfWeek.Thursday => "Th",
        DayOfWeek.Friday => "Fr",
        DayOfWeek.Saturday => "Sa",
        _ => "Su",
    };
}

/// <summary>One event on the calendar (the stored master, not an expanded occurrence).</summary>
/// <param name="Id">Assigned by the store.</param>
/// <param name="Calendar">Named grouping calendar ("Work", "Personal", …).</param>
/// <param name="Title">Human title (plain text).</param>
/// <param name="Location">Where it happens (plain text; empty when nowhere).</param>
/// <param name="Notes">Markdown notes.</param>
/// <param name="Tags">Comma-joined tags (empty when untagged).</param>
/// <param name="Start">First occurrence start (UTC; for all-day events, midnight UTC of the day).</param>
/// <param name="End">First occurrence end, exclusive (all-day: midnight UTC of the next day).</param>
/// <param name="IsAllDay">All-day events span whole days and render without a clock time.</param>
/// <param name="Rule">The repeat rule, or null for a one-off.</param>
/// <param name="Reminders">Minutes-before-start offsets, deduplicated and sorted.</param>
/// <param name="CreatedAt">When the event was created.</param>
/// <param name="UpdatedAt">When the event was last changed (drives sync last-write-wins).</param>
/// <param name="SyncId">Stable cross-device identity (version-7 GUID).</param>
public sealed record TaqvimEvent(
    long Id,
    string Calendar,
    string Title,
    string Location,
    string Notes,
    string Tags,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool IsAllDay,
    Recurrence? Rule,
    IReadOnlyList<int> Reminders,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Guid SyncId = default);

/// <summary>One expanded instance of an event inside an agenda window.</summary>
/// <param name="Event">The master event this occurrence belongs to.</param>
/// <param name="Start">Occurrence start (UTC).</param>
/// <param name="End">Occurrence end, exclusive (UTC).</param>
public sealed record Occurrence(TaqvimEvent Event, DateTimeOffset Start, DateTimeOffset End)
{
    /// <summary>Duration of the occurrence.</summary>
    public TimeSpan Duration => End - Start;
}

/// <summary>A deletion marker for sync: "the event with this sync id was deleted at this time".</summary>
/// <param name="SyncId">The deleted event's sync identity.</param>
/// <param name="DeletedAt">When the deletion happened (UTC).</param>
public sealed record TaqvimTombstone(Guid SyncId, DateTimeOffset DeletedAt);

/// <summary>A stretch of free time inside a requested window.</summary>
/// <param name="Start">Slot start (UTC).</param>
/// <param name="End">Slot end (UTC).</param>
public sealed record FreeSlot(DateTimeOffset Start, DateTimeOffset End)
{
    /// <summary>Length of the slot.</summary>
    public TimeSpan Duration => End - Start;
}

/// <summary>Two events (or occurrences) that overlap in time.</summary>
/// <param name="First">The earlier-scheduled occurrence.</param>
/// <param name="Second">The later-scheduled occurrence.</param>
public sealed record Conflict(Occurrence First, Occurrence Second);

/// <summary>Aggregate numbers for `taqvim stats`.</summary>
/// <param name="Events">Stored events (masters).</param>
/// <param name="Recurring">Events with a repeat rule.</param>
/// <param name="AllDay">All-day events.</param>
/// <param name="Tagged">Events carrying at least one tag.</param>
/// <param name="Reminders">Total reminder offsets across all events.</param>
/// <param name="NextSevenDays">Occurrences scheduled in the next 7 days.</param>
/// <param name="BusyMinutesNextSevenDays">Total scheduled minutes in the next 7 days.</param>
public sealed record TaqvimStats(
    int Events,
    int Recurring,
    int AllDay,
    int Tagged,
    int Reminders,
    int NextSevenDays,
    int BusyMinutesNextSevenDays);

/// <summary>Text helpers: clipping, tag hygiene, time parsing, Jalali formatting.</summary>
public static class TaqvimText
{
    private static readonly PersianCalendar Jalali = new();

    /// <summary>Trims and clips text to a hard length (the shared rail helper).</summary>
    public static string Clip(string? value, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(value);
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    /// <summary>Normalizes a comma/semicolon/newline separated tag list; empty result → empty string.</summary>
    public static string CleanTags(string? tags, int maxTags, int maxTagLength, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(tags))
        {
            return string.Empty;
        }

        List<string> kept = [];
        var total = 0;
        foreach (var raw in tags.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tag = Clip(raw, maxTagLength);
            if (tag.Length == 0 || kept.Contains(tag, StringComparer.Ordinal) || kept.Count >= maxTags)
            {
                continue;
            }

            total += tag.Length + (kept.Count > 0 ? 1 : 0);
            if (total > maxLength)
            {
                break;
            }

            kept.Add(tag);
        }

        return string.Join(',', kept);
    }

    /// <summary>Splits a stored tag string back into tags.</summary>
    public static IReadOnlyList<string> TagsOf(TaqvimEvent ev) =>
        ev.Tags.Length == 0 ? [] : ev.Tags.Split(',');

    /// <summary>
    /// Parses a "when" expression into an instant: <c>2026-09-21</c>, <c>2026-09-21 14:30</c>,
    /// <c>2026-09-21T14:30</c>, or <c>14:30</c> (today). Unspecified zones are local time.
    /// </summary>
    public static DateTimeOffset ParseWhen(string text, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var trimmed = text.Trim();

        if (TimeSpan.TryParseExact(trimmed, ["hh\\:mm", "h\\:mm"], CultureInfo.InvariantCulture, out var clock))
        {
            var local = TimeZoneInfo.Local;
            var today = TimeZoneInfo.ConvertTimeFromUtc(now.UtcDateTime, local).Date;
            return ToLocalInstant(today + clock, local);
        }

        string[] formats =
        [
            "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss",
            "yyyy-MM-dd", "yyyy/MM/dd",
        ];
        if (DateTimeOffset.TryParseExact(
                trimmed, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces, out var parsed))
        {
            return parsed;
        }

        throw new TaqvimException(
            FormattableString.Invariant(
                $"Cannot read the time '{trimmed}'. Use '2026-09-21 14:30', '2026-09-21', or '14:30' (today)."));
    }

    /// <summary>Parses a <c>HH:mm</c> clock time or fails friendly.</summary>
    public static TimeOnly ParseClock(string text, string label)
    {
        if (TimeSpan.TryParseExact(text.Trim(), ["hh\\:mm", "h\\:mm"], CultureInfo.InvariantCulture, out var clock))
        {
            return TimeOnly.FromTimeSpan(clock);
        }

        throw new TaqvimException($"Cannot read {label} '{text}' — use 24-hour clock like 09:00.");
    }

    /// <summary>Parses a bare date <c>yyyy-MM-dd</c> or fails friendly.</summary>
    public static DateOnly ParseDay(string text, string label)
    {
        if (DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            return day;
        }

        throw new TaqvimException($"Cannot read {label} '{text}' — use 2026-09-21.");
    }

    /// <summary>Converts a local wall-clock time to an instant via the machine zone.</summary>
    public static DateTimeOffset ToLocalInstant(DateTime local, TimeZoneInfo? zone = null)
    {
        var target = zone ?? TimeZoneInfo.Local;
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), target);
    }

    /// <summary>Formats an instant in the Jalali (Persian) calendar: "Shahrivar 29, 1405".</summary>
    public static string JalaliDate(DateTimeOffset instant)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(instant.UtcDateTime, TimeZoneInfo.Local);
        var year = Jalali.GetYear(local);
        var month = Jalali.GetMonth(local);
        var day = Jalali.GetDayOfMonth(local);
        var name = TaqvimDefaults.JalaliMonths[month - 1];
        return FormattableString.Invariant($"{name} {day}, {year}");
    }

    /// <summary>Formats an occurrence for lists: "Mon 2026-09-21 14:00–15:00" (culture-invariant).</summary>
    public static string WhenLine(Occurrence occurrence, TimeZoneInfo? zone = null)
    {
        var target = zone ?? TimeZoneInfo.Local;
        var start = TimeZoneInfo.ConvertTimeFromUtc(occurrence.Start.UtcDateTime, target);
        var end = TimeZoneInfo.ConvertTimeFromUtc(occurrence.End.UtcDateTime, target);
        if (occurrence.Event.IsAllDay)
        {
            return FormattableString.Invariant($"{start:ddd yyyy-MM-dd} (all day)");
        }

        var sameDay = start.Date == end.Date;
        var clock = sameDay
            ? FormattableString.Invariant($"{start:HH:mm}–{end:HH:mm}")
            : FormattableString.Invariant($"{start:HH:mm}–{end:ddd HH:mm}");
        return FormattableString.Invariant($"{start:ddd yyyy-MM-dd} {clock}");
    }

    /// <summary>Builds the reminder list: deduplicates, sorts, clamps to the rails.</summary>
    public static IReadOnlyList<int> CleanReminders(string? csv, int maxReminders, int maxMinutes)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return [];
        }

        SortedSet<int> kept = [];
        foreach (var raw in csv.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
                && minutes is >= 0 && minutes <= maxMinutes)
            {
                _ = kept.Add(minutes);
            }

            if (kept.Count >= maxReminders)
            {
                break;
            }
        }

        return [.. kept];
    }

    /// <summary>Serializes reminders for storage.</summary>
    public static string RemindersToCsv(IReadOnlyList<int> reminders) =>
        string.Join(',', reminders);

    /// <summary>Parses stored reminders.</summary>
    public static IReadOnlyList<int> RemindersFromCsv(string csv)
    {
        if (csv.Length == 0)
        {
            return [];
        }

        List<int> kept = [];
        foreach (var raw in csv.Split(','))
        {
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
            {
                kept.Add(minutes);
            }
        }

        return kept;
    }

    /// <summary>Escapes text for an ICS property value.</summary>
    public static string IcsEscape(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            builder.Append(ch switch
            {
                '\\' => "\\\\",
                ';' => "\\;",
                ',' => "\\,",
                '\n' => "\\n",
                '\r' => string.Empty,
                _ => ch,
            });
        }

        return builder.ToString();
    }

    /// <summary>Unescapes an ICS property value.</summary>
    public static string IcsUnescape(string text) => text
        .Replace("\\n", "\n", StringComparison.Ordinal)
        .Replace("\\N", "\n", StringComparison.Ordinal)
        .Replace("\\,", ",", StringComparison.Ordinal)
        .Replace("\\;", ";", StringComparison.Ordinal)
        .Replace("\\\\", "\\", StringComparison.Ordinal);
}
