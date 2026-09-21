using System.Globalization;

namespace JameJam.Anahita;

/// <summary>
/// Derives human insight from raw weather data: safety alerts over the alert horizon,
/// per-day advice, and a ranking of the best upcoming days for outdoor plans.
/// Pure functions — every threshold comes from <see cref="AnahitaDefaults"/>.
/// </summary>
public static class AnahitaInsights
{
    /// <summary>
    /// Evaluates the next <see cref="AnahitaDefaults.AlertHorizonDays"/> forecast days and
    /// returns alerts, most urgent first (Warning before Watch).
    /// </summary>
    public static IReadOnlyList<WeatherAlert> FindAlerts(WeatherReport report, WeatherUnits units)
    {
        ArgumentNullException.ThrowIfNull(report);
        var horizon = report.Daily.Take(AnahitaDefaults.AlertHorizonDays);
        List<WeatherAlert> alerts = [];

        foreach (var day in horizon)
        {
            var label = DayLabel(day.Date);
            if (day.MaxC >= AnahitaDefaults.HeatCelsius)
            {
                alerts.Add(new WeatherAlert(
                    AlertSeverity.Warning, "Heat",
                    $"{label} peaks at {UnitMath.Temperature(day.MaxC, units)} — hydrate and stay out of the midday sun."));
            }

            if (day.MinC <= AnahitaDefaults.ColdCelsius)
            {
                alerts.Add(new WeatherAlert(
                    AlertSeverity.Warning, "Deep freeze",
                    $"{label} falls to {UnitMath.Temperature(day.MinC, units)} — protect skin and pipes."));
            }
            else if (day.MinC <= AnahitaDefaults.FrostCelsius)
            {
                alerts.Add(new WeatherAlert(
                    AlertSeverity.Watch, "Frost",
                    $"{label} dips to {UnitMath.Temperature(day.MinC, units)} — frost is likely before morning."));
            }

            if (day.WindMaxKmh >= AnahitaDefaults.StrongWindKmh)
            {
                alerts.Add(new WeatherAlert(
                    AlertSeverity.Warning, "Strong wind",
                    $"{label} blows up to {UnitMath.Speed(day.WindMaxKmh, units)} — secure loose objects."));
            }

            if (day.PrecipSumMm >= AnahitaDefaults.HeavyRainMm)
            {
                alerts.Add(new WeatherAlert(
                    AlertSeverity.Warning, "Heavy rain",
                    $"{label} may dump {Millimetres(day.PrecipSumMm)} — expect flooding pockets."));
            }

            if (WeatherCode.IsThunderstorm(day.Code))
            {
                alerts.Add(new WeatherAlert(
                    AlertSeverity.Warning, "Thunderstorms",
                    $"{label} brings {WeatherCode.Describe(day.Code).ToLowerInvariant()} — avoid open ground and tall trees."));
            }

            if (day.UvMax is { } uv && uv >= AnahitaDefaults.HighUv)
            {
                alerts.Add(new WeatherAlert(
                    AlertSeverity.Watch, "High UV",
                    $"{label} reaches UV {Millimetres(uv)} — sunscreen and a hat."));
            }
        }

        return [.. alerts.OrderByDescending(static a => (int)a.Severity)];
    }

    /// <summary>Ranks forecast days for outdoor plans; the best day first.</summary>
    public static IReadOnlyList<DayAdvice> RankDays(WeatherReport report, WeatherUnits units, int topCount)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentOutOfRangeException.ThrowIfNegative(topCount);

        return [.. report.Daily
            .Select(day => new DayAdvice(day.Date, ScoreDay(day), Summarize(day, units)))
            .OrderByDescending(static d => d.Score)
            .ThenBy(static d => d.Date)
            .Take(topCount)];
    }

    /// <summary>
    /// Scores one day for outdoor plans (0–<see cref="AnahitaDefaults.PerfectScore"/>):
    /// rain chance, temperature distance from comfort, wind, and thunderstorms subtract.
    /// </summary>
    public static int ScoreDay(DailyPoint day)
    {
        var rainPenalty = day.PrecipProbabilityPercent is { } chance
            ? (int)Math.Round(chance / AnahitaDefaults.RainAdvicePercent * AnahitaDefaults.RainPenaltyMax)
            : day.PrecipSumMm >= AnahitaDefaults.RainTraceMm ? AnahitaDefaults.RainPenaltyMax / 2 : 0;
        rainPenalty = Math.Min(rainPenalty, AnahitaDefaults.RainPenaltyMax);

        var mean = (day.MinC + day.MaxC) / 2;
        var comfortPenalty = (int)(Math.Abs(mean - AnahitaDefaults.ComfortCelsius)
            * AnahitaDefaults.TemperaturePenaltyPerDegree);
        var thunderPenalty = WeatherCode.IsThunderstorm(day.Code) ? AnahitaDefaults.ThunderPenalty : 0;
        var windPenalty = day.WindMaxKmh >= AnahitaDefaults.StrongWindKmh ? AnahitaDefaults.WindPenalty : 0;

        return Math.Clamp(
            AnahitaDefaults.PerfectScore - rainPenalty - comfortPenalty - thunderPenalty - windPenalty,
            0,
            AnahitaDefaults.PerfectScore);
    }

    /// <summary>Builds the one-line human summary for a forecast day.</summary>
    public static string Summarize(DailyPoint day, WeatherUnits units) =>
        $"{WeatherCode.Describe(day.Code)}, {UnitMath.Temperature(day.MinC, units)}"
        + $"–{UnitMath.Temperature(day.MaxC, units)}{AdviceFor(day)}";

    /// <summary>One actionable sentence for a forecast day.</summary>
    public static string AdviceFor(DailyPoint day)
    {
        var rainLikely = (day.PrecipProbabilityPercent ?? 0) >= AnahitaDefaults.RainAdvicePercent
            || day.PrecipSumMm >= AnahitaDefaults.RainTraceMm;

        if (WeatherCode.IsThunderstorm(day.Code))
            return " — thunderstorms likely, stay near shelter.";
        if (rainLikely)
            return " — rain is likely, pack an umbrella.";
        if (day.MaxC >= AnahitaDefaults.HeatCelsius)
            return " — very hot, plan around the midday sun.";
        if (day.MinC <= AnahitaDefaults.FrostCelsius)
            return " — freezing, dress in warm layers.";
        if (day.WindMaxKmh >= AnahitaDefaults.StrongWindKmh)
            return " — very windy, think twice about cycling.";
        return " — good conditions to be outside.";
    }

    private static string Millimetres(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    private static string DayLabel(DateOnly date) => date.ToString("ddd d MMM", CultureInfo.InvariantCulture);
}
