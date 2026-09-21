using System.Globalization;

namespace JameJam.Taqvim;

/// <summary>
/// The recurrence engine: expands a rule into concrete occurrence starts inside a window.
/// Deterministic, bounded (window + count rails), and clamped (31st → month length,
/// Feb 29 → Feb 28) so rules never explode.
/// </summary>
public static class Recurrences
{
    /// <summary>
    /// Yields every occurrence start of <paramref name="first"/> under <paramref name="rule"/>,
    /// up to <paramref name="windowEnd"/> (inclusive of events overlapping the window start).
    /// </summary>
    public static IEnumerable<DateTimeOffset> Starts(Recurrence rule, DateTimeOffset first, DateTimeOffset windowEnd)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rule.Kind == RecurrenceKind.Once)
        {
            if (first <= windowEnd)
            {
                yield return first;
            }

            yield break;
        }

        var produced = 0;
        foreach (var start in Expand(rule, first, windowEnd))
        {
            if (start > windowEnd)
            {
                yield break;
            }

            produced++;
            yield return start;
            if (rule.Count is { } count && produced >= count)
            {
                yield break;
            }
        }
    }

    /// <summary>Expands candidate starts without applying the count cap (the engine's iterator).</summary>
    private static IEnumerable<DateTimeOffset> Expand(Recurrence rule, DateTimeOffset first, DateTimeOffset windowEnd)
    {
        // The until-rail (inclusive day, evaluated in UTC) bounds every unbounded rule.
        // Inclusive through the whole until day: 23:59:59.9999999 on that date.
        var until = rule.Until is { } untilDay
            ? new DateTimeOffset(untilDay.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(1).AddTicks(-1)
            : windowEnd;

        switch (rule.Kind)
        {
            case RecurrenceKind.Daily:
            {
                var next = first;
                while (next <= until)
                {
                    yield return next;
                    next = next.AddDays(rule.Interval);
                }
            }

            break;

            case RecurrenceKind.Weekly:
            {
                var weekdays = rule.Weekdays.Count == 0 ? [first.DayOfWeek] : rule.Weekdays;
                var wanted = weekdays.Distinct().ToList();
                var firstDay = first.Date;
                var day = firstDay;
                var stepped = 0;
                while (day <= until)
                {
                    // Weeks are anchored to the first occurrence's week; the interval skips weeks.
                    var weeks = (int)((day.Date - firstDay).TotalDays / 7);
                    if (wanted.Contains(day.DayOfWeek) && weeks % rule.Interval == 0)
                    {
                        yield return day + first.TimeOfDay;
                    }

                    day = day.AddDays(1);
                    if (++stepped > TaqvimDefaults.MaxAgendaDays * 2 + 8)
                    {
                        yield break; // defensive horizon — rules never iterate unbounded
                    }
                }
            }

            break;

            case RecurrenceKind.Monthly:
            {
                var anchor = DateOnly.FromDateTime(first.Date);
                var monthCursor = new DateOnly(anchor.Year, anchor.Month, 1);
                var lastMonth = new DateOnly(until.Year, until.Month, 1);
                var index = 0;
                while (monthCursor <= lastMonth)
                {
                    if (index % rule.Interval == 0)
                    {
                        var clamped = Math.Min(anchor.Day, DateTime.DaysInMonth(monthCursor.Year, monthCursor.Month));
                        var candidateDay = monthCursor.AddDays(clamped - 1);
                        if (candidateDay >= anchor)
                        {
                            var candidate = candidateDay.ToDateTime(TimeOnly.MinValue) + first.TimeOfDay;
                            if (candidate <= until)
                            {
                                yield return candidate;
                            }
                        }
                    }

                    monthCursor = monthCursor.AddMonths(1);
                    index++;
                }
            }

            break;

            case RecurrenceKind.Yearly:
            {
                var anchor = first.Date;
                var year = anchor.Year;
                while (new DateOnly(year, 1, 1).ToDateTime(TimeOnly.MinValue) <= until)
                {
                    if ((year - anchor.Year) % rule.Interval == 0)
                    {
                        var day = anchor.Day == 29 && anchor.Month == 2 && !DateTime.IsLeapYear(year)
                            ? 28
                            : anchor.Day;
                        var candidate = new DateTime(year, anchor.Month, day) + first.TimeOfDay;
                        if (candidate >= first && candidate <= until)
                        {
                            yield return candidate;
                        }
                    }

                    year++;
                }
            }

            break;
        }
    }

    /// <summary>Expands an event into full occurrences (with its duration) inside the window.</summary>
    public static IEnumerable<Occurrence> Occurrences(TaqvimEvent ev, DateTimeOffset windowStart, DateTimeOffset windowEnd)
    {
        ArgumentNullException.ThrowIfNull(ev);
        var duration = ev.End - ev.Start;
        var rule = ev.Rule ?? new Recurrence(RecurrenceKind.Once);
        foreach (var start in Starts(rule, ev.Start, windowEnd))
        {
            var end = start + duration;
            if (end > windowStart)
            {
                yield return new Occurrence(ev, start, end);
            }
        }
    }

    /// <summary>
    /// Parses an RFC 5545 RRULE subset (FREQ, INTERVAL, COUNT, UNTIL, BYDAY). Unknown or
    /// malformed rules return null — callers import the event as a one-off.
    /// </summary>
    public static Recurrence? FromRrule(string rrule)
    {
        if (string.IsNullOrWhiteSpace(rrule))
        {
            return null;
        }

        string? freq = null;
        var interval = 1;
        int? count = null;
        DateOnly? until = null;
        List<DayOfWeek> days = [];
        foreach (var part in rrule.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var key = part[..eq].Trim().ToUpperInvariant();
            var value = part[(eq + 1)..].Trim();
            switch (key)
            {
                case "FREQ":
                    freq = value.ToUpperInvariant();
                    break;
                case "INTERVAL":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                        && parsed is >= 1 and <= TaqvimDefaults.MaxRecurrenceInterval)
                    {
                        interval = parsed;
                    }

                    break;
                case "COUNT":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var counted)
                        && counted is >= 1 and <= TaqvimDefaults.MaxRecurrenceCount)
                    {
                        count = counted;
                    }

                    break;
                case "UNTIL":
                    until = ParseUntil(value);
                    break;
                case "BYDAY" when freq == "WEEKLY":
                    days = [.. ParseByDays(value)];
                    break;
            }
        }

        return freq switch
        {
            "DAILY" => new Recurrence(RecurrenceKind.Daily, interval, [], count, until),
            "WEEKLY" => new Recurrence(RecurrenceKind.Weekly, interval, days, count, until),
            "MONTHLY" => new Recurrence(RecurrenceKind.Monthly, interval, [], count, until),
            "YEARLY" => new Recurrence(RecurrenceKind.Yearly, interval, [], count, until),
            _ => null,
        };
    }

    /// <summary>Renders the rule as an RRULE value, or null for one-off events.</summary>
    public static string? ToRrule(Recurrence? rule)
    {
        if (rule is null || rule.Kind == RecurrenceKind.Once)
        {
            return null;
        }

        var freq = rule.Kind switch
        {
            RecurrenceKind.Daily => "DAILY",
            RecurrenceKind.Weekly => "WEEKLY",
            RecurrenceKind.Monthly => "MONTHLY",
            _ => "YEARLY",
        };
        var builder = new System.Text.StringBuilder($"FREQ={freq}");
        if (rule.Interval != 1)
        {
            _ = builder.Append(FormattableString.Invariant($";INTERVAL={rule.Interval}"));
        }

        if (rule.Weekdays.Count > 0)
        {
            _ = builder.Append(";BYDAY=").Append(string.Join(',', rule.Weekdays.Select(ToByDay)));
        }

        if (rule.Until is { } until)
        {
            _ = builder.Append(FormattableString.Invariant($";UNTIL={until:yyyyMMdd}"));
        }
        else if (rule.Count is { } count)
        {
            _ = builder.Append(FormattableString.Invariant($";COUNT={count}"));
        }

        return builder.ToString();
    }

    private static IEnumerable<DayOfWeek> ParseByDays(string value)
    {
        foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return token.ToUpperInvariant() switch
            {
                "MO" => DayOfWeek.Monday,
                "TU" => DayOfWeek.Tuesday,
                "WE" => DayOfWeek.Wednesday,
                "TH" => DayOfWeek.Thursday,
                "FR" => DayOfWeek.Friday,
                "SA" => DayOfWeek.Saturday,
                "SU" => DayOfWeek.Sunday,
                _ => DayOfWeek.Sunday,
            };
        }
    }

    private static string ToByDay(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "MO",
        DayOfWeek.Tuesday => "TU",
        DayOfWeek.Wednesday => "WE",
        DayOfWeek.Thursday => "TH",
        DayOfWeek.Friday => "FR",
        DayOfWeek.Saturday => "SA",
        _ => "SU",
    };

    private static DateOnly? ParseUntil(string value)
    {
        if (DateOnly.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            return day;
        }

        if (DateOnly.TryParseExact(value[..Math.Min(8, value.Length)], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var prefix))
        {
            return prefix;
        }

        return null;
    }
}
