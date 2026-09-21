using System.Globalization;

namespace JameJam.Taqvim;

/// <summary>
/// Customizable, guard-validated configuration for the Taqvim calendar. Immutable —
/// use <c>with</c> to modify. Validated at construction and before sensitive operations.
/// </summary>
public sealed record TaqvimOptions
{
    /// <summary>Maximum characters in an event title.</summary>
    public int MaxTitleLength { get; init; } = TaqvimDefaults.MaxTitleLength;

    /// <summary>Maximum characters in event notes.</summary>
    public int MaxNotesLength { get; init; } = TaqvimDefaults.MaxNotesLength;

    /// <summary>Upper rail on stored events.</summary>
    public int MaxEvents { get; init; } = TaqvimDefaults.MaxEvents;

    /// <summary>Maximum reminders on one event.</summary>
    public int MaxRemindersPerEvent { get; init; } = TaqvimDefaults.MaxRemindersPerEvent;

    /// <summary>Maximum lookahead (days) of any agenda query.</summary>
    public int MaxAgendaDays { get; init; } = TaqvimDefaults.MaxAgendaDays;

    /// <summary>How far in the future an event may be scheduled (years).</summary>
    public int MaxScheduleHorizonYears { get; init; } = TaqvimDefaults.MaxScheduleHorizonYears;

    /// <summary>Depth of the undo stack (oldest snapshots fall off).</summary>
    public int UndoDepth { get; init; } = TaqvimDefaults.UndoDepth;

    /// <summary>Default search result limit.</summary>
    public int SearchLimit { get; init; } = TaqvimDefaults.SearchLimit;

    /// <summary>Maximum events embedded in one AI prompt.</summary>
    public int MaxAiEvents { get; init; } = TaqvimDefaults.MaxAiEvents;

    /// <summary>Events carrying this tag get the weather forecast appended (empty disables).</summary>
    public string OutdoorTag { get; init; } = TaqvimDefaults.OutdoorTag;

    /// <summary>Validates every value against its named rail; throws outside the rails.</summary>
    /// <exception cref="TaqvimException">Any value outside its rail.</exception>
    public void Validate()
    {
        if (MaxTitleLength is < 1 or > TaqvimDefaults.MaxTitleLength)
        {
            throw new TaqvimException(
                $"MaxTitleLength must be between 1 and {TaqvimDefaults.MaxTitleLength}.");
        }

        if (MaxNotesLength is < 1 or > TaqvimDefaults.MaxNotesLength)
        {
            throw new TaqvimException(
                $"MaxNotesLength must be between 1 and {TaqvimDefaults.MaxNotesLength}.");
        }

        if (MaxEvents is < 1 or > TaqvimDefaults.MaxEvents)
        {
            throw new TaqvimException($"MaxEvents must be between 1 and {TaqvimDefaults.MaxEvents}.");
        }

        if (MaxRemindersPerEvent is < 0 or > TaqvimDefaults.MaxRemindersPerEvent)
        {
            throw new TaqvimException(
                $"MaxRemindersPerEvent must be between 0 and {TaqvimDefaults.MaxRemindersPerEvent}.");
        }

        if (MaxAgendaDays is < 1 or > TaqvimDefaults.MaxAgendaDays)
        {
            throw new TaqvimException($"MaxAgendaDays must be between 1 and {TaqvimDefaults.MaxAgendaDays}.");
        }

        if (MaxScheduleHorizonYears is < 1 or > TaqvimDefaults.MaxScheduleHorizonYears)
        {
            throw new TaqvimException(
                $"MaxScheduleHorizonYears must be between 1 and {TaqvimDefaults.MaxScheduleHorizonYears}.");
        }

        if (UndoDepth is < 0 or > 100)
        {
            throw new TaqvimException("UndoDepth must be between 0 and 100.");
        }

        if (MaxRemindersPerEvent is < 1 or > TaqvimDefaults.MaxRemindersPerEvent)
        {
            throw new TaqvimException(
                $"MaxRemindersPerEvent must be between 1 and {TaqvimDefaults.MaxRemindersPerEvent}.");
        }

        if (UndoDepth is < 0 or > TaqvimDefaults.UndoDepth)
        {
            throw new TaqvimException($"UndoDepth must be between 0 and {TaqvimDefaults.UndoDepth}.");
        }

        if (OutdoorTag.Length > TaqvimDefaults.MaxTagLength)
        {
            throw new TaqvimException($"OutdoorTag may be at most {TaqvimDefaults.MaxTagLength} characters.");
        }

        if (SearchLimit is < 1 or > TaqvimDefaults.SearchLimitBound)
        {
            throw new TaqvimException($"SearchLimit must be between 1 and {TaqvimDefaults.SearchLimitBound}.");
        }

        if (MaxAiEvents is < 1 or > TaqvimDefaults.MaxAiEvents)
        {
            throw new TaqvimException($"MaxAiEvents must be between 1 and {TaqvimDefaults.MaxAiEvents}.");
        }
    }

    /// <summary>Constructs and validates in one step.</summary>
    public static TaqvimOptions CreateValidated() => CreateValidated(new TaqvimOptions());

    /// <summary>Validates a custom instance (null = defaults) and returns it.</summary>
    public static TaqvimOptions CreateValidated(TaqvimOptions? options)
    {
        var effective = options ?? new TaqvimOptions();
        effective.Validate();
        return effective;
    }
}

/// <summary>Culture-invariant parsing helpers shared by the option rails.</summary>
public static class TaqvimParse
{
    /// <summary>Parses a positive integer or fails with a friendly message.</summary>
    public static int PositiveInt(string text, string label) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : throw new TaqvimException($"{label} must be a positive number.");
}
