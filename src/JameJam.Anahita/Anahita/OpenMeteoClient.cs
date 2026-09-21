using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using JameJam.Soroush;

namespace JameJam.Anahita;

/// <summary>
/// Hardened HTTP transport for the Anahita weather service (Open-Meteo-compatible):
/// HTTPS/loopback policy, optional bearer key (environment only), per-attempt timeouts,
/// retries with jittered backoff honoring Retry-After, a hard response-size cap, and
/// key scrubbing from every error body. Redirects are disabled for the same reason as
/// Soroush and sync: a validated endpoint must not be silently moved elsewhere.
/// </summary>
/// <param name="http">The HTTP pipeline to use. Injected for testability.</param>
/// <param name="options">Validated weather options.</param>
public sealed class OpenMeteoClient(HttpClient http, AnahitaOptions options) : IAnahitaClient
{
    private const string TruncationSuffix = "…";
    private const int MaxErrorBodyCharacters = 500;
    private const string LocalTimestampFormat = "yyyy-MM-ddTHH:mm";
    private const double JitterScale = 0.5;
    private const int ReadChunkBytes = 64 * 1024;

    private static readonly HashSet<HttpStatusCode> RetryableStatusCodes = new()
    {
        HttpStatusCode.RequestTimeout,        // 408
        HttpStatusCode.TooManyRequests,       // 429
        HttpStatusCode.InternalServerError,   // 500
        HttpStatusCode.BadGateway,            // 502
        HttpStatusCode.ServiceUnavailable,    // 503
        HttpStatusCode.GatewayTimeout,        // 504
    };

    private static readonly string[] CurrentVariables =
    [
        "temperature_2m", "apparent_temperature", "relative_humidity_2m",
        "precipitation", "weather_code", "wind_speed_10m", "wind_direction_10m",
    ];

    private static readonly string[] HourlyVariables =
    [
        "temperature_2m", "apparent_temperature", "relative_humidity_2m",
        "precipitation_probability", "precipitation", "weather_code", "wind_speed_10m",
    ];

