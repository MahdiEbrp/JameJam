using JameJam.HaftKhan;
using JameJam.Settings;
using JameJam.Soroush;

namespace JameJam.Tests;

/// <summary>Tests for the <c>haftkhan</c> commands of <see cref="App"/> (CLI surface, AI path, safety gates).</summary>
[Collection("EnvSequential")]
public sealed class AppHaftKhanTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 10, 0, 0, TimeSpan.Zero);

    private readonly MemoryTaskRepository _tasks = new();

    [Fact]
    public async Task Add_ThenList_ShowsTheOpenTask()
    {
        var app = BuildApp(out var output, out var error);

        Assert.Equal(0, await app.RunAsync(["haftkhan", "add", "Slay", "the", "dragon", "--priority", "critical", "--due", "2026-09-25"]));
        Assert.Equal(0, await app.RunAsync(["haftkhan", "list"]));

        var listing = output.ToString();
        Assert.Contains("Added #1: Slay the dragon", listing, StringComparison.Ordinal);
        Assert.Contains("#1 [ ] critical Slay the dragon  [due 2026-09-25]", listing, StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task Start_ThenDone_ProgressesTheTask()
    {
        var app = BuildApp(out var output, out _);
        _ = await app.RunAsync(["haftkhan", "add", "labour"]);

        Assert.Equal(0, await app.RunAsync(["haftkhan", "start", "1"]));
        Assert.Equal(0, await app.RunAsync(["haftkhan", "done", "1"]));

        var listing = output.ToString();
        Assert.Contains("Started #1: labour", listing, StringComparison.Ordinal);
        Assert.Contains("Conquered #1: labour", listing, StringComparison.Ordinal);
        Assert.Equal(TaskState.Done, _tasks.Find(1)!.State);
    }

    [Fact]
    public async Task List_Views_FilterCorrectly()
    {
        var app = BuildApp(out var output, out _);
        _ = await app.RunAsync(["haftkhan", "add", "past", "--due", "2026-09-01"]);
        _ = await app.RunAsync(["haftkhan", "add", "present", "--due", "2026-09-19"]);

        output.GetStringBuilder().Clear(); // drop the "Added ..." echoes first
        Assert.Equal(0, await app.RunAsync(["haftkhan", "list", "overdue"]));
        Assert.Contains("past", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("present", output.ToString(), StringComparison.Ordinal);

        output.GetStringBuilder().Clear();
        Assert.Equal(0, await app.RunAsync(["haftkhan", "list", "today"]));
        Assert.Contains("present", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_MissingTask_Fails()
    {
        var app = BuildApp(out _, out var error);

        Assert.Equal(1, await app.RunAsync(["haftkhan", "remove", "404"]));

        Assert.Contains("Task 404 was not found.", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_WithBadDate_Fails()
    {
        var app = BuildApp(out _, out var error);

        Assert.Equal(1, await app.RunAsync(["haftkhan", "add", "t", "--due", "not-a-date"]));

        Assert.Contains("Invalid date 'not-a-date'", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(_tasks.ListAll());
    }

    [Fact]
    public async Task Add_WithBadPriority_Fails()
    {
        var app = BuildApp(out _, out var error);

        Assert.Equal(1, await app.RunAsync(["haftkhan", "add", "t", "--priority", "urgent"]));

        Assert.Contains("Unknown priority 'urgent'", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(_tasks.ListAll());
    }

    [Fact]
    public async Task Add_WithoutTitle_ShowsUsage()
    {
        var app = BuildApp(out _, out var error);

        Assert.Equal(1, await app.RunAsync(["haftkhan", "add", "--due", "2026-09-25"]));

        Assert.Contains("Usage: JameJam haftkhan add", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Show_PrintsNotesAndDetails()
    {
        var app = BuildApp(out var output, out _);
        _ = await app.RunAsync(["haftkhan", "add", "labour", "--notes", "with Rakhsh"]);

        Assert.Equal(0, await app.RunAsync(["haftkhan", "show", "1"]));

        var shown = output.ToString();
        Assert.Contains("#1 [ ] normal   labour", shown, StringComparison.Ordinal);
        Assert.Contains("with Rakhsh", shown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClearDone_ReportsHowManyWereRemoved()
    {
        var app = BuildApp(out var output, out _);
        _ = await app.RunAsync(["haftkhan", "add", "one"]);
        _ = await app.RunAsync(["haftkhan", "add", "two"]);
        _ = await app.RunAsync(["haftkhan", "done", "1"]);
        _ = await app.RunAsync(["haftkhan", "done", "2"]);

        Assert.Equal(0, await app.RunAsync(["haftkhan", "clear-done"]));

        Assert.Contains("Cleared 2 completed tasks.", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(_tasks.ListOpen());
    }

    [Fact]
    public async Task UnknownSubcommand_Fails()
    {
        var app = BuildApp(out _, out var error);

        Assert.Equal(1, await app.RunAsync(["haftkhan", "conquer-everything"]));

        Assert.Contains("Unknown haftkhan command 'conquer-everything'", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_EmptyView_SaysSo()
    {
        var app = BuildApp(out var output, out _);

        Assert.Equal(0, await app.RunAsync(["haftkhan", "list"]));

        Assert.Contains("No tasks in this view.", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OverdueTasks_AreLabeled()
    {
        var app = BuildApp(out var output, out _);
        _ = await app.RunAsync(["haftkhan", "add", "late", "--due", "2026-09-01"]);

        Assert.Equal(0, await app.RunAsync(["haftkhan", "list"]));

        Assert.Contains("[overdue since 2026-09-01]", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stats_ReportsCounts()
    {
        var app = BuildApp(out var output, out _);
        _ = await app.RunAsync(["haftkhan", "add", "overdue one", "--due", "2026-09-01"]);
        _ = await app.RunAsync(["haftkhan", "add", "plain"]);

        Assert.Equal(0, await app.RunAsync(["haftkhan", "stats"]));

        Assert.Contains(
            "Haft Khan — 2 task(s): 2 to do, 0 doing, 0 done, 1 overdue.",
            output.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_PrintsUsage()
    {
        var app = BuildApp(out var output, out _);

        Assert.Equal(0, await app.RunAsync(["haftkhan"]));

        Assert.Contains("Haft Khan — advanced to-do list", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutRepository_Fails()
    {
        using StringWriter output = new();
        using StringWriter error = new();
        var app = new App(output, error);

        Assert.Equal(1, await app.RunAsync(["haftkhan", "list"]));

        Assert.Contains("not available", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AiBreakdown_WithoutKey_FailsSafely()
    {
        Environment.SetEnvironmentVariable(App.ApiKeyEnvironmentVariable, null);
        try
        {
            var factory = new RecordingFactory();
            using StringWriter output = new();
            using StringWriter error = new();
            var app = new App(output, error, new MemorySettingsStore(), factory.Create, _tasks);
            _ = await app.RunAsync(["haftkhan", "add", "labour"]);

            Assert.Equal(1, await app.RunAsync(["haftkhan", "ai", "breakdown", "1"]));

            Assert.Contains(App.ApiKeyEnvironmentVariable, error.ToString(), StringComparison.Ordinal);
            Assert.Empty(factory.Options);
        }
        finally
        {
            Environment.SetEnvironmentVariable(App.ApiKeyEnvironmentVariable, null);
        }
    }

    [Fact]
    public async Task AiBreakdown_WithStubbedAi_PrintsPlan()
    {
        Environment.SetEnvironmentVariable(App.ApiKeyEnvironmentVariable, "test-key");
        try
        {
            var factory = new RecordingFactory(
                new SoroushResult("1. Sharpen the sword\n2. Pack the shield\n3. Ride at dawn", "stub", "stub-model", 1, TimeSpan.Zero));
            using StringWriter output = new();
            using StringWriter error = new();
            var app = new App(output, error, new MemorySettingsStore(), factory.Create, _tasks);
            _ = await app.RunAsync(["haftkhan", "add", "Slay the dragon"]);

            Assert.Equal(0, await app.RunAsync(["haftkhan", "ai", "breakdown", "1"]));

            var printed = output.ToString();
            Assert.Contains("Plan for #1 — Slay the dragon", printed, StringComparison.Ordinal);
            Assert.Contains("1. Sharpen the sword", printed, StringComparison.Ordinal);
            Assert.Contains("3. Ride at dawn", printed, StringComparison.Ordinal);

            // The prompt that reached the AI layer contains the task and the injection rule.
            var request = Assert.Single(factory.Requests);
            Assert.Contains("Title: Slay the dragon", request.Prompt, StringComparison.Ordinal);
            Assert.Contains("untrusted data", request.Prompt, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(App.ApiKeyEnvironmentVariable, null);
        }
    }

    [Fact]
    public async Task AiSummary_WithoutOpenTasks_DoesNotCallTheAi()
    {
        Environment.SetEnvironmentVariable(App.ApiKeyEnvironmentVariable, "test-key");
        try
        {
            var factory = new RecordingFactory(
                new SoroushResult("should not be reached", "stub", "stub-model", 1, TimeSpan.Zero));
            using StringWriter output = new();
            using StringWriter error = new();
            var app = new App(output, error, new MemorySettingsStore(), factory.Create, _tasks);

            Assert.Equal(0, await app.RunAsync(["haftkhan", "ai", "summary"]));

            Assert.Contains("Nothing open", output.ToString(), StringComparison.Ordinal);
            Assert.Empty(factory.Requests);
        }
        finally
        {
            Environment.SetEnvironmentVariable(App.ApiKeyEnvironmentVariable, null);
        }
    }

    [Fact]
    public async Task Ai_WithoutSubcommand_ShowsUsage()
    {
        var app = BuildApp(out _, out var error);
        _ = await app.RunAsync(["haftkhan", "add", "t"]);

        Assert.Equal(1, await app.RunAsync(["haftkhan", "ai"]));

        Assert.Contains("Usage: JameJam haftkhan ai", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ai_WithUnknownSubcommand_ShowsUsage()
    {
        var app = BuildApp(out _, out var error);

        Assert.Equal(1, await app.RunAsync(["haftkhan", "ai", "conquer"]));

        Assert.Contains("Usage: JameJam haftkhan ai", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AiBreakdown_WithMissingTask_Fails()
    {
        Environment.SetEnvironmentVariable(App.ApiKeyEnvironmentVariable, "test-key");
        try
        {
            var factory = new RecordingFactory();
            using StringWriter output = new();
            using StringWriter error = new();
            var app = new App(output, error, new MemorySettingsStore(), factory.Create, _tasks);

            Assert.Equal(1, await app.RunAsync(["haftkhan", "ai", "breakdown", "404"]));

            Assert.Contains("Task 404 was not found.", error.ToString(), StringComparison.Ordinal);
            Assert.Empty(factory.Requests);
        }
        finally
        {
            Environment.SetEnvironmentVariable(App.ApiKeyEnvironmentVariable, null);
        }
    }

    [Fact]
    public async Task AiSummary_WithOpenTasks_CallsTheAiAndPrintsTheAnswer()
    {
        Environment.SetEnvironmentVariable(App.ApiKeyEnvironmentVariable, "test-key");
        try
        {
            var factory = new RecordingFactory(new SoroushResult(
                "Focus: finish the toolbox", "stub", "stub-model", 1, TimeSpan.Zero));
            using StringWriter output = new();
            using StringWriter error = new();
            var app = new App(output, error, new MemorySettingsStore(), factory.Create, _tasks);
            _ = await app.RunAsync(["haftkhan", "add", "a labour"]);

            Assert.Equal(0, await app.RunAsync(["haftkhan", "ai", "summary"]));

            Assert.Contains("Focus: finish the toolbox", output.ToString(), StringComparison.Ordinal);
            var request = Assert.Single(factory.Requests);
            Assert.Contains("#1 [normal] a labour", request.Prompt, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(App.ApiKeyEnvironmentVariable, null);
        }
    }

    [Fact]
    public async Task Commands_WithoutAiCallback_FailsFast()
    {
        // Via App the AI path always exists, so this guard is only reachable on direct construction.
        using StringWriter output = new();
        using StringWriter error = new();
        _tasks.Add(new NewTask("t", string.Empty, TaskPriority.Normal, null));
        var commands = new HaftKhanCommands(_tasks, TimeProvider.System, output, error, aiCompletion: null);

        Assert.Equal(1, await commands.RunAsync(["ai", "breakdown", "1"]));

        Assert.Contains("AI is not available", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RichAdd_AcceptsAllFlags_IncludingNaturalDates()
    {
        var app = BuildApp(out var output, out var error);

        Assert.Equal(0, await app.RunAsync([
            "haftkhan", "add", "Train Rakhsh", "--due", "tomorrow", "--priority", "high",
            "--project", "stable", "--tags", "horse,hero", "--effort", "l",
            "--every", "daily", "--interval", "2"]));

        var stored = _tasks.Find(1)!;
        Assert.Equal(new DateOnly(2026, 9, 20), stored.DueDate);
        Assert.Equal("stable", stored.Project);
        Assert.Equal(["horse", "hero"], stored.Tags);
        Assert.Equal(TaskEffort.Large, stored.Effort);
        Assert.Equal(RecurrenceKind.Daily, stored.Recurrence);
        Assert.Equal(2, stored.RecurrenceInterval);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task Add_WithBlockers_CreatesDependency()
    {
        var app = BuildApp(out _, out _);
        _ = await app.RunAsync(["haftkhan", "add", "first"]);
        _ = await app.RunAsync(["haftkhan", "add", "second", "--after", "1"]);

        Assert.Equal(1, await app.RunAsync(["haftkhan", "done", "2"])); // blocked
        Assert.Equal(0, await app.RunAsync(["haftkhan", "done", "2", "--force"])); // forced
        Assert.Contains("blocked by open task(s): #1", _errorBuffer(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindCommand_SearchesEverything()
    {
        var app = BuildApp(out var output, out _);
        _ = await app.RunAsync(["haftkhan", "add", "Slay dragon", "--tags", "myth"]);

        Assert.Equal(0, await app.RunAsync(["haftkhan", "find", "MYTH"]));
        Assert.Contains("#1 [ ] normal   Slay dragon  #myth", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListFilters_WorkFromTheCli()
    {
        var app = BuildApp(out var output, out _);
        _ = await app.RunAsync(["haftkhan", "add", "a", "--tags", "x", "--priority", "high"]);
        _ = await app.RunAsync(["haftkhan", "add", "b", "--tags", "y"]);

        output.GetStringBuilder().Clear();
        Assert.Equal(0, await app.RunAsync(["haftkhan", "list", "--tag", "x"]));
        Assert.Contains("a", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("b", output.ToString(), StringComparison.Ordinal);

        output.GetStringBuilder().Clear();
        Assert.Equal(0, await app.RunAsync(["haftkhan", "list", "--priority", "high"]));
        Assert.Contains("a", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("b", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BoardMatrixFocusReview_Render()
    {
        var app = BuildApp(out var output, out _);
        _ = await app.RunAsync(["haftkhan", "add", "urgent thing", "--priority", "critical", "--due", "today"]);
        _ = await app.RunAsync(["haftkhan", "add", "plain thing"]);
        _ = await app.RunAsync(["haftkhan", "start", "2"]);

        Assert.Equal(0, await app.RunAsync(["haftkhan", "board"]));
        var rendered = output.ToString();
        Assert.Contains("── TODO", rendered, StringComparison.Ordinal);
        Assert.Contains("── DOING", rendered, StringComparison.Ordinal);
        Assert.Contains("── DONE", rendered, StringComparison.Ordinal);

        output.GetStringBuilder().Clear();
        Assert.Equal(0, await app.RunAsync(["haftkhan", "matrix"]));
        rendered = output.ToString();
        Assert.Contains("DO NOW — urgent + important", rendered, StringComparison.Ordinal);
        Assert.Contains("urgent thing", rendered, StringComparison.Ordinal);

        output.GetStringBuilder().Clear();
        Assert.Equal(0, await app.RunAsync(["haftkhan", "focus"]));
        Assert.Contains("FOCUS → #1 [critical] urgent thing", output.ToString(), StringComparison.Ordinal);

        output.GetStringBuilder().Clear();
        Assert.Equal(0, await app.RunAsync(["haftkhan", "review"]));
        Assert.Contains("weekly review", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Focus next:", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UndoCommand_RevertsFromTheCli()
    {
        var app = BuildApp(out var output, out _);
        _ = await app.RunAsync(["haftkhan", "add", "labour"]);
        _ = await app.RunAsync(["haftkhan", "done", "1"]);

        Assert.Equal(0, await app.RunAsync(["haftkhan", "undo"]));
        Assert.Contains("Reverted 'done' (1 task affected).", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(TaskState.Todo, _tasks.Find(1)!.State);

        Assert.Equal(0, await app.RunAsync(["haftkhan", "undo"]));
        Assert.Contains("Reverted 'add'", output.ToString(), StringComparison.Ordinal);
        Assert.Null(_tasks.Find(1));

        Assert.Equal(1, await app.RunAsync(["haftkhan", "undo"]));
    }

    [Fact]
    public async Task Done_RecurringTask_RespawnsAndShowsNext()
    {
        var app = BuildApp(out var output, out _);
        _ = await app.RunAsync(["haftkhan", "add", "daily chore", "--every", "daily"]);

        Assert.Equal(0, await app.RunAsync(["haftkhan", "done", "1"]));

        var printed = output.ToString();
        Assert.Contains("Conquered #1: daily chore", printed, StringComparison.Ordinal);
        Assert.Contains("Respawned #2: daily chore (due 2026-09-20)", printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportImport_RoundTripsThroughRealFiles()
    {
        var app = BuildApp(out var output, out _);
        _ = await app.RunAsync(["haftkhan", "add", "first", "--tags", "a,b"]);
        _ = await app.RunAsync(["haftkhan", "add", "second", "--after", "1"]);
        var path = Path.Combine(Path.GetTempPath(), $"jamejam-export-{Guid.NewGuid():N}.json");
        try
        {
            Assert.Equal(0, await app.RunAsync(["haftkhan", "export", path]));
            Assert.Contains("Exported 2 task(s)", output.ToString(), StringComparison.Ordinal);

            var freshTasks = new MemoryTaskRepository();
            using StringWriter freshOutput = new();
            using StringWriter freshError = new();
            var fresh = new App(freshOutput, freshError, null, null, freshTasks, new FixedTimeProvider(Now));
            Assert.Equal(0, await fresh.RunAsync(["haftkhan", "import", path]));
            Assert.Contains("Imported 2 task(s), 1 link(s).", freshOutput.ToString(), StringComparison.Ordinal);
            Assert.Equal(["first", "second"], freshTasks.ListAll().Select(task => task.Title));
            Assert.Equal(["a", "b"], freshTasks.GetTags(1));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExportMarkdown_WritesChecklist()
    {
        var app = BuildApp(out var output, out _);
        _ = await app.RunAsync(["haftkhan", "add", "seen"]);
        var path = Path.Combine(Path.GetTempPath(), $"jamejam-export-{Guid.NewGuid():N}.md");
        try
        {
            Assert.Equal(0, await app.RunAsync(["haftkhan", "export", path]));
            Assert.Contains("- [ ] seen", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Export_WithBadExtension_Fails()
    {
        var app = BuildApp(out _, out var error);

        Assert.Equal(1, await app.RunAsync(["haftkhan", "export", "backup.txt"]));

        Assert.Contains(".json or .md", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_MissingFile_Fails()
    {
        var app = BuildApp(out _, out var error);

        Assert.Equal(1, await app.RunAsync(["haftkhan", "import", "nope.json"]));

        Assert.Contains("File not found: nope.json", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinkCommand_ConnectsExistingTasks()
    {
        var app = BuildApp(out var output, out _);
        _ = await app.RunAsync(["haftkhan", "add", "a"]);
        _ = await app.RunAsync(["haftkhan", "add", "b"]);

        Assert.Equal(0, await app.RunAsync(["haftkhan", "link", "2", "--after", "1"]));
        Assert.Contains("#2 is now blocked by 1.", output.ToString(), StringComparison.Ordinal);

        Assert.Equal(1, await app.RunAsync(["haftkhan", "link", "2", "--after", "2"])); // self
        Assert.Equal(1, await app.RunAsync(["haftkhan", "link", "1", "--after", "2"])); // cycle
        Assert.Contains("would depend on itself", _errorBuffer(), StringComparison.Ordinal);

        Assert.Equal(0, await app.RunAsync(["haftkhan", "unlink", "2"]));
        Assert.Contains("Removed all links of #2.", output.ToString(), StringComparison.Ordinal);
    }

    private string _errorBuffer() => _lastError?.ToString() ?? string.Empty;

    private StringWriter? _lastError;

    private App BuildApp(out StringWriter output, out StringWriter error)
    {
        output = new StringWriter();
        error = new StringWriter();
        _lastError = error;
        return new App(output, error, new MemorySettingsStore(), null, _tasks, new FixedTimeProvider(Now));
    }

    /// <summary>Stub AI path: records the requests that reach the Soroush layer and returns a canned answer.</summary>
    private sealed class RecordingFactory(SoroushResult? result = null)
    {
        public List<SoroushOptions> Options { get; } = [];

        public List<AiRequest> Requests { get; } = [];

        public StubClient Create(SoroushOptions options)
        {
            Options.Add(options);
            return new StubClient(result, Requests);
        }
    }

    private sealed class StubClient(SoroushResult? result, List<AiRequest> requests) : ISoroushClient
    {
        public Task<SoroushResult> CompleteAsync(string prompt, CancellationToken cancellationToken = default)
        {
            requests.Add(new AiRequest(prompt));
            return Task.FromResult(result ?? new SoroushResult("fake answer", "stub", "stub-model", 1, TimeSpan.Zero));
        }
    }
}
