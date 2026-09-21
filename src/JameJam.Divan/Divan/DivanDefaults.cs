namespace JameJam.Divan;

/// <summary>
/// Central, documented constants for the Divan pad. Nothing is hardcoded elsewhere:
/// every bound is referenced from here and made customizable through <see cref="DivanOptions"/> rails.
/// </summary>
public static class DivanDefaults
{
    // ── Undo ──

    /// <summary>Undo snapshots kept.</summary>
    public const int UndoDepth = 20;

    /// <summary>Upper rail for the undo depth.</summary>
    public const int UndoDepthBound = 100;

    // ── Field bounds ──

    /// <summary>Longest note title.</summary>
    public const int MaxTitleLength = 150;

    /// <summary>Longest note body (markdown).</summary>
    public const int MaxBodyLength = 100_000;

    /// <summary>Longest notebook name.</summary>
    public const int MaxNotebookNameLength = 60;

    /// <summary>Maximum notebooks in one pad.</summary>
    public const int MaxNotebooks = 100;

    /// <summary>Maximum notes in one pad.</summary>
    public const int MaxNotes = 10_000;

    /// <summary>Maximum tags per note.</summary>
    public const int MaxTagsPerNote = 12;

    /// <summary>Longest single tag.</summary>
    public const int MaxTagLength = 30;

    /// <summary>Longest joined tags string.</summary>
    public const int MaxTagsLength = 400;

    // ── Reading & search ──

    /// <summary>Average reading speed for the reading-time estimate.</summary>
    public const int ReadingWordsPerMinute = 200;

    /// <summary>Maximum search results returned.</summary>
    public const int SearchLimit = 20;

    /// <summary>Upper rail for the search limit.</summary>
    public const int SearchLimitBound = 100;

    /// <summary>Name of the notebook daily notes land in.</summary>
    public const string JournalNotebook = "Journal";

    // ── Import / export ──

    /// <summary>Maximum markdown files one import may bring in.</summary>
    public const int MaxImportFiles = 500;

    // ── AI ──

    /// <summary>Longest note body sent to the AI.</summary>
    public const int MaxAiBodyChars = 4_000;

    /// <summary>Upper rail for the AI body clip.</summary>
    public const int MaxAiBodyBound = 20_000;

    /// <summary>Longest AI question.</summary>
    public const int MaxAiQuestionChars = 400;

    /// <summary>Maximum context notes sent with an AI question.</summary>
    public const int MaxAiContextNotes = 20;

    /// <summary>Maximum tags the AI may suggest.</summary>
    public const int MaxAiTags = 8;

    /// <summary>Default notebook name used when none exists and none is given.</summary>
    public const string DefaultNotebook = "Notebook";
}
