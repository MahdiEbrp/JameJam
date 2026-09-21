namespace JameJam.HaftKhan;

/// <summary>
/// Every Haft Khan tunable in one place — no hardcoded limits anywhere in the pipeline.
/// <see cref="Validate"/> is the safe guard: it bounds each value against named rails.
/// </summary>
public sealed record HaftKhanOptions
{
    // ── Named defaults ──
    /// <summary>Default maximum title length.</summary>
    public const int DefaultMaxTitleLength = 200;

    /// <summary>Default maximum notes length.</summary>
    public const int DefaultMaxNotesLength = 4_000;

    /// <summary>Default maximum number of tasks embedded in an AI summary prompt.</summary>
    public const int DefaultMaxTasksInSummary = 30;

    /// <summary>Default maximum notes length copied into AI prompts.</summary>
    public const int DefaultMaxNotesInPrompt = 1_000;

    /// <summary>Default minimum subtasks requested in an AI breakdown.</summary>
    public const int DefaultBreakdownMinSubtasks = 3;

    /// <summary>Default maximum subtasks requested in an AI breakdown.</summary>
    public const int DefaultBreakdownMaxSubtasks = 7;

    /// <summary>Default maximum tags per task.</summary>
    public const int DefaultMaxTagsPerTask = 10;

    /// <summary>Default maximum length of a single tag.</summary>
    public const int DefaultMaxTagLength = 32;

    /// <summary>Default maximum recurrence interval (every N days/weeks/months).</summary>
    public const int DefaultMaxRecurrenceInterval = 365;

    /// <summary>Default number of undo snapshots kept.</summary>
    public const int DefaultMaxUndoDepth = 50;

    /// <summary>Default tasks shown per kanban board column.</summary>
    public const int DefaultBoardTasksPerColumn = 10;

    /// <summary>Default focus items listed in the weekly review.</summary>
    public const int DefaultReviewFocusCount = 3;

    /// <summary>Default maximum tasks accepted by one import.</summary>
    public const int DefaultMaxImportTasks = 1_000;

    // ── Settable options ──

    /// <summary>Maximum allowed title length (input guard).</summary>
    public int MaxTitleLength { get; init; } = DefaultMaxTitleLength;

    /// <summary>Maximum allowed notes length (input guard).</summary>
    public int MaxNotesLength { get; init; } = DefaultMaxNotesLength;

    /// <summary>Maximum number of open tasks embedded in an AI summary prompt.</summary>
    public int MaxTasksInSummary { get; init; } = DefaultMaxTasksInSummary;

    /// <summary>Maximum notes length copied into AI prompts (keeps prompts bounded).</summary>
    public int MaxNotesInPrompt { get; init; } = DefaultMaxNotesInPrompt;

    /// <summary>Minimum number of subtasks the AI is asked to produce.</summary>
    public int BreakdownMinSubtasks { get; init; } = DefaultBreakdownMinSubtasks;

    /// <summary>Maximum number of subtasks the AI is asked to produce.</summary>
    public int BreakdownMaxSubtasks { get; init; } = DefaultBreakdownMaxSubtasks;

    /// <summary>Maximum tags per task.</summary>
    public int MaxTagsPerTask { get; init; } = DefaultMaxTagsPerTask;

    /// <summary>Maximum length of a single tag.</summary>
    public int MaxTagLength { get; init; } = DefaultMaxTagLength;

    /// <summary>Maximum recurrence interval.</summary>
    public int MaxRecurrenceInterval { get; init; } = DefaultMaxRecurrenceInterval;

    /// <summary>Number of undo snapshots kept (oldest dropped beyond this).</summary>
    public int MaxUndoDepth { get; init; } = DefaultMaxUndoDepth;

    /// <summary>Tasks shown per kanban board column.</summary>
    public int BoardTasksPerColumn { get; init; } = DefaultBoardTasksPerColumn;

    /// <summary>Focus items listed in the weekly review.</summary>
    public int ReviewFocusCount { get; init; } = DefaultReviewFocusCount;

    /// <summary>Maximum tasks accepted by one import.</summary>
    public int MaxImportTasks { get; init; } = DefaultMaxImportTasks;

