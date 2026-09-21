namespace JameJam.Divan;

/// <summary>
/// Customizable rails for the Divan pad. Nothing is hardcoded: every knob is a
/// validated option with bounds from <see cref="DivanDefaults"/>.
/// </summary>
/// <param name="UndoDepth">How many undo snapshots are kept.</param>
/// <param name="SearchLimit">Maximum search results returned.</param>
/// <param name="ReadingWordsPerMinute">Reading speed for the reading-time estimate.</param>
/// <param name="MaxAiBodyChars">Longest note body clipped into an AI prompt.</param>
public sealed record DivanOptions(
    int UndoDepth = DivanDefaults.UndoDepth,
    int SearchLimit = DivanDefaults.SearchLimit,
    int ReadingWordsPerMinute = DivanDefaults.ReadingWordsPerMinute,
    int MaxAiBodyChars = DivanDefaults.MaxAiBodyChars)
{
    /// <summary>Validates every bound against its rail.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is outside its rail.</exception>
    public void Validate()
    {
        if (UndoDepth is < 0 or > DivanDefaults.UndoDepthBound)
        {
            throw new ArgumentOutOfRangeException(
                nameof(UndoDepth),
                UndoDepth,
                $"UndoDepth must be between 0 and {DivanDefaults.UndoDepthBound}.");
        }

        if (SearchLimit is < 1 or > DivanDefaults.SearchLimitBound)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SearchLimit),
                SearchLimit,
                $"SearchLimit must be between 1 and {DivanDefaults.SearchLimitBound}.");
        }

        if (ReadingWordsPerMinute is < 50 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ReadingWordsPerMinute),
                ReadingWordsPerMinute,
                "ReadingWordsPerMinute must be between 50 and 1000.");
        }

        if (MaxAiBodyChars is < 100 or > DivanDefaults.MaxAiBodyBound)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxAiBodyChars),
                MaxAiBodyChars,
                $"MaxAiBodyChars must be between 100 and {DivanDefaults.MaxAiBodyBound}.");
        }
    }
}
