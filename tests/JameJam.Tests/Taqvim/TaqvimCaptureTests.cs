using JameJam.Taqvim;

namespace JameJam.Tests.Taqvim;

/// <summary>Natural-language capture: dates, times, durations, tags, locations, and safe fallbacks.</summary>
public sealed class TaqvimCaptureTests
{
    // Sunday 2026-09-20, 12:00 UTC.
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static CaptureResult? Parse(string sentence) => TaqvimCapture.TryParse(sentence, Now, Utc);

    [Fact]
    public void NullOrWhitespace_ReturnsNull()
    {
        Assert.Null(TaqvimCapture.TryParse("", Now, Utc));
        Assert.Null(TaqvimCapture.TryParse("   ", Now, Utc));
        Assert.Null(TaqvimCapture.TryParse(null!, Now, Utc));
    }

    [Fact]
    public void NoSignal_ReturnsNull_SoNothingIsInvented()
    {
        Assert.Null(Parse("write the report"));
        Assert.Null(Parse("buy oat milk sometime"));
    }

    [Fact]
    public void LunchWithSara_NextTuesday_At1pm()
    {
        var result = Parse("lunch with Sara next Tuesday at 1pm");
        Assert.NotNull(result);
        Assert.Equal("lunch with Sara", result.Title);
        Assert.Equal(new DateTimeOffset(2026, 9, 29, 13, 0, 0, TimeSpan.Zero), result.Start); // next week's Tuesday
        Assert.Equal(TimeSpan.FromMinutes(TaqvimDefaults.DefaultEventMinutes), result.End - result.Start);
        Assert.False(result.IsAllDay);
    }

    [Fact]
    public void BareWeekday_MeansTheSoonestFuture()
    {
        var result = Parse("dentist friday at 3");
        Assert.NotNull(result);
        Assert.Equal(new DateTimeOffset(2026, 9, 25, 15, 0, 0, TimeSpan.Zero), result.Start); // bare small hour → afternoon
    }

