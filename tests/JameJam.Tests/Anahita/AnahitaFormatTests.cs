using JameJam.Anahita;

namespace JameJam.Tests.Anahita;

/// <summary>Presentation formatting: blocks, tables, compass, and the plan view.</summary>
public sealed class AnahitaFormatTests
{
    private static readonly DateOnly Saturday = new(2026, 9, 19);
    private static readonly DateOnly Sunday = new(2026, 9, 20);

    private static WeatherReport Report(params DailyPoint[] days) => AnahitaTestSupport.MakeReport(days);

    [Fact]
    public void FormatNow_ShowsPlaceConditionsWindAndSun()
    {
        var text = AnahitaFormat.FormatNow(Report(AnahitaTestSupport.Day(Saturday)), WeatherUnits.Metric);

        Assert.Contains("Berlin, Germany — 52.52°N, 13.41°E · Europe/Berlin", text, StringComparison.Ordinal);
        Assert.Contains("Partly cloudy", text, StringComparison.Ordinal);
        Assert.Contains("18.4°C (feels 17.9°C)", text, StringComparison.Ordinal);
        Assert.Contains("humidity 62%", text, StringComparison.Ordinal);
        Assert.Contains("Wind 12.4 km/h NW", text, StringComparison.Ordinal); // 315°
        Assert.Contains("Sunrise 06:41 · Sunset 19:22 · UV max 4.2", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatNow_Imperial_AndSouthernCoordinates()
    {
        GeoPlace place = new("Puerto Williams", "Chile", -54.93, -67.61, "America/Punta_Arenas");
        var report = AnahitaTestSupport.MakeReport() with { Place = place };
        var text = AnahitaFormat.FormatNow(report, WeatherUnits.Imperial);

        Assert.Contains("54.93°S, 67.61°W", text, StringComparison.Ordinal);
        Assert.Contains("65.1°F", text, StringComparison.Ordinal);
        Assert.Contains("mph", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Compass_CoversTheRose()
    {
        Assert.Equal("N", AnahitaFormat.Compass(0));
        Assert.Equal("NNE", AnahitaFormat.Compass(22));
        Assert.Equal("E", AnahitaFormat.Compass(90));
        Assert.Equal("SW", AnahitaFormat.Compass(225));
        Assert.Equal("NNW", AnahitaFormat.Compass(337));
        Assert.Equal("N", AnahitaFormat.Compass(359)); // wraps
    }

    [Fact]
    public void FormatAlerts_EmptyWhenCalm()
    {
        Assert.Equal(string.Empty, AnahitaFormat.FormatAlerts([]));
    }

    [Fact]
    public void FormatAlerts_MarksSeverity()
    {
        List<WeatherAlert> alerts =
        [
            new(AlertSeverity.Warning, "Heat", "peaks today"),
            new(AlertSeverity.Watch, "High UV", "sunscreen time"),
        ];
        var text = AnahitaFormat.FormatAlerts(alerts);

        Assert.StartsWith("Alerts", text, StringComparison.Ordinal);
        Assert.Contains("⚠ Heat — peaks today", text, StringComparison.Ordinal);
        Assert.Contains("☑ High UV — sunscreen time", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatHourly_SlicesAndShowsDashForMissingChance()
    {
        var report = AnahitaTestSupport.MakeReport();
        var text = AnahitaFormat.FormatHourly(report, WeatherUnits.Metric, 1);

        Assert.Contains("Next 1 hour(s)", text, StringComparison.Ordinal);
        Assert.Contains("14:00", text, StringComparison.Ordinal);
        Assert.DoesNotContain("15:00", text, StringComparison.Ordinal);

        var both = AnahitaFormat.FormatHourly(report with
        {
            Hourly = [report.Hourly[0], report.Hourly[1] with { PrecipProbabilityPercent = null }],
        }, WeatherUnits.Metric, 5);
        Assert.Contains("15:00", both, StringComparison.Ordinal);
        Assert.Contains("—", both, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatHourly_EmptyData_IsFriendly() =>
        Assert.Equal("No hourly data available.", AnahitaFormat.FormatHourly(AnahitaTestSupport.MakeReport() with { Hourly = [] }, WeatherUnits.Metric, 4));

    [Fact]
    public void FormatDaily_ListsEveryDay()
    {
        var text = AnahitaFormat.FormatDaily(
            Report(AnahitaTestSupport.Day(Saturday), AnahitaTestSupport.Day(Sunday, code: 61)), WeatherUnits.Metric);

        Assert.StartsWith("2-day forecast", text, StringComparison.Ordinal);
        Assert.Contains("Sat 19 Sep", text, StringComparison.Ordinal);
        Assert.Contains("Sun 20 Sep", text, StringComparison.Ordinal);
        Assert.Contains("Light rain", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatDaily_EmptyData_IsFriendly() =>
        Assert.Equal("No forecast data available.", AnahitaFormat.FormatDaily(AnahitaTestSupport.MakeReport() with { Daily = [] }, WeatherUnits.Metric));

    [Fact]
    public void FormatBest_RanksWithScores()
    {
        List<DayAdvice> days =
        [
            new(Sunday, 92, "Clear sky, 20°C–24°C — good conditions to be outside."),
            new(Saturday, 60, "Overcast, 12.5°C–19.2°C — rain is likely, pack an umbrella."),
        ];
        var text = AnahitaFormat.FormatBest(days);

        Assert.StartsWith("Best days outdoors", text, StringComparison.Ordinal);
        Assert.Contains("1. Sun 20 Sep (score 92)", text, StringComparison.Ordinal);
        Assert.Contains("2. Sat 19 Sep (score 60)", text, StringComparison.Ordinal);
        Assert.Equal("No forecast data to rank.", AnahitaFormat.FormatBest([]));
    }

    [Fact]
    public void FormatPlan_GroupsTasksUnderTheirDay_AndShowsGaps()
    {
        List<(DateOnly Due, string Title)> tasks =
        [
            (Saturday, "Water the garden"),
            (Saturday, "Fix the roof"),
            (Sunday, "Climb Damavand"),
        ];
        var text = AnahitaFormat.FormatPlan(
            Report(
                AnahitaTestSupport.Day(Saturday),
                AnahitaTestSupport.Day(Sunday),
                AnahitaTestSupport.Day(new DateOnly(2026, 9, 21))),
            WeatherUnits.Metric,
            tasks);

        Assert.StartsWith("Weather for your plans", text, StringComparison.Ordinal);
        Assert.Contains("· Water the garden", text, StringComparison.Ordinal);
        Assert.Contains("· Fix the roof", text, StringComparison.Ordinal);
        Assert.Contains("· Climb Damavand", text, StringComparison.Ordinal);
        var saturdayBlock = text.Split("Sun 20 Sep")[0];
        Assert.Contains("Fix the roof", saturdayBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("Climb Damavand", saturdayBlock, StringComparison.Ordinal);
        Assert.Contains("(nothing due)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatPlan_EmptyForecast_IsFriendly() =>
        Assert.Equal(
            "No forecast data available.",
            AnahitaFormat.FormatPlan(AnahitaTestSupport.MakeReport() with { Daily = [] }, WeatherUnits.Metric, []));
}
