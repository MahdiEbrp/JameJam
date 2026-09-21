using System.Globalization;
using JameJam.Text;

namespace JameJam.HaftKhan;

/// <summary>Thrown when a task id does not match any stored task.</summary>
/// <param name="Id">The missing task's id.</param>
public sealed class TaskNotFoundException(long id)
    : Exception($"Task {id} was not found.")
{
    /// <summary>The missing task's id.</summary>
    public long Id { get; } = id;
}

/// <summary>
/// Validation and parsing for Haft Khan input — the security gate for every task field
/// that comes from the command line (or any other untrusted source).
/// All length limits arrive as parameters (defaulting to named rails in
/// <see cref="HaftKhanOptions"/>) — no magic numbers. Due dates accept strict ISO
/// (<c>yyyy-MM-dd</c>) or natural language ("today", "tomorrow", "next monday", "in 3 days").
/// </summary>
public static class TaskGuard
{
    /// <summary>Sanitizes and validates a task title (required, control-char free, bounded).</summary>
    /// <exception cref="ArgumentException">Empty after sanitization.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Longer than <paramref name="maxLength"/>.</exception>
    public static string CleanTitle(string? title, int maxLength = HaftKhanOptions.DefaultMaxTitleLength) =>
        TextGuard.SanitizeRequired(title, maxLength, nameof(title));

    /// <summary>Sanitizes optional notes (control-char free, bounded; whitespace-only becomes empty).</summary>
    /// <exception cref="ArgumentOutOfRangeException">Longer than <paramref name="maxLength"/>.</exception>
    public static string CleanNotes(string? notes, int maxLength = HaftKhanOptions.DefaultMaxNotesLength) =>
        TextGuard.SanitizeOptional(notes, maxLength, nameof(notes));

    /// <summary>Sanitizes an optional project name (single line, bounded).</summary>
    public static string CleanProject(string? project, int maxLength = HaftKhanOptions.DefaultMaxTitleLength)
    {
        var singleLine = TextGuard
            .SanitizeOptional(project, maxLength, nameof(project))
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        return singleLine.Trim();
    }

    /// <summary>
    /// Sanitizes a comma-separated tag list: trims each tag, drops empties, de-duplicates
    /// case-insensitively, and enforces count/length limits.
    /// </summary>
    /// <exception cref="ArgumentException">More tags than allowed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A tag longer than <paramref name="maxTagLength"/>.</exception>
    public static IReadOnlyList<string> CleanTags(
        string? csv,
        int maxTags = HaftKhanOptions.DefaultMaxTagsPerTask,
        int maxTagLength = HaftKhanOptions.DefaultMaxTagLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTags, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTagLength, 1);

        if (string.IsNullOrWhiteSpace(csv))
            return [];

        List<string> tags = [];
        foreach (var raw in csv.Split(','))
        {
            var tag = TextGuard.SanitizeRequired(raw, maxTagLength, nameof(csv));
            if (tags.Any(existing => existing.Equals(tag, StringComparison.OrdinalIgnoreCase)))
                continue;

            tags.Add(tag);
            if (tags.Count > maxTags)
                throw new ArgumentException($"A task can have at most {maxTags} tags.", nameof(csv));
        }

