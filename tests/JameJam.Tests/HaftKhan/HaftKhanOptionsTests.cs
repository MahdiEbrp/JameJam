using JameJam.HaftKhan;
using JameJam.HaftKhan.Ai;

namespace JameJam.Tests.HaftKhan;

/// <summary>
/// Proves nothing is hardcoded: every Haft Khan knob flows from guard-validated
/// <see cref="HaftKhanOptions"/> into the service, the CLI, and the AI prompts.
/// </summary>
public sealed class HaftKhanOptionsTests
{
    private static readonly HaftKhanTask SampleTask = new(
        1, "Slay the dragon", "with Rakhsh", TaskPriority.Normal, TaskState.Todo, null,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

    [Fact]
    public void Defaults_AreValid()
    {
        new HaftKhanOptions().Validate(); // must not throw
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10_001)]
    public void Validate_RejectsOutOfRangeTitleLimit(int maxTitleLength) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new HaftKhanOptions { MaxTitleLength = maxTitleLength }.Validate());

    [Theory]
    [InlineData(0)]
    [InlineData(100_001)]
    public void Validate_RejectsOutOfRangeNotesLimit(int maxNotesLength) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new HaftKhanOptions { MaxNotesLength = maxNotesLength }.Validate());

    [Fact]
    public void Validate_RejectsMaxTasksBelowOne() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new HaftKhanOptions { MaxTasksInSummary = 0 }.Validate());

    [Fact]
    public void Validate_RejectsMinSubtasksBelowOne() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new HaftKhanOptions { BreakdownMinSubtasks = 0 }.Validate());

    [Theory]
    [InlineData(0)]
    [InlineData(32_001)]
    public void Validate_RejectsOutOfRangeNotesPromptCap(int maxNotesInPrompt) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new HaftKhanOptions { MaxNotesInPrompt = maxNotesInPrompt }.Validate());

    [Fact]
    public void Validate_RejectsBrokenSubtaskRange() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new HaftKhanOptions { BreakdownMinSubtasks = 5, BreakdownMaxSubtasks = 2 }.Validate());

    [Theory]
    [InlineData("MaxTagsPerTask")]
    [InlineData("MaxTagLength")]
    [InlineData("MaxRecurrenceInterval")]
    [InlineData("MaxUndoDepth")]
    [InlineData("BoardTasksPerColumn")]
    [InlineData("ReviewFocusCount")]
    [InlineData("MaxImportTasks")]
    public void Validate_RejectsEveryRailFloor(string option)
    {
        HaftKhanOptions options = option switch
        {
            "MaxTagsPerTask" => new HaftKhanOptions { MaxTagsPerTask = 0 },
            "MaxTagLength" => new HaftKhanOptions { MaxTagLength = 0 },
            "MaxRecurrenceInterval" => new HaftKhanOptions { MaxRecurrenceInterval = 0 },
            "MaxUndoDepth" => new HaftKhanOptions { MaxUndoDepth = 0 },
            "BoardTasksPerColumn" => new HaftKhanOptions { BoardTasksPerColumn = 0 },
            "ReviewFocusCount" => new HaftKhanOptions { ReviewFocusCount = 0 },
            _ => new HaftKhanOptions { MaxImportTasks = 0 },
        };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public void Service_EnforcesCustomTitleLimit()
    {
        var service = new HaftKhanService(
            new MemoryTaskRepository(), TimeProvider.System, new HaftKhanOptions { MaxTitleLength = 5 });

        Assert.Equal(5, service.AddTask("12345", null, null, null).Title.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => service.AddTask("123456", null, null, null));
    }

    [Fact]
    public void Service_EnforcesCustomNotesLimit()
    {
        var service = new HaftKhanService(
            new MemoryTaskRepository(), TimeProvider.System, new HaftKhanOptions { MaxNotesLength = 4 });

        Assert.Throws<ArgumentOutOfRangeException>(() => service.AddTask("t", "too long", null, null));
    }

    [Fact]
    public void Assistant_UsesCustomSummaryCap()
    {
        var assistant = new AiTaskAssistant(new HaftKhanOptions { MaxTasksInSummary = 2 });
        var tasks = Enumerable.Range(1, 5)
            .Select(index => SampleTask with { Id = index, Title = $"task {index}" })
            .ToList();

        var prompt = assistant.BuildSummaryPrompt(tasks);

        Assert.Contains("#2 ", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("#3 ", prompt, StringComparison.Ordinal);
        Assert.Contains("3 more tasks omitted", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Assistant_UsesCustomSubtaskRange()
    {
        var assistant = new AiTaskAssistant(new HaftKhanOptions
        {
            BreakdownMinSubtasks = 2,
            BreakdownMaxSubtasks = 4,
        });

        Assert.Contains(
            "into 2 to 4 short, actionable subtasks",
            assistant.BuildBreakdownPrompt(SampleTask),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Assistant_UsesCustomNotesPromptCap()
    {
        var assistant = new AiTaskAssistant(new HaftKhanOptions { MaxNotesInPrompt = 10 });

        var prompt = assistant.BuildBreakdownPrompt(SampleTask with { Notes = new string('n', 50) });

        Assert.Contains("nnnnnnnnnn…", prompt, StringComparison.Ordinal); // 10 chars + ellipsis
    }

    [Fact]
    public async Task App_FlowsCustomOptionsThroughToTheCommands()
    {
        using StringWriter output = new();
        using StringWriter error = new();
        var app = new App(
            output, error, null, null, new MemoryTaskRepository(), null,
            new HaftKhanOptions { MaxTitleLength = 5 });

        Assert.Equal(1, await app.RunAsync(["haftkhan", "add", "too long title"]));

        Assert.Contains("maximum length of 5", error.ToString(), StringComparison.Ordinal);
    }
}