    // ── Safety rails (the bounds of configuration itself) ──
    private const int MaxTitleLengthBound = 10_000;
    private const int MaxNotesLengthBound = 100_000;
    private const int MaxTasksInSummaryBound = 1_000;
    private const int MaxNotesInPromptBound = 32_000;
    private const int MaxTagsPerTaskBound = 100;
    private const int MaxTagLengthBound = 128;
    private const int MaxRecurrenceIntervalBound = 3_650;
    private const int MaxUndoDepthBound = 1_000;
    private const int BoardTasksPerColumnBound = 100;
    private const int ReviewFocusCountBound = 20;
    private const int MaxImportTasksBound = 100_000;

    /// <summary>Validates every value against its rail. Throws when out of range.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Any value outside its rail.</exception>
    public void Validate()
    {
        if (MaxTitleLength is < 1 or > MaxTitleLengthBound)
            throw new ArgumentOutOfRangeException(nameof(MaxTitleLength), $"MaxTitleLength must be between 1 and {MaxTitleLengthBound}.");

        if (MaxNotesLength is < 1 or > MaxNotesLengthBound)
            throw new ArgumentOutOfRangeException(nameof(MaxNotesLength), $"MaxNotesLength must be between 1 and {MaxNotesLengthBound}.");

        if (MaxTasksInSummary is < 1 or > MaxTasksInSummaryBound)
            throw new ArgumentOutOfRangeException(nameof(MaxTasksInSummary), $"MaxTasksInSummary must be between 1 and {MaxTasksInSummaryBound}.");

        if (MaxNotesInPrompt is < 1 or > MaxNotesInPromptBound)
            throw new ArgumentOutOfRangeException(nameof(MaxNotesInPrompt), $"MaxNotesInPrompt must be between 1 and {MaxNotesInPromptBound}.");

        if (BreakdownMinSubtasks < 1)
            throw new ArgumentOutOfRangeException(nameof(BreakdownMinSubtasks), "BreakdownMinSubtasks must be at least 1.");

        if (BreakdownMaxSubtasks < BreakdownMinSubtasks)
            throw new ArgumentOutOfRangeException(nameof(BreakdownMaxSubtasks), "BreakdownMaxSubtasks must not be smaller than BreakdownMinSubtasks.");

        if (MaxTagsPerTask is < 1 or > MaxTagsPerTaskBound)
            throw new ArgumentOutOfRangeException(nameof(MaxTagsPerTask), $"MaxTagsPerTask must be between 1 and {MaxTagsPerTaskBound}.");

        if (MaxTagLength is < 1 or > MaxTagLengthBound)
            throw new ArgumentOutOfRangeException(nameof(MaxTagLength), $"MaxTagLength must be between 1 and {MaxTagLengthBound}.");

        if (MaxRecurrenceInterval is < 1 or > MaxRecurrenceIntervalBound)
            throw new ArgumentOutOfRangeException(nameof(MaxRecurrenceInterval), $"MaxRecurrenceInterval must be between 1 and {MaxRecurrenceIntervalBound}.");

        if (MaxUndoDepth is < 1 or > MaxUndoDepthBound)
            throw new ArgumentOutOfRangeException(nameof(MaxUndoDepth), $"MaxUndoDepth must be between 1 and {MaxUndoDepthBound}.");

        if (BoardTasksPerColumn is < 1 or > BoardTasksPerColumnBound)
            throw new ArgumentOutOfRangeException(nameof(BoardTasksPerColumn), $"BoardTasksPerColumn must be between 1 and {BoardTasksPerColumnBound}.");

        if (ReviewFocusCount is < 1 or > ReviewFocusCountBound)
            throw new ArgumentOutOfRangeException(nameof(ReviewFocusCount), $"ReviewFocusCount must be between 1 and {ReviewFocusCountBound}.");

        if (MaxImportTasks is < 1 or > MaxImportTasksBound)
            throw new ArgumentOutOfRangeException(nameof(MaxImportTasks), $"MaxImportTasks must be between 1 and {MaxImportTasksBound}.");
    }
}
