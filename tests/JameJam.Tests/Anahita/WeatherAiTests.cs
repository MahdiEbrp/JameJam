using JameJam.Anahita;
using JameJam.HaftKhan;
using JameJam.Settings;
using JameJam.Soroush;
using JameJam.Tests.Anahita;

namespace JameJam.Tests;

/// <summary>Tests for the <c>weather ai</c> CLI commands and their Soroush wiring.</summary>
[Collection("EnvSequential")]
public sealed class WeatherAiTests
{
    private static readonly DateOnly Saturday = new(2026, 9, 19);
    private static readonly DateOnly Sunday = new(2026, 9, 20);

    private readonly StubWeatherClient _weather = new();
    private readonly StubAi _ai = new();

    private sealed class StubWeatherClient : IAnahitaClient
    {
        public Task<GeoPlace?> GeocodeAsync(string name, CancellationToken cancellationToken = default)
        {
            GeoPlace place = new(name, "Germany", 52.52, 13.41, "Europe/Berlin");
            return Task.FromResult<GeoPlace?>(place);
        }

        public Task<WeatherReport> GetForecastAsync(GeoPlace place, CancellationToken cancellationToken = default) =>
            Task.FromResult(AnahitaTestSupport.MakeReport(
                AnahitaTestSupport.Day(Saturday),
                AnahitaTestSupport.Day(Sunday, code: 61, chance: 80)));
    }

    private sealed class StubAi : ISoroushClient
    {
        public List<string> Prompts { get; } = [];

        public string Response { get; set; } = "Sunny with light winds — a fine day.";

        public Task<SoroushResult> CompleteAsync(string prompt, CancellationToken cancellationToken = default)
        {
            Prompts.Add(prompt);
            return Task.FromResult(new SoroushResult(Response, "stub", "stub-model", 1, TimeSpan.Zero));
        }
    }

    private AnahitaCommands Build(
        out StringWriter output,
        out StringWriter error,
        Func<IReadOnlyList<HaftKhanTask>>? openTasks = null)
    {
        output = new StringWriter();
        error = new StringWriter();
        return new AnahitaCommands(
            output,
            error,
            new FixedTimeProvider(AnahitaTestSupport.Now),
            clientFactory: _ => _weather,
            openTasksProvider: openTasks,
            aiCompletion: (request, token) => _ai.CompleteAsync(request.Prompt, token));
    }

