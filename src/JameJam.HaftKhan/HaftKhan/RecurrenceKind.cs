namespace JameJam.HaftKhan;

/// <summary>Recurrence schedule of a task. When completed, the next occurrence is spawned automatically.</summary>
public enum RecurrenceKind
{
    /// <summary>One-off task (default).</summary>
    None = 0,

    /// <summary>Repeats every N days.</summary>
    Daily = 1,

    /// <summary>Repeats every N weeks.</summary>
    Weekly = 2,

    /// <summary>Repeats every N months (day clamps to the target month's length).</summary>
    Monthly = 3,
}

/// <summary>Relative size of the effort a task needs. Purely organizational — no magic unit.</summary>
public enum TaskEffort
{
    /// <summary>No estimate.</summary>
    None = 0,

    /// <summary>Minutes.</summary>
    Small = 1,

    /// <summary>About an hour.</summary>
    Medium = 2,

    /// <summary>Half a day.</summary>
    Large = 3,

    /// <summary>Multiple days.</summary>
    XLarge = 4,
}
