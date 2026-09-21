using System.Globalization;

using JameJam.Tests;
using JameJam.Taqvim;
using JameJam.Taqvim.Ai;

namespace JameJam.Tests.Taqvim;

/// <summary>Prompt building (bounded, marker-guarded) and defensive reply parsing.</summary>
public sealed class ScheduleAssistantTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private static Occurrence Occ(string title, string start, string end, string location = "", string notes = "", string calendar = "Work") => new(
        new TaqvimEvent(
            1, calendar, title, location, notes, string.Empty,
            DateTimeOffset.Parse(start, CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(end, CultureInfo.InvariantCulture),
            false, null, [], Now, Now),
        DateTimeOffset.Parse(start, CultureInfo.InvariantCulture),
        DateTimeOffset.Parse(end, CultureInfo.InvariantCulture));

    [Fact]
    public void BriefPrompt_ContainsDay_Markers_AndUntrustedRule()
    {
        var assistant = new ScheduleAssistant();
        var prompt = assistant.BuildBriefPrompt(
            [Occ("Lunch", "2026-09-20T12:00:00+00:00", "2026-09-20T13:00:00+00:00")], Now);
        Assert.Contains("---EVENT BEGIN---", prompt, StringComparison.Ordinal);
        Assert.Contains("---EVENT END---", prompt, StringComparison.Ordinal);
        Assert.Contains("UNTRUSTED USER DATA", prompt, StringComparison.Ordinal);
        Assert.Contains("Lunch", prompt, StringComparison.Ordinal);
        Assert.Contains("2026", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanPrompt_IncludesFreeWindows()
    {
        var assistant = new ScheduleAssistant();
        var prompt = assistant.BuildPlanPrompt(
            [Occ("Deep work", "2026-09-21T09:00:00+00:00", "2026-09-21T11:00:00+00:00")],
            ["Mon 13:00-15:00 (120 min free)"]);
        Assert.Contains("Mon 13:00-15:00", prompt, StringComparison.Ordinal);
        Assert.Contains("Deep work", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanPrompt_HonorsTheEventRail()
    {
        var assistant = new ScheduleAssistant(new TaqvimOptions { MaxAiEvents = 3 });
        var many = Enumerable.Range(0, 10)
            .Select(i => Occ($"Event {i}", $"2026-09-21T0{i % 9}:00:00+00:00", $"2026-09-21T0{i % 9}:30:00+00:00"))
            .ToList();
        var prompt = assistant.BuildPlanPrompt(many, []);
        Assert.DoesNotContain("Event 5", prompt, StringComparison.Ordinal); // clipped at 3
    }

    [Fact]
    public void AskPrompt_EmbedsQuestionAndContext()
    {
        var assistant = new ScheduleAssistant();
        var prompt = assistant.BuildAskPrompt(
            "when is my next free hour?",
            [Occ("Lunch", "2026-09-21T12:00:00+00:00", "2026-09-21T13:00:00+00:00")]);
        Assert.Contains("when is my next free hour?", prompt, StringComparison.Ordinal);
        Assert.Contains("Lunch", prompt, StringComparison.Ordinal);
        Assert.Contains("ONLY the events below", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void AskPrompt_EmptyQuestion_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => new ScheduleAssistant().BuildAskPrompt("  ", []));
    }

    [Fact]
    public void AskPrompt_ClipsLongQuestions()
    {
        var prompt = new ScheduleAssistant().BuildAskPrompt(new string('x', 999), []);
        Assert.True(prompt.Length < 1200);
    }

    [Fact]
    public void CapturePrompt_CarriesTheSentenceAndTemplate()
    {
        var prompt = ScheduleAssistant.BuildCapturePrompt("lunch with Sara tuesday noon", Now);
        Assert.Contains("lunch with Sara tuesday noon", prompt, StringComparison.Ordinal);
        Assert.Contains("taqvim add", prompt, StringComparison.Ordinal);
        Assert.Contains("UNTRUSTED", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void CapturePrompt_ClipsLongSentences()
    {
        var prompt = ScheduleAssistant.BuildCapturePrompt(new string('y', 900), Now);
        Assert.True(prompt.Length < 1200);
    }

    [Theory]
    [InlineData("taqvim add Lunch --at 2026-09-22 12:00", "taqvim add Lunch --at 2026-09-22 12:00")]
    [InlineData("`JameJam taqvim add Standup --at 2026-09-22 09:00 --dur 15m`", "JameJam taqvim add Standup --at 2026-09-22 09:00 --dur 15m")]
    public void ParseCapture_AcceptsCommandLines(string reply, string expected)
    {
        Assert.Equal(expected, ScheduleAssistant.ParseCapture(reply));
    }

    [Fact]
    public void ParseCapture_PicksTheCommandLine_FromChatter()
    {
        var reply = "Sure! Here is what you asked for:\ntaqvim add Gym --at 2026-09-22 18:00\nHope that helps!";
        Assert.Equal("taqvim add Gym --at 2026-09-22 18:00", ScheduleAssistant.ParseCapture(reply));
    }

    [Fact]
    public void ParseCapture_RejectsOverlongCommands()
    {
        Assert.Null(ScheduleAssistant.ParseCapture("taqvim add " + new string('a', 600)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("no command here at all")]
    [InlineData("taqvim remove everything")]
    public void ParseCapture_RejectsNonCommands(string reply)
    {
        Assert.Null(ScheduleAssistant.ParseCapture(reply));
    }

    [Fact]
    public void Notes_AreClippedIntoThePrompt()
    {
        var occurrence = Occ(
            "With notes", "2026-09-21T09:00:00+00:00", "2026-09-21T10:00:00+00:00",
            notes: new string('n', 20_000));
        var prompt = new ScheduleAssistant().BuildBriefPrompt([occurrence], Now);
        Assert.True(prompt.Length < 10_000);
    }

    [Fact]
    public void Options_AreValidated_AtConstruction()
    {
        Assert.Throws<TaqvimException>(() => new ScheduleAssistant(new TaqvimOptions { MaxEvents = 0 }));
    }
}
