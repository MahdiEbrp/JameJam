using System.Globalization;

namespace JameJam.Anahita;

/// <summary>
/// Unit conversions and invariant formatting for weather values.
/// Conversion factors are physical identities, not configuration.
/// </summary>
public static class UnitMath
{
    private const double FahrenheitScale = 1.8;
    private const double FahrenheitOffset = 32.0;
    private const double KilometresPerMile = 1.609344;
    private const double KilometresPerHourPerMetrePerSecond = 3.6;
    private const double KilometresPerNauticalMile = 1.852;

    /// <summary>Converts a temperature from Celsius to Fahrenheit.</summary>
    public static double FahrenheitFromCelsius(double celsius) => (celsius * FahrenheitScale) + FahrenheitOffset;

    /// <summary>Converts a speed from km/h to mph.</summary>
    public static double MphFromKmh(double kmh) => kmh / KilometresPerMile;

    /// <summary>Converts a speed from km/h to m/s.</summary>
    public static double MetresPerSecondFromKmh(double kmh) => kmh / KilometresPerHourPerMetrePerSecond;

    /// <summary>Converts a speed from km/h to knots.</summary>
    public static double KnotsFromKmh(double kmh) => kmh / KilometresPerNauticalMile;

    /// <summary>Formats a temperature for display ("18.4°C" or "65.1°F").</summary>
    public static string Temperature(double celsius, WeatherUnits units) => units == WeatherUnits.Imperial
        ? $"{Format(FahrenheitFromCelsius(celsius))}°F"
        : $"{Format(celsius)}°C";

    /// <summary>Formats a speed for display ("12.4 km/h" or "7.7 mph").</summary>
    public static string Speed(double kmh, WeatherUnits units) => units == WeatherUnits.Imperial
        ? $"{Format(MphFromKmh(kmh))} mph"
        : $"{Format(kmh)} km/h";

    private static string Format(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}
