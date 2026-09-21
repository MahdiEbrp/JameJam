using System.Globalization;
using System.Text.RegularExpressions;

namespace JameJam.Taqvim;

/// <summary>One proposed event parsed from a natural-language sentence.</summary>
/// <param name="Title">The cleaned title (time/date/tag/location words removed).</param>
/// <param name="Start">Start instant (local zone resolved).</param>
/// <param name="End">End instant.</param>
/// <param name="IsAllDay">True when the sentence carried a date but no clock time.</param>
/// <param name="Tags">Tags captured from #hash tokens.</param>
/// <param name="Location">Location captured from "at/in &lt;Place&gt;" or "@place".</param>
public sealed record CaptureResult(
    string Title,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool IsAllDay,
    string Tags,
    string? Location);

/// <summary>
/// Natural-language capture, Fantastical-style: "lunch with Sara next Tuesday at 1pm for 90m
/// at Cafe Riviera #friends" becomes a timed event. Pure functions, culture-invariant, and
/// conservative — words it cannot read stay in the title, and a sentence with no date/time
/// signal parses to null rather than to an invented time.
/// </summary>
public static partial class TaqvimCapture
{
    /// <summary>Tries to parse a sentence. Returns null when there is no date/time signal at all.</summary>
    public static CaptureResult? TryParse(string sentence, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        if (string.IsNullOrWhiteSpace(sentence))
        {
            return null;
        }

        var target = zone ?? TimeZoneInfo.Local;
        var text = sentence.Trim();
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(now.UtcDateTime, target);
        var today = DateOnly.FromDateTime(nowLocal);

        // 1) Tags: #word tokens anywhere.
        List<string> tags = [];
        text = TagRegex().Replace(text, match =>
        {
            var tag = match.Groups["tag"].Value;
            if (tag.Length > 0 && tags.Count < TaqvimDefaults.MaxTagsPerEvent)
            {
                tags.Add(tag);
                return " ";
            }

            return match.Value;
        });

        // 2) Duration: "for 90m", "for 2 hours", "for 45 min".
        TimeSpan? duration = null;
        var durationMatch = DurationRegex().Match(text);
        if (durationMatch.Success)
        {
            duration = ParseDurationText(durationMatch.Groups["amount"].Value, durationMatch.Groups["unit"].Value.ToLowerInvariant());
            if (duration is not null)
            {
                text = Cut(text, durationMatch.Index, durationMatch.Length);
            }
        }

        // 3) Date: ISO "2026-09-22", then "today"/"tomorrow", then weekday (with optional "next").
        DateOnly? day = null;
        var isoMatch = IsoDateRegex().Match(text);
        if (isoMatch.Success)
        {
            day = DateOnly.ParseExact(isoMatch.Groups["iso"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            text = Cut(text, isoMatch.Index, isoMatch.Length);
        }
        else
        {
            var simpleMatch = SimpleDateRegex().Match(text);
            if (simpleMatch.Success)
            {
                var word = simpleMatch.Groups["word"].Value.ToLowerInvariant();
                day = word switch
                {
                    "today" => today,
                    "tomorrow" => today.AddDays(1),
                    _ => null,
                };
                text = Cut(text, simpleMatch.Index, simpleMatch.Length);
            }
            else
            {
                var weekdayMatch = WeekdayDateRegex().Match(text);
                if (weekdayMatch.Success
                    && ParseWeekdayWord(weekdayMatch.Groups["wd"].Value.ToLowerInvariant()) is { } weekday)
                {
                    var offset = ((int)weekday - (int)nowLocal.DayOfWeek + 7) % 7;
                    day = weekdayMatch.Groups["next"].Success
                        ? today.AddDays(offset + 7) // "next Friday" = the one in the following week
                        : today.AddDays(offset);
                    text = Cut(text, weekdayMatch.Index, weekdayMatch.Length);
                }
            }
        }

        // 3b) "Mar 5" / "on March 5" — month name plus day number (rolls to next year when past).
        if (day is null)
        {
            var monthDay = MonthDayRegex().Match(text);
            if (monthDay.Success
                && MonthNameIndex(monthDay.Groups["mon"].Value) is { } month
                && int.TryParse(monthDay.Groups["dnum"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dayNumber)
                && dayNumber is >= 1 and <= 31)
            {
                var candidate = new DateOnly(nowLocal.Year, month, Math.Min(dayNumber, DateTime.DaysInMonth(nowLocal.Year, month)));
                if (candidate < today)
                {
                    candidate = candidate.AddYears(1);
                }

                day = candidate;
                text = Cut(text, monthDay.Index, monthDay.Length);
            }
        }

        // 4) Time: "at 3", "at 15:30", "at 1pm", "10:05 am", bare "15:30", "noon", "midnight".
        TimeOnly? clock = null;
        var timeMatch = TimeRegex().Match(text);
        if (timeMatch.Success)
        {
            if (timeMatch.Groups["noon"].Success)
            {
                clock = timeMatch.Groups["noon"].Value.Equals("noon", StringComparison.OrdinalIgnoreCase)
                    ? new TimeOnly(12, 0)
                    : new TimeOnly(0, 0);
                text = Cut(text, timeMatch.Index, timeMatch.Length);
            }
            else
            {
                var hourText = timeMatch.Groups["h"].Success
                    ? timeMatch.Groups["h"].Value
                    : timeMatch.Groups["h3"].Success ? timeMatch.Groups["h3"].Value : timeMatch.Groups["h2"].Value;
                var minuteText = timeMatch.Groups["m"].Success
                    ? timeMatch.Groups["m"].Value
                    : timeMatch.Groups["m2"].Success ? timeMatch.Groups["m2"].Value : "0";
                var meridian = timeMatch.Groups["mer"].Success
                    ? timeMatch.Groups["mer"].Value.Trim().ToLowerInvariant()
                    : timeMatch.Groups["mer2"].Success ? timeMatch.Groups["mer2"].Value.Trim().ToLowerInvariant() : string.Empty;
                clock = ParseClockText(hourText, minuteText, meridian);
                if (clock is not null)
                {
                    text = Cut(text, timeMatch.Index, timeMatch.Length);
                }
            }
        }

        if (day is null && clock is null)
        {
            return null; // no calendar signal — the caller keeps the sentence as plain text
        }

        var allDay = clock is null;
        day ??= today;

        // 5) Location: "at/in <Place-ish>" left after time extraction, or "@place".
        string? location = null;
        var locationMatch = LocationRegex().Match(text);
        if (locationMatch.Success)
        {
            var candidate = (locationMatch.Groups["place"].Success
                    ? locationMatch.Groups["place"].Value
                    : locationMatch.Groups["handle"].Value)
                .Trim([' ', ',', '.', '!', '?']);
            if (candidate.Length >= 2 && candidate.Any(char.IsLetter))
            {
                location = candidate;
                text = Cut(text, locationMatch.Index, locationMatch.Length);
            }
        }

        // 6) Title: collapse the leftovers, then peel dangling connectives ("football on" → "football").
        var title = WhitespaceRegex().Replace(text, " ").Trim([' ', '-', ',', ':']);
        title = ConnectiveTailRegex().Replace(title, string.Empty).Trim([' ', '-', ',', ':']);
        if (title.Length == 0)
        {
            title = allDay ? day.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "Event";
        }

        var start = TaqvimText.ToLocalInstant(day.Value.ToDateTime(clock ?? new TimeOnly(0, 0)), target);
        var end = allDay
            ? start.AddDays(1)
            : start + (duration ?? TimeSpan.FromMinutes(TaqvimDefaults.DefaultEventMinutes));
        return new CaptureResult(
            TaqvimText.Clip(title, 200),
            start,
            end,
            allDay,
            tags.Count == 0 ? string.Empty : string.Join(',', tags),
            location);
    }

    /// <summary>Removes a matched span and tidies the join.</summary>
    private static string Cut(string text, int index, int length)
    {
        var before = text[..index].TrimEnd();
        var after = text[(index + length)..].TrimStart();
        return before.Length == 0 ? after : after.Length == 0 ? before : $"{before} {after}";
    }

    private static TimeSpan? ParseDurationText(string amount, string unit)
    {
        if (!int.TryParse(amount, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            return null;
        }

        return unit switch
        {
            "m" or "min" or "mins" or "minute" or "minutes" => TimeSpan.FromMinutes(value),
            "h" or "hr" or "hrs" or "hour" or "hours" => TimeSpan.FromHours(value),
            "d" or "day" or "days" => TimeSpan.FromDays(value),
            _ => null,
        };
    }

    /// <summary>
    /// Reads a clock time. Without a meridian, a bare small hour reads as afternoon
    /// ("lunch at 1" → 13:00) while 8–12 stay morning ("at 9" → 09:00) and 13+ are literal.
    /// </summary>
    private static TimeOnly? ParseClockText(string hourText, string minuteText, string meridian)
    {
        if (!int.TryParse(hourText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hour)
            || !int.TryParse(minuteText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minute))
        {
            return null;
        }

        if (minute is < 0 or > 59)
        {
            return null;
        }

        switch (meridian)
        {
            case "pm" or "p":
                if (hour is >= 1 and <= 11)
                {
                    hour += 12;
                }

                break;
            case "am" or "a":
                if (hour == 12)
                {
                    hour = 0;
                }

                break;
            case "":
                if (hour is >= 1 and <= 7 && hourText.Length == 1)
                {
                    hour += 12; // "at 1"–"at 7" read as afternoon; "at 9" as morning
                }

                break;
        }

        return hour is < 0 or > 23 ? null : new TimeOnly(hour, minute);
    }

    private static DayOfWeek? ParseWeekdayWord(string word) => word switch
    {
        "sun" or "sunday" => DayOfWeek.Sunday,
        "mon" or "monday" => DayOfWeek.Monday,
        "tue" or "tues" or "tuesday" => DayOfWeek.Tuesday,
        "wed" or "wednesday" => DayOfWeek.Wednesday,
        "thu" or "thur" or "thurs" or "thursday" => DayOfWeek.Thursday,
        "fri" or "friday" => DayOfWeek.Friday,
        "sat" or "saturday" => DayOfWeek.Saturday,
        _ => null,
    };

    private static int? MonthNameIndex(string word)
    {
        var lower = word.ToLowerInvariant();
        for (var i = 0; i < TaqvimDefaults.MonthNames.Length; i++)
        {
            var full = TaqvimDefaults.MonthNames[i].ToLowerInvariant();
            if (lower == full || (full.StartsWith(lower, StringComparison.Ordinal) && lower.Length >= 3))
            {
                return i + 1;
            }
        }

        return null;
    }

    [GeneratedRegex(@"#(?<tag>\w[\w-]*)")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\bfor\s+(?<amount>\d{1,4})\s*(?<unit>m|min|mins|minute|minutes|h|hr|hrs|hour|hours|d|day|days)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DurationRegex();

    [GeneratedRegex(@"\b(?<iso>\d{4}-\d{2}-\d{2})\b")]
    private static partial Regex IsoDateRegex();

    [GeneratedRegex(@"\b(?<word>today|tomorrow)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SimpleDateRegex();

    // Full names first so "tuesday" wins over "tue"; trailing \b keeps "monitor" out.
    [GeneratedRegex(@"\b(?:(?<next>next)\s+)?(?<wd>monday|tuesday|wednesday|thursday|friday|saturday|sunday|mon|tue|tues|wed|thu|thur|thurs|fri|sat|sun)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WeekdayDateRegex();

    [GeneratedRegex(@"\b(on\s+)?(?<mon>january|february|march|april|may|june|july|august|september|october|november|december|jan|feb|mar|apr|jun|jul|aug|sep|oct|nov|dec)\.?\s+(?<dnum>\d{1,2})\b", RegexOptions.IgnoreCase)]
    private static partial Regex MonthDayRegex();

    // "at 3", "at 15:30", "at 1pm", "10:05 am", bare "15:30", bare "3pm", "noon", "midnight".
    [GeneratedRegex(
        @"\b(?:at\s+(?<h>\d{1,2})(?:\:(?<m>\d{2}))?(?:\s*(?<mer>am|pm))?\b(?![\d:])|\b(?<h2>\d{1,2}):(?<m2>\d{2})\b|\b(?<h3>\d{1,2})(?:\s*)(?<mer2>am|pm)\b|\b(?<noon>noon|midnight)\b)",
        RegexOptions.IgnoreCase)]
    private static partial Regex TimeRegex();

    [GeneratedRegex(@"\b(?:at|in)\s+(?<place>[A-Z][\w'.]*(?:\s+(?:of|the|de|da|al)?\s*[A-Z][\w'.?]*)*)|@(?<handle>[\w][\w-]*)")]
    private static partial Regex LocationRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\s*\b(?:on|at|in|for|next|by|to)\b\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectiveTailRegex();
}
