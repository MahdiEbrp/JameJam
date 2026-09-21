using System.Net;
using JameJam.Soroush.Providers;

namespace JameJam.Soroush;

/// <summary>Configuration for talking to an AI provider. Immutable — use <c>with</c> to modify.</summary>
public sealed record SoroushOptions
{
    private static readonly IReadOnlySet<HttpStatusCode> DefaultRetryableStatusCodes =
        new HashSet<HttpStatusCode>
        {
            HttpStatusCode.RequestTimeout,        // 408
            HttpStatusCode.TooManyRequests,       // 429
            HttpStatusCode.InternalServerError,   // 500
            HttpStatusCode.BadGateway,            // 502
            HttpStatusCode.ServiceUnavailable,    // 503
            HttpStatusCode.GatewayTimeout,        // 504
        };

    /// <summary>Provider name. See <see cref="SoroushProviders"/>. Default: OpenAI-compatible.</summary>
    public string Provider { get; init; } = SoroushProviders.OpenAiCompatible;

    /// <summary>API endpoint. HTTPS required; plain HTTP allowed only on loopback (local dev servers).</summary>
    public string Endpoint { get; init; } = SoroushDefaults.OpenAiCompatibleEndpoint;

    /// <summary>API key. Treated as a secret: never logged, never echoed, never accepted on the CLI.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Model identifier. Falls back to the provider default when null.</summary>
    public string? Model { get; init; }

    /// <summary>Maximum completion tokens to request.</summary>
    public int MaxTokens { get; init; } = SoroushLimits.DefaultMaxTokens;

    /// <summary>Per-attempt request timeout.</summary>
    public TimeSpan RequestTimeout { get; init; } = SoroushLimits.DefaultRequestTimeout;

    /// <summary>How many times to retry transient failures (retryable statuses, network errors, timeouts).</summary>
    public int MaxRetries { get; init; } = SoroushLimits.DefaultMaxRetries;

    /// <summary>Base delay for exponential backoff between retries. Honors Retry-After when present.</summary>
    public TimeSpan RetryBaseDelay { get; init; } = SoroushLimits.DefaultRetryBaseDelay;

    /// <summary>Maximum allowed prompt length (safety layer).</summary>
    public int MaxPromptLength { get; init; } = SoroushLimits.DefaultMaxPromptLength;

    /// <summary>Maximum provider error-body length echoed in exception messages (secrets are scrubbed first).</summary>
    public int MaxErrorBodyLength { get; init; } = SoroushLimits.DefaultMaxErrorBodyLength;

    /// <summary>
    /// Fraction of the computed backoff used as jitter: 0 = deterministic delays,
    /// 0.5 (default) = 50%–150% of the computed delay. Spreads out retry storms.
    /// </summary>
    public double JitterScale { get; init; } = SoroushLimits.DefaultJitterScale;

    /// <summary>HTTP status codes considered transient. Customize for proxies or provider-specific behavior.</summary>
    public IReadOnlySet<HttpStatusCode> RetryableStatusCodes { get; init; } = DefaultRetryableStatusCodes;
}
