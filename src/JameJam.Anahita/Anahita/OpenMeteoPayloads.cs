using System.Text.Json.Serialization;

namespace JameJam.Anahita;

// Open-Meteo wire payloads. Every numeric field is a nullable double: the API uses
// null for "no data" (e.g. precipitation probability in dry hours) and integers are
// still valid doubles — one representation, no parse surprises.

internal sealed class GeocodingDto
{
    [JsonPropertyName("results")]
    public List<GeocodingResultDto>? Results { get; set; }
}

internal sealed class GeocodingResultDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("latitude")]
    public double Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double Longitude { get; set; }

    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("admin1")]
    public string? Admin1 { get; set; }

    [JsonPropertyName("timezone")]
    public string? Timezone { get; set; }

    [JsonPropertyName("population")]
    public long? Population { get; set; }
}

internal sealed class ForecastDto
{
    [JsonPropertyName("timezone")]
    public string? Timezone { get; set; }

    [JsonPropertyName("current")]
    public CurrentDto? Current { get; set; }

    [JsonPropertyName("hourly")]
    public HourlyDto? Hourly { get; set; }

    [JsonPropertyName("daily")]
    public DailyDto? Daily { get; set; }
}

internal sealed class CurrentDto
{
    [JsonPropertyName("time")]
    public string? Time { get; set; }

    [JsonPropertyName("temperature_2m")]
    public double? Temperature { get; set; }

    [JsonPropertyName("apparent_temperature")]
    public double? Apparent { get; set; }

    [JsonPropertyName("relative_humidity_2m")]
    public double? Humidity { get; set; }

    [JsonPropertyName("precipitation")]
    public double? Precipitation { get; set; }

    [JsonPropertyName("weather_code")]
    public double? Code { get; set; }

    [JsonPropertyName("wind_speed_10m")]
    public double? Wind { get; set; }

    [JsonPropertyName("wind_direction_10m")]
    public double? WindDirection { get; set; }
}

internal sealed class HourlyDto
{
    [JsonPropertyName("time")]
    public List<string>? Time { get; set; }

    [JsonPropertyName("temperature_2m")]
    public List<double?>? Temperature { get; set; }

    [JsonPropertyName("apparent_temperature")]
    public List<double?>? Apparent { get; set; }

    [JsonPropertyName("relative_humidity_2m")]
    public List<double?>? Humidity { get; set; }

    [JsonPropertyName("precipitation_probability")]
    public List<double?>? PrecipProbability { get; set; }

    [JsonPropertyName("precipitation")]
    public List<double?>? Precipitation { get; set; }

    [JsonPropertyName("weather_code")]
    public List<double?>? Code { get; set; }

    [JsonPropertyName("wind_speed_10m")]
    public List<double?>? Wind { get; set; }
}

internal sealed class DailyDto
{
    [JsonPropertyName("time")]
    public List<string>? Time { get; set; }

    [JsonPropertyName("weather_code")]
    public List<double?>? Code { get; set; }

    [JsonPropertyName("temperature_2m_max")]
    public List<double?>? Max { get; set; }

    [JsonPropertyName("temperature_2m_min")]
    public List<double?>? Min { get; set; }

    [JsonPropertyName("precipitation_sum")]
    public List<double?>? PrecipSum { get; set; }

    [JsonPropertyName("precipitation_probability_max")]
    public List<double?>? PrecipProbability { get; set; }

    [JsonPropertyName("wind_speed_10m_max")]
    public List<double?>? Wind { get; set; }

    [JsonPropertyName("uv_index_max")]
    public List<double?>? Uv { get; set; }

    [JsonPropertyName("sunrise")]
    public List<string>? Sunrise { get; set; }

    [JsonPropertyName("sunset")]
    public List<string>? Sunset { get; set; }
}
