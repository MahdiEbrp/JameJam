using System.Globalization;

namespace JameJam.Anahita;

/// <summary>Configuration for the Anahita weather service. Immutable — use <c>with</c> to modify.</summary>
public sealed record AnahitaOptions
{
    /// <summary>Forecast endpoint (Open-Meteo-compatible). HTTPS required; plain HTTP only on loopback.</summary>
    public string ForecastEndpoint { get; init; } = AnahitaDefaults.ForecastEndpoint;

    /// <summary>Geocoding endpoint (place name → coordinates). Same HTTPS/loopback policy.</summary>
    public string GeocodingEndpoint { get; init; } = AnahitaDefaults.GeocodingEndpoint;

    /// <summary>
    /// Optional API key for private/proxied endpoints. From the environment only —
    /// never the command line, never stored, scrubbed from every error.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>Per-attempt HTTP timeout.</summary>
    public TimeSpan RequestTimeout { get; init; } = AnahitaDefaults.RequestTimeout;

    /// <summary>Retries for transient failures (408/429/5xx, network errors, timeouts).</summary>
    public int MaxRetries { get; init; } = AnahitaDefaults.MaxRetries;

    /// <summary>Base delay for exponential backoff (honors Retry-After).</summary>
    public TimeSpan RetryBaseDelay { get; init; } = AnahitaDefaults.RetryBaseDelay;

    /// <summary>Maximum accepted response size — larger responses are rejected before buffering.</summary>
    public long MaxResponseBytes { get; init; } = AnahitaDefaults.MaxResponseBytes;

    /// <summary>How long a downloaded report stays valid in the cache.</summary>
    public TimeSpan CacheTtl { get; init; } = AnahitaDefaults.CacheTtl;

    /// <summary>How many days of daily forecast to request (1–16).</summary>
    public int ForecastDays { get; init; } = AnahitaDefaults.ForecastDays;

    /// <summary>How many hours the hourly view shows by default (1–48).</summary>
    public int HourlyWindow { get; init; } = AnahitaDefaults.HourlyWindow;

    /// <summary>Maximum characters of a user weather question included in AI prompts.</summary>
    public int AiMaxQuestionChars { get; init; } = AnahitaDefaults.AiMaxQuestionChars;

    /// <summary>Maximum open tasks included in the AI planning prompt.</summary>
    public int AiMaxTaskCount { get; init; } = AnahitaDefaults.AiMaxTaskCount;

    /// <summary>Maximum hourly lines included in the AI weather context.</summary>
    public int AiHourlyLines { get; init; } = AnahitaDefaults.AiHourlyLines;

    /// <summary>
    /// Builds options from the environment (endpoint, geocoding, and API-key overrides),
    /// falling back to the named defaults for anything unset.
    /// </summary>
    public static AnahitaOptions FromEnvironment() => new()
    {
        ForecastEndpoint = OrDefault(
            Environment.GetEnvironmentVariable(AnahitaDefaults.EndpointEnvironmentVariable),
            AnahitaDefaults.ForecastEndpoint),
        GeocodingEndpoint = OrDefault(
            Environment.GetEnvironmentVariable(AnahitaDefaults.GeocodingEnvironmentVariable),
            AnahitaDefaults.GeocodingEndpoint),
        ApiKey = Environment.GetEnvironmentVariable(AnahitaDefaults.ApiKeyEnvironmentVariable),
    };

    /// <summary>
    /// Validates every value against its named rail, and both endpoints against the HTTPS/loopback policy.
    /// </summary>
    /// <exception cref="AnahitaException">Any value outside its rail, or an insecure endpoint.</exception>
    public void Validate()
    {
        ValidateEndpoint(ForecastEndpoint, "forecast");
        ValidateEndpoint(GeocodingEndpoint, "geocoding");

        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > AnahitaDefaults.RequestTimeoutBound)
        {
            throw new AnahitaException(
                $"RequestTimeout must be positive and at most {AnahitaDefaults.RequestTimeoutBound.TotalMinutes:0} minutes.");
        }

        if (MaxRetries is < 0 or > AnahitaDefaults.MaxRetriesBound)
        {
            throw new AnahitaException(
                $"MaxRetries must be between 0 and {AnahitaDefaults.MaxRetriesBound}.");
        }

        if (RetryBaseDelay < TimeSpan.Zero)
            throw new AnahitaException("RetryBaseDelay must not be negative.");

        if (MaxResponseBytes is < 1 or > AnahitaDefaults.MaxResponseBytesBound)
        {
            throw new AnahitaException(
                $"MaxResponseBytes must be between 1 and {AnahitaDefaults.MaxResponseBytesBound}.");
        }

        if (CacheTtl < TimeSpan.Zero || CacheTtl > AnahitaDefaults.CacheTtlBound)
        {
            throw new AnahitaException(
                $"CacheTtl must be between zero and {AnahitaDefaults.CacheTtlBound.TotalHours:0} hours.");
        }

        if (ForecastDays is < 1 or > AnahitaDefaults.ForecastDaysBound)
        {
            throw new AnahitaException(
                $"ForecastDays must be between 1 and {AnahitaDefaults.ForecastDaysBound}.");
        }

        if (HourlyWindow is < 1 or > AnahitaDefaults.HourlyWindowBound)
        {
            throw new AnahitaException(
                $"HourlyWindow must be between 1 and {AnahitaDefaults.HourlyWindowBound}.");
        }

        if (AiMaxQuestionChars is < 1 or > AnahitaDefaults.AiMaxQuestionCharsBound)
        {
            throw new AnahitaException(
                $"AiMaxQuestionChars must be between 1 and {AnahitaDefaults.AiMaxQuestionCharsBound}.");
        }

        if (AiMaxTaskCount is < 1 or > AnahitaDefaults.AiMaxTaskCountBound)
        {
            throw new AnahitaException(
                $"AiMaxTaskCount must be between 1 and {AnahitaDefaults.AiMaxTaskCountBound}.");
        }

        if (AiHourlyLines is < 1 or > AnahitaDefaults.AiHourlyLinesBound)
        {
            throw new AnahitaException(
                $"AiHourlyLines must be between 1 and {AnahitaDefaults.AiHourlyLinesBound}.");
        }
    }

    private static void ValidateEndpoint(string endpoint, string kind)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new AnahitaException($"The {kind} endpoint must not be empty.");

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            throw new AnahitaException($"Invalid {kind} endpoint '{endpoint}'.");

        if (uri.Query.Length > 0)
        {
            throw new AnahitaException(
                $"The {kind} endpoint must be a base URL without a query string ('{endpoint}').");
        }

        var secureEnough = uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
        if (!secureEnough)
        {
            throw new AnahitaException(
                $"Insecure {kind} endpoint '{endpoint}'. Use HTTPS (plain HTTP is only allowed on loopback).");
        }
    }

    private static string OrDefault(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;
}
