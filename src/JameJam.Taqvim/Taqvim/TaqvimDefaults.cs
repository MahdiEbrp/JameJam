namespace JameJam.Taqvim;

/// <summary>Named defaults for the Taqvim calendar — no magic numbers anywhere else.</summary>
public static class TaqvimDefaults
{
    /// <summary>Maximum characters in an event title.</summary>
    public const int MaxTitleLength = 150;

    /// <summary>Maximum characters in a location.</summary>
    public const int MaxLocationLength = 200;

    /// <summary>Maximum characters in event notes.</summary>
    public const int MaxNotesLength = 10_000;

    /// <summary>Maximum characters in the tag list of one event.</summary>
    public const int MaxTagsLength = 400;

    /// <summary>Maximum characters in a single tag.</summary>
    public const int MaxTagLength = 30;

    /// <summary>Maximum tags on one event.</summary>
    public const int MaxTagsPerEvent = 12;

    /// <summary>Upper rail on stored events (the pad's own safety valve).</summary>
    public const int MaxEvents = 10_000;

    /// <summary>Maximum reminders on one event.</summary>
    public const int MaxRemindersPerEvent = 8;

    /// <summary>Minutes before an event a reminder may fire at most (4 weeks).</summary>
    public const int MaxReminderMinutes = 40_320;

    /// <summary>Maximum interval between recurrences (e.g. "every 1000 weeks" is refused).</summary>
    public const int MaxRecurrenceInterval = 1_000;

    /// <summary>Maximum occurrences a counted recurrence may promise.</summary>
    public const int MaxRecurrenceCount = 10_000;

    /// <summary>How far ahead agendas may look at most (about a year).</summary>
    public const int MaxAgendaDays = 370;

    /// <summary>How far in the future an event may be scheduled (years).</summary>
    public const int MaxScheduleHorizonYears = 10;

    /// <summary>Default duration of a timed event with no explicit end.</summary>
    public const int DefaultEventMinutes = 60;

    /// <summary>Default calendar name for events that do not name one.</summary>
    public const string DefaultCalendar = "Personal";

    /// <summary>Default free-slot window start (a named default, settable per call).</summary>
    public const string WorkingDayStart = "09:00";

    /// <summary>Default free-slot window end.</summary>
    public const string WorkingDayEnd = "17:00";

    /// <summary>Default minimum length of a free slot worth reporting (minutes).</summary>
    public const int DefaultFreeSlotMinutes = 30;

    /// <summary>Upper rail for the free-slot minimum (half a day).</summary>
    public const int MaxFreeSlotMinutes = 720;

    /// <summary>Maximum .ics file size accepted for import (4 MiB).</summary>
    public const long MaxIcsBytes = 4 * 1024 * 1024;

    /// <summary>Maximum events a single .ics import may add.</summary>
    public const int MaxIcsEvents = 2_000;

    /// <summary>Maximum events embedded in one AI prompt.</summary>
    public const int MaxAiEvents = 120;

    /// <summary>Maximum characters of one event's notes in an AI prompt.</summary>
    public const int MaxAiNotesChars = 800;

    /// <summary>Maximum characters of an AI question.</summary>
    public const int MaxAiQuestionChars = 400;

    /// <summary>Default depth of the undo stack.</summary>
    public const int UndoDepth = 20;

    /// <summary>Default search result limit.</summary>
    public const int SearchLimit = 20;

    /// <summary>Upper rail for the search limit.</summary>
    public const int SearchLimitBound = 100;

    /// <summary>Persian month names (Farvardin … Esfand) for Jalali display.</summary>
    public static readonly string[] JalaliMonths =
    [
        "Farvardin", "Ordibehesht", "Khordad", "Tir", "Mordad", "Shahrivar",
        "Mehr", "Aban", "Azar", "Dey", "Bahman", "Esfand",
    ];
    /// <summary>Events tagged with this get the Anahita forecast on day views (customizable via options).</summary>
    public const string OutdoorTag = "outdoor";

    /// <summary>At most this many due Haft Khan tasks appear on a day view.</summary>
    public const int MaxDueTasksOnAgenda = 8;

    /// <summary>English month names, indexed 1-12 (January first) — for natural-language capture.</summary>
    public static readonly string[] MonthNames =
    [
        "January", "February", "March", "April", "May", "June",
        "July", "August", "September", "October", "November", "December",
    ];

}
