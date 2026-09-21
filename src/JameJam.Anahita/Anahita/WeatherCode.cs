namespace JameJam.Anahita;

/// <summary>
/// The WMO weather interpretation codes (0–99) used by Open-Meteo, mapped to
/// human descriptions and terminal glyphs. Protocol values — enumerated here so
/// no call site ever compares raw numbers.
/// </summary>
public static class WeatherCode
{
    /// <summary>First code of the thunderstorm range (95–99).</summary>
    private const int ThunderstormMin = 95;

    /// <summary>Last code of the thunderstorm range.</summary>
    private const int ThunderstormMax = 99;

    /// <summary>Describes a WMO code in plain words.</summary>
    public static string Describe(int code) => code switch
    {
        0 => "Clear sky",
        1 => "Mainly clear",
        2 => "Partly cloudy",
        3 => "Overcast",
        45 => "Fog",
        48 => "Rime fog",
        51 => "Light drizzle",
        53 => "Drizzle",
        55 => "Dense drizzle",
        56 => "Freezing drizzle",
        57 => "Dense freezing drizzle",
        61 => "Light rain",
        63 => "Rain",
        65 => "Heavy rain",
        66 => "Freezing rain",
        67 => "Heavy freezing rain",
        71 => "Light snow",
        73 => "Snow",
        75 => "Heavy snow",
        77 => "Snow grains",
        80 => "Light showers",
        81 => "Showers",
        82 => "Violent showers",
        85 => "Snow showers",
        86 => "Heavy snow showers",
        95 => "Thunderstorm",
        96 => "Thunderstorm with hail",
        99 => "Severe thunderstorm with hail",
        _ => "Unrecognized weather code",
    };

    /// <summary>A single terminal glyph for a WMO code (basic symbols, safe everywhere).</summary>
    public static string Icon(int code) => code switch
    {
        0 or 1 => "☀",
        2 or 3 => "☁",
        45 or 48 => "☁",
        >= 51 and <= 67 or >= 80 and <= 82 => "☂",
        >= 71 and <= 77 or 85 or 86 => "❆",
        >= ThunderstormMin and <= ThunderstormMax => "⚡",
        _ => "·",
    };

    /// <summary>True when the code denotes a thunderstorm (WMO 95–99).</summary>
    public static bool IsThunderstorm(int code) => code is >= ThunderstormMin and <= ThunderstormMax;
}