    private static readonly string[] DailyVariables =
    [
        "weather_code", "temperature_2m_max", "temperature_2m_min", "precipitation_sum",
        "precipitation_probability_max", "wind_speed_10m_max", "uv_index_max", "sunrise", "sunset",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly AnahitaOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    public async Task<GeoPlace?> GeocodeAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        options.Validate();
        var endpoint = new Uri(BuildGeocodingUrl(name));

        using var response = await SendWithRetriesAsync(() => BuildRequest(HttpMethod.Get, endpoint), cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        EnsureSuccess(response);
        var json = await ReadCappedAsync(response, cancellationToken).ConfigureAwait(false);
        var dto = Parse<GeocodingDto>(json, "geocoding");
        var first = dto.Results is { Count: > 0 } ? dto.Results[0] : null;
        return first is null ? null : ToPlace(first);
    }

    /// <inheritdoc />
    public async Task<WeatherReport> GetForecastAsync(GeoPlace place, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(place);
        options.Validate();
        var endpoint = new Uri(BuildForecastUrl(place));

        using var response = await SendWithRetriesAsync(() => BuildRequest(HttpMethod.Get, endpoint), cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(response);

        var json = await ReadCappedAsync(response, cancellationToken).ConfigureAwait(false);
        var dto = Parse<ForecastDto>(json, "weather");
        return ToReport(place, dto);
    }

    private static GeoPlace ToPlace(GeocodingResultDto dto) => new(
        dto.Name ?? string.Empty,
        dto.Country ?? string.Empty,
        dto.Latitude,
        dto.Longitude,
        string.IsNullOrWhiteSpace(dto.Timezone) ? "auto" : dto.Timezone)
    {
        Admin1 = dto.Admin1,
        Population = dto.Population ?? 0,
    };

    private static WeatherReport ToReport(GeoPlace place, ForecastDto dto)
    {
        if (dto.Current is null)
        {
            throw new AnahitaException(
                "The weather endpoint returned no current conditions (invalid payload).");
        }

        var timezone = string.IsNullOrWhiteSpace(dto.Timezone) ? place.Timezone : dto.Timezone;
        var resolvedPlace = place with { Timezone = timezone };
        return new WeatherReport(
            resolvedPlace,
            DateTimeOffset.UtcNow,
            ToCurrent(dto.Current),
            ToHourly(dto.Hourly),
            ToDaily(dto.Daily));
    }

    private static CurrentConditions ToCurrent(CurrentDto dto) => new(
        ParseLocal(dto.Time, "current time"),
        dto.Temperature ?? 0,
        dto.Apparent ?? dto.Temperature ?? 0,
        ToInt(dto.Humidity),
        dto.Precipitation ?? 0,
        ToInt(dto.Code),
        dto.Wind ?? 0,
        ToInt(dto.WindDirection));

    private static List<HourlyPoint> ToHourly(HourlyDto? dto)
    {
        if (dto?.Time is not { Count: > 0 } times)
            return [];

        EnsureConsistent(times.Count, dto.Temperature, "hourly temperature");
        EnsureConsistent(times.Count, dto.Apparent, "hourly feels-like");
        EnsureConsistent(times.Count, dto.Humidity, "hourly humidity");
        EnsureConsistent(times.Count, dto.PrecipProbability, "hourly rain chance");
        EnsureConsistent(times.Count, dto.Precipitation, "hourly precipitation");
        EnsureConsistent(times.Count, dto.Code, "hourly weather code");
        EnsureConsistent(times.Count, dto.Wind, "hourly wind");

        var points = new List<HourlyPoint>(times.Count);
        for (var i = 0; i < times.Count; i++)
        {
            points.Add(new HourlyPoint(
                ParseLocal(times[i], "hourly time"),
                dto.Temperature![i] ?? 0,
                dto.Apparent![i] ?? dto.Temperature[i] ?? 0,
                dto.PrecipProbability![i],
                dto.Precipitation![i] ?? 0,
                ToInt(dto.Code![i]),
                dto.Wind![i] ?? 0,
                ToInt(dto.Humidity![i])));
        }

        return points;
    }

    private static List<DailyPoint> ToDaily(DailyDto? dto)
    {
        if (dto?.Time is not { Count: > 0 } dates)
            return [];

        EnsureConsistent(dates.Count, dto.Code, "daily weather code");
        EnsureConsistent(dates.Count, dto.Max, "daily high");
        EnsureConsistent(dates.Count, dto.Min, "daily low");
        EnsureConsistent(dates.Count, dto.PrecipSum, "daily precipitation");
        EnsureConsistent(dates.Count, dto.PrecipProbability, "daily rain chance");
        EnsureConsistent(dates.Count, dto.Wind, "daily wind");
        EnsureConsistent(dates.Count, dto.Uv, "daily UV");
        EnsureConsistent(dates.Count, dto.Sunrise, "daily sunrise");
        EnsureConsistent(dates.Count, dto.Sunset, "daily sunset");

        var points = new List<DailyPoint>(dates.Count);
        for (var i = 0; i < dates.Count; i++)
        {
            points.Add(new DailyPoint(
                ParseDate(dates[i]),
                ToInt(dto.Code![i]),
                dto.Min![i] ?? 0,
                dto.Max![i] ?? 0,
                dto.PrecipProbability![i],
                dto.PrecipSum![i] ?? 0,
                dto.Wind![i] ?? 0,
                dto.Uv![i],
                ParseTimeOfDay(dto.Sunrise![i]),
                ParseTimeOfDay(dto.Sunset![i])));
        }

        return points;
    }

    private static void EnsureConsistent<T>(int expected, List<T>? actual, string what)
    {
        if (actual is null || actual.Count != expected)
        {
            throw new AnahitaException(
                $"The weather endpoint returned inconsistent data ({what} is missing or misaligned).");
        }
    }

    private static int ToInt(double? value) => (int)Math.Round(value ?? 0, MidpointRounding.AwayFromZero);

    private static DateTime ParseLocal(string? value, string what)
    {
        if (value is not null
            && DateTime.TryParseExact(
                value, LocalTimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }

        throw new AnahitaException($"The weather endpoint returned an unreadable {what} ('{value}').");
    }

    private static DateOnly ParseDate(string? value)
    {
        if (value is not null
            && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }

        throw new AnahitaException($"The weather endpoint returned an unreadable date ('{value}').");
    }

    private static TimeOnly? ParseTimeOfDay(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        var local = ParseLocal(value, "sun time");
        return TimeOnly.FromDateTime(local);
    }

    private static T Parse<T>(string json, string kind)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions)
                ?? throw new AnahitaException($"The {kind} endpoint returned an empty payload.");
        }
        catch (JsonException ex)
        {
            throw new AnahitaException(
                $"The {kind} endpoint did not return a valid Anahita weather response.", innerException: ex);
        }
    }

