using System.Globalization;

using JameJam.Ganjoor;

namespace JameJam.Tests.Ganjoor;

/// <summary>Backup format, SQLite persistence, and the AI prompt builder.</summary>
public sealed class GanjoorBackupTests
{
    [Fact]
    public void RoundTrip_PreservesEverything()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 9, 19, 10, 0, 0, TimeSpan.Zero));
        var service = new GanjoorService(new MemoryGanjoorStore(), clock, new GanjoorOptions());
        var id = service.AddAccount("Bank", "USD", "500").Id;
        var idText = id.ToString(CultureInfo.InvariantCulture);
        service.Record(idText, "42.5", "food", income: false, tags: "lunch", notes: "soup");
        service.SetBudget("food", "100");
        service.AddBill("Rent", "950", "out", "housing", "monthly", accountText: idText);
        service.AddGoal("Laptop", "1000", "2026-12-31");
        service.AddDebt("Sara", "150", owedByMe: false, notes: "concert");

        var json = service.ExportJson();
        var file = GanjoorBackup.FromJson(json);

        Assert.Equal(GanjoorBackup.CurrentVersion, file.Version);
        Assert.Single(file.Accounts);
        Assert.Single(file.Transactions);
        Assert.Single(file.Budgets);
        Assert.Single(file.Bills);
        Assert.Single(file.Goals);
        Assert.Single(file.Debts);
        Assert.Equal(42.5m, file.Transactions[0].Amount);
        Assert.Equal("lunch", file.Transactions[0].Tags[0]);
        Assert.Equal(1, file.Accounts[0].Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{broken")]
    [InlineData("""{"version":99,"exportedAt":"x"}""")]
    public void FromJson_RejectsUnreadableOrFutureFiles(string json) =>
        Assert.Throws<ArgumentException>(() => GanjoorBackup.FromJson(json));
}

/// <summary>The wallet survives the process: SQLite persistence with identical ids and undo.</summary>
public sealed class SqliteGanjoorStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"gj-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        File.Delete(_path);
        File.Delete(_path + "-wal");
        File.Delete(_path + "-shm");
    }

    [Fact]
    public void Persistence_RoundTrips_Everything()
    {
        long accountId;
        long txId;
        {
            var first = new SqliteGanjoorStore(_path);
            var account = first.AddAccount(new GanjoorAccount(0, "Bank", "USD", 250, DateTimeOffset.UtcNow));
            accountId = account.Id;
            var tx = first.AddTransaction(new GanjoorTransaction(
                0, GanjoorTxKind.Expense, accountId, 12.5m, "food", new DateOnly(2026, 9, 19), DateTimeOffset.UtcNow)
            {
                Tags = ["lunch", "soup"],
                Notes = "nice",
            });
            txId = tx.Id;
            first.SetBudget(new GanjoorBudget("food", 100));
            _ = first.AddBill(new GanjoorBill(
                0, "Rent", 950, GanjoorTxKind.Expense, "housing", GanjoorFrequency.Monthly, 1,
                new DateOnly(2026, 10, 1), DateTimeOffset.UtcNow) { AccountId = accountId });
            _ = first.AddGoal(new GanjoorGoal(0, "Laptop", 1000, 400, new DateOnly(2026, 12, 31), DateTimeOffset.UtcNow));
            _ = first.AddDebt(new GanjoorDebt(0, "Sara", 150, 0, false, null, "concert", DateTimeOffset.UtcNow));
            first.PushUndo("{}");
        }

        {
            var second = new SqliteGanjoorStore(_path);
            var account = second.FindAccount(accountId);
            Assert.NotNull(account);
            Assert.Equal(250, account.InitialBalance);

            var tx = second.FindTransaction(txId);
            Assert.NotNull(tx);
            Assert.Equal(12.5m, tx.Amount);
            Assert.Equal(["lunch", "soup"], tx.Tags);
            Assert.Equal("nice", tx.Notes);

            Assert.Equal("food", Assert.Single(second.ListBudgets()).Category);
            var bill = Assert.Single(second.ListBills());
            Assert.Equal(accountId, bill.AccountId);
            Assert.Equal(400, Assert.Single(second.ListGoals()).Contributed);
            Assert.Single(second.ListDebts());
            Assert.Equal(1, second.UndoCount);

            Assert.Equal("Bank", second.FindAccountByName("bank")?.Name);
        }
    }

    [Fact]
    public void Updates_AndDeletes_Work()
    {
        var store = new SqliteGanjoorStore(_path);
        var account = store.AddAccount(new GanjoorAccount(0, "Bank", "USD", 0, DateTimeOffset.UtcNow));
        var renamed = account with { Name = "Main", InitialBalance = 5 };
        store.UpdateAccount(renamed);
        Assert.Equal("Main", store.FindAccount(account.Id)!.Name);
        Assert.Equal(5, store.FindAccount(account.Id)!.InitialBalance);
        Assert.True(store.RemoveAccount(account.Id));
        Assert.False(store.RemoveAccount(account.Id));

        var tx = store.AddTransaction(new GanjoorTransaction(
            0, GanjoorTxKind.Income, account.Id, 9, "gift", new DateOnly(2026, 9, 19), DateTimeOffset.UtcNow));
        var updated = tx with { Category = "presents" };
        store.UpdateTransaction(updated);
        Assert.Equal("presents", store.FindTransaction(tx.Id)!.Category);
        Assert.True(store.RemoveTransaction(tx.Id));

        store.SetBudget(new GanjoorBudget("food", 10));
        Assert.True(store.RemoveBudget("FOOD"));

        Assert.Null(store.PopUndo());
        store.PushUndo("a");
        store.PushUndo("b");
        Assert.Equal(2, store.UndoCount);
        Assert.Equal("b", store.PopUndo()); // LIFO
        Assert.Equal("a", store.PopUndo());
    }
}
