namespace JameJam.Ganjoor;

/// <summary>One money movement: money in, money out, or a transfer between two accounts.</summary>
public enum GanjoorTxKind
{
    /// <summary>Money added to an account.</summary>
    Income = 0,

    /// <summary>Money removed from an account.</summary>
    Expense = 1,

    /// <summary>Money moved between two accounts (never counted as income or spending).</summary>
    Transfer = 2,
}

/// <summary>How often a bill repeats.</summary>
public enum GanjoorFrequency
{
    /// <summary>Every N days.</summary>
    Daily = 0,

    /// <summary>Every N weeks.</summary>
    Weekly = 1,

    /// <summary>Every N months (clamps to month length).</summary>
    Monthly = 2,

    /// <summary>Every N years.</summary>
    Yearly = 3,
}

/// <summary>A wallet account: cash, bank, card — any place money lives.</summary>
/// <param name="Id">Stable identifier used by the CLI.</param>
/// <param name="Name">Display name (sanitized, unique case-insensitively).</param>
/// <param name="Currency">ISO-style currency code (3 letters).</param>
/// <param name="InitialBalance">Balance before any recorded transaction.</param>
/// <param name="CreatedAt">When the account was created (UTC).</param>
public sealed record GanjoorAccount(
    long Id,
    string Name,
    string Currency,
    decimal InitialBalance,
    DateTimeOffset CreatedAt)
{
    /// <summary>Archived accounts are hidden from listings but keep their history.</summary>
    public bool IsArchived { get; init; }
}

/// <summary>A recorded movement of money.</summary>
/// <param name="Id">Stable identifier used by the CLI.</param>
/// <param name="Kind">Income, expense, or transfer.</param>
/// <param name="AccountId">The account money moved out of (or into, for income).</param>
/// <param name="Amount">Always positive — the kind carries the direction.</param>
/// <param name="Category">Spending/income category (sanitized).</param>
/// <param name="Date">When the movement happened (day precision).</param>
/// <param name="CreatedAt">When the record was created (UTC).</param>
public sealed record GanjoorTransaction(
    long Id,
    GanjoorTxKind Kind,
    long AccountId,
    decimal Amount,
    string Category,
    DateOnly Date,
    DateTimeOffset CreatedAt)
{
    /// <summary>Destination account for transfers; null otherwise.</summary>
    public long? TransferToAccountId { get; init; }

    /// <summary>Free-form tags (never null).</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Optional details (empty when absent).</summary>
    public string Notes { get; init; } = string.Empty;

    /// <summary>Bill that produced this transaction, when it did.</summary>
    public long? FromBillId { get; init; }
}

/// <summary>A monthly spending cap for one category.</summary>
/// <param name="Category">The capped category.</param>
/// <param name="MonthlyLimit">Maximum planned spending per month.</param>
public sealed record GanjoorBudget(string Category, decimal MonthlyLimit);

/// <summary>A repeating bill or income (rent, salary, subscriptions).</summary>
/// <param name="Id">Stable identifier used by the CLI.</param>
/// <param name="Name">Display name (e.g. "Rent", "Payday").</param>
/// <param name="Amount">Always positive.</param>
/// <param name="Kind">Income or expense (transfers cannot repeat).</param>
/// <param name="Category">Applied category.</param>
/// <param name="AccountId">Account the bill applies to.</param>
/// <param name="Frequency">Repeat schedule.</param>
/// <param name="Interval">Every N days/weeks/months/years (≥ 1).</param>
/// <param name="NextDue">The next unrecorded occurrence.</param>
/// <param name="CreatedAt">When the bill was created (UTC).</param>
public sealed record GanjoorBill(
    long Id,
    string Name,
    decimal Amount,
    GanjoorTxKind Kind,
    string Category,
    GanjoorFrequency Frequency,
    int Interval,
    DateOnly NextDue,
    DateTimeOffset CreatedAt)
{
    /// <summary>Account the bill records against.</summary>
    public long AccountId { get; init; }
}

/// <summary>A savings goal (virtual envelope).</summary>
/// <param name="Id">Stable identifier used by the CLI.</param>
/// <param name="Name">Display name (e.g. "New laptop").</param>
/// <param name="Target">Amount to reach.</param>
/// <param name="Contributed">Amount set aside so far.</param>
/// <param name="Deadline">Optional target date.</param>
/// <param name="CreatedAt">When the goal was created (UTC).</param>
public sealed record GanjoorGoal(
    long Id,
    string Name,
    decimal Target,
    decimal Contributed,
    DateOnly? Deadline,
    DateTimeOffset CreatedAt);

/// <summary>A personal debt: money a person owes the user, or the user owes them.</summary>
/// <param name="Id">Stable identifier used by the CLI.</param>
/// <param name="Person">Counterparty name (sanitized).</param>
/// <param name="Amount">Total amount in the base currency.</param>
/// <param name="Settled">Amount already repaid.</param>
/// <param name="OwedByMe">True when the user owes the person; false when they owe the user.</param>
/// <param name="DueDate">Optional repayment deadline.</param>
/// <param name="Notes">Optional details.</param>
/// <param name="CreatedAt">When the debt was recorded (UTC).</param>
public sealed record GanjoorDebt(
    long Id,
    string Person,
    decimal Amount,
    decimal Settled,
    bool OwedByMe,
    DateOnly? DueDate,
    string Notes,
    DateTimeOffset CreatedAt);

/// <summary>Spending or income aggregated per category.</summary>
/// <param name="Category">The category.</param>
/// <param name="Amount">Total across the period.</param>
/// <param name="Count">Number of transactions.</param>
public sealed record GanjoorCategoryTotal(string Category, decimal Amount, int Count);

/// <summary>Money in versus money out for one month.</summary>
/// <param name="Month">The reported month.</param>
/// <param name="Income">Total income.</param>
/// <param name="Expenses">Total expenses (transfers excluded).</param>
/// <param name="ByCategory">Expense totals per category, largest first.</param>
public sealed record GanjoorCashFlow(
    DateOnly Month,
    decimal Income,
    decimal Expenses,
    IReadOnlyList<GanjoorCategoryTotal> ByCategory)
{
    /// <summary>Income minus expenses for the month.</summary>
    public decimal Net => Income - Expenses;
}

/// <summary>Budget versus actual spending for one category and month.</summary>
/// <param name="Category">The capped category.</param>
/// <param name="Limit">Monthly limit.</param>
/// <param name="Spent">Actual spending in the month.</param>
public sealed record GanjoorBudgetStatus(string Category, decimal Limit, decimal Spent)
{
    /// <summary>Limit minus spent; negative once over budget.</summary>
    public decimal Remaining => Limit - Spent;

    /// <summary>True when spending passed the limit.</summary>
    public bool Over => Spent > Limit;

    /// <summary>Spending as percent of the limit (0 when the limit is zero).</summary>
    public int PercentUsed => Limit == 0 ? 0 : (int)Math.Round(Spent * 100 / Limit, MidpointRounding.AwayFromZero);
}

/// <summary>Everything the wallet is worth, in the base currency.</summary>
/// <param name="BaseCurrency">Currency all values were converted into.</param>
/// <param name="Accounts">Sum of every account balance.</param>
/// <param name="Receivable">Outstanding debts owed to the user.</param>
/// <param name="Payable">Outstanding debts the user owes.</param>
public sealed record GanjoorNetWorth(string BaseCurrency, decimal Accounts, decimal Receivable, decimal Payable)
{
    /// <summary>Accounts plus receivable minus payable.</summary>
    public decimal Total => Accounts + Receivable - Payable;
}
