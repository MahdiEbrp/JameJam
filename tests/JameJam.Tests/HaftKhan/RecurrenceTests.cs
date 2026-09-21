using JameJam.HaftKhan;

namespace JameJam.Tests.HaftKhan;

/// <summary>Tests for the recurring-task date math (pure, culture-invariant).</summary>
public sealed class RecurrenceTests
{
    private static readonly DateOnly Day = new(2026, 9, 19);

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void NextDue_Daily_AddsIntervalDays(int interval) =>
        Assert.Equal(Day.AddDays(interval), Recurrence.NextDue(RecurrenceKind.Daily, interval, Day, Day));

    [Fact]
    public void NextDue_Weekly_MultipliesBySeven() =>
        Assert.Equal(Day.AddDays(14), Recurrence.NextDue(RecurrenceKind.Weekly, 2, Day, Day));

    [Fact]
    public void NextDue_Monthly_ClampsToMonthLength()
    {
        var lastOfJanuary = new DateOnly(2026, 1, 31);

        Assert.Equal(new DateOnly(2026, 2, 28), Recurrence.NextDue(RecurrenceKind.Monthly, 1, lastOfJanuary, lastOfJanuary));
    }

    [Fact]
    public void NextDue_WithoutDueDate_UsesCompletionDate() =>
        Assert.Equal(Day.AddDays(7), Recurrence.NextDue(RecurrenceKind.Weekly, 1, null, Day));

    [Fact]
    public void NextDue_NonRecurring_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Recurrence.NextDue(RecurrenceKind.None, 1, Day, Day));

    [Fact]
    public void NextDue_ZeroInterval_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Recurrence.NextDue(RecurrenceKind.Daily, 0, Day, Day));
}
