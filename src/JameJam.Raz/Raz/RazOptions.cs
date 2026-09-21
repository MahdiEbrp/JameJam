namespace JameJam.Raz;

/// <summary>
/// Customizable rails for the Raz vault. Nothing is hardcoded: every knob is a validated
/// option with bounds from <see cref="RazDefaults"/>.
/// </summary>
/// <param name="Iterations">PBKDF2-HMAC-SHA512 iteration count.</param>
/// <param name="UndoDepth">How many undo snapshots are kept.</param>
/// <param name="PasswordLength">Default length for generated passwords.</param>
/// <param name="ExpiringSoonDays">"Expiring soon" window for reminders and audits.</param>
/// <param name="OldAfterDays">Secret age (days) after which audits suggest rotation.</param>
/// <param name="WeakScoreThreshold">Strength scores at or below this count as weak.</param>
public sealed record RazOptions(
    int Iterations = RazDefaults.DefaultIterations,
    int UndoDepth = RazDefaults.UndoDepth,
    int PasswordLength = RazDefaults.DefaultPasswordLength,
    int ExpiringSoonDays = RazDefaults.ExpiringSoonDays,
    int OldAfterDays = RazDefaults.OldAfterDays,
    int WeakScoreThreshold = RazDefaults.WeakScoreThreshold)
{
    /// <summary>Validates every bound against its rail.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is outside its rail.</exception>
    public void Validate()
    {
        if (Iterations is < RazDefaults.MinIterations or > RazDefaults.MaxIterations)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Iterations),
                Iterations,
                $"Iterations must be between {RazDefaults.MinIterations} and {RazDefaults.MaxIterations}.");
        }

        if (UndoDepth is < 0 or > RazDefaults.UndoDepthBound)
        {
            throw new ArgumentOutOfRangeException(
                nameof(UndoDepth),
                UndoDepth,
                $"UndoDepth must be between 0 and {RazDefaults.UndoDepthBound}.");
        }

        if (PasswordLength is < RazDefaults.MinPasswordLength or > RazDefaults.MaxPasswordLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PasswordLength),
                PasswordLength,
                $"PasswordLength must be between {RazDefaults.MinPasswordLength} and {RazDefaults.MaxPasswordLength}.");
        }

        if (ExpiringSoonDays is < 1 or > RazDefaults.ExpiringSoonDaysBound)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ExpiringSoonDays),
                ExpiringSoonDays,
                $"ExpiringSoonDays must be between 1 and {RazDefaults.ExpiringSoonDaysBound}.");
        }

        if (OldAfterDays is < 1 or > RazDefaults.ExpiringSoonDaysBound)
        {
            throw new ArgumentOutOfRangeException(
                nameof(OldAfterDays),
                OldAfterDays,
                $"OldAfterDays must be between 1 and {RazDefaults.ExpiringSoonDaysBound}.");
        }

        if (WeakScoreThreshold is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(
                nameof(WeakScoreThreshold),
                WeakScoreThreshold,
                "WeakScoreThreshold must be between 0 and 3.");
        }
    }
}
