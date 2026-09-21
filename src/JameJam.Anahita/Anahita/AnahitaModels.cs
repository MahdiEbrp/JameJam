namespace JameJam.Anahita;

/// <summary>Measurement system for displaying weather values.</summary>
public enum WeatherUnits
{
    /// <summary>Celsius, km/h, millimetres — the default.</summary>
    Metric = 0,

    /// <summary>Fahrenheit and mph.</summary>
    Imperial = 1,
}

/// <summary>A resolved location with coordinates.</summary>
/// <param name="Name">Primary place name (or a "lat, lon" label for raw coordinates).</param>
/// <param name="Country">Country name; empty when unknown.</param>
/// <param name="Latitude">Latitude in degrees.</param>
/// <param name="Longitude">Longitude in degrees.</param>
/// <param name="Timezone">IANA timezone of the place (or "auto" before resolution).</param>
public sealed record GeoPlace(
    string Name,
    string Country,
    double Latitude,
    double Longitude,
    string Timezone)
{
    /// <summary>Administrative division (state/region); null when unknown.</summary>
    public string? Admin1 { get; init; }

    /// <summary>Population when known (used by geocoders for ranking); 0 when unknown.</summary>
    public long Population { get; init; }

    /// <summary>Human-friendly display name, e.g. "Berlin, Germany".</summary>
    public string DisplayName
    {
        get
        {
            var parts = (string[])[Name];
            if (!string.IsNullOrWhiteSpace(Admin1)
                && !string.Equals(Admin1, Name, StringComparison.OrdinalIgnoreCase))
            {
                parts = [.. parts, Admin1];
            }

            if (!string.IsNullOrWhiteSpace(Country))
            {
                parts = [.. parts, Country];
            }

            return string.Join(", ", parts);
        }
    }
}

/// <summary>Conditions observed right now at a place.</summary>
/// <param name="LocalTime">Local wall-clock time of the observation (naive local, from the API).</param>
/// <param name="TemperatureC">Air temperature (°C).</param>
/// <param name="ApparentC">Feels-like temperature (°C).</param>
/// <param name="HumidityPercent">Relative humidity (%).</param>
/// <param name="PrecipMm">Precipitation in the current hour (mm).</param>
/// <param name="Code">WMO weather code (see <see cref="WeatherCode"/>).</param>
/// <param name="WindKmh">Wind speed (km/h).</param>
/// <param name="WindDirectionDeg">Wind direction in degrees (0 = north).</param>
public sealed record CurrentConditions(
    DateTime LocalTime,
    double TemperatureC,
    double ApparentC,
    int HumidityPercent,
    double PrecipMm,
    int Code,
    double WindKmh,
    int WindDirectionDeg);

/// <summary>One hour of forecast weather.</summary>
/// <param name="LocalTime">Local wall-clock time (naive local, from the API).</param>
/// <param name="TemperatureC">Air temperature (°C).</param>
/// <param name="ApparentC">Feels-like temperature (°C).</param>
/// <param name="PrecipProbabilityPercent">Chance of precipitation (%); null when the API omits it.</param>
/// <param name="PrecipMm">Expected precipitation (mm).</param>
/// <param name="Code">WMO weather code.</param>
/// <param name="WindKmh">Wind speed (km/h).</param>
/// <param name="HumidityPercent">Relative humidity (%).</param>
public sealed record HourlyPoint(
    DateTime LocalTime,
    double TemperatureC,
    double ApparentC,
    double? PrecipProbabilityPercent,
    double PrecipMm,
    int Code,
    double WindKmh,
    int HumidityPercent);

/// <summary>One day of forecast weather.</summary>
/// <param name="Date">Forecast date.</param>
/// <param name="Code">Dominant WMO weather code.</param>
/// <param name="MinC">Minimum temperature (°C).</param>
/// <param name="MaxC">Maximum temperature (°C).</param>
/// <param name="PrecipProbabilityPercent">Maximum chance of precipitation (%); null when omitted.</param>
/// <param name="PrecipSumMm">Expected precipitation total (mm).</param>
/// <param name="WindMaxKmh">Maximum wind speed (km/h).</param>
/// <param name="UvMax">Maximum UV index; null when omitted.</param>
/// <param name="Sunrise">Local sunrise time; null when omitted.</param>
/// <param name="Sunset">Local sunset time; null when omitted.</param>
public sealed record DailyPoint(
    DateOnly Date,
    int Code,
    double MinC,
    double MaxC,
    double? PrecipProbabilityPercent,
    double PrecipSumMm,
    double WindMaxKmh,
    double? UvMax,
    TimeOnly? Sunrise,
    TimeOnly? Sunset);

/// <summary>A complete weather report for one place.</summary>
/// <param name="Place">Resolved location (with the API-resolved timezone).</param>
/// <param name="FetchedAt">When the report was downloaded (UTC).</param>
/// <param name="Current">Current conditions.</param>
/// <param name="Hourly">Hourly forecast, starting at the current hour.</param>
/// <param name="Daily">Daily forecast, starting today.</param>
public sealed record WeatherReport(
    GeoPlace Place,
    DateTimeOffset FetchedAt,
    CurrentConditions Current,
    IReadOnlyList<HourlyPoint> Hourly,
    IReadOnlyList<DailyPoint> Daily);

/// <summary>How urgently an alert should be treated.</summary>
public enum AlertSeverity
{
    /// <summary>Worth knowing about — plan around it.</summary>
    Watch = 0,

    /// <summary>Take action — protect yourself and others.</summary>
    Warning = 1,
}

/// <summary>A derived warning about upcoming conditions (heat, frost, wind, storms…).</summary>
/// <param name="Severity">How urgent the alert is.</param>
/// <param name="Title">Short label, e.g. "Heat".</param>
/// <param name="Message">Human-friendly one-line detail.</param>
public sealed record WeatherAlert(AlertSeverity Severity, string Title, string Message);

/// <summary>One day scored for outdoor plans.</summary>
/// <param name="Date">Forecast date.</param>
/// <param name="Score">0–100; higher is better for being outside.</param>
/// <param name="Summary">Human-friendly summary of the conditions.</param>
public sealed record DayAdvice(DateOnly Date, int Score, string Summary);
