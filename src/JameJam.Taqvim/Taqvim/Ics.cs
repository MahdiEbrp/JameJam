using System.Globalization;
using System.Text;

namespace JameJam.Taqvim;

/// <summary>One event as parsed from (or prepared for) an iCalendar file.</summary>
/// <param name="Title">Summary.</param>
/// <param name="Location">Location (empty when absent).</param>
/// <param name="Notes">Description (empty when absent).</param>
/// <param name="Tags">Categories joined with commas (empty when absent).</param>
/// <param name="Start">Start instant (all-day: midnight UTC of the day).</param>
/// <param name="End">End instant, exclusive (all-day: midnight UTC of the next day).</param>
/// <param name="IsAllDay">All-day flag.</param>
/// <param name="Rule">Parsed RRULE, or null (unknown rules import as one-offs).</param>
/// <param name="Reminders">VALARM offsets in minutes-before.</param>
public sealed record IcsEvent(
    string Title,
    string Location,
    string Notes,
    string Tags,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool IsAllDay,
    Recurrence? Rule,
    IReadOnlyList<int> Reminders);

/// <summary>
/// A defensive RFC 5545 (iCalendar) subset: line unfolding, VEVENT blocks, DTSTART/DTEND
/// (DATE, UTC, and floating forms), RRULE (FREQ/INTERVAL/COUNT/UNTIL/BYDAY), VALARM triggers,
/// and compliant escaping. Unknown properties are ignored; malformed events are skipped.
/// </summary>
public static class Ics
{
    private const string Crlf = "\r\n";

    /// <summary>Parses the VEVENTs of an .ics document. Never throws on content — bad events are skipped.</summary>
    public static IReadOnlyList<IcsEvent> Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        // Unfold: RFC 5545 folds long lines with CRLF + space/tab.
        var lines = new List<string>();
        foreach (var raw in content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (raw.Length > 0 && raw[0] is ' ' or '\t' && lines.Count > 0)
            {
                lines[^1] += raw[1..];
            }
            else
            {
                lines.Add(raw.TrimEnd('\r'));
            }
        }

