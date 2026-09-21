using System.Globalization;

using JameJam.Text;

namespace JameJam.Ganjoor;

/// <summary>Filters for listing transactions.</summary>
/// <param name="AccountId">Only this account.</param>
/// <param name="Category">Only this category (case-insensitive).</param>
/// <param name="Tag">Only transactions carrying this tag.</param>
/// <param name="Month">Only this month.</param>
/// <param name="Kind">Only this kind.</param>
/// <param name="Query">Substring match over category, notes, and tags.</param>
public sealed record GanjoorFilter(
    long? AccountId = null,
    string? Category = null,
    string? Tag = null,
    DateOnly? Month = null,
    GanjoorTxKind? Kind = null,
    string? Query = null);

/// <summary>Outcome of applying due bills.</summary>
/// <param name="Transactions">The transactions just recorded.</param>
/// <param name="Bills">The bills that were applied (with advanced due dates).</param>
public sealed record GanjoorApplyResult(
    IReadOnlyList<GanjoorTransaction> Transactions,
    IReadOnlyList<GanjoorBill> Bills);

/// <summary>Outcome of a CSV import.</summary>
/// <param name="Imported">Rows recorded.</param>
/// <param name="Skipped">Rows skipped (unreadable date or amount).</param>
public sealed record GanjoorImportResult(int Imported, int Skipped);

/// <summary>
/// Business logic for the Ganjoor wallet: accounts, transactions, transfers, budgets,
/// bills, goals, debts, reports, and a snapshot-based undo that reverts one whole
/// command. Every mutating operation pushes a restore point first; balances are pure
/// reductions over the transaction log. Money is exact <c>decimal</c>; conversion into
/// the base currency uses the configurable rate table.
/// </summary>
/// <param name="store">Wallet storage. Injected for testability.</param>
/// <param name="clock">Time source.</param>
/// <param name="options">Customizable rails; defaults apply when null.</param>
public sealed class GanjoorService(IGanjoorStore store, TimeProvider clock, GanjoorOptions? options = null)
{
    /// <summary>Bills never advance more than this many catch-up cycles in one apply.</summary>
    private const int BillMaxCatchUp = 100;

    private readonly IGanjoorStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly GanjoorOptions _options = Initialize(store ?? throw new ArgumentNullException(nameof(store)), options ?? new GanjoorOptions());

    /// <summary>Validates the options and binds the undo depth to the store.</summary>
    private static GanjoorOptions Initialize(IGanjoorStore targetStore, GanjoorOptions options)
    {
        options.Validate();
        targetStore.UndoDepth = options.UndoDepth;
        return options;
    }

    /// <summary>The validated options in effect.</summary>
    public GanjoorOptions Options => _options;

    /// <summary>Today according to the injected clock.</summary>
    public DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    // ── Accounts ──

    /// <summary>Creates an account; names are unique case-insensitively.</summary>
    public GanjoorAccount AddAccount(string? name, string? currency, string? initialBalance)
    {
        var clean = MoneyGuard.Name(name, GanjoorDefaults.MaxNameLength);
        if (_store.FindAccountByName(clean) is not null)
            throw new GanjoorException($"An account named '{clean}' already exists.");

        var accounts = _store.ListAccounts();
        if (accounts.Count >= _options.MaxAccounts)
            throw new GanjoorException($"At most {_options.MaxAccounts} accounts are allowed.");

        PushSnapshot();
        return _store.AddAccount(new(
            0,
            clean,
            MoneyGuard.Currency(string.IsNullOrWhiteSpace(currency) ? _options.DefaultCurrency : currency),
            initialBalance is null ? 0 : MoneyGuard.Amount(initialBalance, _options.MaxAmount),
            clock.GetUtcNow()));
    }

