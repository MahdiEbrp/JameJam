using JameJam.Ganjoor;
using JameJam.Soroush;

namespace JameJam.Tests.Ganjoor;

/// <summary>
/// Coverage completion for the CLI surface: usage failures, filters, detail views,
/// empty states, the CSV pipeline, and the AI verbs.
/// </summary>
public sealed class GanjoorCoverageTests
{
    private static readonly FixedTimeProvider Clock =
        new(new DateTimeOffset(2026, 9, 19, 10, 0, 0, TimeSpan.Zero));

    private static GanjoorCommands Build(
        out StringWriter output,
        out StringWriter error,
        Func<AiRequest, CancellationToken, Task<SoroushResult>>? ai = null,
        IGanjoorStore? store = null) =>
        new(store ?? new MemoryGanjoorStore(), Clock, output = new StringWriter(), error = new StringWriter(), aiCompletion: ai);

    private static Task<SoroushResult> FallbackAi(AiRequest request, CancellationToken token) =>
        Task.FromResult(new SoroushResult("ok", "stub", "stub-model", 1, TimeSpan.Zero));

    // ── Usage failures ──

    [Theory]
    [InlineData("spend", "Bank", "5")]
    [InlineData("transfer", "A", "B")]
    [InlineData("show")]
    [InlineData("delete")]
    [InlineData("export")]
    [InlineData("import")]
    [InlineData("import-csv")]
    public async Task MissingArguments_PrintUsage(params string[] args)
    {
        var commands = Build(out _, out var error);

        Assert.Equal(1, await commands.RunAsync(args));

        Assert.Contains("Usage:", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CsvImport_NeedsAnAccount()
    {
        var commands = Build(out _, out var error);

        Assert.Equal(1, await commands.RunAsync(["import-csv", "statement.csv", "junk"]));

        Assert.Contains("CSV imports need a target account", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownBudgetBillGoalSubcommands_PrintUsage()
    {
        var commands = Build(out _, out var error);

        Assert.Equal(1, await commands.RunAsync(["budget", "gamble"]));
        Assert.Contains("Usage: JameJam ganjoor budget", error.ToString(), StringComparison.Ordinal);

        Assert.Equal(1, await commands.RunAsync(["goal", "gamble"]));
        Assert.Contains("Usage: JameJam ganjoor goal", error.ToString(), StringComparison.Ordinal);

        Assert.Equal(1, await commands.RunAsync(["debt", "gamble"]));
        Assert.Contains("Usage: JameJam ganjoor debt", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownAiSubcommand_PrintsUsage_AndAskNeedsAQuestion()
    {
        var commands = Build(out _, out var error, ai: FallbackAi);

        Assert.Equal(1, await commands.RunAsync(["ai", "gamble"]));
        Assert.Contains("Usage: JameJam ganjoor ai", error.ToString(), StringComparison.Ordinal);

        Assert.Equal(1, await commands.RunAsync(["ai", "ask"]));
        Assert.Contains("Ask a question:", error.ToString(), StringComparison.Ordinal);
    }

    // ── List, filters, and empty states ──

    [Fact]
    public async Task List_EmptyAndFiltered()
    {
        var commands = Build(out var output, out _);
        await commands.RunAsync(["account", "add", "Bank"]);
        await commands.RunAsync(["spend", "bank", "4.80", "food", "--tags", "lunch", "--notes", "soup"]);
        await commands.RunAsync(["account", "add", "Cash"]);
        await commands.RunAsync(["earn", "cash", "100", "salary"]);
        output.GetStringBuilder().Clear();

        Assert.Equal(0, await commands.RunAsync(["list", "--account", "cash"]));
        var text = output.ToString();
        Assert.Contains("+100.00", text, StringComparison.Ordinal);
        Assert.DoesNotContain("-4.80", text, StringComparison.Ordinal);

        output.GetStringBuilder().Clear();
        Assert.Equal(0, await commands.RunAsync(["list", "--kind", "spend"]));
        Assert.Contains("\u22124.80", output.ToString(), StringComparison.Ordinal); // the ledger prints the Unicode minus

        output.GetStringBuilder().Clear();
        Assert.Equal(0, await commands.RunAsync(["list", "--q", "soup"]));
        Assert.Contains("soup", output.ToString(), StringComparison.Ordinal);

        Assert.Equal(0, await commands.RunAsync(["list", "--category", "nothing"]));
        Assert.Contains("No transactions match.", output.ToString(), StringComparison.Ordinal);

        var fresh = Build(out var freshOut, out _);
        Assert.Equal(0, await fresh.RunAsync(["list"]));
        Assert.Contains("No transactions match.", freshOut.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BudgetList_Empty_SetRemove_AndCloseWarning()
    {
        var commands = Build(out var output, out _);
        await commands.RunAsync(["account", "add", "Bank"]);

        Assert.Equal(0, await commands.RunAsync(["budget", "list"]));
        Assert.Contains("No budgets set.", output.ToString(), StringComparison.Ordinal);

        Assert.Equal(0, await commands.RunAsync(["budget", "set", "food", "100"]));
        Assert.Contains("Budget set: food ≤ 100.00 per month.", output.ToString(), StringComparison.Ordinal);

        await commands.RunAsync(["spend", "bank", "79", "food"]); // 79% — no warning yet
        Assert.Equal(0, await commands.RunAsync(["budget", "list"]));
        Assert.DoesNotContain("⚠ close", output.ToString(), StringComparison.Ordinal);

        await commands.RunAsync(["spend", "bank", "1", "food"]); // 80% → close
        Assert.Equal(0, await commands.RunAsync(["budget", "list"]));
        Assert.Contains("⚠ close", output.ToString(), StringComparison.Ordinal);

        Assert.Equal(0, await commands.RunAsync(["budget", "remove", "food"]));
        Assert.Contains("Budget removed: food.", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BillList_AndApplyWhenNothingIsDue()
    {
        var commands = Build(out var output, out _);
        await commands.RunAsync(["account", "add", "Bank", "--start", "5000"]);

        output.GetStringBuilder().Clear();
        Assert.Equal(0, await commands.RunAsync(["bill", "list"])); // empty list prints nothing
        Assert.Equal(string.Empty, output.ToString());

        Assert.Equal(0, await commands.RunAsync(["bill", "add", "Rent", "950", "out", "--category", "housing", "--next", "2026-10-01"]));
        Assert.Contains("Added bill #1: Rent 950.00 Expense Monthly (next 2026-10-01).", output.ToString(), StringComparison.Ordinal);

        Assert.Equal(0, await commands.RunAsync(["bill", "apply"]));
        Assert.Contains("Nothing was due.", output.ToString(), StringComparison.Ordinal);

        Assert.Equal(0, await commands.RunAsync(["bill", "list"]));
        Assert.Contains("#1 Rent 950.00 Expense housing — Monthly ×1, next 2026-10-01", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GoalList_Empty_Withdraw_Remove()
    {
        var commands = Build(out var output, out _);

        Assert.Equal(0, await commands.RunAsync(["goal", "list"]));
        Assert.Contains("No goals yet.", output.ToString(), StringComparison.Ordinal);

        await commands.RunAsync(["goal", "add", "Laptop", "100", "--by", "2026-12-31"]);
        Assert.Equal(0, await commands.RunAsync(["goal", "contribute", "1", "60"]));
        Assert.Equal(0, await commands.RunAsync(["goal", "withdraw", "1", "10"]));
        Assert.Equal(0, await commands.RunAsync(["goal", "list"]));
        var goalText = output.ToString();
        Assert.Contains("#1 Laptop [", goalText, StringComparison.Ordinal);
        Assert.Contains("50% — 50.00 of 100.00 by 2026-12-31", goalText, StringComparison.Ordinal);

        Assert.Equal(0, await commands.RunAsync(["goal", "remove", "1"]));
        Assert.Contains("Removed goal 'Laptop'.", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DebtList_ShowsOutstanding_AndSettleFlow()
    {
        var commands = Build(out var output, out _);
        await commands.RunAsync(["account", "add", "Bank", "--start", "1000"]);

        Assert.Equal(0, await commands.RunAsync(["debt", "list"]));
        Assert.Contains("No debts tracked.", output.ToString(), StringComparison.Ordinal);

        await commands.RunAsync(["debt", "add", "Sara", "150", "--owes-me"]);
        await commands.RunAsync(["debt", "add", "Landlord", "600", "--i-owe"]);
        await commands.RunAsync(["debt", "settle", "2", "100"]);
        Assert.Equal(0, await commands.RunAsync(["debt", "list"]));

        var text = output.ToString();
        Assert.Contains("Debt #2 (Landlord): 500.00 outstanding.", text, StringComparison.Ordinal);
        Assert.Contains("#1 ← owed by Sara: 150.00 outstanding of 150.00", text, StringComparison.Ordinal);
        Assert.Contains("#2 → you owe Landlord: 500.00 outstanding of 600.00 (settled 100.00)", text, StringComparison.Ordinal);

        Assert.Equal(0, await commands.RunAsync(["debt", "remove", "1"]));
        Assert.Contains("Removed debt #1 (Sara).", output.ToString(), StringComparison.Ordinal);
    }

    // ── Detail views ──

    [Fact]
    public async Task Show_PrintsEveryField()
    {
        var commands = Build(out var output, out _);
        await commands.RunAsync(["account", "add", "Bank", "--start", "5000"]);
        await commands.RunAsync(["bill", "add", "Rent", "950", "out", "--category", "housing", "--next", "2026-09-01"]);
        await commands.RunAsync(["bill", "apply"]);
        await commands.RunAsync(["spend", "bank", "4.80", "food", "--tags", "lunch", "--notes", "soup"]);

        output.GetStringBuilder().Clear();
        Assert.Equal(0, await commands.RunAsync(["show", "1"]));
        var text = output.ToString();
        Assert.Contains("#1 — Expense 950.00 USD in housing", text, StringComparison.Ordinal);
        Assert.Contains("From bill #1", text, StringComparison.Ordinal);

        output.GetStringBuilder().Clear();
        Assert.Equal(0, await commands.RunAsync(["show", "2"]));
        text = output.ToString();
        Assert.Contains("Tags: lunch", text, StringComparison.Ordinal);
        Assert.Contains("Notes: soup", text, StringComparison.Ordinal);
        Assert.DoesNotContain("into", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Show_Transfer_AndUnknownId()
    {
        var commands = Build(out var output, out var error);
        await commands.RunAsync(["account", "add", "A", "--start", "100"]);
        await commands.RunAsync(["account", "add", "B"]);
        await commands.RunAsync(["transfer", "a", "b", "40"]);

        Assert.Equal(0, await commands.RunAsync(["show", "1"]));
        Assert.Contains("→ into B", output.ToString(), StringComparison.Ordinal);

        Assert.Equal(1, await commands.RunAsync(["show", "99"]));
        Assert.Contains("No transaction #99.", error.ToString(), StringComparison.Ordinal);
    }

    // ── Net worth detail + undo empty ──

    [Fact]
    public async Task NetWorth_Detail_AndUndoWhenEmpty()
    {
        var commands = Build(out var output, out _);
        await commands.RunAsync(["account", "add", "Bank", "--start", "900"]);
        await commands.RunAsync(["debt", "add", "Sara", "150", "--owes-me"]);
        await commands.RunAsync(["debt", "add", "Landlord", "100", "--i-owe"]);

        Assert.Equal(0, await commands.RunAsync(["networth"]));
        Assert.Contains(
            "Net worth: 950.00 USD  (accounts 900.00, owed to you 150.00, you owe 100.00)",
            output.ToString(),
            StringComparison.Ordinal);

        var fresh = Build(out var freshOut, out _);
        Assert.Equal(0, await fresh.RunAsync(["undo"]));
        Assert.Contains("Nothing to undo.", freshOut.ToString(), StringComparison.Ordinal);
    }

    // ── CSV pipeline through the CLI ──

    [Fact]
    public async Task CsvImport_EndToEnd_WithUndo()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gj-{Guid.NewGuid():N}.csv");
        await File.WriteAllLinesAsync(path,
        [
            "Date,Description,Amount",
            "2026-09-02,Coffee,-4.80",
            "2026-09-03,Gig,450",
            "broken,row",
        ]);
        try
        {
            var commands = Build(out var output, out _);
            await commands.RunAsync(["account", "add", "Bank"]);

            Assert.Equal(0, await commands.RunAsync(["import-csv", path, "--account", "bank", "--header"]));
            Assert.Contains("Imported 2 row(s), skipped 1.", output.ToString(), StringComparison.Ordinal);

            Assert.Equal(0, await commands.RunAsync(["undo"]));
            Assert.Contains("Undone", output.ToString(), StringComparison.Ordinal);
            Assert.Equal(0, await commands.RunAsync(["list"]));
            Assert.Contains("No transactions match.", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Export_Import_UsageFails()
    {
        var commands = Build(out _, out var error);

        Assert.Equal(1, await commands.RunAsync(["import"]));
        Assert.Contains("Usage: JameJam ganjoor import", error.ToString(), StringComparison.Ordinal);

        Assert.Equal(1, await commands.RunAsync(["export"]));
        Assert.Contains("Usage: JameJam ganjoor export", error.ToString(), StringComparison.Ordinal);
    }

    // ── AI verbs ──

    [Fact]
    public async Task AiAsk_EmptyQuestion_AndCategorizeUnknownId()
    {
        var commands = Build(out _, out var error, ai: FallbackAi);
        await commands.RunAsync(["account", "add", "Bank"]);

        Assert.Equal(1, await commands.RunAsync(["ai", "categorize", "42"]));
        Assert.Contains("No transaction #42.", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AiInsights_FullFlow()
    {
        List<string> prompts = [];
        var commands = Build(
            out var output,
            out _,
            ai: (request, _) =>
            {
                prompts.Add(request.Prompt);
                return Task.FromResult(new SoroushResult("Spend less on coffee.", "stub", "stub-model", 1, TimeSpan.Zero));
            });
        await commands.RunAsync(["account", "add", "Bank", "--start", "100"]);
        await commands.RunAsync(["spend", "bank", "4.80", "food"]);

        Assert.Equal(0, await commands.RunAsync(["ai", "insights", "--month", "2026-09"]));
        Assert.Contains("Spend less on coffee.", output.ToString(), StringComparison.Ordinal);
        Assert.Single(prompts);
        Assert.Contains("---FINANCE BEGIN---", prompts[0], StringComparison.Ordinal);
    }

    // ── Error translation for store failures ──

    private sealed class ThrowingStore : IGanjoorStore
    {
        private readonly MemoryGanjoorStore _inner = new();

        public int UndoDepth { get; set; }

        public GanjoorAccount AddAccount(GanjoorAccount account) => _inner.AddAccount(account);

        public void UpdateAccount(GanjoorAccount account) => _inner.UpdateAccount(account);

        public bool RemoveAccount(long id) => _inner.RemoveAccount(id);

        public GanjoorAccount? FindAccount(long id) => _inner.FindAccount(id);

        public GanjoorAccount? FindAccountByName(string name) => _inner.FindAccountByName(name);

        public IReadOnlyList<GanjoorAccount> ListAccounts() => _inner.ListAccounts();

        public GanjoorTransaction AddTransaction(GanjoorTransaction transaction) =>
            throw new InvalidOperationException("store is broken");

        public void UpdateTransaction(GanjoorTransaction transaction) => _inner.UpdateTransaction(transaction);

        public bool RemoveTransaction(long id) => _inner.RemoveTransaction(id);

        public GanjoorTransaction? FindTransaction(long id) => _inner.FindTransaction(id);

        public IReadOnlyList<GanjoorTransaction> ListTransactions() => _inner.ListTransactions();

        public void SetBudget(GanjoorBudget budget) => _inner.SetBudget(budget);

        public bool RemoveBudget(string category) => _inner.RemoveBudget(category);

        public IReadOnlyList<GanjoorBudget> ListBudgets() => _inner.ListBudgets();

        public GanjoorBill AddBill(GanjoorBill bill) => _inner.AddBill(bill);

        public void UpdateBill(GanjoorBill bill) => _inner.UpdateBill(bill);

        public bool RemoveBill(long id) => _inner.RemoveBill(id);

        public IReadOnlyList<GanjoorBill> ListBills() => _inner.ListBills();

        public GanjoorGoal AddGoal(GanjoorGoal goal) => _inner.AddGoal(goal);

        public void UpdateGoal(GanjoorGoal goal) => _inner.UpdateGoal(goal);

        public bool RemoveGoal(long id) => _inner.RemoveGoal(id);

        public IReadOnlyList<GanjoorGoal> ListGoals() => _inner.ListGoals();

        public GanjoorDebt AddDebt(GanjoorDebt debt) => _inner.AddDebt(debt);

        public void UpdateDebt(GanjoorDebt debt) => _inner.UpdateDebt(debt);

        public bool RemoveDebt(long id) => _inner.RemoveDebt(id);

        public IReadOnlyList<GanjoorDebt> ListDebts() => _inner.ListDebts();

        public void PushUndo(string payload) => _inner.PushUndo(payload);

        public string? PopUndo() => _inner.PopUndo();

        public int UndoCount => _inner.UndoCount;
    }

    [Fact]
    public async Task StoreFailures_BecomeFriendlyErrors()
    {
        var commands = Build(out _, out var error, store: new ThrowingStore());
        await commands.RunAsync(["account", "add", "Bank"]);

        Assert.Equal(1, await commands.RunAsync(["spend", "bank", "5", "food"]));

        Assert.Contains("store is broken", error.ToString(), StringComparison.Ordinal);
    }
}

/// <summary>The SQLite store, exercised across every entity's update/remove/list path.</summary>
public sealed class SqliteGanjoorStoreCompletionTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"gj-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        File.Delete(_path);
        File.Delete(_path + "-wal");
        File.Delete(_path + "-shm");
    }

    [Fact]
    public void Lists_Updates_AndRemoves_ForEveryEntity()
    {
        var store = new SqliteGanjoorStore(_path);
        Assert.Equal(_path, store.DatabasePath);

        var first = store.AddAccount(new GanjoorAccount(0, "A", "USD", 0, DateTimeOffset.UtcNow));
        var second = store.AddAccount(new GanjoorAccount(0, "B", "EUR", 0, DateTimeOffset.UtcNow));
        Assert.Equal([first.Id, second.Id], store.ListAccounts().Select(a => a.Id));

        var inA = new GanjoorTransaction(0, GanjoorTxKind.Expense, first.Id, 5, "food", new(2026, 9, 1), DateTimeOffset.UtcNow);
        var inB = new GanjoorTransaction(0, GanjoorTxKind.Income, second.Id, 9, "gift", new(2026, 9, 2), DateTimeOffset.UtcNow);
        _ = store.AddTransaction(inA);
        var txB = store.AddTransaction(inB);
        Assert.Equal(txB.Id, store.ListTransactions()[0].Id); // newest date first

        store.SetBudget(new GanjoorBudget("food", 10));
        store.SetBudget(new GanjoorBudget("fun", 20));
        Assert.Equal(["food", "fun"], store.ListBudgets().Select(b => b.Category));

        var bill = store.AddBill(new GanjoorBill(
            0, "Rent", 950, GanjoorTxKind.Expense, "housing", GanjoorFrequency.Monthly, 1,
            new(2026, 10, 1), DateTimeOffset.UtcNow) { AccountId = first.Id });
        store.UpdateBill(bill with { Name = "House", Amount = 900, Category = "roof", AccountId = second.Id });
        var updatedBill = Assert.Single(store.ListBills());
        Assert.Equal(("House", 900m, "roof", second.Id), (updatedBill.Name, updatedBill.Amount, updatedBill.Category, updatedBill.AccountId));
        Assert.True(store.RemoveBill(bill.Id));
        Assert.False(store.RemoveBill(bill.Id));
        Assert.Empty(store.ListBills());

        var goal = store.AddGoal(new GanjoorGoal(0, "Laptop", 1000, 100, new(2026, 12, 31), DateTimeOffset.UtcNow));
        store.UpdateGoal(goal with { Contributed = 250, Deadline = new(2027, 1, 31) });
        var updatedGoal = Assert.Single(store.ListGoals());
        Assert.Equal(250, updatedGoal.Contributed);
        Assert.Equal(new(2027, 1, 31), updatedGoal.Deadline);
        Assert.True(store.RemoveGoal(goal.Id));
        Assert.Empty(store.ListGoals());

        var debt = store.AddDebt(new GanjoorDebt(0, "Sara", 150, 0, false, null, "concert", DateTimeOffset.UtcNow));
        store.UpdateDebt(debt with { Settled = 50 });
        Assert.Equal(50, Assert.Single(store.ListDebts()).Settled);
        Assert.True(store.RemoveDebt(debt.Id));
        Assert.Empty(store.ListDebts());
    }

    [Fact]
    public void FindAccount_Misses_ReturnNull()
    {
        var store = new SqliteGanjoorStore(_path);
        Assert.Null(store.FindAccount(42));
        Assert.Null(store.FindAccountByName("ghost"));
        Assert.Null(store.FindTransaction(42));
    }
}
