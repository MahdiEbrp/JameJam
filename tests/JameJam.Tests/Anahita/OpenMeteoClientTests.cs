using System.Diagnostics;
using System.Net;
using System.Text;

using JameJam.Anahita;
using JameJam.Tests.HaftKhan.Sync;

namespace JameJam.Tests.Anahita;

/// <summary>Tests for the hardened weather transport against a stubbed HTTP pipeline (no network).</summary>
public sealed class OpenMeteoClientTests
{
    private static AnahitaOptions Options(
        string forecast = "https://weather.test/v1/forecast",
        string geocoding = "https://weather.test/search",
        string? apiKey = null) =>
        new()
        {
            ForecastEndpoint = forecast,
            GeocodingEndpoint = geocoding,
            ApiKey = apiKey,
            RetryBaseDelay = TimeSpan.FromMilliseconds(1),
        };

    private static OpenMeteoClient Client(HttpMessageHandler handler, AnahitaOptions options) =>
        new(new HttpClient(handler), options);

    [Fact]
    public async Task Forecast_ParsesTheFullPayload()
    {
        var handler = new HttpSyncClientTests.StubHandler(_ => StubJson(HttpStatusCode.OK, AnahitaTestSupport.ForecastJson()));
        using var http = new HttpClient(handler);
        var client = Client(handler, Options());

        var report = await client.GetForecastAsync(AnahitaTestSupport.Berlin());

        Assert.Equal("Europe/Berlin", report.Place.Timezone); // API-resolved
        Assert.Equal(18.4, report.Current.TemperatureC);
        Assert.Equal(62, report.Current.HumidityPercent);
        Assert.Equal(315, report.Current.WindDirectionDeg);
        Assert.Equal(2, report.Hourly.Count);
        Assert.Null(report.Hourly[1].PrecipProbabilityPercent); // API null preserved
        Assert.Equal(2, report.Daily.Count);
        Assert.Equal(new DateOnly(2026, 9, 20), report.Daily[1].Date);
        Assert.Equal(new TimeOnly(6, 43), report.Daily[1].Sunrise);
        Assert.Null(report.Daily[1].UvMax);
        Assert.Equal(80, report.Daily[1].PrecipProbabilityPercent);
    }

