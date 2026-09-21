using JameJam.Ganjoor;

namespace JameJam.Tests.Ganjoor;

/// <summary>
/// Business logic for the wallet: balances, budgets, bills, goals, debts, reports,
/// CSV import, and the snapshot undo.
/// </summary>
public sealed class GanjoorServiceTests
{
    private static readonly DateOnly Today = new(2026, 9, 19);

    private readonly MemoryGanjoorStore _store = new();
    private readonly GanjoorService _service;

    public GanjoorServiceTests() =>
        _service = new GanjoorService(_store, new FixedTimeProvider(new DateTime(Today.Year, Today.Month, Today.Day, 10, 0, 0)), new GanjoorOptions());

    private long NewAccount(string name = "Bank", string currency = "USD", decimal start = 0) =>
        _service.AddAccount(name, currency, start > 0 ? start.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : null).Id;

    private static string IdText(long id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void Accounts_Balances_WalkTheLedger()
    {
        var bank = NewAccount(start: 100);
        _service.Record(IdText(bank), "50", "salary", income: true);
        _service.Record(IdText(bank), "20", "food", income: false);
        Assert.Equal(130, _service.Balance(IdText(bank)));
    }

    [Fact]
    public void AccountNames_AreUnique_AndResolvable()
    {
        _ = NewAccount("Cash");
        var exception = Assert.Throws<GanjoorException>(() => _service.AddAccount("CASH", null, null));
        Assert.Contains("already exists", exception.Message, StringComparison.Ordinal);
        _service.Record("cash", "10", "food", income: false);
        Assert.Equal(-10, _service.Balance("CASH"));
    }

    [Fact]
    public void Transfers_MoveMoney_WithoutTouchingIncomeOrSpending()
    {
        var from = NewAccount("A", start: 500);
        var to = NewAccount("B");
        _service.Transfer(IdText(from), IdText(to), "120");

        Assert.Equal(380, _service.Balance("A"));
        Assert.Equal(120, _service.Balance("B"));
        var flow = _service.CashFlow();
        Assert.Equal(0, flow.Income);
        Assert.Equal(0, flow.Expenses);
    }

    [Fact]
    public void Transfers_NeedTwoAccounts_OfOneCurrency()
    {
        var a = NewAccount("A", start: 10);
        _ = NewAccount("B");
        Assert.Throws<GanjoorException>(() => _service.Transfer("A", "A", "5"));
        _ = NewAccount("Euro", currency: "EUR");
        var exception = Assert.Throws<GanjoorException>(() => _service.Transfer("A", "Euro", "5"));
        Assert.Contains("matching currencies", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoveAccount_IsGatedByForce_ThenCascades()
    {
        var id = NewAccount("Card");
        _service.Record(IdText(id), "9", "food", income: false);
        var exception = Assert.Throws<GanjoorException>(() => _service.RemoveAccount(IdText(id), force: false));
        Assert.Contains("--force", exception.Message, StringComparison.Ordinal);

        Assert.Equal(1, _service.RemoveAccount(IdText(id), force: true));
        Assert.Empty(_service.Transactions());
        Assert.Null(_service.Accounts().FirstOrDefault(a => a.Id == id));
    }

    [Fact]
    public void Filters_NarrowTheLedger()
    {
        var a = NewAccount("A");
        var b = NewAccount("B");
        _service.Record(IdText(a), "10", "food", income: false, tags: "lunch", notes: "soup");
        _service.Record(IdText(a), "100", "rent", income: false, date: "2026-08-01");
        _service.Record(IdText(b), "500", "salary", income: true);

        Assert.Single(_service.Transactions(new GanjoorFilter { Category = "FOOD" }));
        Assert.Single(_service.Transactions(new GanjoorFilter { Tag = "lunch" }));
        Assert.Single(_service.Transactions(new GanjoorFilter { Kind = GanjoorTxKind.Income }));
        Assert.Single(_service.Transactions(new GanjoorFilter { Month = new DateOnly(2026, 8, 1) }));
        Assert.Single(_service.Transactions(new GanjoorFilter { Query = "oo" })); // category food, note soup — one tx
        Assert.Equal(3, _service.Transactions().Count);
    }

    [Fact]
    public void Budgets_TrackSpending_AndWarnThresholds()
    {
        var id = NewAccount("A", start: 1000);
        _service.SetBudget("food", "100");
        _service.Record(IdText(id), "90", "food", income: false);
        _service.Record(IdText(id), "20", "food", income: false);

        var status = Assert.Single(_service.BudgetStatuses());
        Assert.Equal(110, status.Spent);
        Assert.True(status.Over);
        Assert.Equal(110, status.PercentUsed);

        // Budgets ignore other months and non-expenses.
        Assert.All(_service.BudgetStatuses(new DateOnly(2026, 8, 1)), static s => Assert.Equal(0, s.Spent));
        var flow = _service.CashFlow();
        Assert.Equal(110, flow.Expenses);
    }

    [Fact]
    public void BudgetSet_IsIdempotent_AndRemovalFailsFriendly()
    {
        _service.SetBudget("food", "100");
        _service.SetBudget("FOOD", "200");
        var status = Assert.Single(_service.BudgetStatuses());
        Assert.Equal(200, status.Limit);

        var removed = _service.RemoveBudget("food");
        Assert.Equal("FOOD", removed.Category);
        var exception = Assert.Throws<GanjoorException>(() => _service.RemoveBudget("food"));
        Assert.Contains("No budget", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CashFlow_SumsTheMonth_ByCategory()
    {
        var id = NewAccount("A", start: 0);
        _service.Record(IdText(id), "2000", "salary", income: true);
        _service.Record(IdText(id), "300", "rent", income: false);
        _service.Record(IdText(id), "120", "food", income: false);
        _service.Record(IdText(id), "80", "food", income: false);

        var flow = _service.CashFlow(new DateOnly(2026, 9, 1));
        Assert.Equal(2000, flow.Income);
        Assert.Equal(500, flow.Expenses);
        Assert.Equal(1500, flow.Net);
        Assert.Equal("rent", flow.ByCategory[0].Category);
        Assert.Equal(300, flow.ByCategory[0].Amount);
        Assert.Equal("food", flow.ByCategory[1].Category);
        Assert.Equal(2, flow.ByCategory[1].Count);
    }

    [Fact]
    public void Bills_AddAdvanceAndCatchUp()
    {
        var id = NewAccount("A", start: 5000);
        _service.AddBill("Rent", "950", "out", "housing", "monthly", nextText: "2026-09-01", accountText: IdText(id));

        var due = Assert.Single(_service.DueBills());
        Assert.Equal("Rent", due.Name);

        var result = _service.ApplyDueBills();
        var tx = Assert.Single(result.Transactions);
        Assert.Equal(950, tx.Amount);
        Assert.Equal(new DateOnly(2026, 9, 1), tx.Date);
        Assert.Equal(GanjoorTxKind.Expense, tx.Kind);
        Assert.Equal(due.Id, tx.FromBillId);

        var advanced = Assert.Single(result.Bills);
        Assert.Equal(new DateOnly(2026, 10, 1), advanced.NextDue);
        Assert.Empty(_service.DueBills());

        // Bills always land in their own account.
        Assert.Equal(4050, _service.Balance(IdText(id)));
    }

    [Fact]
    public void Bills_SupportYearly_AndWeeklyMath()
    {
        Assert.Equal(new DateOnly(2027, 2, 28), GanjoorService.NextDue(GanjoorFrequency.Yearly, 1, new(2026, 2, 28)));
        Assert.Equal(new DateOnly(2026, 10, 3), GanjoorService.NextDue(GanjoorFrequency.Weekly, 2, new(2026, 9, 19)));
        Assert.Equal(new DateOnly(2026, 2, 28), GanjoorService.NextDue(GanjoorFrequency.Monthly, 1, new(2026, 1, 31))); // clamps
        Assert.Equal(new DateOnly(2026, 9, 29), GanjoorService.NextDue(GanjoorFrequency.Daily, 10, new(2026, 9, 19)));
    }

    [Fact]
    public void Bills_RejectTransfers_AndUnknowableKinds()
    {
        _ = NewAccount("A");
        var exception = Assert.Throws<GanjoorException>(
            () => _service.AddBill("Move", "10", "transfer", "moving", "monthly"));
        Assert.Contains("not a transfer", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Goals_TrackProgress_AndGuardTheEdges()
    {
        var goal = _service.AddGoal("Laptop", "1000", "2026-12-31");
        _service.Contribute(IdText(goal.Id), "400");
        _service.Contribute(IdText(goal.Id), "600");

        var done = Assert.Single(_service.Goals());
        Assert.Equal(1000, done.Contributed);

        var over = Assert.Throws<GanjoorException>(() => _service.Contribute(IdText(goal.Id), "1"));
        Assert.Contains("only needs 0.00 more", over.Message, StringComparison.Ordinal);

        _service.Withdraw(IdText(goal.Id), "250");
        Assert.Equal(750, Assert.Single(_service.Goals()).Contributed);

        var under = Assert.Throws<GanjoorException>(() => _service.Withdraw(IdText(goal.Id), "1000"));
        Assert.Contains("holds only 750.00", under.Message, StringComparison.Ordinal);

        _service.RemoveGoal(IdText(goal.Id));
        Assert.Empty(_service.Goals());
    }

    [Fact]
    public void Debts_TrackBothDirections_AndSettlePartially()
    {
        _service.AddDebt("Sara", "150", owedByMe: false, due: "2026-10-01");
        _service.AddDebt("Landlord", "600", owedByMe: true);

        _service.SettleDebt("2", "250");
        var landlord = _service.Debts().Single(d => d.Person == "Landlord");
        Assert.Equal(250, landlord.Settled);

        var over = Assert.Throws<GanjoorException>(() => _service.SettleDebt("2", "1000"));
        Assert.Contains("only 350.00 outstanding", over.Message, StringComparison.Ordinal);

        _service.SettleDebt("2", "350");
        Assert.Equal(600, _service.Debts().Single(d => d.Id == 2).Settled);
    }

    [Fact]
    public void NetWorth_ConvertsAccounts_AndCountsDebts()
    {
        _service.AddAccount("Cash", "USD", "1000");
        _service.AddAccount("Euro", "EUR", "1000");
        _service.AddDebt("Sara", "300", owedByMe: false);
        _service.AddDebt("Landlord", "100", owedByMe: true);

        var withoutRates = Assert.Throws<GanjoorException>(() => _service.NetWorth());
        Assert.Contains("No exchange rate for EUR", withoutRates.Message, StringComparison.Ordinal);

        var converted = new GanjoorService(
            _store,
            new FixedTimeProvider(new DateTime(Today.Year, Today.Month, Today.Day, 10, 0, 0)),
            new GanjoorOptions { Rates = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["EUR"] = 2 } });
        var worth = converted.NetWorth();
        Assert.Equal("USD", worth.BaseCurrency);
        Assert.Equal(3000, worth.Accounts); // 1000 + 1000×2
        Assert.Equal(300, worth.Receivable);
        Assert.Equal(100, worth.Payable);
        Assert.Equal(3200, worth.Total);
    }

    [Fact]
    public void Undo_RevertsOneWholeCommand_AtATime()
    {
        var id = NewAccount("A", start: 100);
        _service.Record(IdText(id), "30", "food", income: false);
        Assert.Equal(2, _store.UndoCount); // one for the account, one for the expense

        Assert.True(_service.Undo()); // expense reverted
        Assert.Equal(100, _service.Balance("A"));
        Assert.Empty(_service.Transactions());

        Assert.True(_service.Undo()); // account creation reverted
        Assert.Empty(_service.Accounts());

        Assert.False(_service.Undo()); // nothing left
    }

    [Fact]
    public void ExportImport_RoundTripsTheWholeWallet_WithIdenticalIds()
    {
        var id = NewAccount("Bank", start: 500);
        var other = NewAccount("Cash");
        _service.Record(IdText(id), "42", "food", income: false, tags: "lunch", notes: "soup");
        _service.Transfer(IdText(id), IdText(other), "25");
        _service.SetBudget("food", "100");
        _service.AddBill("Rent", "950", "out", "housing", "monthly", accountText: IdText(id));
        _service.AddGoal("Laptop", "1000");
        _service.AddDebt("Sara", "150", owedByMe: false);

        var json = _service.ExportJson();
        var fresh = new GanjoorService(
            new MemoryGanjoorStore(),
            new FixedTimeProvider(new DateTime(Today.Year, Today.Month, Today.Day, 10, 0, 0)),
            new GanjoorOptions());
        fresh.ImportJson(json);

        Assert.Equal(fresh.Balance("Bank"), _service.Balance("Bank"));
        Assert.Equal(fresh.Transactions().Select(t => (t.Id, t.Category)), _service.Transactions().Select(t => (t.Id, t.Category)));
        Assert.Equal(fresh.KnownCategories().Count, _service.KnownCategories().Count);
        Assert.Single(fresh.Goals());
        Assert.Single(fresh.Debts());
        Assert.Single(fresh.BudgetStatuses());
        Assert.Equal(other, fresh.Accounts().Single(a => a.Name == "Cash").Id);
    }

    [Fact]
    public void CsvImport_DetectsSigns_SkipsJunk_AndUndoRevertsItAll()
    {
        var id = NewAccount("Bank");
        var path = Path.Combine(Path.GetTempPath(), $"gj-{Guid.NewGuid():N}.csv");
        File.WriteAllLines(path,
        [
            "Date,Description,Amount",
            "2026-09-02,Coffee,-4.80",
            "2026-09-03,Gig,450",
            "nonsense,row,here",
            "2026-09-04,Zero,0",
        ]);
        try
        {
            var result = _service.ImportCsv(path, IdText(id), "imported", hasHeader: true, maxRows: 100);
            Assert.Equal(2, result.Imported);
            Assert.Equal(2, result.Skipped);

            var rows = _service.Transactions(new GanjoorFilter { Category = "imported" });
            Assert.Equal(450, rows.Single(t => t.Kind == GanjoorTxKind.Income).Amount);
            Assert.Equal(4.8m, rows.Single(t => t.Kind == GanjoorTxKind.Expense).Amount);
            Assert.Equal("Coffee", rows.Single(t => t.Kind == GanjoorTxKind.Expense).Notes);

            Assert.True(_service.Undo());
            Assert.Empty(_service.Transactions());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CsvImport_RespectsTheRowRail()
    {
        var id = NewAccount("Bank");
        var exception = Assert.Throws<GanjoorException>(
            () => _service.ImportCsv("whatever.csv", IdText(id), "x", hasHeader: false, maxRows: 0));
        Assert.Contains("between 1 and", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void KnownCategories_UnifyBudgetsAndHistory()
    {
        var id = NewAccount("A");
        _service.SetBudget("groceries", "100");
        _service.Record(IdText(id), "5", "coffee", income: false);
        var categories = _service.KnownCategories();
        Assert.Equal(["groceries", "coffee"], categories);
    }
}
