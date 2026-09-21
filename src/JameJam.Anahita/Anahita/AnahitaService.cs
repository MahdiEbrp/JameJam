using System.Globalization;

namespace JameJam.Anahita;

/// <summary>
/// Orchestrates Anahita: resolves a location (argument → environment → saved setting,
/// either a place name or "lat,lon" coordinates), downloads the forecast through the
/// transport, and serves cached reports while they are fresh.
/// </summary>
/// <param name="client">The weather transport. Injected for testability.</param>
/// <param name="clock">Time source for the cache TTL.</param>
/// <param name="options">Validated weather options; environment-driven defaults apply when null.</param>
/// <param name="savedLocation">Resolves the stored default location (usually the settings store).</param>
/// <param name="cache">Report/place cache; a fresh one is created when null.</param>
public sealed class AnahitaService(
    IAnahitaClient client,
    TimeProvider clock,
    AnahitaOptions? options = null,
    Func<string?>? savedLocation = null,
    AnahitaCache? cache = null)
{
    private readonly IAnahitaClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly AnahitaOptions _options = options ?? AnahitaOptions.FromEnvironment();
    private readonly Func<string?>? _savedLocation = savedLocation;
    private readonly AnahitaCache _cache = cache ?? new AnahitaCache(clock, options?.CacheTtl ?? AnahitaDefaults.CacheTtl);

    /// <summary>
    /// Resolves the location and returns the current report (cached when fresh).
    /// </summary>
    /// <param name="placeArg">Explicit place from the command line; beats env and settings.</param>
    /// <exception cref="AnahitaException">No location, unknown place, or transport failure.</exception>
    public async Task<WeatherReport> CurrentAsync(string? placeArg = null, CancellationToken cancellationToken = default)
    {
        var location = FirstNonEmpty(
            placeArg,
            Environment.GetEnvironmentVariable(AnahitaDefaults.LocationEnvironmentVariable),
            _savedLocation?.Invoke())
            ?? throw new AnahitaException(AnahitaDefaults.NoLocationMessage);

        var place = await ResolvePlaceAsync(location, cancellationToken).ConfigureAwait(false);
        var report = _cache.GetReport(place, _options.ForecastDays);
        if (report is null)
        {
            report = await _client.GetForecastAsync(place, cancellationToken).ConfigureAwait(false);
            _cache.SetReport(place, _options.ForecastDays, report);
        }

        return report;
    }


    private async Task<GeoPlace> ResolvePlaceAsync(string location, CancellationToken cancellationToken)
    {
        if (TryParseCoordinates(location, out var latitude, out var longitude))
        {
            return new GeoPlace(
                $"{latitude.ToString("0.####", CultureInfo.InvariantCulture)}, "
                + longitude.ToString("0.####", CultureInfo.InvariantCulture),
                string.Empty,
                latitude,
                longitude,
                "auto");
        }

        var cached = _cache.GetPlace(location);
        if (cached is not null)
            return cached;

        var resolved = await _client.GeocodeAsync(location, cancellationToken).ConfigureAwait(false)
            ?? throw new AnahitaException(
                $"Anahita could not find '{location}'. Check the spelling, or pass coordinates as --at 52.52,13.41");

        _cache.SetPlace(location, resolved);
        return resolved;
    }

    private static bool TryParseCoordinates(string location, out double latitude, out double longitude)
    {
        latitude = longitude = 0;
        var parts = location.Split(',');
        if (parts.Length != 2)
            return false;

        if (!double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out latitude)
            || !double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out longitude))
        {
            return false;
        }

        var inRange = Math.Abs(latitude) <= AnahitaDefaults.LatitudeBound
            && Math.Abs(longitude) <= AnahitaDefaults.LongitudeBound;
        if (!inRange)
            return false;

        return true;
    }

    private static string? FirstNonEmpty(params string?[] candidates) =>
        candidates.FirstOrDefault(static c => !string.IsNullOrWhiteSpace(c));
}
