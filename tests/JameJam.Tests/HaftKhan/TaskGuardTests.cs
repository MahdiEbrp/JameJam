using JameJam.HaftKhan;

namespace JameJam.Tests.HaftKhan;

/// <summary>Tests for Haft Khan input validation and parsing.</summary>
public sealed class TaskGuardTests
{
    [Theory]
    [InlineData("  Ship it  ", "Ship it")]
    [InlineData("a\u0007b", "ab")]
    [InlineData("multi\nline", "multi\nline")]
    public void CleanTitle_Sanitizes(string title, string expected) =>
        Assert.Equal(expected, TaskGuard.CleanTitle(title));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CleanTitle_Empty_Throws(string? title) =>
        Assert.Throws<ArgumentException>(() => TaskGuard.CleanTitle(title));

    [Fact]
    public void CleanTitle_TooLong_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TaskGuard.CleanTitle(new string('t', 201)));

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  ", "")]
    [InlineData("note\u0002", "note")]
    public void CleanNotes_Sanitizes(string? notes, string expected) =>
        Assert.Equal(expected, TaskGuard.CleanNotes(notes));

    [Fact]
    public void CleanNotes_TooLong_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TaskGuard.CleanNotes(new string('n', 4_001)));

    [Theory]
    [InlineData(null, TaskPriority.Normal)]
    [InlineData("", TaskPriority.Normal)]
    [InlineData("low", TaskPriority.Low)]
    [InlineData("HIGH", TaskPriority.High)]
    [InlineData(" Critical ", TaskPriority.Critical)]
    public void ParsePriority_KnownNames(string? name, TaskPriority expected) =>
        Assert.Equal(expected, TaskGuard.ParsePriority(name));

    [Theory]
    [InlineData("urgent")]
    [InlineData("2")]
    public void ParsePriority_Unknown_Throws(string? name) =>
        Assert.Throws<ArgumentException>(() => TaskGuard.ParsePriority(name));

    private static readonly DateOnly Today = new(2026, 9, 19); // a Saturday

    [Fact]
    public void ParseDueDate_Null_StaysNull() =>
        Assert.Null(TaskGuard.ParseDueDate(null, Today));

    [Fact]
    public void ParseDueDate_ValidIso_Parses() =>
        Assert.Equal(new DateOnly(2026, 12, 31), TaskGuard.ParseDueDate("2026-12-31", Today));

    [Theory]
    [InlineData("today", 0)]
    [InlineData("tod", 0)]
    [InlineData("tomorrow", 1)]
    [InlineData("tmr", 1)]
    [InlineData("next week", 7)]
    [InlineData("in 3 days", 3)]
    [InlineData("in 2 weeks", 14)]
    public void ParseDueDate_NaturalLanguage_RelativeDays(string text, int days)
    {
        var parsed = TaskGuard.ParseDueDate(text, Today);

        Assert.Equal(Today.AddDays(days), parsed);
    }

    [Fact]
    public void ParseDueDate_InMonths_UsesCalendarMonths() =>
        Assert.Equal(Today.AddMonths(2), TaskGuard.ParseDueDate("in 2 months", Today));

    [Fact]
    public void ParseDueDate_BareWeekday_TodayWhenMatching_ElseNextOccurrence()
    {
        Assert.Equal(Today, TaskGuard.ParseDueDate("saturday", Today)); // today is Saturday
        Assert.Equal(Today, TaskGuard.ParseDueDate("sat", Today));
        Assert.Equal(new DateOnly(2026, 9, 21), TaskGuard.ParseDueDate("monday", Today)); // next Monday
    }

    [Fact]
    public void ParseDueDate_NextWeekday_AlwaysStrictlyFuture()
    {
        Assert.Equal(new DateOnly(2026, 9, 26), TaskGuard.ParseDueDate("next saturday", Today)); // +7, not today
        Assert.Equal(new DateOnly(2026, 9, 21), TaskGuard.ParseDueDate("next monday", Today));
    }

    [Theory]
    [InlineData("31/12/2026")]
    [InlineData("2026-13-01")]
    [InlineData("yesterday")]
    [InlineData("in 0 days")]
    [InlineData("in x days")]
    [InlineData("next someday")]
    public void ParseDueDate_Invalid_Throws(string text) =>
        Assert.Throws<ArgumentException>(() => TaskGuard.ParseDueDate(text, Today));

    [Theory]
    [InlineData(null, TaskEffort.None)]
    [InlineData("none", TaskEffort.None)]
    [InlineData("s", TaskEffort.Small)]
    [InlineData("LARGE", TaskEffort.Large)]
    [InlineData("xl", TaskEffort.XLarge)]
    public void ParseEffort_KnownNames(string? name, TaskEffort expected) =>
        Assert.Equal(expected, TaskGuard.ParseEffort(name));

    [Fact]
    public void ParseEffort_Unknown_Throws() =>
        Assert.Throws<ArgumentException>(() => TaskGuard.ParseEffort("huge"));

    [Theory]
    [InlineData(null, RecurrenceKind.None)]
    [InlineData("daily", RecurrenceKind.Daily)]
    [InlineData("WEEKLY", RecurrenceKind.Weekly)]
    [InlineData("month", RecurrenceKind.Monthly)]
    public void ParseRecurrenceKind_KnownNames(string? name, RecurrenceKind expected) =>
        Assert.Equal(expected, TaskGuard.ParseRecurrenceKind(name));

    [Fact]
    public void ParseRecurrenceKind_Unknown_Throws() =>
        Assert.Throws<ArgumentException>(() => TaskGuard.ParseRecurrenceKind("hourly"));

    [Fact]
    public void ParseRecurrenceInterval_DefaultsToOne_Bounded()
    {
        Assert.Equal(1, TaskGuard.ParseRecurrenceInterval(null));
        Assert.Equal(3, TaskGuard.ParseRecurrenceInterval("3"));
        Assert.Throws<ArgumentException>(() => TaskGuard.ParseRecurrenceInterval("0"));
        Assert.Throws<ArgumentException>(() => TaskGuard.ParseRecurrenceInterval("11", maxInterval: 10));
    }

    [Fact]
    public void CleanTags_Trims_Dedupes_Bounded()
    {
        Assert.Equal(["alpha", "beta"], TaskGuard.CleanTags(" alpha , beta ,ALPHA"));
        Assert.Equal([], TaskGuard.CleanTags(null));
        Assert.Equal([], TaskGuard.CleanTags("  "));
        Assert.Throws<ArgumentException>(() => TaskGuard.CleanTags("a,b,c", maxTags: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => TaskGuard.CleanTags("way-too-long-tag", maxTagLength: 4));
    }

    [Fact]
    public void CleanProject_SanitizesToSingleLine()
    {
        Assert.Equal("home stuff", TaskGuard.CleanProject(" home\nstuff "));
        Assert.Equal(string.Empty, TaskGuard.CleanProject(null));
    }

    [Fact]
    public void ParseIdList_ParsesDeduplicated()
    {
        Assert.Equal([1, 2, 3], TaskGuard.ParseIdList("1,2,3"));
        Assert.Equal([1, 2], TaskGuard.ParseIdList(" 1, 2,1 "));
        Assert.Throws<ArgumentException>(() => TaskGuard.ParseIdList("1,x"));
    }

    [Theory]
    [InlineData("3", 3)]
    [InlineData(" 42 ", 42)]
    public void ParseId_Valid(string text, long expected) =>
        Assert.Equal(expected, TaskGuard.ParseId(text));

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData(null)]
    public void ParseId_Invalid_Throws(string? text) =>
        Assert.Throws<ArgumentException>(() => TaskGuard.ParseId(text));

    [Theory]
    [InlineData(null, TaskView.Open)]
    [InlineData("ALL", TaskView.All)]
    [InlineData("today", TaskView.Today)]
    [InlineData("overdue", TaskView.Overdue)]
    [InlineData("done", TaskView.Done)]
    public void ParseView_KnownNames(string? name, TaskView expected) =>
        Assert.Equal(expected, TaskGuard.ParseView(name));

    [Fact]
    public void ParseView_Unknown_Throws() =>
        Assert.Throws<ArgumentException>(() => TaskGuard.ParseView("someday"));
}
