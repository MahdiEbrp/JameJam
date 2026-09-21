using System.Globalization;

namespace JameJam.Anahita;

/// <summary>
/// Short-lived in-process cache for weather reports and geocoding results, so repeated
/// calls in one session never hammer the endpoint. TTL-governed; zero TTL disables caching.
/// </summary>
public sealed class AnahitaCache
{
    private const string AutoTimezone = "auto";

    private sealed record Entry(DateTimeOffset StoredAt, WeatherReport Report);

    private readonly TimeProvider _clock;
    private readonly TimeSpan _ttl;
    private readonly Dictionary<string, Entry> _reports = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GeoPlace> _places = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes the cache with a time source and a time-to-live.</summary>
    public AnahitaCache(TimeProvider clock, TimeSpan ttl)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _ttl = ttl;
    }

    /// <summary>Returns the cached report for a place and forecast length, or null when absent/expired.</summary>
    public WeatherReport? GetReport(GeoPlace place, int forecastDays)
    {
        ArgumentNullException.ThrowIfNull(place);
        if (_reports.TryGetValue(ReportKey(place, forecastDays), out var entry)
            && _clock.GetUtcNow() - entry.StoredAt < _ttl)
        {
            return entry.Report;
        }

        return null;
    }

    /// <summary>Caches a report for a place and forecast length.</summary>
    public void SetReport(GeoPlace place, int forecastDays, WeatherReport report)
    {
        ArgumentNullException.ThrowIfNull(place);
        ArgumentNullException.ThrowIfNull(report);
        _reports[ReportKey(place, forecastDays)] = new(_clock.GetUtcNow(), report);
    }

    /// <summary>Returns the cached geocode for a name (case-insensitive), or null when absent.</summary>
    public GeoPlace? GetPlace(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _places.TryGetValue(name, out var place) ? place : null;
    }

    /// <summary>Caches a resolved place under the queried name.</summary>
    public void SetPlace(string name, GeoPlace place)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(place);
        _places[name] = place;
    }

    private static string ReportKey(GeoPlace place, int forecastDays) => string.Create(
        CultureInfo.InvariantCulture,
        $"{place.Latitude:0.####},{place.Longitude:0.####}|{forecastDays}|{place.Timezone == AutoTimezone}");
}