    /// <summary>Renames an account.</summary>
    public GanjoorAccount RenameAccount(string? idText, string? newName)
    {
        var account = Account(idText);
        var clean = MoneyGuard.Name(newName, GanjoorDefaults.MaxNameLength);
        if (_store.FindAccountByName(clean) is { } other && other.Id != account.Id)
            throw new GanjoorException($"An account named '{clean}' already exists.");

        PushSnapshot();
        var renamed = account with { Name = clean };
        _store.UpdateAccount(renamed);
        return renamed;
    }

    /// <summary>Archives or unarchives an account.</summary>
    public GanjoorAccount ArchiveAccount(string? idText, bool archived)
    {
        var account = Account(idText);
        PushSnapshot();
        var updated = account with { IsArchived = archived };
        _store.UpdateAccount(updated);
        return updated;
    }

    /// <summary>
    /// Removes an account and its history. Refuses while transactions exist unless
    /// <paramref name="force"/> is set — the safety gate against accidental wipe-outs.
    /// </summary>
    public int RemoveAccount(string? idText, bool force)
    {
        var account = Account(idText);
        var history = _store.ListTransactions().Where(t => TouchesAccount(t, account.Id)).ToList();
        if (history.Count > 0 && !force)
        {
            throw new GanjoorException(
                $"Account '{account.Name}' holds {history.Count} transaction(s). "
                + "Remove them first, or pass --force to delete the account with its history.");
        }

        PushSnapshot();
        foreach (var tx in history)
            _ = _store.RemoveTransaction(tx.Id);

        _ = _store.RemoveAccount(account.Id);
        return history.Count;
    }

    /// <summary>Lists accounts, optionally including archived ones.</summary>
    public IReadOnlyList<GanjoorAccount> Accounts(bool includeArchived = true) =>
        [.. _store.ListAccounts().Where(a => includeArchived || !a.IsArchived)];

    /// <summary>Current balance of an account (id or name).</summary>
    public decimal Balance(string? accountText) => Balance(Account(accountText));

    /// <summary>Current balance of a resolved account.</summary>
    public decimal Balance(GanjoorAccount account)
    {
        var balance = account.InitialBalance;
        foreach (var tx in _store.ListTransactions())
        {
            if (tx.Kind == GanjoorTxKind.Income && tx.AccountId == account.Id)
                balance += tx.Amount;
            else if (tx.Kind == GanjoorTxKind.Expense && tx.AccountId == account.Id)
                balance -= tx.Amount;
            else if (tx.Kind == GanjoorTxKind.Transfer)
            {
                if (tx.AccountId == account.Id)
                    balance -= tx.Amount;
                if (tx.TransferToAccountId == account.Id)
                    balance += tx.Amount;
            }
        }

        return balance;
    }

    // ── Transactions ──

    /// <summary>Records an expense (or income when <paramref name="income"/> is set).</summary>
    public GanjoorTransaction Record(
        string? accountText,
        string? amountText,
        string? category,
        bool income,
        string? tags = null,
        string? notes = null,
        string? date = null)
    {
        var account = Account(accountText);
        var amount = MoneyGuard.Amount(amountText, _options.MaxAmount);
        var cleanCategory = MoneyGuard.Category(category);
        var cleanTags = MoneyGuard.Tags(tags, GanjoorDefaults.MaxTags);
        var cleanNotes = MoneyGuard.Notes(notes, GanjoorDefaults.MaxNotesLength);
        var when = date is null ? Today : MoneyGuard.Date(date);

        PushSnapshot();
        return _store.AddTransaction(new(
            0,
            income ? GanjoorTxKind.Income : GanjoorTxKind.Expense,
            account.Id,
            amount,
            cleanCategory,
            when,
            clock.GetUtcNow())
        {
            Tags = cleanTags,
            Notes = cleanNotes,
        });
    }

