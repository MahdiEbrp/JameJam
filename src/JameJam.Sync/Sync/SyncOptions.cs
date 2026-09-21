using JameJam.Soroush;

namespace JameJam.Sync;

/// <summary>Configuration for the remote sync endpoint. Immutable — use <c>with</c> to modify.</summary>
public sealed record SyncOptions
{
    /// <summary>Sync endpoint (custom URL). HTTPS required; plain HTTP only on loopback.</summary>
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>Optional bearer token. From the environment only — never the command line, never stored.</summary>
    public string? BearerToken { get; init; }

    /// <summary>Per-attempt HTTP timeout.</summary>
    public TimeSpan RequestTimeout { get; init; } = SyncDefaults.RequestTimeout;

    /// <summary>Retries for transient failures (408/429/5xx, network errors, timeouts).</summary>
    public int MaxRetries { get; init; } = SyncDefaults.MaxRetries;

    /// <summary>Base delay for exponential backoff (honors Retry-After).</summary>
    public TimeSpan RetryBaseDelay { get; init; } = SyncDefaults.RetryBaseDelay;

    /// <summary>Maximum accepted response size — larger responses are rejected before buffering.</summary>
    public long MaxResponseBytes { get; init; } = SyncDefaults.MaxResponseBytes;

    /// <summary>Validates every value against its named rail, and the endpoint against the HTTPS/loopback policy.</summary>
    /// <exception cref="SyncException">Any value outside its rail, or an insecure endpoint.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Endpoint))
            throw new SyncException("Sync endpoint must not be empty.");

        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri))
            throw new SyncException($"Invalid sync endpoint '{Endpoint}'.");

        var secureEnough = uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
        if (!secureEnough)
        {
            throw new SyncException(
                $"Insecure sync endpoint '{Endpoint}'. Use HTTPS (plain HTTP is only allowed on loopback).");
        }

        if (BearerToken is not null && BearerToken.Length == 0)
            throw new SyncException("Bearer token must not be empty when provided.");

        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > SyncDefaults.RequestTimeoutBound)
            throw new SyncException(
                $"RequestTimeout must be positive and at most {SyncDefaults.RequestTimeoutBound.TotalMinutes:0} minutes.");

        if (MaxRetries is < 0 or > SyncDefaults.MaxRetriesBound)
            throw new SyncException($"MaxRetries must be between 0 and {SyncDefaults.MaxRetriesBound}.");

        if (RetryBaseDelay < TimeSpan.Zero)
            throw new SyncException("RetryBaseDelay must not be negative.");

        if (MaxResponseBytes is < 1 or > SyncDefaults.MaxResponseBytesBound)
            throw new SyncException($"MaxResponseBytes must be between 1 and {SyncDefaults.MaxResponseBytesBound}.");
    }
}
