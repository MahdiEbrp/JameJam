using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JameJam.Ganjoor;

/// <summary>
/// Versioned, portable JSON backup of the whole wallet. The same format backs undo
/// snapshots, <c>ganjoor export</c>, and <c>ganjoor import</c>. Records are stored in
/// id order so a restore reproduces identical ids.
/// </summary>
public static class GanjoorBackup
{
    /// <summary>Current backup format version.</summary>
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serializes the wallet state (entities in id order).</summary>
    public static string ToJson(
        IReadOnlyList<GanjoorAccount> accounts,
        IReadOnlyList<GanjoorTransaction> transactions,
        IReadOnlyList<GanjoorBudget> budgets,
        IReadOnlyList<GanjoorBill> bills,
        IReadOnlyList<GanjoorGoal> goals,
        IReadOnlyList<GanjoorDebt> debts)
    {
        var file = new BackupFile(
            CurrentVersion,
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            [.. accounts.OrderBy(a => a.Id).Select(ToDto)],
            [.. transactions.OrderBy(t => t.Id).Select(ToDto)],
            [.. budgets.OrderBy(b => b.Category, StringComparer.OrdinalIgnoreCase).Select(ToDto)],
            [.. bills.OrderBy(b => b.Id).Select(ToDto)],
            [.. goals.OrderBy(g => g.Id).Select(ToDto)],
            [.. debts.OrderBy(d => d.Id).Select(ToDto)]);
        return JsonSerializer.Serialize(file, JsonOptions);
    }

    /// <summary>
    /// Parses and validates a backup file.
    /// </summary>
    /// <exception cref="ArgumentException">Unknown version or unreadable structure.</exception>
    public static BackupFile FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        BackupFile? file;
        try
        {
            file = JsonSerializer.Deserialize<BackupFile>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The backup is not a valid Ganjoor file.", ex);
        }

        if (file is null)
            throw new ArgumentException("The backup is empty.");

        if (file.Version != CurrentVersion)
        {
            throw new ArgumentException(
                $"Unsupported backup version {file.Version} (this Ganjoor reads version {CurrentVersion}).");
        }

        return file;
    }

    private static AccountDto ToDto(GanjoorAccount a) => new(
        a.Id, a.Name, a.Currency, a.InitialBalance, a.IsArchived, a.CreatedAt.ToString("O", CultureInfo.InvariantCulture));

    private static TransactionDto ToDto(GanjoorTransaction t) => new(
        t.Id, (int)t.Kind, t.AccountId, t.Amount, t.Category,
        t.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        t.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
        t.TransferToAccountId, [.. t.Tags], t.Notes, t.FromBillId);

    private static BudgetDto ToDto(GanjoorBudget b) => new(b.Category, b.MonthlyLimit);

    private static BillDto ToDto(GanjoorBill b) => new(
        b.Id, b.Name, b.Amount, (int)b.Kind, b.Category, (int)b.Frequency, b.Interval,
        b.NextDue.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        b.CreatedAt.ToString("O", CultureInfo.InvariantCulture));

    private static GoalDto ToDto(GanjoorGoal g) => new(
        g.Id, g.Name, g.Target, g.Contributed,
        g.Deadline?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        g.CreatedAt.ToString("O", CultureInfo.InvariantCulture));

    private static DebtDto ToDto(GanjoorDebt d) => new(
        d.Id, d.Person, d.Amount, d.Settled, d.OwedByMe,
        d.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        d.Notes, d.CreatedAt.ToString("O", CultureInfo.InvariantCulture));

    /// <summary>The backup document.</summary>
    public sealed record BackupFile(
        int Version,
        string ExportedAt,
        List<AccountDto> Accounts,
        List<TransactionDto> Transactions,
        List<BudgetDto> Budgets,
        List<BillDto> Bills,
        List<GoalDto> Goals,
        List<DebtDto> Debts);

    /// <summary>Account record inside a backup.</summary>
    public sealed record AccountDto(
        long Id, string Name, string Currency, decimal InitialBalance, bool IsArchived, string CreatedAt);

    /// <summary>Transaction record inside a backup.</summary>
    public sealed record TransactionDto(
        long Id, int Kind, long AccountId, decimal Amount, string Category, string Date, string CreatedAt,
        long? TransferToAccountId, List<string> Tags, string Notes, long? FromBillId);

    /// <summary>Budget record inside a backup.</summary>
    public sealed record BudgetDto(string Category, decimal MonthlyLimit);

    /// <summary>Bill record inside a backup.</summary>
    public sealed record BillDto(
        long Id, string Name, decimal Amount, int Kind, string Category, int Frequency, int Interval,
        string NextDue, string CreatedAt);

    /// <summary>Goal record inside a backup.</summary>
    public sealed record GoalDto(long Id, string Name, decimal Target, decimal Contributed, string? Deadline, string CreatedAt);

    /// <summary>Debt record inside a backup.</summary>
    public sealed record DebtDto(
        long Id, string Person, decimal Amount, decimal Settled, bool OwedByMe, string? DueDate,
        string Notes, string CreatedAt);
}
