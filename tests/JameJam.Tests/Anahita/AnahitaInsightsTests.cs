using JameJam.Anahita;

namespace JameJam.Tests.Anahita;

/// <summary>Alert thresholds, per-day advice, and outdoor-day ranking.</summary>
public sealed class AnahitaInsightsTests
{
    private static readonly DateOnly Saturday = new(2026, 9, 19);
    private static readonly DateOnly Sunday = new(2026, 9, 20);
    private static readonly DateOnly Monday = new(2026, 9, 21);

    private static IReadOnlyList<WeatherAlert> Alerts(params DailyPoint[] days) =>
        AnahitaInsights.FindAlerts(AnahitaTestSupport.MakeReport(days), WeatherUnits.Metric);

    [Fact]
    public void MildDays_ProduceNoAlerts()
    {
        var alerts = Alerts(AnahitaTestSupport.Day(Saturday), AnahitaTestSupport.Day(Sunday));
        Assert.Empty(alerts);
    }

    [Fact]
    public void Heat_FiresAtAndAboveThreshold_Only()
    {
        Assert.Contains(Alerts(AnahitaTestSupport.Day(Saturday, max: 35.0)), a => a.Title == "Heat");
        Assert.DoesNotContain(Alerts(AnahitaTestSupport.Day(Saturday, max: 34.9)), a => a.Title == "Heat");
    }

    [Fact]
    public void DeepFreeze_FiresBelowColdThreshold()
    {
        var alerts = Alerts(AnahitaTestSupport.Day(Saturday, min: -15));
        Assert.Contains(alerts, a => a.Title == "Deep freeze" && a.Severity == AlertSeverity.Warning);
        Assert.DoesNotContain(alerts, a => a.Title == "Frost");
    }

    [Fact]
    public void Frost_IsAWatch_BetweenFrostAndCold()
    {
        var alerts = Alerts(AnahitaTestSupport.Day(Saturday, min: -5));
        Assert.DoesNotContain(alerts, a => a.Title == "Deep freeze");
        Assert.Contains(alerts, a => a.Title == "Frost" && a.Severity == AlertSeverity.Watch);
    }

    [Fact]
    public void StrongWind_HeavyRain_AndThunder_AreWarnings()
    {
        var alerts = Alerts(AnahitaTestSupport.Day(Saturday, code: 95, wind: 61, sum: 26));
        Assert.Contains(alerts, a => a.Title == "Strong wind");
        Assert.Contains(alerts, a => a.Title == "Heavy rain");
        Assert.Contains(alerts, a => a.Title == "Thunderstorms");
        Assert.All(alerts, a => Assert.Equal(AlertSeverity.Warning, a.Severity));
    }

    [Fact]
    public void HighUv_IsAWatch_AndNullUvIsSilent()
    {
        Assert.Contains(Alerts(AnahitaTestSupport.Day(Saturday, uv: 8)), a => a.Title == "High UV");
        Assert.DoesNotContain(Alerts(AnahitaTestSupport.Day(Saturday, uv: null)), a => a.Title == "High UV");
    }

    [Fact]
    public void Alerts_ObeyTheTwoDayHorizon()
    {
        var monday = AnahitaTestSupport.Day(Monday, max: 40);
        var alerts = Alerts(AnahitaTestSupport.Day(Saturday), AnahitaTestSupport.Day(Sunday), monday);
        Assert.Empty(alerts);
    }

    [Fact]
    public void Warnings_SortBeforeWatches()
    {
        var hotDay = AnahitaTestSupport.Day(Saturday, max: 36, uv: 9); // heat (warning) + UV (watch)
        var alerts = Alerts(hotDay);
        Assert.Equal(AlertSeverity.Warning, alerts[0].Severity);
        Assert.Equal(AlertSeverity.Watch, alerts[^1].Severity);
    }