    private string BuildForecastUrl(GeoPlace place)
    {
        var current = string.Join(',', CurrentVariables);
        var hourly = string.Join(',', HourlyVariables);
        var daily = string.Join(',', DailyVariables);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{_options.ForecastEndpoint}?latitude={place.Latitude:0.####}&longitude={place.Longitude:0.####}"
            + $"&current={current}&hourly={hourly}&daily={daily}"
            + $"&timezone=auto&forecast_days={_options.ForecastDays}");
    }

    private string BuildGeocodingUrl(string name) => string.Create(
        CultureInfo.InvariantCulture,
        $"{_options.GeocodingEndpoint}?name={Uri.EscapeDataString(name)}"
        + $"&count={AnahitaDefaults.GeocodeResultLimit}&language=en&format=json");

    private HttpRequestMessage BuildRequest(HttpMethod method, Uri endpoint)
    {
        var request = new HttpRequestMessage(method, endpoint);
        if (!string.IsNullOrEmpty(_options.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        return request;
    }

    private async Task<HttpResponseMessage> SendWithRetriesAsync(
        Func<HttpRequestMessage> requestFactory, CancellationToken cancellationToken)
    {
        var maxAttempts = _options.MaxRetries + 1;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // A fresh request per attempt — HttpRequestMessage cannot be sent twice.
                using var request = requestFactory();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_options.RequestTimeout);
                var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);

                if (response.IsSuccessStatusCode
                    || !RetryableStatusCodes.Contains(response.StatusCode)
                    || attempt >= maxAttempts)
                {
                    return response;
                }

                using (response)
                {
                    await DelayBeforeRetryAsync(response.Headers.RetryAfter?.Delta, attempt, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt >= maxAttempts)
                {
                    throw new AnahitaException(
                        $"Weather request timed out after {attempt} attempt(s) without success.");
                }

                await DelayBeforeRetryAsync(null, attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < maxAttempts)
            {
                await DelayBeforeRetryAsync(null, attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new AnahitaException($"Network error talking to the weather endpoint: {ex.Message}", innerException: ex);
            }
        }
    }

    private async Task<string> ReadCappedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var budget = _options.MaxResponseBytes + 1;
        var content = response.Content;
        if (content.Headers.ContentLength is > AnahitaDefaults.MaxResponseBytesBound)
            throw new AnahitaException("The weather response is too large.");

        var buffer = new StringBuilder();
        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using (stream)
        {
            var chunk = new byte[ReadChunkBytes];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                budget -= read;
                if (budget < 0)
                {
                    throw new AnahitaException(
                        $"The weather response exceeds the configured maximum of {_options.MaxResponseBytes} bytes.");
                }

                _ = buffer.Append(Encoding.UTF8.GetString(chunk, 0, read));
            }
        }

        return buffer.ToString();
    }

    private void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = response.Content.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (body.Length > MaxErrorBodyCharacters)
            body = body[..MaxErrorBodyCharacters] + TruncationSuffix;

        body = SoroushGuard.RedactIn(body, _options.ApiKey);
        throw new AnahitaException(
            $"Weather endpoint returned {(int)response.StatusCode} ({response.StatusCode}). Body: {body}",
            (int)response.StatusCode);
    }

    private async Task DelayBeforeRetryAsync(TimeSpan? retryAfter, int attempt, CancellationToken cancellationToken)
    {
        var backoff = _options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        var jittered = backoff * (1 - JitterScale + (2 * JitterScale * Random.Shared.NextDouble()));
        var delay = retryAfter ?? TimeSpan.FromMilliseconds(jittered);

        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }
}
