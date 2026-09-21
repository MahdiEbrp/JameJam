using JameJam.Taqvim;

namespace JameJam.Tests.Taqvim;

/// <summary>The recurrence engine: expansion windows, clamps, count/until rails, RRULE round-trips.</summary>
public sealed class RecurrenceTests
{
    private static readonly DateTimeOffset First = new(2026, 9, 21, 9, 0, 0, TimeSpan.Zero); // Monday
    private static readonly DateTimeOffset Horizon = new(2027, 9, 21, 9, 0, 0, TimeSpan.Zero);

    private static List<DateTimeOffset> Daily(int interval, DateOnly? until = null, int? count = null)
    {
        var rule = new Recurrence(RecurrenceKind.Daily, interval, null, count, until);
        return [.. Recurrences.Starts(rule, First, Horizon)];
    }

    [Fact]
    public void Once_YieldsOnlyTheFirst()
    {
        var starts = Recurrences.Starts(new Recurrence(RecurrenceKind.Once), First, Horizon).ToList();
        var single = Assert.Single(starts);
        Assert.Equal(First, single);
    }

    [Fact]
    public void Once_OutsideWindow_YieldsNothing()
    {
        var starts = Recurrences.Starts(new Recurrence(RecurrenceKind.Once), Horizon.AddHours(1), Horizon).ToList();
        Assert.Empty(starts);
    }

    [Fact]
    public void Daily_EveryDay()
    {
        var starts = Daily(1, count: 5);
        Assert.Equal(5, starts.Count);
        Assert.Equal(First.AddDays(4), starts[^1]);
    }

    [Fact]
    public void Daily_EveryThirdDay()
    {
        var starts = Daily(3, count: 4);
        Assert.Equal(First.AddDays(9), starts[^1]);
        Assert.All(starts, start => Assert.Equal(0, (start - First).Days % 3));
    }

    [Fact]
    public void Daily_Until_InclusiveLastDay()
    {
        var until = DateOnly.FromDateTime(First.AddDays(3).UtcDateTime);
        var starts = Daily(1, until);
        Assert.Equal(4, starts.Count);
        Assert.Equal(First.AddDays(3), starts[^1]);
    }

    [Fact]
    public void Count_And_Until_WhoeverEndsFirstWins()
    {
        var until = DateOnly.FromDateTime(First.AddDays(30).UtcDateTime);
        var starts = Daily(1, until, count: 2);
        Assert.Equal(2, starts.Count);
    }

