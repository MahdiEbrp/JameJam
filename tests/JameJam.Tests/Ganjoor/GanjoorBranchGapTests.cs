using JameJam.Ganjoor;

namespace JameJam.Tests.Ganjoor;

/// <summary>
/// Closes the remaining branch gaps: bill intervals, report summaries (over-budget +
/// bills awaiting apply), bare-verb defaults, constructor guards, and list filters.
/// </summary>
public sealed class GanjoorBranchGapTests
{
    private static readonly FixedTimeProvider Clock =
        new(new DateTimeOffset(2026, 9, 19, 10, 0, 0, TimeSpan.Zero));

    private static GanjoorCommands Build(out StringWriter output, out StringWriter error) =>
        new(new MemoryGanjoorStore(), Clock, output = new StringWriter(), error = new StringWriter());

    // ── Bill intervals ──

    [Fact]
    public void BillInterval_MustBeBetweenOneAnd365()
    {
        var service = NewService();
        _ = service.AddAccount("Bank", "USD", null);
        Assert.Equal(2, service.AddBill("Water", "10", "out", "utilities", "weekly", intervalText: "2").Interval);
        Assert.Throws<ArgumentException>(() => service.AddBill("X", "10", "out", "c", "weekly", intervalText: "0"));
        Assert.Throws<ArgumentException>(() => service.AddBill("X", "10", "out", "c", "weekly", intervalText: "366"));
        Assert.Throws<ArgumentException>(() => service.AddBill("X", "10", "out", "c", "weekly", intervalText: "soon"));
    }

    [Fact]
    public async Task BillInterval_ThroughTheCli_AdvancesByInterval()
    {
        var commands = Build(out var output, out var error);
        await commands.RunAsync(["account", "add", "Bank", "--start", "5000"]);

        Assert.Equal(0, await commands.RunAsync(["bill", "add", "Water", "10", "out", "--category", "utilities", "--every", "weekly", "--interval", "2", "--next", "2026-09-01"]));
        Assert.Equal(0, await commands.RunAsync(["bill", "apply"]));

        var text = output.ToString();
        Assert.Contains("Recorded #1: \u221210.00 utilities (2026-09-01)", text, StringComparison.Ordinal);
        Assert.Contains("Recorded #2: \u221210.00 utilities (2026-09-15)", text, StringComparison.Ordinal);

        Assert.Equal(0, await commands.RunAsync(["bill", "list"]));
        Assert.Contains("Weekly ×2, next 2026-09-29", output.ToString(), StringComparison.Ordinal); // 15 → +2 weeks

        Assert.Equal(1, await commands.RunAsync(["bill", "add", "Bad", "10", "out", "--category", "c", "--interval", "0"]));
        Assert.Contains("Interval must be between 1 and 365", error.ToString(), StringComparison.Ordinal);
    }

    // ── Report summaries ──

    [Fact]
    public async Task Report_ShowsOverBudget_AndBillsAwaitingApply()
    {
        var commands = Build(out var output, out _);
        await commands.RunAsync(["account", "add", "Bank", "--start", "5000"]);
        await commands.RunAsync(["budget", "set", "food", "50"]);
        await commands.RunAsync(["spend", "bank", "60", "food"]);
        await commands.RunAsync(["bill", "add", "Rent", "950", "out", "--category", "housing", "--next", "2026-09-01"]);

        Assert.Equal(0, await commands.RunAsync(["report"]));

        var text = output.ToString();
        Assert.Contains("Over budget:", text, StringComparison.Ordinal);
        Assert.Contains("⚠ food: 60.00 of 50.00", text, StringComparison.Ordinal);
        Assert.Contains("Bills awaiting apply: 1 (ganjoor bill apply)", text, StringComparison.Ordinal);
    }

    // ── Bare verbs default to list ──

    [Fact]
    public async Task BareVerbs_DefaultToList()
    {
        var commands = Build(out var output, out _);

        Assert.Equal(0, await commands.RunAsync(["account"]));
        Assert.Equal(0, await commands.RunAsync(["list"]));
        Assert.Equal(0, await commands.RunAsync(["budget"]));
        Assert.Contains("No budgets set.", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, await commands.RunAsync(["bill"]));
        Assert.Equal(0, await commands.RunAsync(["goal"]));
        Assert.Equal(0, await commands.RunAsync(["debt"]));
    }

    [Fact]
    public async Task BareAi_ShowsUsage()
    {
        var commands = Build(out _, out var error);

        Assert.Equal(1, await commands.RunAsync(["ai"]));

        Assert.Contains("Usage: JameJam ganjoor ai", error.ToString(), StringComparison.Ordinal);
    }

    // ── Constructor guards ──

    [Fact]
    public void Constructor_GuardsItsDependencies()
    {
        Assert.Throws<ArgumentNullException>(() => new GanjoorCommands(new MemoryGanjoorStore(), Clock, null!, new StringWriter()));
        Assert.Throws<ArgumentNullException>(() => new GanjoorCommands(new MemoryGanjoorStore(), Clock, new StringWriter(), null!));
        Assert.Throws<ArgumentNullException>(() => new GanjoorCommands(new MemoryGanjoorStore(), null!, new StringWriter(), new StringWriter()));
        Assert.Throws<ArgumentNullException>(() => new GanjoorService(null!, Clock));
    }

    // ── Defaults sanity (executes the constant rail initializer) ──

    [Fact]
    public void Defaults_ExposeSaneRails()
    {
        Assert.Equal(0.01m, GanjoorDefaults.MinAmount);
        Assert.Equal(1_000_000_000m, GanjoorDefaults.MaxAmount);
        Assert.Equal(20, GanjoorDefaults.UndoDepth);
        Assert.Equal(100, GanjoorDefaults.UndoDepthBound);
        Assert.True(GanjoorDefaults.MinAmount < GanjoorDefaults.MaxAmount);
    }

    // ── List month filter ──

    [Fact]
    public async Task List_MonthFilter_NarrowsToTheMonth()
    {
        var commands = Build(out var output, out _);
        await commands.RunAsync(["account", "add", "Bank"]);
        await commands.RunAsync(["spend", "bank", "5", "food", "--date", "2026-08-02"]);
        await commands.RunAsync(["spend", "bank", "7", "food"]);

        Assert.Equal(0, await commands.RunAsync(["list", "--month", "2026-08"]));
        var text = output.ToString();
        Assert.Contains("2026-08-02", text, StringComparison.Ordinal);
        Assert.DoesNotContain("#2", text, StringComparison.Ordinal); // the September row stays hidden
    }

    private static GanjoorService NewService() =>
        new(new MemoryGanjoorStore(), Clock, new GanjoorOptions());
}