    /// <summary>Records a transfer between two accounts (same currency required).</summary>
    public GanjoorTransaction Transfer(string? fromText, string? toText, string? amountText, string? notes = null, string? date = null)
    {
        var from = Account(fromText);
        var to = Account(toText);
        if (from.Id == to.Id)
            throw new GanjoorException("A transfer needs two different accounts.");

        if (!string.Equals(from.Currency, to.Currency, StringComparison.OrdinalIgnoreCase))
        {
            throw new GanjoorException(
                $"Transfers need matching currencies ({from.Currency} → {to.Currency}). "
                + "Record the move as separate transactions instead.");
        }

        var amount = MoneyGuard.Amount(amountText, _options.MaxAmount);
        var cleanNotes = MoneyGuard.Notes(notes, GanjoorDefaults.MaxNotesLength);
        var when = date is null ? Today : MoneyGuard.Date(date);

        PushSnapshot();
        return _store.AddTransaction(new(
            0,
            GanjoorTxKind.Transfer,
            from.Id,
            amount,
            "transfer",
            when,
            clock.GetUtcNow())
        {
            TransferToAccountId = to.Id,
            Notes = cleanNotes,
        });
    }

    /// <summary>Sets a new category on one transaction.</summary>
    public GanjoorTransaction Recategorize(string? idText, string? category)
    {
        var tx = Transaction(idText);
        var clean = MoneyGuard.Category(category);
        PushSnapshot();
        var updated = tx with { Category = clean };
        _store.UpdateTransaction(updated);
        return updated;
    }

    /// <summary>Deletes one transaction.</summary>
    public GanjoorTransaction DeleteTransaction(string? idText)
    {
        var tx = Transaction(idText);
        PushSnapshot();
        _ = _store.RemoveTransaction(tx.Id);
        return tx;
    }

    /// <summary>Lists transactions newest-first, optionally filtered.</summary>
    public IReadOnlyList<GanjoorTransaction> Transactions(GanjoorFilter? filter = null)
    {
        filter ??= new GanjoorFilter();
        IEnumerable<GanjoorTransaction> query = _store.ListTransactions();
        if (filter.AccountId is { } accountId)
            query = query.Where(t => t.AccountId == accountId || t.TransferToAccountId == accountId);

        if (filter.Category is { } category)
            query = query.Where(t => string.Equals(t.Category, category.Trim(), StringComparison.OrdinalIgnoreCase));

        if (filter.Tag is { } tag)
            query = query.Where(t => t.Tags.Any(existing => string.Equals(existing, tag.Trim(), StringComparison.OrdinalIgnoreCase)));

        if (filter.Month is { } month)
            query = query.Where(t => t.Date.Year == month.Year && t.Date.Month == month.Month);

        if (filter.Kind is { } kind)
            query = query.Where(t => t.Kind == kind);

        if (!string.IsNullOrWhiteSpace(filter.Query))
        {
            var needle = filter.Query.Trim();
            query = query.Where(t =>
                t.Category.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || t.Notes.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || t.Tags.Any(tag => tag.Contains(needle, StringComparison.OrdinalIgnoreCase)));
        }

        return [.. query];
    }