    [Fact]
    public async Task Forecast_BuildsTheExpectedQuery()
    {
        var handler = new HttpSyncClientTests.StubHandler(_ => StubJson(HttpStatusCode.OK, AnahitaTestSupport.ForecastJson()));
        using var http = new HttpClient(handler);
        var client = Client(handler, Options() with { ForecastDays = 5 });

        await client.GetForecastAsync(AnahitaTestSupport.Berlin());

        var url = handler.Requests[0].RequestUri!.ToString();
        Assert.Contains("latitude=52.52", url, StringComparison.Ordinal);
        Assert.Contains("longitude=13.41", url, StringComparison.Ordinal);
        Assert.Contains("forecast_days=5", url, StringComparison.Ordinal);
        Assert.Contains("timezone=auto", url, StringComparison.Ordinal);
        Assert.Contains("temperature_2m_max", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forecast_SendsBearerKey_WhenConfigured()
    {
        var handler = new HttpSyncClientTests.StubHandler(_ => StubJson(HttpStatusCode.OK, AnahitaTestSupport.ForecastJson()));
        using var http = new HttpClient(handler);
        var client = Client(handler, Options(apiKey: "mirror-key-1"));

        await client.GetForecastAsync(AnahitaTestSupport.Berlin());

        Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization?.Scheme);
        Assert.Equal("mirror-key-1", handler.Requests[0].Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task Forecast_RetriesTransientFailures_ThenSucceeds()
    {
        var calls = 0;
        var handler = new HttpSyncClientTests.StubHandler(_ =>
        {
            calls++;
            return calls == 1
                ? StubJson(HttpStatusCode.ServiceUnavailable, "busy")
                : StubJson(HttpStatusCode.OK, AnahitaTestSupport.ForecastJson());
        });
        using var http = new HttpClient(handler);
        var client = Client(handler, Options());

        var report = await client.GetForecastAsync(AnahitaTestSupport.Berlin());

        Assert.Equal(18.4, report.Current.TemperatureC);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Forecast_RejectsOversizedResponses()
    {
        var handler = new HttpSyncClientTests.StubHandler(_ => StubJson(HttpStatusCode.OK, new string('x', 5_000)));
        using var http = new HttpClient(handler);
        var client = Client(handler, Options() with { MaxResponseBytes = 1_000 });

        var exception = await Assert.ThrowsAsync<AnahitaException>(() => client.GetForecastAsync(AnahitaTestSupport.Berlin()));
        Assert.Contains("exceeds the configured maximum", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forecast_ScrubsTheKeyFromErrorBodies()
    {
        var handler = new HttpSyncClientTests.StubHandler(_ => StubJson(
            HttpStatusCode.Unauthorized, """{"error":"bad key mirror-key-1"}"""));
        using var http = new HttpClient(handler);
        var client = Client(handler, Options(apiKey: "mirror-key-1"));

        var exception = await Assert.ThrowsAsync<AnahitaException>(() => client.GetForecastAsync(AnahitaTestSupport.Berlin()));

        Assert.Contains("****", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("mirror-key-1", exception.Message, StringComparison.Ordinal);
        Assert.Equal(401, exception.StatusCode);
    }

    [Fact]
    public async Task Forecast_404_IsAFriendlyError_NotNull()
    {
        var handler = new HttpSyncClientTests.StubHandler(_ => StubJson(HttpStatusCode.NotFound, "{}"));
        using var http = new HttpClient(handler);
        var client = Client(handler, Options());

        var exception = await Assert.ThrowsAsync<AnahitaException>(() => client.GetForecastAsync(AnahitaTestSupport.Berlin()));
        Assert.Contains("404", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forecast_MissingCurrentSection_IsAFriendlyError()
    {
        var handler = new HttpSyncClientTests.StubHandler(_ => StubJson(HttpStatusCode.OK, """{"timezone":"UTC"}"""));
        using var http = new HttpClient(handler);
        var client = Client(handler, Options());

        var exception = await Assert.ThrowsAsync<AnahitaException>(() => client.GetForecastAsync(AnahitaTestSupport.Berlin()));
        Assert.Contains("no current conditions", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forecast_MisalignedArrays_AreAFriendlyError()
    {
        const string misaligned = """
            {"timezone":"UTC",
             "current": {"time": "2026-09-19T14:00", "temperature_2m": 18.4, "apparent_temperature": 17.9,
                         "relative_humidity_2m": 62, "precipitation": 0.4, "weather_code": 2,
                         "wind_speed_10m": 12.4, "wind_direction_10m": 315},
             "hourly": {"time": ["2026-09-19T14:00", "2026-09-19T15:00"], "temperature_2m": [18.4]}}
            """;
        var handler = new HttpSyncClientTests.StubHandler(_ => StubJson(HttpStatusCode.OK, misaligned));
        using var http = new HttpClient(handler);
        var client = Client(handler, Options());

        var exception = await Assert.ThrowsAsync<AnahitaException>(() => client.GetForecastAsync(AnahitaTestSupport.Berlin()));
        Assert.Contains("inconsistent data", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forecast_InvalidJson_IsAFriendlyError()
    {
        var handler = new HttpSyncClientTests.StubHandler(_ => StubJson(HttpStatusCode.OK, """{"broken":[1,2"""));
        using var http = new HttpClient(handler);
        var client = Client(handler, Options());

        var exception = await Assert.ThrowsAsync<AnahitaException>(() => client.GetForecastAsync(AnahitaTestSupport.Berlin()));
        Assert.Contains("did not return a valid Anahita weather response", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forecast_TimesOut_AfterAllAttempts()
    {
        var handler = new BlockingHandler();
        using var http = new HttpClient(handler);
        var client = Client(handler, Options() with { RequestTimeout = TimeSpan.FromMilliseconds(100), MaxRetries = 1 });

        await Assert.ThrowsAsync<AnahitaException>(() => client.GetForecastAsync(AnahitaTestSupport.Berlin()));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Geocode_ParsesTheBestResult_AndBuildsTheQuery()
    {
        var handler = new HttpSyncClientTests.StubHandler(_ => StubJson(HttpStatusCode.OK, AnahitaTestSupport.GeocodeJson()));
        using var http = new HttpClient(handler);
        var client = Client(handler, Options());

        var place = await client.GeocodeAsync("Berlin");

        Assert.NotNull(place);
        Assert.Equal("Berlin", place.Name);
        Assert.Equal("Germany", place.Country);
        Assert.Equal(52.52437, place.Latitude, 5);
        Assert.Equal("Europe/Berlin", place.Timezone);
        Assert.Equal(3_664_088, place.Population);

        var url = handler.Requests[0].RequestUri!.ToString();
        Assert.Contains("name=Berlin", url, StringComparison.Ordinal);
        Assert.Contains("count=5", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Geocode_EncodesSpecialCharacters()
    {
        var handler = new HttpSyncClientTests.StubHandler(_ => StubJson(HttpStatusCode.OK, AnahitaTestSupport.GeocodeJson()));
        using var http = new HttpClient(handler);
        var client = Client(handler, Options());

        await client.GeocodeAsync("Frankfurt (Oder)");

        Assert.Contains("name=Frankfurt%20%28Oder%29", handler.Requests[0].RequestUri!.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Geocode_EmptyResults_ReturnsNull()
    {
        var handler = new HttpSyncClientTests.StubHandler(_ => StubJson(HttpStatusCode.OK, AnahitaTestSupport.GeocodeEmptyJson));
        using var http = new HttpClient(handler);
        var client = Client(handler, Options());

        Assert.Null(await client.GeocodeAsync("Nowhereville"));
    }

    [Fact]
    public async Task Geocode_404_ReturnsNull()
    {
        var handler = new HttpSyncClientTests.StubHandler(_ => StubJson(HttpStatusCode.NotFound, "{}"));
        using var http = new HttpClient(handler);
        var client = Client(handler, Options());

        Assert.Null(await client.GeocodeAsync("Nowhereville"));
    }

    [Fact]
    public async Task NetworkFailures_AreRetried_ThenSurfacedFriendly()
    {
        var handler = new HttpSyncClientTests.StubHandler(_ => throw new HttpRequestException("connection refused"));
        using var http = new HttpClient(handler);
        var client = Client(handler, Options() with { MaxRetries = 1 });

        var exception = await Assert.ThrowsAsync<AnahitaException>(() => client.GetForecastAsync(AnahitaTestSupport.Berlin()));
        Assert.Contains("Network error", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task RetryableStatus_OnTheLastAttempt_IsSurfacedWithoutRetry()
    {
        var handler = new HttpSyncClientTests.StubHandler(_ => StubJson(HttpStatusCode.BadGateway, "down"));
        using var http = new HttpClient(handler);
        var client = Client(handler, Options() with { MaxRetries = 0 });

        var exception = await Assert.ThrowsAsync<AnahitaException>(() => client.GetForecastAsync(AnahitaTestSupport.Berlin()));
        Assert.Contains("502", exception.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RetryAfter_Header_IsHonored()
    {
        var calls = 0;
        var handler = new HttpSyncClientTests.StubHandler(_ =>
        {
            calls++;
            if (calls == 1)
            {
                var retry = StubJson(HttpStatusCode.TooManyRequests, "slow down");
                retry.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(20));
                return retry;
            }

            return StubJson(HttpStatusCode.OK, AnahitaTestSupport.ForecastJson());
        });
        using var http = new HttpClient(handler);
        var client = Client(handler, Options() with { MaxRetries = 1 });

        Assert.NotNull(await client.GetForecastAsync(AnahitaTestSupport.Berlin()));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task HugeDeclaredContentLength_IsRejectedBeforeBuffering()
    {
        var handler = new HttpSyncClientTests.StubHandler(_ =>
        {
            var response = StubJson(HttpStatusCode.OK, "{\"tiny\":true}");
            response.Content.Headers.ContentLength = AnahitaDefaults.MaxResponseBytesBound + 1;
            return response;
        });
        using var http = new HttpClient(handler);
        var client = Client(handler, Options());

        var exception = await Assert.ThrowsAsync<AnahitaException>(() => client.GetForecastAsync(AnahitaTestSupport.Berlin()));
        Assert.Contains("too large", exception.Message, StringComparison.Ordinal); // thrown from the header check, before any buffering
    }

    [Fact]
    public async Task InsecureEndpoints_FailBeforeAnyCall()
    {
        var handler = new HttpSyncClientTests.StubHandler(_ => StubJson(HttpStatusCode.OK, AnahitaTestSupport.ForecastJson()));
        using var http = new HttpClient(handler);
        var client = Client(handler, Options(
            forecast: "http://weather.example.com/v1/forecast",
            geocoding: "http://geo.example.com/search"));

        await Assert.ThrowsAsync<AnahitaException>(() => client.GetForecastAsync(AnahitaTestSupport.Berlin()));
        await Assert.ThrowsAsync<AnahitaException>(() => client.GeocodeAsync("Berlin"));
        Assert.Empty(handler.Requests);
    }

    private static HttpResponseMessage StubJson(HttpStatusCode statusCode, string json) =>
        new(statusCode) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>Never answers — forces the per-attempt timeout path.</summary>
    private sealed class BlockingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new UnreachableException();
        }
    }
}
