using JameJam.Anahita;

namespace JameJam.Tests.Anahita;

/// <summary>Location resolution precedence, coordinate parsing, caching, and ranking delegation.</summary>
public sealed class AnahitaServiceTests
{
    /// <summary>A time source tests can move forward.</summary>
    public sealed class SteppingTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;

        public void Advance(TimeSpan delta) => Now += delta;
    }

    /// <summary>Records geocode calls and returns canned results.</summary>
    private sealed class StubClient : IAnahitaClient
    {
        public GeoPlace? GeocodeResult { get; set; } = AnahitaTestSupport.Berlin();

        public WeatherReport Report { get; set; } = AnahitaTestSupport.MakeReport(AnahitaTestSupport.Day(new DateOnly(2026, 9, 19)));

        public List<string> GeocodeNames { get; } = [];

        public List<GeoPlace> ForecastPlaces { get; } = [];

        public int ForecastCalls => ForecastPlaces.Count;

        public Task<GeoPlace?> GeocodeAsync(string name, CancellationToken cancellationToken = default)
        {
            GeocodeNames.Add(name);
            return Task.FromResult(GeocodeResult);
        }

        public Task<WeatherReport> GetForecastAsync(GeoPlace place, CancellationToken cancellationToken = default)
        {
            ForecastPlaces.Add(place);
            return Task.FromResult(Report);
        }
    }

    private static AnahitaOptions Options() => new() { CacheTtl = TimeSpan.FromMinutes(10) };

    [Fact]
    public async Task NoLocationAnywhere_ThrowsWithGuidance()
    {
        var store = new Dictionary<string, string?>();
        var service = new AnahitaService(
            new StubClient(), new SteppingTimeProvider(AnahitaTestSupport.Now), Options(),
            savedLocation: () => store.GetValueOrDefault("loc"));

        var exception = await Assert.ThrowsAsync<AnahitaException>(() => service.CurrentAsync());
        Assert.Contains("No location", exception.Message, StringComparison.Ordinal);
        Assert.Contains("JameJam weather set", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlaceName_IsGeocoded_AndDisplayed()
    {
        var client = new StubClient();
        var service = new AnahitaService(client, new SteppingTimeProvider(AnahitaTestSupport.Now), Options());

        var report = await service.CurrentAsync("Berlin");

        Assert.Equal("Berlin", client.GeocodeNames.Single());
        Assert.Equal("Berlin", report.Place.Name);
    }

    [Fact]
    public async Task Coordinates_AreUsedDirectly_WithoutGeocoding()
    {
        var client = new StubClient();
        var service = new AnahitaService(client, new SteppingTimeProvider(AnahitaTestSupport.Now), Options());

        await service.CurrentAsync("  64.15, -21.94 ");

        Assert.Empty(client.GeocodeNames);
        Assert.Equal(64.15, client.ForecastPlaces.Single().Latitude);
        Assert.Equal(-21.94, client.ForecastPlaces.Single().Longitude);
        Assert.Equal("64.15, -21.94", client.ForecastPlaces.Single().Name);
    }

    [Fact]
    public async Task UnparseableCoordinatePairs_FallBackToGeocoding()
    {
        var client = new StubClient();
        var service = new AnahitaService(client, new SteppingTimeProvider(AnahitaTestSupport.Now), Options());

        await service.CurrentAsync("52.5,not-a-number");

        Assert.Equal(["52.5,not-a-number"], client.GeocodeNames);
    }

    [Fact]
    public async Task OutOfRangeCoordinates_FallBackToGeocoding()
    {
        var client = new StubClient();
        var service = new AnahitaService(client, new SteppingTimeProvider(AnahitaTestSupport.Now), Options());

        await service.CurrentAsync("99, 200"); // beyond the coordinate rails → a (doomed) name lookup

        Assert.Equal(["99, 200"], client.GeocodeNames);
    }

    [Fact]
    public async Task UnknownPlace_ThrowsWithHelpfulHint()
    {
        var client = new StubClient { GeocodeResult = null };
        var service = new AnahitaService(client, new SteppingTimeProvider(AnahitaTestSupport.Now), Options());

        var exception = await Assert.ThrowsAsync<AnahitaException>(() => service.CurrentAsync("Nowhereville"));
        Assert.Contains("could not find 'Nowhereville'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("52.52,13.41", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlaceResolution_Argument_Beats_SavedSetting()
    {
        var client = new StubClient();
        var service = new AnahitaService(
            client, new SteppingTimeProvider(AnahitaTestSupport.Now), Options(),
            savedLocation: () => "Hamburg");

        await service.CurrentAsync("Berlin");

        Assert.Equal(["Berlin"], client.GeocodeNames);
    }

    [Fact]
    public async Task PlaceResolution_SavedSetting_UsedWhenNoArgument()
    {
        var client = new StubClient();
        var service = new AnahitaService(
            client, new SteppingTimeProvider(AnahitaTestSupport.Now), Options(),
            savedLocation: () => "Hamburg");

        await service.CurrentAsync();

        Assert.Equal(["Hamburg"], client.GeocodeNames);
    }

    [Fact]
    public async Task GeocodeResults_AreCached_PerName_Forever_Reports_UntilTtl()
    {
        var clock = new SteppingTimeProvider(AnahitaTestSupport.Now);
        var client = new StubClient();
        var service = new AnahitaService(client, clock, Options());

        await service.CurrentAsync("berlin");
        await service.CurrentAsync("BERLIN");

        // Both caches hot: one geocode, one forecast — and the place cache never expires.
        Assert.Equal(["berlin"], client.GeocodeNames);
        Assert.Equal(1, client.ForecastCalls);

        clock.Advance(TimeSpan.FromMinutes(11)); // report stale, place still cached
        await service.CurrentAsync("Berlin");
        Assert.Equal(["berlin"], client.GeocodeNames);
        Assert.Equal(2, client.ForecastCalls);
    }

    [Fact]
    public async Task Reports_AreCached_UntilTheTtlExpires()
    {
        var clock = new SteppingTimeProvider(AnahitaTestSupport.Now);
        var client = new StubClient();
        var service = new AnahitaService(client, clock, Options());

        await service.CurrentAsync("Berlin");
        clock.Advance(TimeSpan.FromMinutes(9));
        await service.CurrentAsync("Berlin");
        Assert.Equal(1, client.ForecastCalls); // still fresh

        clock.Advance(TimeSpan.FromMinutes(2)); // beyond the 10-minute TTL
        await service.CurrentAsync("Berlin");
        Assert.Equal(2, client.ForecastCalls);
    }

    [Fact]
    public async Task Cache_DistinguishesForecastLengths()
    {
        var clock = new SteppingTimeProvider(AnahitaTestSupport.Now);
        var client = new StubClient();
        await new AnahitaService(client, clock, Options() with { ForecastDays = 3 }).CurrentAsync("Berlin");
        await new AnahitaService(client, clock, Options() with { ForecastDays = 7 }).CurrentAsync("Berlin");

        Assert.Equal(2, client.ForecastCalls);
    }
}

/// <summary>Cache behaviour with a movable clock.</summary>
public sealed class AnahitaCacheTests
{
    private sealed class SteppingTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;

        public void Advance(TimeSpan delta) => Now += delta;
    }

    [Fact]
    public void PlaceCache_IsCaseInsensitive()
    {
        var cache = new AnahitaCache(new SteppingTimeProvider(AnahitaTestSupport.Now), TimeSpan.FromMinutes(5));
        cache.SetPlace("Berlin", AnahitaTestSupport.Berlin());

        Assert.Equal("Berlin", cache.GetPlace("BERLIN")?.Name);
        Assert.Null(cache.GetPlace("Hamburg"));
    }

    [Fact]
    public void Reports_ExpireAfterTheTtl()
    {
        var clock = new SteppingTimeProvider(AnahitaTestSupport.Now);
        var cache = new AnahitaCache(clock, TimeSpan.FromMinutes(15));
        var report = AnahitaTestSupport.MakeReport();

        cache.SetReport(AnahitaTestSupport.Berlin(), 7, report);
        Assert.Same(report, cache.GetReport(AnahitaTestSupport.Berlin(), 7));

        clock.Advance(TimeSpan.FromMinutes(14));
        Assert.NotNull(cache.GetReport(AnahitaTestSupport.Berlin(), 7));

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(cache.GetReport(AnahitaTestSupport.Berlin(), 7));
    }

    [Fact]
    public void ZeroTtl_DisablesReportCaching_ButPlacesStay()
    {
        var cache = new AnahitaCache(new SteppingTimeProvider(AnahitaTestSupport.Now), TimeSpan.Zero);
        cache.SetPlace("Berlin", AnahitaTestSupport.Berlin());
        cache.SetReport(AnahitaTestSupport.Berlin(), 7, AnahitaTestSupport.MakeReport());

        Assert.NotNull(cache.GetPlace("Berlin"));  // geocodes never go stale
        Assert.Null(cache.GetReport(AnahitaTestSupport.Berlin(), 7));
    }
}