    [Fact]
    public void Today_And_Tomorrow()
    {
        var today = Parse("standup today at 9:30");
        Assert.NotNull(today);
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 9, 30, 0, TimeSpan.Zero), today.Start);

        var tomorrow = Parse("standup tomorrow at 9:30");
        Assert.NotNull(tomorrow);
        Assert.Equal(new DateTimeOffset(2026, 9, 21, 9, 30, 0, TimeSpan.Zero), tomorrow.Start);
    }

    [Fact]
    public void IsoDate_WithClock()
    {
        var result = Parse("review 2026-12-01 14:00");
        Assert.NotNull(result);
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 14, 0, 0, TimeSpan.Zero), result.Start);
        Assert.Equal("review", result.Title);
    }

    [Fact]
    public void MonthDay_RollsToNextYear_WhenPast()
    {
        var result = Parse("trip to Shiraz on March 21");
        Assert.NotNull(result);
        Assert.Equal(new DateTimeOffset(2027, 3, 21, 0, 0, 0, TimeSpan.Zero), result.Start);
        Assert.True(result.IsAllDay);
        Assert.Equal("trip to Shiraz", result.Title);
    }

    [Fact]
    public void MonthDay_ThisYear_WhenFuture()
    {
        var result = Parse("checkup on Dec 5 at 10:00");
        Assert.NotNull(result);
        Assert.Equal(new DateTimeOffset(2026, 12, 5, 10, 0, 0, TimeSpan.Zero), result.Start);
    }

    [Fact]
    public void AmPm_BothWays()
    {
        var am = Parse("run at 7am");
        Assert.NotNull(am);
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 7, 0, 0, TimeSpan.Zero), am.Start);

        var pm = Parse("gym at 6pm");
        Assert.NotNull(pm);
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 18, 0, 0, TimeSpan.Zero), pm.Start);
    }

    [Fact]
    public void Noon_And_Midnight()
    {
        var noon = Parse("lunch meeting at noon");
        Assert.NotNull(noon);
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero), noon.Start);

        var midnight = Parse("deploy at midnight");
        Assert.NotNull(midnight);
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero), midnight.Start);
    }

    [Fact]
    public void Duration_Words()
    {
        var result = Parse("yoga tomorrow at 6pm for 75m");
        Assert.NotNull(result);
        Assert.Equal(TimeSpan.FromMinutes(75), result.End - result.Start);
    }

    [Fact]
    public void Duration_Hours()
    {
        var result = Parse("workshop Friday for 2 hours at 2pm");
        Assert.NotNull(result);
        Assert.Equal(TimeSpan.FromHours(2), result.End - result.Start);
    }

    [Fact]
    public void Tags_AreHarvested()
    {
        var result = Parse("lunch with Sara tomorrow at noon #friends #food");
        Assert.NotNull(result);
        Assert.Equal("friends,food", result.Tags);
        Assert.DoesNotContain("#", result.Title);
    }

    [Fact]
    public void Location_AtCapitalizedWords()
    {
        var result = Parse("lunch at Cafe Riviera tomorrow at 1pm");
        Assert.NotNull(result);
        Assert.Equal("Cafe Riviera", result.Location);
        Assert.Equal("lunch", result.Title);
    }

    [Fact]
    public void Location_AtHandle()
    {
        var result = Parse("standup @studio tomorrow at 9am");
        Assert.NotNull(result);
        Assert.Equal("studio", result.Location);
    }

    [Fact]
    public void LowercasePlace_StaysInTheTitle()
    {
        var result = Parse("picnic in the park tomorrow at noon");
        Assert.NotNull(result);
        Assert.Null(result.Location); // "the park" is not a capitalized place
        Assert.Contains("park", result.Title);
    }

    [Fact]
    public void AllDay_WhenOnlyADateIsGiven()
    {
        var result = Parse("Nowruz holiday 2027-03-21");
        Assert.NotNull(result);
        Assert.True(result.IsAllDay);
        Assert.Equal(result.Start.AddDays(1), result.End);
    }

    [Fact]
    public void TitleOnly_Time_GetsADefaultTitle()
    {
        var result = Parse("at 3pm");
        Assert.NotNull(result);
        Assert.Equal("Event", result.Title);
    }

    [Fact]
    public void WeekdayWords_DoNotMatchInsideOtherWords()
    {
        var result = Parse("monitor the build for 10 minutes");
        // "monitor" must not read as Monday — but "for 10 minutes" alone is no date/time either.
        Assert.Null(result);
    }

    [Fact]
    public void InvalidClock_ReturnsNull()
    {
        Assert.Null(Parse("meet at 25:99"));
        Assert.Null(Parse("meet at 99pm"));
    }

    [Fact]
    public void BareNine_ReadsMorning_BareThreeReadsAfternoon()
    {
        var nine = Parse("call at 9");
        Assert.NotNull(nine);
        Assert.Equal(9, nine.Start.Hour);

        var three = Parse("call at 3");
        Assert.NotNull(three);
        Assert.Equal(15, three.Start.Hour);

        var eleven = Parse("call at 11");
        Assert.NotNull(eleven);
        Assert.Equal(11, eleven.Start.Hour);
    }

    [Fact]
    public void MidnightHour_TwelveAm()
    {
        var result = Parse("silent retreat at 12am");
        Assert.NotNull(result);
        Assert.Equal(0, result.Start.Hour);
    }

    [Fact]
    public void TwelvePm_IsNoon()
    {
        var result = Parse("board lunch at 12pm");
        Assert.NotNull(result);
        Assert.Equal(12, result.Start.Hour);
    }

    [Fact]
    public void Saturday_Sunday_Words()
    {
        var result = Parse("football on sunday at 5pm");
        Assert.NotNull(result);
        Assert.Equal(DayOfWeek.Sunday, result.Start.DayOfWeek);
        Assert.Equal(17, result.Start.Hour);
        Assert.Equal("football", result.Title);
    }

    [Fact]
    public void EmptyResultTitle_FallsBackToTheDate()
    {
        var result = Parse("2027-01-05");
        Assert.NotNull(result);
        Assert.Equal("2027-01-05", result.Title);
        Assert.True(result.IsAllDay);
    }
}
