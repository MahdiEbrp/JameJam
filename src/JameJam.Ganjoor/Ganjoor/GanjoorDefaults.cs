namespace JameJam.Ganjoor;

/// <summary>
/// Named defaults, safety rails, and setting keys for the Ganjoor wallet —
/// no magic numbers anywhere else. Named after the Persian word for treasury.
/// </summary>
public static class GanjoorDefaults
{
    /// <summary>Human-facing service name used in help and errors.</summary>
    public const string ServiceName = "Ganjoor";

    /// <summary>Environment variable carrying the wallet database path.</summary>
    public const string DatabaseEnvironmentVariable = "JAMEJAM_GANJOOR_DB";

    /// <summary>Settings key that stores the base currency code (e.g. <c>"EUR"</c>).</summary>
    public const string CurrencySettingKey = "ganjoor.currency";

    /// <summary>Default base currency for new accounts and reports.</summary>
    public const string DefaultCurrency = "USD";

    /// <summary>Currency shown for values that live outside accounts (debts, goals).</summary>
    public const string BaseCurrencyLabel = "base";

    // ── Financial rails ──

    /// <summary>Default maximum size of a single amount.</summary>
    public const decimal MaxAmount = 1_000_000_000m;

    /// <summary>Smallest meaningful amount (one cent).</summary>
    public const decimal MinAmount = 0.01m;

    /// <summary>Default number of undo snapshots kept.</summary>
    public const int UndoDepth = 20;

    // ── Reporting ──

    /// <summary>How many category rows the cash-flow report shows by default.</summary>
    public const int TopCategories = 8;

    /// <summary>How many upcoming bill days the due view looks ahead.</summary>
    public const int BillHorizonDays = 7;

    /// <summary>Goals at or above this completion percent are marked nearly done.</summary>
    public const int GoalNearlyDonePercent = 90;

    /// <summary>Budgets at or above this percent of the limit draw a warning.</summary>
    public const int BudgetWarnPercent = 80;

    /// <summary>Debts count as overdue within this many days of the due date.</summary>
    public const int DebtOverdueDays = 0;

    // ── Input shape ──

    /// <summary>Maximum length of account, bill, goal, and person names.</summary>
    public const int MaxNameLength = 80;

    /// <summary>Maximum length of transaction/debt notes.</summary>
    public const int MaxNotesLength = 400;

    /// <summary>Maximum number of tags on one transaction.</summary>
    public const int MaxTags = 10;

    /// <summary>Maximum length of one tag or a category name.</summary>
    public const int MaxTagLength = 30;

    /// <summary>Maximum length of a currency code (ISO 4217 = 3 letters).</summary>
    public const int CurrencyCodeLength = 3;

    // ── AI prompt tunables (Soroush layer) ──

    /// <summary>Maximum characters of a user finance question included in AI prompts.</summary>
    public const int AiMaxQuestionChars = 400;

    /// <summary>Maximum transactions included in the AI insights context.</summary>
    public const int AiMaxTransactions = 40;

    /// <summary>Maximum categories listed for the AI categorizer.</summary>
    public const int AiMaxCategories = 40;

    // ── Safety rails (bounds) ──

    /// <summary>Upper rail for a single amount.</summary>
    public const decimal MaxAmountBound = 1_000_000_000_000m;

    /// <summary>Upper rail for the undo depth.</summary>
    public const int UndoDepthBound = 100;

    /// <summary>Upper rail for the number of accounts.</summary>
    public const int MaxAccountsBound = 1000;

    /// <summary>Default cap on the number of accounts.</summary>
    public const int MaxAccounts = 50;

    /// <summary>Upper rail for CSV import rows.</summary>
    public const int MaxImportRowsBound = 50_000;

    /// <summary>Default cap on CSV import rows.</summary>
    public const int MaxImportRows = 5_000;

    /// <summary>Upper rail for budgets, goals, bills, and debts respectively.</summary>
    public const int MaxBudgetsBound = 500;

    /// <summary>Upper rail for goals.</summary>
    public const int MaxGoalsBound = 500;

    /// <summary>Upper rail for bills.</summary>
    public const int MaxBillsBound = 500;

    /// <summary>Upper rail for debts.</summary>
    public const int MaxDebtsBound = 2000;

    /// <summary>Upper rail for AI question length.</summary>
    public const int AiMaxQuestionCharsBound = 2000;
}