        return tags;
    }

    /// <summary>Parses a priority name (case-insensitive). Null defaults to <see cref="TaskPriority.Normal"/>.</summary>
    /// <exception cref="ArgumentException">Unknown priority name.</exception>
    public static TaskPriority ParsePriority(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return TaskPriority.Normal;

        var normalized = name.Trim().ToLowerInvariant();
        return normalized switch
        {
            "low" => TaskPriority.Low,
            "normal" => TaskPriority.Normal,
            "high" => TaskPriority.High,
            "critical" => TaskPriority.Critical,
            _ => throw new ArgumentException(
                $"Unknown priority '{name}'. Use low, normal, high, or critical."),
        };
    }

    /// <summary>Parses an effort estimate (case-insensitive). Null defaults to <see cref="TaskEffort.None"/>.</summary>
    /// <exception cref="ArgumentException">Unknown effort name.</exception>
    public static TaskEffort ParseEffort(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return TaskEffort.None;

        var normalized = name.Trim().ToLowerInvariant();
        return normalized switch
        {
            "none" => TaskEffort.None,
            "s" or "small" => TaskEffort.Small,
            "m" or "medium" => TaskEffort.Medium,
            "l" or "large" => TaskEffort.Large,
            "xl" or "xlarge" => TaskEffort.XLarge,
            _ => throw new ArgumentException(
                $"Unknown effort '{name}'. Use none, s, m, l, or xl."),
        };
    }

    /// <summary>Parses a recurrence kind (case-insensitive). Null defaults to <see cref="RecurrenceKind.None"/>.</summary>
    /// <exception cref="ArgumentException">Unknown recurrence name.</exception>
    public static RecurrenceKind ParseRecurrenceKind(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return RecurrenceKind.None;

        var normalized = name.Trim().ToLowerInvariant();
        return normalized switch
        {
            "none" => RecurrenceKind.None,
            "daily" or "day" => RecurrenceKind.Daily,
            "weekly" or "week" => RecurrenceKind.Weekly,
            "monthly" or "month" => RecurrenceKind.Monthly,
            _ => throw new ArgumentException(
                $"Unknown recurrence '{name}'. Use none, daily, weekly, or monthly."),
        };
    }

    /// <summary>Parses a recurrence interval (default 1, bounded by <paramref name="maxInterval"/>).</summary>
    /// <exception cref="ArgumentException">Not a positive integer within bounds.</exception>
    public static int ParseRecurrenceInterval(string? text, int maxInterval = HaftKhanOptions.DefaultMaxRecurrenceInterval)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 1;

        return int.TryParse(text.Trim(), out var interval)
            && interval >= 1
            && interval <= maxInterval
            ? interval
            : throw new ArgumentException(
                $"Invalid interval '{text}'. Use a number between 1 and {maxInterval}.");
    }

    /// <summary>
    /// Parses a due date: strict ISO <c>yyyy-MM-dd</c> or natural language relative to
    /// <paramref name="today"/> — "today", "tomorrow" (or "tmr"), "next week",
    /// "next <c>&lt;weekday&gt;</c>" (always strictly in the future), "in N day(s)/week(s)/month(s)",
    /// or a bare weekday (today when it matches, otherwise the next occurrence).
    /// Null/empty stays null.
    /// </summary>
    /// <exception cref="ArgumentException">Unrecognized date text.</exception>
    public static DateOnly? ParseDueDate(string? text, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var input = text.Trim();
        if (DateOnly.TryParseExact(input, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso))
            return iso;

        return ParseNaturalDate(input.ToLowerInvariant(), today)
            ?? throw new ArgumentException(
                $"Invalid date '{text}'. Use yyyy-MM-dd or natural language like 'tomorrow', 'next monday', 'in 3 days'.");
    }

    /// <summary>Parses a task id typed on the command line.</summary>
    /// <exception cref="ArgumentException">Not a positive integer.</exception>
    public static long ParseId(string? text)
    {
        return long.TryParse(text, out var id) && id > 0
            ? id
            : throw new ArgumentException($"Invalid task id '{text}'. Use a positive number, e.g. 3.");
    }

    /// <summary>Parses a comma-separated list of task ids (deduplicated, order preserved).</summary>
    /// <exception cref="ArgumentException">Any entry is not a positive integer.</exception>
    public static IReadOnlyList<long> ParseIdList(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        List<long> ids = [];
        foreach (var part in text.Split(','))
        {
            var id = ParseId(part);
            if (!ids.Contains(id))
                ids.Add(id);
        }

        return ids;
    }

    /// <summary>Parses a list-view name (case-insensitive). Null defaults to <see cref="TaskView.Open"/>.</summary>
    /// <exception cref="ArgumentException">Unknown view name.</exception>
    public static TaskView ParseView(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return TaskView.Open;

        var normalized = name.Trim().ToLowerInvariant();
        return normalized switch
        {
            "open" => TaskView.Open,
            "all" => TaskView.All,
            "done" => TaskView.Done,
            "today" => TaskView.Today,
            "overdue" => TaskView.Overdue,
            _ => throw new ArgumentException(
                $"Unknown view '{name}'. Use open, all, done, today, or overdue."),
        };
    }

    private static DateOnly? ParseNaturalDate(string input, DateOnly today)
    {
        if (input is "today" or "tod")
            return today;
        if (input is "tomorrow" or "tmr")
            return today.AddDays(1);
        if (input == "next week")
            return today.AddDays(7);

        if (input.StartsWith("in ", StringComparison.Ordinal))
        {
            var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && int.TryParse(parts[1], out var amount) && amount >= 1)
            {
                return parts[2] switch
                {
                    "day" or "days" => today.AddDays(amount),
                    "week" or "weeks" => today.AddDays(7 * amount),
                    "month" or "months" => today.AddMonths(amount),
                    _ => null,
                };
            }

            return null;
        }

        var strictNext = input.StartsWith("next ", StringComparison.Ordinal);
        var weekdayText = strictNext ? input[5..] : input;
        DayOfWeek? weekday = weekdayText switch
        {
            "monday" or "mon" => DayOfWeek.Monday,
            "tuesday" or "tue" => DayOfWeek.Tuesday,
            "wednesday" or "wed" => DayOfWeek.Wednesday,
            "thursday" or "thu" => DayOfWeek.Thursday,
            "friday" or "fri" => DayOfWeek.Friday,
            "saturday" or "sat" => DayOfWeek.Saturday,
            "sunday" or "sun" => DayOfWeek.Sunday,
            _ => (DayOfWeek?)null,
        };

        return weekday.HasValue ? NextWeekday(today, weekday.Value, strictNext) : null;
    }

    private static DateOnly NextWeekday(DateOnly today, DayOfWeek target, bool strictlyFuture)
    {
        var daysAhead = ((int)target - (int)today.DayOfWeek + 7) % 7;
        if (daysAhead == 0 && strictlyFuture)
            daysAhead = 7;

        return today.AddDays(daysAhead);
    }
}