    /// <summary>Distinct known categories: budgets first, then transaction history.</summary>
    public IReadOnlyList<string> KnownCategories()
    {
        var categories = _store.ListBudgets()
            .Select(b => b.Category)
            .Concat(_store.ListTransactions().Select(t => t.Category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(_options.AiMaxCategories)
            .ToList();
        return categories;
    }

    // ── Budgets ──

    /// <summary>Sets a monthly cap for a category.</summary>
    public GanjoorBudget SetBudget(string? category, string? limitText)
    {
        var clean = MoneyGuard.Category(category);
        var limit = MoneyGuard.Amount(limitText, _options.MaxAmount);
        if (_store.ListBudgets().Count >= GanjoorDefaults.MaxBudgetsBound)
            throw new GanjoorException($"At most {GanjoorDefaults.MaxBudgetsBound} budgets are allowed.");

        PushSnapshot();
        var budget = new GanjoorBudget(clean, limit);
        _store.SetBudget(budget);
        return budget;
    }

    /// <summary>Removes a budget.</summary>
    public GanjoorBudget RemoveBudget(string? category)
    {
        var clean = MoneyGuard.Category(category);
        var existing = _store.ListBudgets()
            .FirstOrDefault(b => string.Equals(b.Category, clean, StringComparison.OrdinalIgnoreCase))
            ?? throw new GanjoorException($"No budget for '{clean}'.");
        PushSnapshot();
        _ = _store.RemoveBudget(existing.Category);
        return existing;
    }

    /// <summary>Budget statuses (spent versus limit) for a month.</summary>
    public IReadOnlyList<GanjoorBudgetStatus> BudgetStatuses(DateOnly? month = null)
    {
        var when = month ?? FirstOfMonth(Today);
        var budgets = _store.ListBudgets();
        var spentByCategory = _store.ListTransactions()
            .Where(t => t.Kind == GanjoorTxKind.Expense
                && t.Date.Year == when.Year && t.Date.Month == when.Month)
            .GroupBy(t => t.Category, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(t => t.Amount), StringComparer.OrdinalIgnoreCase);

        return [.. budgets
            .Select(b => new GanjoorBudgetStatus(
                b.Category, b.MonthlyLimit,
                spentByCategory.GetValueOrDefault(b.Category)))
            .OrderBy(s => s.Category, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Cash flow (income vs expenses by category) for a month.</summary>
    public GanjoorCashFlow CashFlow(DateOnly? month = null)
    {
        var when = month ?? FirstOfMonth(Today);
        var inMonth = _store.ListTransactions()
            .Where(t => t.Date.Year == when.Year && t.Date.Month == when.Month)
            .ToList();
        var income = inMonth.Where(t => t.Kind == GanjoorTxKind.Income).Sum(t => t.Amount);
        var expenses = inMonth.Where(t => t.Kind == GanjoorTxKind.Expense).ToList();
        var byCategory = expenses
            .GroupBy(t => t.Category, StringComparer.OrdinalIgnoreCase)
            .Select(group => new GanjoorCategoryTotal(group.Key, group.Sum(t => t.Amount), group.Count()))
            .OrderByDescending(row => row.Amount)
            .ToList();
        return new GanjoorCashFlow(when, income, expenses.Sum(t => t.Amount), byCategory);
    }

    // ── Bills (recurring) ──

    /// <summary>Adds a repeating bill or income.</summary>
    public GanjoorBill AddBill(
        string? name,
        string? amountText,
        string? kindText,
        string? category,
        string? frequencyText,
        string? intervalText = null,
        string? nextText = null,
        string? accountText = null)
    {
        var kind = MoneyGuard.Kind(kindText);
        if (kind == GanjoorTxKind.Transfer)
            throw new GanjoorException("Bills can be income or expense — not a transfer.");

        var clean = MoneyGuard.Name(name, GanjoorDefaults.MaxNameLength);
        var amount = MoneyGuard.Amount(amountText, _options.MaxAmount);
        var cleanCategory = MoneyGuard.Category(category);
        var frequency = MoneyGuard.Frequency(frequencyText);
        var interval = intervalText is null
            ? 1
            : ParseInterval(intervalText);
        var next = nextText is null ? Today : MoneyGuard.Date(nextText);
        var accountId = accountText is null ? FirstAccountId() : Account(accountText).Id;
        if (_store.ListBills().Count >= GanjoorDefaults.MaxBillsBound)
            throw new GanjoorException($"At most {GanjoorDefaults.MaxBillsBound} bills are allowed.");

        PushSnapshot();
        return _store.AddBill(new(
            0, clean, amount, kind, cleanCategory, frequency, interval, next, clock.GetUtcNow())
        {
            AccountId = accountId,
        });
    }

    /// <summary>Removes a bill.</summary>
    public GanjoorBill RemoveBill(string? idText)
    {
        var bill = _store.ListBills().FirstOrDefault(b => b.Id == MoneyGuard.Id(idText, "Bill id"))
            ?? throw new GanjoorException($"No bill #{MoneyGuard.Id(idText, "Bill id")}.");
        PushSnapshot();
        _ = _store.RemoveBill(bill.Id);
        return bill;
    }

    /// <summary>Bills due on or before the given day.</summary>
    public IReadOnlyList<GanjoorBill> DueBills(DateOnly? asOf = null)
    {
        var when = asOf ?? Today;
        return [.. _store.ListBills().Where(b => b.NextDue <= when)];
    }

    /// <summary>Records every bill occurrence that has come due and advances the schedules.</summary>
    public GanjoorApplyResult ApplyDueBills(DateOnly? asOf = null)
    {
        var when = asOf ?? Today;
        PushSnapshot();
        List<GanjoorTransaction> recorded = [];
        List<GanjoorBill> applied = [];
        foreach (var bill in _store.ListBills())
        {
            var due = bill.NextDue;
            var cycles = 0;
            while (due <= when && cycles < BillMaxCatchUp)
            {
                recorded.Add(_store.AddTransaction(new(
                    0,
                    bill.Kind,
                    bill.AccountId,
                    bill.Amount,
                    bill.Category,
                    due,
                    clock.GetUtcNow())
                {
                    Notes = bill.Name,
                    FromBillId = bill.Id,
                }));
                due = NextDue(bill.Frequency, bill.Interval, due);
                cycles++;
            }

            if (cycles > 0)
            {
                var updated = bill with { NextDue = due };
                _store.UpdateBill(updated);
                applied.Add(updated);
            }
        }

        return new GanjoorApplyResult(recorded, applied);
    }

    /// <summary>Next occurrence of a bill schedule (pure date math).</summary>
    public static DateOnly NextDue(GanjoorFrequency frequency, int interval, DateOnly current) => frequency switch
    {
        GanjoorFrequency.Daily => current.AddDays(interval),
        GanjoorFrequency.Weekly => current.AddDays(7 * interval),
        GanjoorFrequency.Monthly => current.AddMonths(interval),
        GanjoorFrequency.Yearly => current.AddYears(interval),
        _ => throw new ArgumentOutOfRangeException(nameof(frequency), frequency, "Unknown frequency."),
    };

    // ── Goals ──

    /// <summary>Creates a savings goal.</summary>
    public GanjoorGoal AddGoal(string? name, string? targetText, string? deadline = null)
    {
        var clean = MoneyGuard.Name(name, GanjoorDefaults.MaxNameLength);
        var target = MoneyGuard.Amount(targetText, _options.MaxAmount);
        var by = deadline is null ? (DateOnly?)null : MoneyGuard.Date(deadline);
        if (_store.ListGoals().Count >= GanjoorDefaults.MaxGoalsBound)
            throw new GanjoorException($"At most {GanjoorDefaults.MaxGoalsBound} goals are allowed.");

        PushSnapshot();
        return _store.AddGoal(new(0, clean, target, 0, by, clock.GetUtcNow()));
    }

    /// <summary>Moves money into a goal (never past the target).</summary>
    public GanjoorGoal Contribute(string? idText, string? amountText)
    {
        var goal = Goal(idText);
        var amount = MoneyGuard.Amount(amountText, _options.MaxAmount);
        if (goal.Contributed + amount > goal.Target)
        {
            throw new GanjoorException(
                $"Goal '{goal.Name}' only needs "
                + MoneyGuard.Money(goal.Target - goal.Contributed)
                + " more — contribute that or raise the target.");
        }

        PushSnapshot();
        var updated = goal with { Contributed = goal.Contributed + amount };
        _store.UpdateGoal(updated);
        return updated;
    }

    /// <summary>Takes money back out of a goal.</summary>
    public GanjoorGoal Withdraw(string? idText, string? amountText)
    {
        var goal = Goal(idText);
        var amount = MoneyGuard.Amount(amountText, _options.MaxAmount);
        if (amount > goal.Contributed)
            throw new GanjoorException($"Goal '{goal.Name}' holds only {MoneyGuard.Money(goal.Contributed)}.");

        PushSnapshot();
        var updated = goal with { Contributed = goal.Contributed - amount };
        _store.UpdateGoal(updated);
        return updated;
    }

    /// <summary>Removes a goal.</summary>
    public GanjoorGoal RemoveGoal(string? idText)
    {
        var goal = Goal(idText);
        PushSnapshot();
        _ = _store.RemoveGoal(goal.Id);
        return goal;
    }

    /// <summary>All goals with completion percent.</summary>
    public IReadOnlyList<GanjoorGoal> Goals() => _store.ListGoals();

    // ── Debts ──

    /// <summary>Records a personal debt.</summary>
    public GanjoorDebt AddDebt(
        string? person,
        string? amountText,
        bool owedByMe,
        string? due = null,
        string? notes = null)
    {
        var clean = MoneyGuard.Name(person, GanjoorDefaults.MaxNameLength);
        var amount = MoneyGuard.Amount(amountText, _options.MaxAmount);
        var cleanNotes = MoneyGuard.Notes(notes, GanjoorDefaults.MaxNotesLength);
        var by = due is null ? (DateOnly?)null : MoneyGuard.Date(due);
        if (_store.ListDebts().Count >= GanjoorDefaults.MaxDebtsBound)
            throw new GanjoorException($"At most {GanjoorDefaults.MaxDebtsBound} debts are allowed.");

        PushSnapshot();
        return _store.AddDebt(new(0, clean, amount, 0, owedByMe, by, cleanNotes, clock.GetUtcNow()));
    }

    /// <summary>Records a (partial) repayment.</summary>
    public GanjoorDebt SettleDebt(string? idText, string? amountText)
    {
        var debt = Debt(idText);
        var amount = MoneyGuard.Amount(amountText, _options.MaxAmount);
        var outstanding = debt.Amount - debt.Settled;
        if (amount > outstanding)
        {
            throw new GanjoorException(
                $"Debt #{debt.Id} has only {MoneyGuard.Money(outstanding)} outstanding.");
        }

        PushSnapshot();
        var updated = debt with { Settled = debt.Settled + amount };
        _store.UpdateDebt(updated);
        return updated;
    }

    /// <summary>Removes a debt.</summary>
    public GanjoorDebt RemoveDebt(string? idText)
    {
        var debt = Debt(idText);
        PushSnapshot();
        _ = _store.RemoveDebt(debt.Id);
        return debt;
    }

    /// <summary>All debts, outstanding first.</summary>
    public IReadOnlyList<GanjoorDebt> Debts() =>
        [.. _store.ListDebts().OrderBy(d => d.Settled >= d.Amount).ThenBy(d => d.Id)];

    /// <summary>Net worth: account balances plus receivable minus payable, in base currency.</summary>
    public GanjoorNetWorth NetWorth()
    {
        decimal accounts = 0;
        foreach (var account in _store.ListAccounts())
        {
            accounts += ConvertToBase(Balance(account), account.Currency);
        }

        decimal receivable = 0;
        decimal payable = 0;
        foreach (var debt in _store.ListDebts())
        {
            var outstanding = debt.Amount - debt.Settled;
            if (debt.OwedByMe)
                payable += outstanding;
            else
                receivable += outstanding;
        }

        return new GanjoorNetWorth(_options.DefaultCurrency, accounts, receivable, payable);
    }

    /// <summary>Converts an amount into the base currency using the rate table.</summary>
    public decimal ConvertToBase(decimal amount, string currency)
    {
        if (string.Equals(currency, _options.DefaultCurrency, StringComparison.OrdinalIgnoreCase))
            return amount;

        if (_options.Rates.TryGetValue(currency, out var rate))
            return amount * rate;

        throw new GanjoorException(
            $"No exchange rate for {currency.ToUpperInvariant()} — add it to GanjoorOptions.Rates "
            + $"or keep accounts in {_options.DefaultCurrency}.");
    }

    // ── Undo, backup, CSV ──

    /// <summary>Reverts the last mutating command. Returns false when nothing to undo.</summary>
    public bool Undo()
    {
        var snapshot = _store.PopUndo();
        if (snapshot is null)
            return false;

        Restore(snapshot);
        return true;
    }

    /// <summary>Serializes the whole wallet (export and undo payloads).</summary>
    public string ExportJson() => GanjoorBackup.ToJson(
        _store.ListAccounts(),
        _store.ListTransactions(),
        _store.ListBudgets(),
        _store.ListBills(),
        _store.ListGoals(),
        _store.ListDebts());

    /// <summary>Replaces the wallet with a backup (this is what undo restores).</summary>
    public void ImportJson(string json)
    {
        var file = GanjoorBackup.FromJson(json);
        ClearAll();
        foreach (var dto in file.Accounts)
        {
            _ = _store.AddAccount(new(
                0, dto.Name, dto.Currency, dto.InitialBalance, ParseStamp(dto.CreatedAt))
            {
                IsArchived = dto.IsArchived,
            });
        }

        foreach (var dto in file.Transactions)
        {
            _ = _store.AddTransaction(new(
                0, (GanjoorTxKind)dto.Kind, dto.AccountId, dto.Amount, dto.Category,
                ParseDate(dto.Date), ParseStamp(dto.CreatedAt))
            {
                TransferToAccountId = dto.TransferToAccountId,
                Tags = dto.Tags,
                Notes = dto.Notes,
                FromBillId = dto.FromBillId,
            });
        }

        foreach (var dto in file.Budgets)
            _store.SetBudget(new(dto.Category, dto.MonthlyLimit));

        foreach (var dto in file.Bills)
        {
            _ = _store.AddBill(new(
                0, dto.Name, dto.Amount, (GanjoorTxKind)dto.Kind, dto.Category,
                (GanjoorFrequency)dto.Frequency, dto.Interval, ParseDate(dto.NextDue), ParseStamp(dto.CreatedAt)));
        }

        foreach (var dto in file.Goals)
        {
            _ = _store.AddGoal(new(
                0, dto.Name, dto.Target, dto.Contributed,
                dto.Deadline is null ? (DateOnly?)null : ParseDate(dto.Deadline), ParseStamp(dto.CreatedAt)));
        }

        foreach (var dto in file.Debts)
        {
            _ = _store.AddDebt(new(
                0, dto.Person, dto.Amount, dto.Settled, dto.OwedByMe,
                dto.DueDate is null ? (DateOnly?)null : ParseDate(dto.DueDate), dto.Notes, ParseStamp(dto.CreatedAt)));
        }
    }

    /// <summary>
    /// Imports a bank-style CSV (<c>date,description,amount</c>; negative = debit) into an
    /// account. Unreadable rows are skipped and counted, never fatal.
    /// </summary>
    public GanjoorImportResult ImportCsv(
        string path, string? accountText, string? category, bool hasHeader, int maxRows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var account = Account(accountText);
        var cleanCategory = MoneyGuard.Category(category);
        if (maxRows < 1 || maxRows > _options.MaxImportRows)
            throw new GanjoorException($"Import rows must be between 1 and {_options.MaxImportRows}.");

        var lines = File.ReadAllLines(path);
        PushSnapshot();
        var imported = 0;
        var skipped = 0;
        foreach (var raw in lines.Skip(hasHeader ? 1 : 0))
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            if (imported >= maxRows)
                throw new GanjoorException($"More than {maxRows} importable rows — raise the limit or split the file.");

            var parts = raw.Split(',', 3);
            if (parts.Length != 3
                || !TryParseCsvDate(parts[0].Trim(), out var when)
                || !decimal.TryParse(parts[2].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var signed)
                || signed == 0)
            {
                skipped++;
                continue;
            }

            _ = _store.AddTransaction(new(
                0,
                signed > 0 ? GanjoorTxKind.Income : GanjoorTxKind.Expense,
                account.Id,
                Math.Abs(signed),
                cleanCategory,
                when,
                clock.GetUtcNow())
            {
                Notes = MoneyGuard.Notes(parts[1].Trim(), GanjoorDefaults.MaxNotesLength),
            });
            imported++;
        }

        return new GanjoorImportResult(imported, skipped);
    }

    private static bool TryParseCsvDate(string text, out DateOnly date) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
        || DateOnly.TryParseExact(text, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
        || DateOnly.TryParseExact(text, "MM/dd/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private void ClearAll()
    {
        foreach (var tx in _store.ListTransactions())
            _ = _store.RemoveTransaction(tx.Id);
        foreach (var account in _store.ListAccounts())
            _ = _store.RemoveAccount(account.Id);
        foreach (var budget in _store.ListBudgets())
            _ = _store.RemoveBudget(budget.Category);
        foreach (var bill in _store.ListBills())
            _ = _store.RemoveBill(bill.Id);
        foreach (var goal in _store.ListGoals())
            _ = _store.RemoveGoal(goal.Id);
        foreach (var debt in _store.ListDebts())
            _ = _store.RemoveDebt(debt.Id);
    }

    /// <summary>Snapshots the wallet so the next change can be undone (used before destructive imports).</summary>
    public void PushUndoSnapshot() => PushSnapshot();

    private void PushSnapshot() => _store.PushUndo(ExportJson());

    private void Restore(string snapshot) => ImportJson(snapshot);

    private GanjoorAccount Account(string? text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.All(char.IsAsciiDigit))
        {
            var id = MoneyGuard.Id(text, "Account");
            return _store.FindAccount(id)
                ?? throw new GanjoorException($"No account #{id}.");
        }

        return _store.FindAccountByName(text)
            ?? throw new GanjoorException($"No account named '{text}'. Create one: JameJam ganjoor account add {text}");
    }

    private GanjoorTransaction Transaction(string? idText)
    {
        var id = MoneyGuard.Id(idText, "Transaction id");
        return _store.FindTransaction(id) ?? throw new GanjoorException($"No transaction #{id}.");
    }

    private GanjoorGoal Goal(string? idText)
    {
        var id = MoneyGuard.Id(idText, "Goal id");
        return _store.ListGoals().FirstOrDefault(g => g.Id == id)
            ?? throw new GanjoorException($"No goal #{id}.");
    }

    private GanjoorDebt Debt(string? idText)
    {
        var id = MoneyGuard.Id(idText, "Debt id");
        return _store.ListDebts().FirstOrDefault(d => d.Id == id)
            ?? throw new GanjoorException($"No debt #{id}.");
    }

    private long FirstAccountId()
    {
        var accounts = _store.ListAccounts();
        return accounts.Count > 0
            ? accounts[0].Id
            : throw new GanjoorException("Create an account first: JameJam ganjoor account add <name>");
    }

    private static int ParseInterval(string text)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var interval) || interval < 1 || interval > 365)
            throw new ArgumentException("Interval must be between 1 and 365.");

        return interval;
    }

    private static DateOnly FirstOfMonth(DateOnly date) => new(date.Year, date.Month, 1);

    private static DateTimeOffset ParseStamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static DateOnly ParseDate(string value) =>
        DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static bool TouchesAccount(GanjoorTransaction tx, long accountId) =>
        tx.AccountId == accountId || tx.TransferToAccountId == accountId;
}