        List<IcsEvent> events = [];
        Dictionary<string, string>? current = null;
        List<int> reminders = [];
        foreach (var line in lines)
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var name = line[..colon].Split(';')[0].Trim().ToUpperInvariant();
            var value = line[(colon + 1)..].Trim();
            switch (name)
            {
                case "BEGIN" when value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase):
                    current = [];
                    reminders = [];
                    break;
                case "END" when value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase):
                    if (current is { } block)
                    {
                        var parsed = FromBlock(block, reminders);
                        if (parsed is not null)
                        {
                            events.Add(parsed);
                        }
                    }

                    current = null;
                    break;
                default:
                    if (current is not null)
                    {
                        if (name == "TRIGGER")
                        {
                            var minutes = ParseTrigger(value);
                            if (minutes is { } offset && reminders.Count < TaqvimDefaults.MaxRemindersPerEvent)
                            {
                                reminders.Add(offset);
                            }

                            break;
                        }

                        current[name] = value;
                    }

                    break;
            }
        }

        return events;
    }

    /// <summary>Renders events as an iCalendar document.</summary>
    public static string Export(IReadOnlyList<TaqvimEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var builder = new StringBuilder();
        _ = builder.Append("BEGIN:VCALENDAR").Append(Crlf)
            .Append("VERSION:2.0").Append(Crlf)
            .Append("PRODID:-//JameJam//Taqvim//EN").Append(Crlf)
            .Append("CALSCALE:GREGORIAN").Append(Crlf);
        foreach (var ev in events)
        {
            _ = builder.Append("BEGIN:VEVENT").Append(Crlf)
                .Append("UID:").Append(ev.SyncId == Guid.Empty ? Guid.CreateVersion7().ToString() : ev.SyncId.ToString())
                .Append("@jamejam").Append(Crlf)
                .Append("DTSTAMP:").Append(Utc(ev.UpdatedAt == default ? ev.Start : ev.UpdatedAt)).Append(Crlf)
                .Append("SUMMARY:").Append(TaqvimText.IcsEscape(ev.Title)).Append(Crlf);
            if (ev.Location.Length > 0)
            {
                _ = builder.Append("LOCATION:").Append(TaqvimText.IcsEscape(ev.Location)).Append(Crlf);
            }

            if (ev.Notes.Length > 0)
            {
                _ = builder.Append("DESCRIPTION:").Append(TaqvimText.IcsEscape(ev.Notes)).Append(Crlf);
            }

            if (ev.Tags.Length > 0)
            {
                _ = builder.Append("CATEGORIES:").Append(TaqvimText.IcsEscape(ev.Tags.Replace(',', ';'))).Append(Crlf);
            }

            if (ev.IsAllDay)
            {
                _ = builder.Append("DTSTART;VALUE=DATE:").Append(ev.Start.ToString("yyyyMMdd", CultureInfo.InvariantCulture)).Append(Crlf)
                    .Append("DTEND;VALUE=DATE:").Append(ev.End.ToString("yyyyMMdd", CultureInfo.InvariantCulture)).Append(Crlf);
            }
            else
            {
                _ = builder.Append("DTSTART:").Append(Utc(ev.Start)).Append(Crlf)
                    .Append("DTEND:").Append(Utc(ev.End)).Append(Crlf);
            }

            if (Recurrences.ToRrule(ev.Rule) is { } rrule)
            {
                _ = builder.Append("RRULE:").Append(rrule).Append(Crlf);
            }

            foreach (var minutes in ev.Reminders)
            {
                _ = builder.Append("BEGIN:VALARM").Append(Crlf)
                    .Append("ACTION:DISPLAY").Append(Crlf)
                    .Append("TRIGGER:-PT").Append(minutes.ToString(CultureInfo.InvariantCulture)).Append('M').Append(Crlf)
                    .Append("END:VALARM").Append(Crlf);
            }

            _ = builder.Append("END:VEVENT").Append(Crlf);
        }

        _ = builder.Append("END:VCALENDAR").Append(Crlf);
        return builder.ToString();
    }

    private static IcsEvent? FromBlock(Dictionary<string, string> block, List<int> reminders)
    {
        if (!block.TryGetValue("SUMMARY", out var summary) || TaqvimText.IcsUnescape(summary).Trim().Length == 0)
        {
            return null; // an event without a title is not worth importing
        }

        var allDay = false;
        DateTimeOffset start;
        DateTimeOffset end;
        if (block.TryGetValue("DTSTART", out var startText))
        {
            var startDate = ParseDateProperty(startText, out var isDate);
            if (startDate is null)
            {
                return null;
            }

            allDay = isDate;
            start = startDate.Value;
            if (block.TryGetValue("DTEND", out var endText) && ParseDateProperty(endText, out _) is { } parsedEnd)
            {
                end = parsedEnd;
            }
            else if (block.TryGetValue("DURATION", out var durationText)
                && ParseDuration(durationText) is { } duration)
            {
                end = allDay
                    ? start.AddDays(Math.Max(1, duration.Days))
                    : start + duration;
            }
            else
            {
                end = allDay ? start.AddDays(1) : start + TimeSpan.FromMinutes(TaqvimDefaults.DefaultEventMinutes);
            }
        }
        else
        {
            return null;
        }

        var tags = block.TryGetValue("CATEGORIES", out var categories)
            ? TaqvimText.IcsUnescape(categories).Replace(';', ',')
            : string.Empty;
        var notes = block.TryGetValue("DESCRIPTION", out var description) ? TaqvimText.IcsUnescape(description) : string.Empty;
        var location = block.TryGetValue("LOCATION", out var where) ? TaqvimText.IcsUnescape(where) : string.Empty;
        var rule = block.TryGetValue("RRULE", out var rruleText) ? Recurrences.FromRrule(rruleText) : null;

        // Keep windows sane: invert or empty windows become the default duration.
        if (end <= start)
        {
            end = allDay ? start.AddDays(1) : start + TimeSpan.FromMinutes(TaqvimDefaults.DefaultEventMinutes);
        }

        return new IcsEvent(
            TaqvimText.IcsUnescape(summary),
            location,
            notes,
            tags,
            start,
            end,
            allDay,
            rule,
            [.. reminders]);
    }

    private static DateTimeOffset? ParseDateProperty(string value, out bool isDate)
    {
        isDate = false;
        var clean = value.Trim();
        if (clean.EndsWith('Z'))
        {
            if (DateTimeOffset.TryParseExact(
                    clean.TrimEnd('Z'), ["yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmmss'Z'", "yyyyMMdd"],
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var utc))
            {
                return utc;
            }
        }

        if (DateTimeOffset.TryParseExact(
                clean, ["yyyyMMdd'T'HHmmss", "yyyyMMdd"],
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local))
        {
            if (clean.Length == 8)
            {
                isDate = true;
                return new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, TimeSpan.Zero);
            }

            return local;
        }

        // TZID parameters arrive via the name part we dropped; a last-ditch general parse:
        if (DateTimeOffset.TryParse(clean, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var loose))
        {
            return loose;
        }

        return null;
    }

    private static TimeSpan? ParseDuration(string value)
    {
        // Forms like P1D, PT90M, P2DT3H.
        var text = value.Trim().ToUpperInvariant();
        if (text.Length < 3 || text[0] != 'P')
        {
            return null;
        }

        var days = 0;
        var hours = 0;
        var minutes = 0;
        var number = new StringBuilder();
        var inTime = false;
        foreach (var ch in text[1..])
        {
            if (char.IsDigit(ch))
            {
                _ = number.Append(ch);
                continue;
            }

            if (!int.TryParse(number.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount))
            {
                amount = 0;
            }

            _ = number.Clear();
            switch (ch)
            {
                case 'D': days = amount; break;
                case 'T': inTime = true; break;
                case 'H' when inTime: hours = amount; break;
                case 'M' when inTime: minutes = amount; break;
            }
        }

        if (days == 0 && hours == 0 && minutes == 0)
        {
            return null;
        }

        return new TimeSpan(days, hours, minutes, 0);
    }

    private static int? ParseTrigger(string value)
    {
        // Only negative durations are meaningful for "minutes before": -PT15M, -PT1H, -P1D.
        var text = value.Trim().ToUpperInvariant();
        if (!text.StartsWith('-'))
        {
            return null; // alarms that fire after the start are not reminders-before
        }

        var duration = ParseDuration(text[1..]);
        if (duration is null)
        {
            return null;
        }

        var minutes = (int)duration.Value.TotalMinutes;
        return minutes is >= 0 and <= TaqvimDefaults.MaxReminderMinutes ? minutes : null;
    }

    private static string Utc(DateTimeOffset instant) => instant.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
}
