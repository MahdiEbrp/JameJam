using JameJam.Anahita;
using JameJam.Ganjoor;
using JameJam.Settings;
using JameJam.Soroush;

namespace JameJam.Tests;

/// <summary>Tests for the <c>ganjoor</c> CLI surface: parsing, output, exit codes, undo.</summary>
public sealed class GanjoorCommandsTests
{
    private readonly MemoryGanjoorStore _store = new();

    private GanjoorCommands Build(out StringWriter output, out StringWriter error, Func<string?>? currency = null) =>
        new(
            _store,
            new FixedTimeProvider(new DateTimeOffset(2026, 9, 19, 10, 0, 0, TimeSpan.Zero)),
            output = new StringWriter(),
            error = new StringWriter(),
            currencyProvider: currency);

    [Fact]
    public async Task Help_ListsTheArsenal()
    {
        var commands = Build(out var output, out _);

        Assert.Equal(0, await commands.RunAsync(["help"]));

        var text = output.ToString();
        Assert.Contains("Ganjoor", text, StringComparison.Ordinal);
        Assert.Contains("spend", text, StringComparison.Ordinal);
        Assert.Contains("budget", text, StringComparison.Ordinal);
        Assert.Contains("bill", text, StringComparison.Ordinal);
        Assert.Contains("goal", text, StringComparison.Ordinal);
        Assert.Contains("debt", text, StringComparison.Ordinal);
        Assert.Contains("import-csv", text, StringComparison.Ordinal);
        Assert.Contains("ai insights", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownCommand_FailsWithHint()
    {
        var commands = Build(out _, out var error);

        Assert.Equal(1, await commands.RunAsync(["gamble"]));

        Assert.Contains("Unknown ganjoor command 'gamble'", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownOption_FailsStrictly()
    {
        var commands = Build(out _, out var error);

        Assert.Equal(1, await commands.RunAsync(["spend", "Bank", "5", "food", "--gamble"]));

        Assert.Contains("Unknown option '--gamble'", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MoneyLifecycle_PrintsBalances_AndWarnsOnBudget()
    {
        var commands = Build(out var output, out var error);

        Assert.Equal(0, await commands.RunAsync(["account", "add", "Bank", "--start", "100"]));
        Assert.Equal(0, await commands.RunAsync(["budget", "set", "food", "50"]));
        Assert.Equal(0, await commands.RunAsync(["spend", "bank", "45", "food"]));
        Assert.Equal(0, await commands.RunAsync(["spend", "bank", "10", "food"]));

        var text = output.ToString();
        Assert.Contains("starting 100.00 USD", text, StringComparison.Ordinal);
        Assert.Contains("balance 55.00 USD", text, StringComparison.Ordinal);
        Assert.Contains("⚠ Budget check: food at 90%", text, StringComparison.Ordinal);
        Assert.Contains("⚠ Over budget: food — 55.00 of 50.00", text, StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task Earn_Transfer_List_Show_Delete()
    {
        var commands = Build(out var output, out _);

        await commands.RunAsync(["account", "add", "Bank", "--start", "100"]);
        await commands.RunAsync(["account", "add", "Cash"]);
        Assert.Equal(0, await commands.RunAsync(["earn", "bank", "500", "salary"]));
        Assert.Equal(0, await commands.RunAsync(["transfer", "bank", "cash", "60", "--notes", "pocket"]));
        Assert.Equal(0, await commands.RunAsync(["list"]));
        Assert.Equal(0, await commands.RunAsync(["show", "2"]));
        Assert.Equal(0, await commands.RunAsync(["delete", "2"]));
        Assert.Equal(0, await commands.RunAsync(["undo"]));

        var text = output.ToString();
        Assert.Contains("+500.00 salary", text, StringComparison.Ordinal);
        Assert.Contains("⇄ 60.00 → Cash", text, StringComparison.Ordinal);
        Assert.Contains("→ into Cash", text, StringComparison.Ordinal); // show
        Assert.Contains("Undone", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Goals_And_Debts_Flow()
    {
        var commands = Build(out var output, out _);

        Assert.Equal(0, await commands.RunAsync(["goal", "add", "Laptop", "100", "--by", "2026-12-31"]));
        Assert.Equal(0, await commands.RunAsync(["goal", "contribute", "1", "95"]));
        Assert.Equal(0, await commands.RunAsync(["debt", "add", "Sara", "150", "--owes-me"]));
        Assert.Equal(0, await commands.RunAsync(["debt", "settle", "1", "150"]));
        Assert.Equal(0, await commands.RunAsync(["debt", "list"]));

        var text = output.ToString();
        Assert.Contains("✨ almost there", text, StringComparison.Ordinal);
        Assert.Contains("fully settled. ✔", text, StringComparison.Ordinal);
        Assert.Contains("← owed by Sara", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NetWorth_And_Report()
    {
        var commands = Build(out var output, out _);

        await commands.RunAsync(["account", "add", "Bank", "--start", "900"]);
        await commands.RunAsync(["spend", "bank", "300", "housing"]);
        Assert.Equal(0, await commands.RunAsync(["report"]));
        Assert.Equal(0, await commands.RunAsync(["report", "--month", "2026-09"]));
        Assert.Equal(0, await commands.RunAsync(["networth"]));

        var text = output.ToString();
        Assert.Contains("income 0.00 USD · expenses 300.00 USD · net -300.00 USD", text, StringComparison.Ordinal);
        Assert.Contains("housing", text, StringComparison.Ordinal);
        Assert.Contains("Net worth: 600.00 USD", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CurrencySetting_FlowsIntoNewAccounts()
    {
        var commands = Build(out _, out _, currency: () => "eur");

        Assert.Equal(0, await commands.RunAsync(["account", "add", "Bank"]));

        Assert.Equal("EUR", _store.ListAccounts().Single().Currency);
    }

    [Fact]
    public async Task ExportImport_WorkThroughFiles()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gj-{Guid.NewGuid():N}.json");
        try
        {
            var commands = Build(out var output, out _);
            await commands.RunAsync(["account", "add", "Bank", "--start", "10"]);

            Assert.Equal(0, await commands.RunAsync(["export", path]));
            Assert.Equal(0, await commands.RunAsync(["account", "remove", "1", "--force"]));
            Assert.Empty(_store.ListAccounts());
            Assert.Equal(0, await commands.RunAsync(["import", path]));

            Assert.Single(_store.ListAccounts());
            Assert.Contains("ganjoor undo reverts it", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task BillCommands_Flow()
    {
        var commands = Build(out var output, out _);

        await commands.RunAsync(["account", "add", "Bank", "--start", "5000"]);
        Assert.Equal(0, await commands.RunAsync(["bill", "add", "Rent", "950", "out", "--category", "housing", "--every", "monthly", "--next", "2026-09-01"]));
        Assert.Equal(0, await commands.RunAsync(["bill", "due"]));
        Assert.Equal(0, await commands.RunAsync(["bill", "apply"]));
        Assert.Equal(0, await commands.RunAsync(["bill", "list"]));
        Assert.Equal(0, await commands.RunAsync(["bill", "remove", "1"]));

        var text = output.ToString();
        Assert.Contains("was due 2026-09-01", text, StringComparison.Ordinal);
        Assert.Contains("Recorded #1: −950.00 housing", text, StringComparison.Ordinal);
        Assert.Contains("next 2026-10-01", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BillsNeedACategory()
    {
        var commands = Build(out _, out var error);
        await commands.RunAsync(["account", "add", "Bank"]);

        Assert.Equal(1, await commands.RunAsync(["bill", "add", "Rent", "950", "out", "--every", "monthly"]));

        Assert.Contains("Bills need a category", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DebtDirection_IsMandatory()
    {
        var commands = Build(out _, out var error);

        Assert.Equal(1, await commands.RunAsync(["debt", "add", "Sara", "150"]));

        Assert.Contains("Say who owes whom", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutStorage_OnlyHelpWorks()
    {
        using StringWriter output = new();
        using StringWriter error = new();
        var commands = new GanjoorCommands(
            null, TimeProvider.System, output, error);

        Assert.Equal(1, await commands.RunAsync(["account", "list"]));
        Assert.Contains("Wallet storage is not available", error.ToString(), StringComparison.Ordinal);

        Assert.Equal(0, await commands.RunAsync(["help"]));
    }
}

/// <summary>App routing, settings, and the AI gate for Ganjoor.</summary>
[Collection("EnvSequential")]
public sealed class AppGanjoorTests
{
    private sealed class StubAi : ISoroushClient
    {
        public List<string> Prompts { get; } = [];

        public string Response { get; set; } = "coffee";

        public Task<SoroushResult> CompleteAsync(string prompt, CancellationToken cancellationToken = default)
        {
            Prompts.Add(prompt);
            return Task.FromResult(new SoroushResult(Response, "stub", "stub-model", 1, TimeSpan.Zero));
        }
    }

    private sealed class StubWeatherClient : IAnahitaClient
    {
        public Task<GeoPlace?> GeocodeAsync(string name, CancellationToken cancellationToken = default)
        {
            GeoPlace place = new(name, string.Empty, 0, 0, "auto");
            return Task.FromResult<GeoPlace?>(place);
        }

        public Task<WeatherReport> GetForecastAsync(GeoPlace place, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("not used");
    }

    [Fact]
    public async Task AiCategorize_Suggests_ThenApplies()
    {
        var store = new MemoryGanjoorStore();
        var ai = new StubAi { Response = "**coffee**" };
        using StringWriter output = new();
        using StringWriter error = new();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 9, 19, 10, 0, 0, TimeSpan.Zero));
        var commands = new GanjoorCommands(
            store, clock, output, error,
            aiCompletion: (request, token) => ai.CompleteAsync(request.Prompt, token));

        await commands.RunAsync(["account", "add", "Bank"]);
        await commands.RunAsync(["spend", "bank", "4.80", "uncategorised", "--notes", "espresso bar"]);
        await commands.RunAsync(["budget", "set", "coffee", "10"]); // gives the AI a known category

        Assert.Equal(0, await commands.RunAsync(["ai", "categorize", "1"]));
        Assert.Contains("Suggestion: coffee", output.ToString(), StringComparison.Ordinal);

        Assert.Equal(0, await commands.RunAsync(["ai", "categorize", "1", "--apply"]));
        Assert.Equal("coffee", store.ListTransactions().Single().Category);

        Assert.Equal(2, ai.Prompts.Count); // suggestion + apply
        var prompt = ai.Prompts[0];
        Assert.Contains("---CATEGORIES BEGIN---", prompt, StringComparison.Ordinal);
        Assert.Contains("untrusted data", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AiCategorize_UnknownSuggestion_FailsFriendly()
    {
        var store = new MemoryGanjoorStore();
        var ai = new StubAi { Response = "spaceships" };
        using StringWriter error = new();
        var commands = new GanjoorCommands(
            store,
            new FixedTimeProvider(new DateTimeOffset(2026, 9, 19, 10, 0, 0, TimeSpan.Zero)),
            new StringWriter(), error,
            aiCompletion: (request, token) => ai.CompleteAsync(request.Prompt, token));

        await commands.RunAsync(["account", "add", "Bank"]);
        await commands.RunAsync(["spend", "bank", "4.80", "uncategorised"]);

        Assert.Equal(1, await commands.RunAsync(["ai", "categorize", "1"]));

        Assert.Contains("no known category", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AiInsights_AndAsk_FlowThroughTheGate()
    {
        var store = new MemoryGanjoorStore();
        var ai = new StubAi { Response = "You are fine." };
        using StringWriter output = new();
        var app = new App(
            output, new StringWriter(), new MemorySettingsStore(),
            soroushFactory: _ => ai,
            wallet: store,
            anahitaClientFactory: _ => new StubWeatherClient());
        Environment.SetEnvironmentVariable(App.ApiKeyEnvironmentVariable, "test-key");
        try
        {
            Assert.Equal(0, await app.RunAsync(["ganjoor", "account", "add", "Bank", "--start", "50"]));
            Assert.Equal(0, await app.RunAsync(["ganjoor", "ai", "insights"]));
            Assert.Equal(0, await app.RunAsync(["wallet", "ai", "ask", "Where does my money go?"]));

            Assert.EndsWith("You are fine.", output.ToString().TrimEnd(), StringComparison.Ordinal);
            Assert.Equal(2, ai.Prompts.Count);
            Assert.Contains("---FINANCE BEGIN---", ai.Prompts[0], StringComparison.Ordinal);
            Assert.Contains("Question: Where does my money go?", ai.Prompts[1], StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(App.ApiKeyEnvironmentVariable, null);
        }
    }

    [Fact]
    public async Task Ai_WithoutKey_FailsTheGate()
    {
        Environment.SetEnvironmentVariable(App.ApiKeyEnvironmentVariable, null);
        using StringWriter error = new();
        var app = new App(
            new StringWriter(), error, new MemorySettingsStore(),
            soroushFactory: _ => new StubAi(),
            wallet: new MemoryGanjoorStore());

        Assert.Equal(1, await app.RunAsync(["ganjoor", "ai", "insights"]));

        Assert.Contains("Missing API key", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task App_Help_MentionsGanjoor_AndAliasesRoute()
    {
        using StringWriter output = new();
        var app = new App(
            output, new StringWriter(), new MemorySettingsStore(),
            wallet: new MemoryGanjoorStore());

        Assert.Equal(0, await app.RunAsync(["--help"]));
        Assert.Contains("ganjoor", output.ToString(), StringComparison.Ordinal);

        Assert.Equal(0, await app.RunAsync(["ganjoor", "account", "list"]));
        Assert.Equal(0, await app.RunAsync(["wallet", "account", "list"]));
    }
}