    [Fact]
    public void AlertMessages_ReactToUnits()
    {
        var alerts = AnahitaInsights.FindAlerts(
            AnahitaTestSupport.MakeReport(AnahitaTestSupport.Day(Saturday, max: 35.2)), WeatherUnits.Imperial);
        var heat = Assert.Single(alerts);
        Assert.Contains("95.4°F", heat.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ScoreDay_PerfectConditions_ScoreFull()
    {
        var day = AnahitaTestSupport.Day(Saturday, min: 20, max: 24, chance: 0, sum: 0, wind: 10, uv: 3);
        Assert.Equal(AnahitaDefaults.PerfectScore, AnahitaInsights.ScoreDay(day));
    }

    [Theory]
    [InlineData(0, 100)]   // 0% chance → no rain penalty
    [InlineData(25, 80)]   // 25% → penalty 20
    [InlineData(50, 60)]   // at RainAdvicePercent → full 40 penalty
    [InlineData(100, 60)]  // clamped at the max penalty
    public void ScoreDay_RainChancePenalty_IsProportional(double chance, int expectedScore)
    {
        // mean 22 (no comfort penalty), calm, no thunder: score = 100 - rainPenalty.
        var day = AnahitaTestSupport.Day(Saturday, min: 20, max: 24, chance: chance, sum: 0, wind: 10);
        Assert.Equal(expectedScore, AnahitaInsights.ScoreDay(day));
    }

    [Fact]
    public void ScoreDay_MissingRainChance_UsesTheSum() =>
        Assert.Equal(80, AnahitaInsights.ScoreDay(AnahitaTestSupport.Day(Saturday, min: 20, max: 24, chance: null, sum: 5, wind: 10)));

    [Fact]
    public void ScoreDay_PenalizesHeatColdWindAndThunder()
    {
        // mean 35 → comfort penalty 26; wind 70 → 25; thunder → 60; mean -9 → comfort 62.
        var scorcher = AnahitaTestSupport.Day(Saturday, min: 30, max: 40, chance: 0, wind: 10);
        var freezing = AnahitaTestSupport.Day(Saturday, min: -12, max: -6, chance: 0, wind: 10);
        var gale = AnahitaTestSupport.Day(Saturday, min: 20, max: 24, chance: 0, wind: 70);
        var storm = AnahitaTestSupport.Day(Saturday, min: 20, max: 24, chance: 0, wind: 10, code: 95);
        var miserable = AnahitaTestSupport.Day(Saturday, min: -12, max: -6, chance: 100, wind: 70, code: 95);

        Assert.Equal(74, AnahitaInsights.ScoreDay(scorcher));
        Assert.Equal(38, AnahitaInsights.ScoreDay(freezing));
        Assert.Equal(75, AnahitaInsights.ScoreDay(gale));
        Assert.Equal(40, AnahitaInsights.ScoreDay(storm));
        Assert.Equal(0, AnahitaInsights.ScoreDay(miserable)); // clamped at zero
    }

    [Fact]
    public void RankDays_OrdersByScore_ThenDate_AndRespectsTopCount()
    {
        var report = AnahitaTestSupport.MakeReport(
            AnahitaTestSupport.Day(Saturday, min: 20, max: 24, chance: 90),   // score 60
            AnahitaTestSupport.Day(Sunday, min: 20, max: 24, chance: 0),      // score 100
            AnahitaTestSupport.Day(Monday, min: 20, max: 24, chance: 10));    // score 92

        var ranked = AnahitaInsights.RankDays(report, WeatherUnits.Metric, 2);

        Assert.Equal(2, ranked.Count);
        Assert.Equal(Sunday, ranked[0].Date);
        Assert.Equal(Monday, ranked[1].Date);
        Assert.Equal(100, ranked[0].Score);
    }

    [Fact]
    public void Summarize_DescribesTheDay_AndReactsToUnits()
    {
        var day = AnahitaTestSupport.Day(Saturday, code: 61, min: 12.5, max: 19.2, chance: 80);
        var metric = AnahitaInsights.Summarize(day, WeatherUnits.Metric);
        Assert.Contains("Light rain", metric, StringComparison.Ordinal);
        Assert.Contains("12.5°C–19.2°C", metric, StringComparison.Ordinal);
        Assert.Contains("umbrella", metric, StringComparison.Ordinal);
        Assert.Contains("°F", AnahitaInsights.Summarize(day, WeatherUnits.Imperial), StringComparison.Ordinal);
    }

    [Fact]
    public void AdviceFor_ThunderDay() =>
        Assert.Contains("thunderstorms likely", AnahitaInsights.AdviceFor(AnahitaTestSupport.Day(Saturday, code: 95)), StringComparison.Ordinal);

    [Fact]
    public void AdviceFor_RainByChance() =>
        Assert.Contains("umbrella", AnahitaInsights.AdviceFor(AnahitaTestSupport.Day(Saturday, code: 61, chance: 80)), StringComparison.Ordinal);

    [Fact]
    public void AdviceFor_RainBySum_EvenWithoutChance() =>
        Assert.Contains("umbrella", AnahitaInsights.AdviceFor(AnahitaTestSupport.Day(Saturday, chance: null, sum: 5)), StringComparison.Ordinal);

    [Fact]
    public void AdviceFor_HotDay() =>
        Assert.Contains("very hot", AnahitaInsights.AdviceFor(AnahitaTestSupport.Day(Saturday, max: 36)), StringComparison.Ordinal);

    [Fact]
    public void AdviceFor_FreezingDay() =>
        Assert.Contains("freezing", AnahitaInsights.AdviceFor(AnahitaTestSupport.Day(Saturday, min: -4, max: 4)), StringComparison.Ordinal);

    [Fact]
    public void AdviceFor_WindyDay() =>
        Assert.Contains("windy", AnahitaInsights.AdviceFor(AnahitaTestSupport.Day(Saturday, wind: 75)), StringComparison.Ordinal);

    [Fact]
    public void AdviceFor_PleasantDay() =>
        Assert.Contains("good conditions to be outside", AnahitaInsights.AdviceFor(AnahitaTestSupport.Day(Saturday, min: 20, max: 24, chance: 0, sum: 0, wind: 10)), StringComparison.Ordinal);
}
