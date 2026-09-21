using System.Net;
using System.Text;

using JameJam.Anahita;
using JameJam.Tests.HaftKhan.Sync;

namespace JameJam.Tests.Anahita;

/// <summary>
/// Edge-case sweep: null-constructor guards, missing flag values, unit aliases,
/// truncated error bodies, malformed timestamps, and zero-length Retry-After.
/// </summary>
public sealed class AnahitaEdgeCaseTests
{
    private static HttpResponseMessage StubJson(HttpStatusCode statusCode, string json) =>
        new(statusCode) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static AnahitaOptions Options() => new()
    {
        ForecastEndpoint = "https://weather.test/v1/forecast",
        GeocodingEndpoint = "https://weather.test/search",
        RetryBaseDelay = TimeSpan.FromMilliseconds(1),
    };

    [Fact]
    public void Client_RejectsNullDependencies()
    {
        Assert.Throws<ArgumentNullException>(() => new OpenMeteoClient(null!, Options()));
        Assert.Throws<ArgumentNullException>(() => new OpenMeteoClient(new HttpClient(), null!));
    }

    [Fact]
    public void Commands_RejectNullWritersAndClock()
    {
        Assert.Throws<ArgumentNullException>(() => new AnahitaCommands(null!, new StringWriter(), TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new AnahitaCommands(new StringWriter(), null!, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new AnahitaCommands(new StringWriter(), new StringWriter(), null!));
    }

    [Fact]
    public async Task FlagWithoutValue_BecomesAnUnknownOption()
    {
        using StringWriter error = new();
        var commands = new AnahitaCommands(
            new StringWriter(), error, TimeProvider.System, options: Options());

        Assert.Equal(1, await commands.RunAsync(["now", "--at"]));
        Assert.Contains("Unknown weather option '--at'", error.ToString(), StringComparison.Ordinal);

        Assert.Equal(1, await commands.RunAsync(["forecast", "--days"]));
        Assert.Contains("Unknown weather option '--days'", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnitAliases_AreAccepted()
    {
        using StringWriter output = new();
        var commands = new AnahitaCommands(
            output, new StringWriter(), TimeProvider.System,
            clientFactory: _ => new TinyClient(),
            options: Options());

        Assert.Equal(0, await commands.RunAsync(["now", "--at", "Berlin", "--units", "c"]));
        Assert.Contains("°C", output.ToString(), StringComparison.Ordinal);

        Assert.Equal(0, await commands.RunAsync(["now", "--at", "Berlin", "--units", "fahrenheit"]));
        Assert.Contains("°F", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hourly_WithoutHours_UsesTheWindowOption()
    {
        using StringWriter output = new();
        var commands = new AnahitaCommands(
            output, new StringWriter(), TimeProvider.System,
            clientFactory: _ => new TinyClient(),
            options: Options() with { HourlyWindow = 1 });

        Assert.Equal(0, await commands.RunAsync(["hourly", "--at", "Berlin"]));
        Assert.Contains("Next 1 hour(s)", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyApiKey_SendsNoAuthorizationHeader()
    {
        var handler = new HttpSyncClientTests.StubHandler(_ => StubJson(HttpStatusCode.OK, AnahitaTestSupport.ForecastJson()));
        using var http = new HttpClient(handler);
        var client = new OpenMeteoClient(http, Options() with { ApiKey = string.Empty });

        await client.GetForecastAsync(AnahitaTestSupport.Berlin());

        Assert.Null(handler.Requests[0].Headers.Authorization);
    }

    [Fact]
    public async Task LongErrorBodies_AreTruncated()
    {
        var handler = new HttpSyncClientTests.StubHandler(_ => StubJson(HttpStatusCode.NotFound, new string('x', 900)));
        using var http = new HttpClient(handler);
        var client = new OpenMeteoClient(http, Options());

        var exception = await Assert.ThrowsAsync<AnahitaException>(() => client.GetForecastAsync(AnahitaTestSupport.Berlin()));

        Assert.EndsWith("…", exception.Message, StringComparison.Ordinal);
        Assert.Contains(new string('x', 500), exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 501), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedTimestamps_AreFriendlyErrors()
    {
        const string badTime = """
            {"timezone":"UTC",
             "current": {"time": "not-a-time", "temperature_2m": 1, "apparent_temperature": 1,
                         "relative_humidity_2m": 1, "precipitation": 0, "weather_code": 0,
                         "wind_speed_10m": 0, "wind_direction_10m": 0}}
            """;
        var handler = new HttpSyncClientTests.StubHandler(_ => StubJson(HttpStatusCode.OK, badTime));
        using var http = new HttpClient(handler);
        var client = new OpenMeteoClient(http, Options());

        var exception = await Assert.ThrowsAsync<AnahitaException>(() => client.GetForecastAsync(AnahitaTestSupport.Berlin()));
        Assert.Contains("unreadable current time", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedDates_AreFriendlyErrors()
    {
        const string badDate = """
            {"timezone":"UTC",
             "current": {"time": "2026-09-19T14:00", "temperature_2m": 1, "apparent_temperature": 1,
                         "relative_humidity_2m": 1, "precipitation": 0, "weather_code": 0,
                         "wind_speed_10m": 0, "wind_direction_10m": 0},
             "daily": {"time": ["nope"], "weather_code": [0], "temperature_2m_max": [1], "temperature_2m_min": [0],
                       "precipitation_sum": [0], "precipitation_probability_max": [0], "wind_speed_10m_max": [0],
                       "uv_index_max": [0], "sunrise": ["2026-09-19T06:41"], "sunset": ["2026-09-19T19:22"]}}
            """;
        var handler = new HttpSyncClientTests.StubHandler(_ => StubJson(HttpStatusCode.OK, badDate));
        using var http = new HttpClient(handler);
        var client = new OpenMeteoClient(http, Options());

        var exception = await Assert.ThrowsAsync<AnahitaException>(() => client.GetForecastAsync(AnahitaTestSupport.Berlin()));
        Assert.Contains("unreadable date", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ZeroRetryAfter_SkipsTheDelay()
    {
        var calls = 0;
        var handler = new HttpSyncClientTests.StubHandler(_ =>
        {
            calls++;
            if (calls == 1)
            {
                var retry = StubJson(HttpStatusCode.ServiceUnavailable, "busy");
                retry.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return retry;
            }

            return StubJson(HttpStatusCode.OK, AnahitaTestSupport.ForecastJson());
        });
        using var http = new HttpClient(handler);
        var client = new OpenMeteoClient(http, Options() with { MaxRetries = 2 });

        Assert.NotNull(await client.GetForecastAsync(AnahitaTestSupport.Berlin()));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task WhitespacePlaceArgument_FallsThroughToLaterSources()
    {
        var client = new TinyClient();
        var service = new AnahitaService(
            client, new FixedTimeProvider(AnahitaTestSupport.Now), Options(), savedLocation: () => "Hamburg");

        var report = await service.CurrentAsync("   ");
        Assert.Equal("Hamburg", client.GeocodeNames.Single()); // "Hamburg" resolved the request
        Assert.Equal(AnahitaTestSupport.Berlin().Name, report.Place.Name);
    }

    [Fact]
    public void NoRainPenalty_WhenBothChanceAndSumAreQuiet()
    {
        var day = AnahitaTestSupport.Day(
            new DateOnly(2026, 9, 19), min: 20, max: 24, chance: null, sum: 0, wind: 10);
        Assert.Equal(AnahitaDefaults.PerfectScore, AnahitaInsights.ScoreDay(day));
    }

    [Fact]
    public void FormatNow_SkipsSunLine_WhenSunTimesMissing()
    {
        var day = new DailyPoint(
            new DateOnly(2026, 9, 19), 2, 12.5, 19.2, 10, 0.4, 21.3, null, null, null);
        var text = AnahitaFormat.FormatNow(AnahitaTestSupport.MakeReport(day), WeatherUnits.Metric);

        Assert.DoesNotContain("Sunrise", text, StringComparison.Ordinal);
    }

    /// <summary>A stub transport that answers everything with the canonical report.</summary>
    private sealed class TinyClient : IAnahitaClient
    {
        public List<string> GeocodeNames { get; } = [];

        public Task<GeoPlace?> GeocodeAsync(string name, CancellationToken cancellationToken = default)
        {
            GeocodeNames.Add(name);
            GeoPlace place = new(name, "Germany", 52.52, 13.41, "Europe/Berlin");
            return Task.FromResult<GeoPlace?>(place);
        }

        public Task<WeatherReport> GetForecastAsync(GeoPlace place, CancellationToken cancellationToken = default) =>
            Task.FromResult(AnahitaTestSupport.MakeReport(AnahitaTestSupport.Day(new DateOnly(2026, 9, 19))));
    }
}
