using System.Globalization;

namespace JameJam.Ganjoor;

/// <summary>Configuration for the Ganjoor wallet. Immutable — use <c>with</c> to modify.</summary>
public sealed record GanjoorOptions
{
    /// <summary>Base currency: new accounts default to it, reports convert into it.</summary>
    public string DefaultCurrency { get; init; } = GanjoorDefaults.DefaultCurrency;

    /// <summary>Rail for a single amount.</summary>
    public decimal MaxAmount { get; init; } = GanjoorDefaults.MaxAmount;

    /// <summary>How many undo snapshots to keep.</summary>
    public int UndoDepth { get; init; } = GanjoorDefaults.UndoDepth;

    /// <summary>Maximum number of accounts.</summary>
    public int MaxAccounts { get; init; } = GanjoorDefaults.MaxAccounts;

    /// <summary>Maximum rows accepted by one CSV import.</summary>
    public int MaxImportRows { get; init; } = GanjoorDefaults.MaxImportRows;

    /// <summary>Maximum transactions included in the AI insights context.</summary>
    public int AiMaxTransactions { get; init; } = GanjoorDefaults.AiMaxTransactions;

    /// <summary>Maximum categories listed for the AI categorizer.</summary>
    public int AiMaxCategories { get; init; } = GanjoorDefaults.AiMaxCategories;

    /// <summary>Maximum characters of a user question inside AI prompts.</summary>
    public int AiMaxQuestionChars { get; init; } = GanjoorDefaults.AiMaxQuestionChars;

    /// <summary>
    /// Exchange rates into the base currency (1 unit of the key = value base units).
    /// The base currency itself always converts as 1 and may be omitted.
    /// </summary>
    public IReadOnlyDictionary<string, decimal> Rates { get; init; } =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Validates every value against its named rail and the currency policy.
    /// </summary>
    /// <exception cref="GanjoorException">Any value outside its rail.</exception>
    public void Validate()
    {
        try
        {
            _ = MoneyGuard.Currency(DefaultCurrency);
        }
        catch (ArgumentException ex)
        {
            throw new GanjoorException($"Default currency is invalid: {ex.Message}");
        }

        if (MaxAmount < GanjoorDefaults.MinAmount || MaxAmount > GanjoorDefaults.MaxAmountBound)
        {
            throw new GanjoorException(
                $"MaxAmount must be between {GanjoorDefaults.MinAmount.ToString("0.##", CultureInfo.InvariantCulture)} "
                + $"and {GanjoorDefaults.MaxAmountBound.ToString("0.##", CultureInfo.InvariantCulture)}.");
        }

        if (UndoDepth is < 0 or > GanjoorDefaults.UndoDepthBound)
            throw new GanjoorException($"UndoDepth must be between 0 and {GanjoorDefaults.UndoDepthBound}.");

        if (MaxAccounts is < 1 or > GanjoorDefaults.MaxAccountsBound)
            throw new GanjoorException($"MaxAccounts must be between 1 and {GanjoorDefaults.MaxAccountsBound}.");

        if (MaxImportRows is < 1 or > GanjoorDefaults.MaxImportRowsBound)
            throw new GanjoorException($"MaxImportRows must be between 1 and {GanjoorDefaults.MaxImportRowsBound}.");

        if (AiMaxTransactions is < 1 or > GanjoorDefaults.AiMaxTransactions)
            throw new GanjoorException($"AiMaxTransactions must be between 1 and {GanjoorDefaults.AiMaxTransactions}.");

        if (AiMaxCategories is < 1 or > GanjoorDefaults.AiMaxCategories)
            throw new GanjoorException($"AiMaxCategories must be between 1 and {GanjoorDefaults.AiMaxCategories}.");

        if (AiMaxQuestionChars is < 1 or > GanjoorDefaults.AiMaxQuestionCharsBound)
            throw new GanjoorException($"AiMaxQuestionChars must be between 1 and {GanjoorDefaults.AiMaxQuestionCharsBound}.");

        foreach (var (code, rate) in Rates)
        {
            try
            {
                _ = MoneyGuard.Currency(code);
            }
            catch (ArgumentException ex)
            {
                throw new GanjoorException($"Rate currency is invalid: {ex.Message}");
            }

            if (rate <= 0 || rate > GanjoorDefaults.MaxAmountBound)
            {
                throw new GanjoorException(
                    $"Rate for {code.ToUpperInvariant()} must be between 0 and "
                    + $"{GanjoorDefaults.MaxAmountBound.ToString("0.##", CultureInfo.InvariantCulture)}.");
            }
        }
    }
}