    [Fact]
    public void Weekly_MonWedFri()
    {
        var rule = new Recurrence(RecurrenceKind.Weekly, 1, [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday], 6, null);
        var starts = Recurrences.Starts(rule, First, Horizon).ToList();
        Assert.Equal(6, starts.Count);
        Assert.All(starts, start => Assert.Contains(start.DayOfWeek, new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday }));
        Assert.Equal(new DateTimeOffset(2026, 9, 23, 9, 0, 0, TimeSpan.Zero), starts[1]);
    }

    [Fact]
    public void Weekly_EmptyDays_UsesTheEventsOwnWeekday()
    {
        var rule = new Recurrence(RecurrenceKind.Weekly, 1, [], 3, null);
        var starts = Recurrences.Starts(rule, First, Horizon).ToList();
        Assert.Equal(3, starts.Count);
        Assert.All(starts, start => Assert.Equal(DayOfWeek.Monday, start.DayOfWeek));
    }

    [Fact]
    public void Weekly_IntervalTwo_SkipsAlternateWeeks()
    {
        var rule = new Recurrence(RecurrenceKind.Weekly, 2, [DayOfWeek.Monday], 3, null);
        var starts = Recurrences.Starts(rule, First, Horizon).ToList();
        Assert.Equal(First, starts[0]);
        Assert.Equal(First.AddDays(14), starts[1]);
        Assert.Equal(First.AddDays(28), starts[2]);
    }

    [Fact]
    public void Monthly_ClampsToMonthLength()
    {
        var jan31 = new DateTimeOffset(2026, 1, 31, 10, 0, 0, TimeSpan.Zero);
        var rule = new Recurrence(RecurrenceKind.Monthly, 1, null, 3, null);
        var starts = Recurrences.Starts(rule, jan31, new DateTimeOffset(2027, 6, 1, 0, 0, 0, TimeSpan.Zero)).ToList();
        Assert.Equal(3, starts.Count);
        Assert.Equal(new DateTimeOffset(2026, 2, 28, 10, 0, 0, TimeSpan.Zero), starts[1]); // 2026 is not a leap year
        Assert.Equal(new DateTimeOffset(2026, 3, 31, 10, 0, 0, TimeSpan.Zero), starts[2]);
    }

    [Fact]
    public void Yearly_Feb29_ClampsTo28InCommonYears()
    {
        var leap = new DateTimeOffset(2024, 2, 29, 8, 0, 0, TimeSpan.Zero);
        var rule = new Recurrence(RecurrenceKind.Yearly, 1, null, 3, null);
        var starts = Recurrences.Starts(rule, leap, new DateTimeOffset(2028, 6, 1, 0, 0, 0, TimeSpan.Zero)).ToList();
        Assert.Equal(3, starts.Count);
        Assert.Equal(leap, starts[0]); // 2024 itself is a leap year — the real Feb 29
        Assert.Equal(new DateTimeOffset(2025, 2, 28, 8, 0, 0, TimeSpan.Zero), starts[1]); // clamped
        Assert.Equal(new DateTimeOffset(2026, 2, 28, 8, 0, 0, TimeSpan.Zero), starts[2]); // clamped
    }

    [Fact]
    public void Expansion_NeverExceedsTheWindow()
    {
        var rule = new Recurrence(RecurrenceKind.Daily, 1, null, null, null);
        var starts = Recurrences.Starts(rule, First, First.AddDays(10)).ToList();
        Assert.Equal(11, starts.Count);
    }

    [Fact]
    public void Occurrences_RespectDurationAndWindowStart()
    {
        var ev = new TaqvimEvent(
            1, "Work", "Standup", string.Empty, string.Empty, string.Empty,
            First, First.AddMinutes(15), false,
            new Recurrence(RecurrenceKind.Daily, 1, null, 3, null), [],
            First, First, Guid.NewGuid());
        var window = Recurrences.Occurrences(ev, First.AddDays(1), First.AddDays(2)).ToList();
        Assert.Equal(2, window.Count); // the 22nd and the 23rd (window start overlaps the 22nd)
        Assert.Equal(First.AddDays(1), window[0].Start);
        Assert.Equal(TimeSpan.FromMinutes(15), window[0].Duration);
    }

    [Theory]
    [InlineData("FREQ=DAILY;COUNT=3", RecurrenceKind.Daily, 1, 3)]
    [InlineData("FREQ=DAILY;INTERVAL=2", RecurrenceKind.Daily, 2, null)]
    [InlineData("FREQ=WEEKLY;BYDAY=MO,WE", RecurrenceKind.Weekly, 1, null)]
    [InlineData("FREQ=MONTHLY", RecurrenceKind.Monthly, 1, null)]
    [InlineData("FREQ=YEARLY;INTERVAL=2", RecurrenceKind.Yearly, 2, null)]
    public void FromRrule_ParsesTheSupportedSubset(
        string rrule, RecurrenceKind kind, int interval, int? count)
    {
        var rule = Recurrences.FromRrule(rrule);
        Assert.NotNull(rule);
        Assert.Equal(kind, rule.Kind);
        Assert.Equal(interval, rule.Interval);
        Assert.Equal(count, rule.Count);
    }

    [Fact]
    public void FromRrule_UntilAndByDay()
    {
        var rule = Recurrences.FromRrule("FREQ=WEEKLY;UNTIL=20261231;BYDAY=TU,TH");
        Assert.NotNull(rule);
        Assert.Equal(new DateOnly(2026, 12, 31), rule.Until);
        Assert.Equal(2, rule.Weekdays.Count);
        Assert.Contains(DayOfWeek.Tuesday, rule.Weekdays);
        Assert.Contains(DayOfWeek.Thursday, rule.Weekdays);
    }

    [Theory]
    [InlineData("FREQ=SECONDLY")]
    [InlineData("GARBAGE")]
    [InlineData("")]
    [InlineData("   ")]
    public void FromRrule_Unknown_ReturnsNull(string rrule)
    {
        Assert.Null(Recurrences.FromRrule(rrule));
    }

    [Fact]
    public void FromRrule_InsaneValues_FallBackToRails()
    {
        var rule = Recurrences.FromRrule("FREQ=DAILY;INTERVAL=99999;COUNT=99999");
        Assert.NotNull(rule);
        Assert.Equal(1, rule.Interval); // out-of-rail values keep the default
        Assert.Null(rule.Count);
    }

    [Fact]
    public void FromRrule_Until_Malformed_ReturnsNullUntil()
    {
        var rule = Recurrences.FromRrule("FREQ=DAILY;UNTIL=not-a-date");
        Assert.NotNull(rule);
        Assert.Null(rule.Until);
    }

    [Fact]
    public void Rrule_RoundTrips()
    {
        var rule = new Recurrence(RecurrenceKind.Weekly, 2, [DayOfWeek.Monday, DayOfWeek.Friday], 10, null);
        var text = Recurrences.ToRrule(rule);
        Assert.Equal("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,FR;COUNT=10", text);
        Assert.NotNull(text);
        var parsed = Recurrences.FromRrule(text);
        Assert.NotNull(parsed);
        Assert.Equal(RecurrenceKind.Weekly, parsed.Kind);
        Assert.Equal(2, parsed.Interval);
        Assert.Equal(10, parsed.Count);
        Assert.Equal(2, parsed.Weekdays.Count);
        Assert.Contains(DayOfWeek.Monday, parsed.Weekdays);
        Assert.Contains(DayOfWeek.Friday, parsed.Weekdays);
    }

    [Fact]
    public void ToRrule_NullAndOnce_ReturnNull()
    {
        Assert.Null(Recurrences.ToRrule(null));
        Assert.Null(Recurrences.ToRrule(new Recurrence(RecurrenceKind.Once)));
    }

    [Fact]
    public void ToRrule_IncludesUntil_WhenPresent()
    {
        var text = Recurrences.ToRrule(new Recurrence(RecurrenceKind.Daily, 1, null, null, new DateOnly(2026, 12, 31)));
        Assert.Equal("FREQ=DAILY;UNTIL=20261231", text);
    }

    [Fact]
    public void Describe_ReadsNaturally()
    {
        Assert.Equal("once", new Recurrence(RecurrenceKind.Once).Describe());
        Assert.Equal("every day", new Recurrence(RecurrenceKind.Daily).Describe());
        Assert.Equal("every 3 days", new Recurrence(RecurrenceKind.Daily, 3).Describe());
        Assert.Equal("every 2 weeks on Mo, We", new Recurrence(RecurrenceKind.Weekly, 2, [DayOfWeek.Monday, DayOfWeek.Wednesday]).Describe());
        Assert.Equal("every week", new Recurrence(RecurrenceKind.Weekly).Describe());
        Assert.Equal("every month", new Recurrence(RecurrenceKind.Monthly).Describe());
        Assert.Equal("every 6 months", new Recurrence(RecurrenceKind.Monthly, 6).Describe());
        Assert.Equal("every year", new Recurrence(RecurrenceKind.Yearly).Describe());
        Assert.Equal("every 2 years", new Recurrence(RecurrenceKind.Yearly, 2).Describe());
    }

    [Fact]
    public void ShortNames_AreTwoLetters()
    {
        Assert.Equal("Mo", Recurrence.ShortName(DayOfWeek.Monday));
        Assert.Equal("Su", Recurrence.ShortName(DayOfWeek.Sunday));
        Assert.Equal("Sa", Recurrence.ShortName(DayOfWeek.Saturday));
    }

    [Fact]
    public void Weekly_IterationStopsAtTheDefensiveHorizon()
    {
        // A rule whose window is huge still terminates via the defensive horizon.
        var rule = new Recurrence(RecurrenceKind.Weekly, 5, [], null, null);
        var farFuture = new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var starts = Recurrences.Starts(rule, First, farFuture).ToList();
        Assert.True(starts.Count < 60); // bounded, not a year of Mondays
        Assert.All(starts, start => Assert.Equal(DayOfWeek.Monday, start.DayOfWeek));
    }
}
