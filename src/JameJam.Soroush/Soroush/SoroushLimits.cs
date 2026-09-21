namespace JameJam.Soroush;

/// <summary>
/// Central, named safety rails for Soroush configuration. These bound what can be customized;
/// everything inside the rails is a settable option on <see cref="SoroushOptions"/> (no magic numbers).
/// </summary>
public static class SoroushLimits
{
    /// <summary>Default maximum completion tokens to request.</summary>
    public const int DefaultMaxTokens = 512;

    /// <summary>Default per-attempt request timeout.</summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Default number of transient-failure retries.</summary>
    public const int DefaultMaxRetries = 2;

    /// <summary>Default base delay for exponential backoff.</summary>
    public static readonly TimeSpan DefaultRetryBaseDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>Default maximum allowed prompt length.</summary>
    public const int DefaultMaxPromptLength = 8_000;

    /// <summary>Default maximum provider error-body length echoed in exceptions.</summary>
    public const int DefaultMaxErrorBodyLength = 500;

    /// <summary>Default retry-backoff jitter fraction (50%–150% of the computed delay).</summary>
    public const double DefaultJitterScale = 0.5;

    // ── Safety rails (the bounds of configuration itself) ──

    /// <summary>Upper rail for MaxTokens.</summary>
    public const int MaxTokensBound = 100_000;

    /// <summary>Upper rail for RequestTimeout.</summary>
    public static readonly TimeSpan RequestTimeoutBound = TimeSpan.FromMinutes(10);

    /// <summary>Upper rail for MaxRetries.</summary>
    public const int MaxRetriesBound = 10;

    /// <summary>Lower rail for MaxErrorBodyLength.</summary>
    public const int MinErrorBodyLength = 50;

    /// <summary>Upper rail for MaxErrorBodyLength.</summary>
    public const int MaxErrorBodyLengthBound = 100_000;

    /// <summary>Default number of secret characters kept visible when redacting.</summary>
    public const int RedactDefaultSuffixLength = 4;

    /// <summary>Upper rail for the redaction suffix length.</summary>
    public const int RedactSuffixLengthBound = 64;
}
