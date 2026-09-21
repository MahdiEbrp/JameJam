namespace JameJam.HaftKhan;

/// <summary>Pure date math for recurring tasks (unit-tested, culture-invariant).</summary>
public static class Recurrence
{
    /// <summary>
    /// Computes the due date of the next occurrence.
    /// </summary>
    /// <param name="kind">Recurrence schedule.</param>
    /// <param name="interval">Every N days/weeks/months (must be positive).</param>
    /// <param name="currentDue">The completed occurrence's due date, when it had one.</param>
    /// <param name="completedOn">The completion date — the base when the task had no due date.</param>
    /// <returns>The next due date.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Interval below 1 or unknown kind.</exception>
    public static DateOnly NextDue(
        RecurrenceKind kind,
        int interval,
        DateOnly? currentDue,
        DateOnly completedOn)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, 1);

        var baseDate = currentDue ?? completedOn;
        return kind switch
        {
            RecurrenceKind.Daily => baseDate.AddDays(interval),
            RecurrenceKind.Weekly => baseDate.AddDays(7 * interval),
            RecurrenceKind.Monthly => baseDate.AddMonths(interval), // DateOnly clamps to month length
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Task is not recurring."),
        };
    }
}
