namespace JameJam.Ganjoor;

/// <summary>In-memory wallet store — tests and non-persistent contexts.</summary>
public sealed class MemoryGanjoorStore : IGanjoorStore
{
    private readonly List<GanjoorAccount> _accounts = [];
    private readonly List<GanjoorTransaction> _transactions = [];
    private readonly List<GanjoorBudget> _budgets = [];
    private readonly List<GanjoorBill> _bills = [];
    private readonly List<GanjoorGoal> _goals = [];
    private readonly List<GanjoorDebt> _debts = [];
    private readonly List<string> _undo = [];
    private long _accountId;
    private long _transactionId;
    private long _billId;
    private long _goalId;
    private long _debtId;

    /// <inheritdoc />
    public GanjoorAccount AddAccount(GanjoorAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        var stored = account with { Id = ++_accountId };
        _accounts.Add(stored);
        return stored;
    }

    /// <inheritdoc />
    public void UpdateAccount(GanjoorAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        Replace(_accounts, account);
    }

    /// <inheritdoc />
    public bool RemoveAccount(long id) => Remove(_accounts, id);

    /// <inheritdoc />
    public GanjoorAccount? FindAccount(long id) => _accounts.FirstOrDefault(a => a.Id == id);

    /// <inheritdoc />
    public GanjoorAccount? FindAccountByName(string name) => _accounts.FirstOrDefault(
        a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public IReadOnlyList<GanjoorAccount> ListAccounts() => [.. _accounts.OrderBy(a => a.Id)];

    /// <inheritdoc />
    public GanjoorTransaction AddTransaction(GanjoorTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var stored = transaction with { Id = ++_transactionId };
        _transactions.Add(stored);
        return stored;
    }

    /// <inheritdoc />
    public bool RemoveTransaction(long id) => Remove(_transactions, id);

    /// <inheritdoc />
    public void UpdateTransaction(GanjoorTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        Replace(_transactions, transaction);
    }

    /// <inheritdoc />
    public GanjoorTransaction? FindTransaction(long id) => _transactions.FirstOrDefault(t => t.Id == id);

    /// <inheritdoc />
    public IReadOnlyList<GanjoorTransaction> ListTransactions() =>
        [.. _transactions.OrderByDescending(t => t.Date).ThenByDescending(t => t.Id)];

    /// <inheritdoc />
    public void SetBudget(GanjoorBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        _budgets.RemoveAll(b => string.Equals(b.Category, budget.Category, StringComparison.OrdinalIgnoreCase));
        _budgets.Add(budget);
    }

    /// <inheritdoc />
    public bool RemoveBudget(string category) =>
        _budgets.RemoveAll(b => string.Equals(b.Category, category, StringComparison.OrdinalIgnoreCase)) > 0;

    /// <inheritdoc />
    public IReadOnlyList<GanjoorBudget> ListBudgets() =>
        [.. _budgets.OrderBy(b => b.Category, StringComparer.OrdinalIgnoreCase)];

    /// <inheritdoc />
    public GanjoorBill AddBill(GanjoorBill bill)
    {
        ArgumentNullException.ThrowIfNull(bill);
        var stored = bill with { Id = ++_billId };
        _bills.Add(stored);
        return stored;
    }

    /// <inheritdoc />
    public void UpdateBill(GanjoorBill bill)
    {
        ArgumentNullException.ThrowIfNull(bill);
        Replace(_bills, bill);
    }

    /// <inheritdoc />
    public bool RemoveBill(long id) => Remove(_bills, id);

    /// <inheritdoc />
    public IReadOnlyList<GanjoorBill> ListBills() => [.. _bills.OrderBy(b => b.NextDue).ThenBy(b => b.Id)];

    /// <inheritdoc />
    public GanjoorGoal AddGoal(GanjoorGoal goal)
    {
        ArgumentNullException.ThrowIfNull(goal);
        var stored = goal with { Id = ++_goalId };
        _goals.Add(stored);
        return stored;
    }

    /// <inheritdoc />
    public void UpdateGoal(GanjoorGoal goal)
    {
        ArgumentNullException.ThrowIfNull(goal);
        Replace(_goals, goal);
    }

    /// <inheritdoc />
    public bool RemoveGoal(long id) => Remove(_goals, id);

    /// <inheritdoc />
    public IReadOnlyList<GanjoorGoal> ListGoals() => [.. _goals.OrderBy(g => g.Id)];

    /// <inheritdoc />
    public GanjoorDebt AddDebt(GanjoorDebt debt)
    {
        ArgumentNullException.ThrowIfNull(debt);
        var stored = debt with { Id = ++_debtId };
        _debts.Add(stored);
        return stored;
    }

    /// <inheritdoc />
    public void UpdateDebt(GanjoorDebt debt)
    {
        ArgumentNullException.ThrowIfNull(debt);
        Replace(_debts, debt);
    }

    /// <inheritdoc />
    public bool RemoveDebt(long id) => Remove(_debts, id);

    /// <inheritdoc />
    public IReadOnlyList<GanjoorDebt> ListDebts() => [.. _debts.OrderBy(d => d.Id)];

    /// <inheritdoc />
    public void PushUndo(string payload)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        _undo.Add(payload);
        while (_undo.Count > _undoDepth)
        {
            _undo.RemoveAt(0);
        }
    }

    /// <inheritdoc />
    public string? PopUndo()
    {
        if (_undo.Count == 0)
            return null;

        var last = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        return last;
    }

    /// <inheritdoc />
    public int UndoCount => _undo.Count;

    /// <inheritdoc />
    public int UndoDepth
    {
        get => _undoDepth;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _undoDepth = value;
        }
    }

    private int _undoDepth = GanjoorDefaults.UndoDepth;

    private static void Replace<T>(List<T> list, T item) where T : notnull
    {
        var id = item switch
        {
            GanjoorAccount a => a.Id,
            GanjoorTransaction t => t.Id,
            GanjoorBill b => b.Id,
            GanjoorGoal g => g.Id,
            GanjoorDebt d => d.Id,
            _ => 0,
        };
        var index = list.FindIndex(existing => existing switch
        {
            GanjoorAccount a => a.Id == id,
            GanjoorTransaction t => t.Id == id,
            GanjoorBill b => b.Id == id,
            GanjoorGoal g => g.Id == id,
            GanjoorDebt d => d.Id == id,
            _ => false,
        });
        if (index < 0)
            throw new InvalidOperationException($"No record with id {id} to update.");

        list[index] = item;
    }

    private static bool Remove<T>(List<T> list, long id) where T : notnull
    {
        var index = list.FindIndex(existing => existing switch
        {
            GanjoorAccount a => a.Id == id,
            GanjoorTransaction t => t.Id == id,
            GanjoorBill b => b.Id == id,
            GanjoorGoal g => g.Id == id,
            GanjoorDebt d => d.Id == id,
            _ => false,
        });
        if (index < 0)
            return false;

        list.RemoveAt(index);
        return true;
    }
}
