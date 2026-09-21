namespace JameJam.Anahita;

/// <summary>Transport for weather data: geocoding plus forecast download.</summary>
public interface IAnahitaClient
{
    /// <summary>
    /// Resolves a place name to coordinates. Returns null when nothing matches.
    /// </summary>
    /// <exception cref="AnahitaException">Transport failure, oversized response, or an invalid payload.</exception>
    Task<GeoPlace?> GeocodeAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Downloads the forecast for an already-resolved place.</summary>
    /// <exception cref="AnahitaException">Transport failure, oversized response, or an invalid payload.</exception>
    Task<WeatherReport> GetForecastAsync(GeoPlace place, CancellationToken cancellationToken = default);
}
