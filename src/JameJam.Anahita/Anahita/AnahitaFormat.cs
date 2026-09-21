using System.Globalization;

namespace JameJam.Anahita;

/// <summary>
/// Pure presentation helpers that turn weather data into terminal-friendly text.
/// No I/O, no clocks — trivially testable. All formatting is culture-invariant.
/// </summary>
public static class AnahitaFormat
{
    private const int CompassPoints = 16;
    private const double MillimetresPerInch = 25.4;
    private const string DayFormat = "ddd d MMM";
    private const string ClockFormat = "HH:mm";

    private static readonly string[] CompassRose =
    [
        "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE",
        "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW",
    ];

    /// <summary>Converts a wind direction in degrees to a 16-point compass label.</summary>
    public static string Compass(int degrees) =>
        CompassRose[(int)Math.Round(degrees % 360 / (360.0 / CompassPoints)) % CompassPoints];

    /// <summary>Formats the "right now" block: place header, conditions, and sun times.</summary>
    public static string FormatNow(WeatherReport report, WeatherUnits units)
    {
        ArgumentNullException.ThrowIfNull(report);
        var current = report.Current;
        var today = report.Daily.Count > 0 ? report.Daily[0] : null;

        var header = $"{report.Place.DisplayName} — {Coordinates(report.Place)} · {report.Place.Timezone}";
        var conditions = $"{WeatherCode.Icon(current.Code)} {WeatherCode.Describe(current.Code)}"
            + $" · {UnitMath.Temperature(current.TemperatureC, units)} (feels {UnitMath.Temperature(current.ApparentC, units)})"
            + $" · humidity {current.HumidityPercent}%";
        var wind = $"Wind {UnitMath.Speed(current.WindKmh, units)} {Compass(current.WindDirectionDeg)}"
            + $" · precip now {Precipitation(current.PrecipMm, units)}";

        var lines = new List<string> { header, conditions, wind };
        if (today is { Sunrise: { } sunrise, Sunset: { } sunset })
        {
            lines.Add($"Sunrise {sunrise.ToString(ClockFormat, CultureInfo.InvariantCulture)}"
                + $" · Sunset {sunset.ToString(ClockFormat, CultureInfo.InvariantCulture)}"
                + (today.UvMax is { } uv ? $" · UV max {uv.ToString("0.#", CultureInfo.InvariantCulture)}" : string.Empty));
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Formats the alert list; empty string (no section) when there is nothing to warn about.</summary>
    public static string FormatAlerts(IReadOnlyList<WeatherAlert> alerts)
    {
        ArgumentNullException.ThrowIfNull(alerts);
        if (alerts.Count == 0)
            return string.Empty;

        var lines = new List<string> { "Alerts" };
        lines.AddRange(alerts.Select(alert => $"{(alert.Severity == AlertSeverity.Warning ? "⚠" : "☑")} "
            + $"{alert.Title} — {alert.Message}"));
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Formats the next hours as a compact table.</summary>
    public static string FormatHourly(WeatherReport report, WeatherUnits units, int hours)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentOutOfRangeException.ThrowIfNegative(hours);

        var slice = report.Hourly.Take(hours).ToList();
        if (slice.Count == 0)
            return "No hourly data available.";

        var lines = new List<string> { $"Next {slice.Count} hour(s)" };
        lines.AddRange(slice.Select(point => $"{point.LocalTime.ToString(ClockFormat, CultureInfo.InvariantCulture)}"
            + $"  {UnitMath.Temperature(point.TemperatureC, units),7}  {WeatherCode.Icon(point.Code)} "
            + $"{RainChance(point.PrecipProbabilityPercent),3}  {UnitMath.Speed(point.WindKmh, units)}"));
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Formats the daily forecast, one line per day.</summary>
    public static string FormatDaily(WeatherReport report, WeatherUnits units)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Daily.Count == 0)
            return "No forecast data available.";

        var lines = new List<string> { $"{report.Daily.Count}-day forecast" };
        lines.AddRange(report.Daily.Select(day => $"{DayLabel(day.Date)}  "
            + $"{UnitMath.Temperature(day.MinC, units),7}–{UnitMath.Temperature(day.MaxC, units),7}  "
            + $"{WeatherCode.Icon(day.Code)} {RainChance(day.PrecipProbabilityPercent),3}  "
            + $"wind {UnitMath.Speed(day.WindMaxKmh, units)}  {WeatherCode.Describe(day.Code)}"));
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Formats the best-days ranking.</summary>
    public static string FormatBest(IReadOnlyList<DayAdvice> days)
    {
        ArgumentNullException.ThrowIfNull(days);
        if (days.Count == 0)
            return "No forecast data to rank.";

        var lines = new List<string> { "Best days outdoors" };
        for (var i = 0; i < days.Count; i++)
        {
            lines.Add($"{i + 1}. {DayLabel(days[i].Date)} (score {days[i].Score}) — {days[i].Summary}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Formats the weather-for-your-plans view: open Haft Khan tasks grouped under
    /// their due day's forecast. Days without tasks still show, so gaps are visible.
    /// </summary>
    public static string FormatPlan(
        WeatherReport report,
        WeatherUnits units,
        IReadOnlyList<(DateOnly Due, string Title)> tasks)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(tasks);

        if (report.Daily.Count == 0)
            return "No forecast data available.";

        var lines = new List<string> { "Weather for your plans" };
        foreach (var day in report.Daily)
        {
            var due = tasks.Where(t => t.Due == day.Date).ToList();
            lines.Add($"{DayLabel(day.Date)}  {WeatherCode.Icon(day.Code)} "
                + $"{UnitMath.Temperature(day.MinC, units)}–{UnitMath.Temperature(day.MaxC, units)}  "
                + $"{RainChance(day.PrecipProbabilityPercent)} rain{AnahitaInsights.AdviceFor(day)}");
            lines.AddRange(due.Select(t => $"    · {t.Title}"));
            if (due.Count == 0)
                lines.Add("    · (nothing due)");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string DayLabel(DateOnly date) => date.ToString(DayFormat, CultureInfo.InvariantCulture);

    private static string Coordinates(GeoPlace place) => string.Create(
        CultureInfo.InvariantCulture,
        $"{Math.Abs(place.Latitude):0.##}°{(place.Latitude < 0 ? "S" : "N")}, "
        + $"{Math.Abs(place.Longitude):0.##}°{(place.Longitude < 0 ? "W" : "E")}");

    private static string RainChance(double? percent) => percent is null ? "—" : $"{percent:0}%";

    private static string Precipitation(double mm, WeatherUnits units) => units == WeatherUnits.Imperial
        ? $"{(mm / MillimetresPerInch).ToString("0.##", CultureInfo.InvariantCulture)} in"
        : $"{mm.ToString("0.#", CultureInfo.InvariantCulture)} mm";
}