    [Fact]
    public async Task Explain_SendsAWeatherPrompt_AndPrintsTheAnswer()
    {
        var commands = Build(out var output, out var error);

        Assert.Equal(0, await commands.RunAsync(["ai", "explain", "--at", "Berlin"]));

        var prompt = Assert.Single(_ai.Prompts);
        Assert.Contains("---WEATHER BEGIN---", prompt, StringComparison.Ordinal);
        Assert.Contains("Place: Berlin", prompt, StringComparison.Ordinal);
        Assert.Equal("Sunny with light winds — a fine day.", output.ToString().TrimEnd());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task Ask_SendsTheQuestion_AndPrintsTheAnswer()
    {
        var commands = Build(out var output, out _);
        _ai.Response = "Yes, but take a rain jacket for Sunday.";

        Assert.Equal(0, await commands.RunAsync(["ai", "ask", "Should", "I", "bike", "tomorrow?", "--at", "Berlin"]));

        var prompt = Assert.Single(_ai.Prompts);
        Assert.Contains("Question: Should I bike tomorrow?", prompt, StringComparison.Ordinal);
        Assert.Contains("---WEATHER BEGIN---", prompt, StringComparison.Ordinal);
        Assert.Contains("rain jacket", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ask_WithoutAQuestion_ShowsUsage()
    {
        var commands = Build(out _, out var error);

        Assert.Equal(1, await commands.RunAsync(["ai", "ask", "--at", "Berlin"]));

        Assert.Contains("Ask a question", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(_ai.Prompts);
    }

    [Fact]
    public async Task Ask_UnknownFlag_IsRejected()
    {
        var commands = Build(out _, out var error);

        Assert.Equal(1, await commands.RunAsync(["ai", "ask", "hello", "--fly"]));

        Assert.Contains("Unknown weather option '--fly'", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(_ai.Prompts);
    }

    [Fact]
    public async Task Ask_InvalidUnits_AreRejected()
    {
        var commands = Build(out _, out var error);

        Assert.Equal(1, await commands.RunAsync(["ai", "ask", "hello", "--units", "kelvin"]));

        Assert.Contains("Unknown units 'kelvin'", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plan_MatchesTasksToDays_AndPrintsNumberedSteps()
    {
        var created = AnahitaTestSupport.Now;
        IReadOnlyList<HaftKhanTask> tasks =
        [
            new(1, "Water the garden", string.Empty, TaskPriority.Normal, TaskState.Todo, Saturday, created, created, null),
            new(2, "Paint the shed", string.Empty, TaskPriority.Normal, TaskState.Todo, null, created, created, null),
        ];
        var commands = Build(out var output, out _, openTasks: () => tasks);
        _ai.Response = "1. Water the garden → Sat 19 Sep (already due)\n2. Paint the shed → Tue 22 Sep (sunny)";

        Assert.Equal(0, await commands.RunAsync(["ai", "plan", "--at", "Berlin"]));

        var prompt = Assert.Single(_ai.Prompts);
        Assert.Contains("---TASKS BEGIN---", prompt, StringComparison.Ordinal);
        Assert.Contains("#1 [normal] Water the garden (due 2026-09-19)", prompt, StringComparison.Ordinal);
        Assert.Contains("Place: Berlin", prompt, StringComparison.Ordinal);

        var text = output.ToString();
        Assert.StartsWith("Suggested schedule:", text, StringComparison.Ordinal);
        Assert.Contains("1. Water the garden → Sat 19 Sep (already due)", text, StringComparison.Ordinal);
        Assert.Contains("2. Paint the shed → Tue 22 Sep (sunny)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plan_WithoutTasks_IsPeaceful()
    {
        var commands = Build(out var output, out _, openTasks: () => []);

        Assert.Equal(0, await commands.RunAsync(["ai", "plan", "--at", "Berlin"]));

        Assert.Contains("Nothing open", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(_ai.Prompts);
    }

    [Fact]
    public async Task Plan_WithoutTaskStorage_Fails()
    {
        var commands = Build(out _, out var error); // no openTasks provider

        Assert.Equal(1, await commands.RunAsync(["ai", "plan", "--at", "Berlin"]));

        Assert.Contains("needs the Haft Khan task list", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutAnAiRoute_Fails()
    {
        using StringWriter output = new();
        using StringWriter error = new();
        var commands = new AnahitaCommands(
            output, error, new FixedTimeProvider(AnahitaTestSupport.Now),
            clientFactory: _ => _weather,
            aiCompletion: null);

        Assert.Equal(1, await commands.RunAsync(["ai", "explain"]));

        Assert.Contains("AI is not available in this context.", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyAiResponse_IsAFriendlyError()
    {
        var created = AnahitaTestSupport.Now;
        IReadOnlyList<HaftKhanTask> tasks =
        [
            new(1, "Water the garden", string.Empty, TaskPriority.Normal, TaskState.Todo, null, created, created, null),
        ];
        var commands = Build(out _, out var error, openTasks: () => tasks);
        _ai.Response = "   ";

        Assert.Equal(1, await commands.RunAsync(["ai", "plan", "--at", "Berlin"]));

        Assert.Contains("The AI returned an empty response.", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownAiSubcommand_ShowsUsage()
    {
        var commands = Build(out _, out var error);

        Assert.Equal(1, await commands.RunAsync(["ai", "teleport"]));

        Assert.Contains("Usage: JameJam weather ai", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_ListsTheAiCommands()
    {
        var commands = Build(out var output, out _);

        Assert.Equal(0, await commands.RunAsync(["help"]));

        var text = output.ToString();
        Assert.Contains("ai explain", text, StringComparison.Ordinal);
        Assert.Contains("ai ask", text, StringComparison.Ordinal);
        Assert.Contains("ai plan", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task App_RoutesWeatherAi_EndToEnd_ThroughTheSoroushGate()
    {
        Environment.SetEnvironmentVariable(App.ApiKeyEnvironmentVariable, "test-key-1234");
        try
        {
            using StringWriter output = new();
            using StringWriter error = new();
            var app = new App(
                output,
                error,
                new MemorySettingsStore(),
                soroushFactory: _ => _ai,
                anahitaClientFactory: _ => _weather);

            Assert.Equal(0, await app.RunAsync(["weather", "ai", "explain", "--at", "Berlin"]));

            Assert.Contains("fine day", output.ToString(), StringComparison.Ordinal);
            var prompt = Assert.Single(_ai.Prompts);
            Assert.Contains("WEATHER BEGIN", prompt, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(App.ApiKeyEnvironmentVariable, null);
        }
    }

    [Fact]
    public async Task App_WeatherAi_WithoutKey_FailsTheGate()
    {
        Environment.SetEnvironmentVariable(App.ApiKeyEnvironmentVariable, null);
        using StringWriter output = new();
        using StringWriter error = new();
        var app = new App(
            output,
            error,
            new MemorySettingsStore(),
            soroushFactory: _ => _ai,
            anahitaClientFactory: _ => _weather);

        Assert.Equal(1, await app.RunAsync(["weather", "ai", "explain", "--at", "Berlin"]));

        Assert.Contains("Missing API key", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(_ai.Prompts);
    }

    [Fact]
    public async Task OpenTasks_FeedsTheAiPlanner()
    {
        using StringWriter noise = new();
        MemoryTaskRepository tasks = new();
        var created = AnahitaTestSupport.Now;
        tasks.Add(new NewTask("dated", string.Empty, TaskPriority.Normal, Saturday));
        tasks.Add(new NewTask("undated", string.Empty, TaskPriority.Normal, null));
        tasks.Add(new NewTask("gone-fishing", string.Empty, TaskPriority.Normal, Sunday));
        var commands = new HaftKhanCommands(
            tasks, new FixedTimeProvider(AnahitaTestSupport.Now), noise, noise, aiCompletion: null);
        Assert.Equal(0, await commands.RunAsync(["done", "3"])); // complete one → no longer open

        var open = commands.OpenTasks();

        Assert.NotNull(open);
        Assert.Equal(["dated", "undated"], open.Select(t => t.Title).ToList());
    }
}
