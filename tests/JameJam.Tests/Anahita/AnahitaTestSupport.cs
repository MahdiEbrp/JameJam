using JameJam.Anahita;

namespace JameJam.Tests.Anahita;

/// <summary>Shared fixtures for the Anahita tests: deterministic places, reports, and payloads.</summary>
internal static class AnahitaTestSupport
{
    /// <summary>The frozen test instant.</summary>
    public static readonly DateTimeOffset Now = new(2026, 9, 19, 10, 0, 0, TimeSpan.Zero);

    /// <summary>A canonical Berlin place.</summary>
    public static GeoPlace Berlin() => new("Berlin", "Germany", 52.52, 13.41, "Europe/Berlin")
    {
        Admin1 = "State of Berlin",
        Population = 3_664_088,
    };

    /// <summary>Builds a configurable daily point.</summary>
    public static DailyPoint Day(
        DateOnly date,
        int code = 2,
        double min = 12.5,
        double max = 19.2,
        double? chance = 10,
        double sum = 0.4,
        double wind = 21.3,
        double? uv = 4.2) => new(date, code, min, max, chance, sum, wind, uv, new TimeOnly(6, 41), new TimeOnly(19, 22));

    /// <summary>Builds a deterministic report from the given daily points.</summary>
    public static WeatherReport MakeReport(params DailyPoint[] daily)
    {
        GeoPlace place = new("Berlin", "Germany", 52.52, 13.41, "Europe/Berlin");
        IReadOnlyList<HourlyPoint> hourly =
        [
            new(new DateTime(2026, 9, 19, 14, 0, 0), 18.4, 17.9, 10, 0.0, 2, 12.4, 62),
            new(new DateTime(2026, 9, 19, 15, 0, 0), 18.9, 18.1, 20, 0.1, 3, 13.1, 60),
        ];
        return new WeatherReport(
            place,
            Now,
            new(new DateTime(2026, 9, 19, 14, 0, 0), 18.4, 17.9, 62, 0.4, 2, 12.4, 315),
            hourly,
            daily);
    }

    /// <summary>A minimal realistic Open-Meteo forecast payload.</summary>
    public static string ForecastJson() => """
        {
          "latitude": 52.52, "longitude": 13.41, "timezone": "Europe/Berlin",
          "current": {"time": "2026-09-19T14:00", "temperature_2m": 18.4, "apparent_temperature": 17.9,
                      "relative_humidity_2m": 62, "precipitation": 0.4, "weather_code": 2,
                      "wind_speed_10m": 12.4, "wind_direction_10m": 315},
          "hourly": {
            "time": ["2026-09-19T14:00", "2026-09-19T15:00"],
            "temperature_2m": [18.4, 18.9], "apparent_temperature": [17.9, 18.1],
            "relative_humidity_2m": [62, 60], "precipitation_probability": [10, null],
            "precipitation": [0.0, 0.1], "weather_code": [2, 3], "wind_speed_10m": [12.4, 13.1]},
          "daily": {
            "time": ["2026-09-19", "2026-09-20"],
            "weather_code": [2, 61], "temperature_2m_max": [19.2, 16.1], "temperature_2m_min": [12.5, 11.0],
            "precipitation_sum": [0.4, 8.2], "precipitation_probability_max": [10, 80],
            "wind_speed_10m_max": [21.3, 33.5], "uv_index_max": [4.2, null],
            "sunrise": ["2026-09-19T06:41", "2026-09-20T06:43"], "sunset": ["2026-09-19T19:22", "2026-09-20T19:20"]}
        }
        """;

    /// <summary>A minimal Open-Meteo geocoding payload with one result.</summary>
    public static string GeocodeJson() => """
        {
          "results": [
            {"name": "Berlin", "latitude": 52.52437, "longitude": 13.41053,
             "country": "Germany", "admin1": "State of Berlin", "timezone": "Europe/Berlin",
             "population": 3664088},
            {"name": "Berlin", "latitude": 44.46867, "longitude": -71.18508,
             "country": "United States", "timezone": "America/New_York", "population": 9365}
          ]
        }
        """;

    /// <summary>An empty geocoding payload (no place matched).</summary>
    public const string GeocodeEmptyJson = """{"generationtime_ms": 0.5}""";
}
