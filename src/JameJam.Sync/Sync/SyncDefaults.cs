namespace JameJam.Sync;

/// <summary>Named defaults for remote sync — no magic numbers anywhere else.</summary>
public static class SyncDefaults
{
    /// <summary>Default per-attempt HTTP timeout for sync calls.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Default number of retries for transient sync failures.</summary>
    public const int MaxRetries = 2;

    /// <summary>Default base delay for exponential backoff between sync retries.</summary>
    public static readonly TimeSpan RetryBaseDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>Default maximum accepted sync response size (8 MiB).</summary>
    public const long MaxResponseBytes = 8 * 1024 * 1024;

    /// <summary>Default maximum sealed payload size (8 MiB).</summary>
    public const long MaxPayloadBytes = 8 * 1024 * 1024;

    /// <summary>Environment variable carrying the optional sync bearer token (never the CLI).</summary>
    public const string TokenEnvironmentVariable = "JAMEJAM_SYNC_TOKEN";

    /// <summary>Environment variable carrying the sync URL (flag and settings override it).</summary>
    public const string UrlEnvironmentVariable = "JAMEJAM_SYNC_URL";

    /// <summary>Settings key that stores a default sync URL.</summary>
    public const string UrlSettingKey = "haftkhan.syncUrl";

    // ── Safety rails ──

    /// <summary>Upper rail for the request timeout.</summary>
    public static readonly TimeSpan RequestTimeoutBound = TimeSpan.FromMinutes(10);

    /// <summary>Upper rail for retries.</summary>
    public const int MaxRetriesBound = 10;

    /// <summary>Upper rail for the response size cap (64 MiB).</summary>
    public const long MaxResponseBytesBound = 64L * 1024 * 1024;

    /// <summary>Upper rail for the sealed payload size (64 MiB).</summary>
    public const long MaxPayloadBytesBound = 64L * 1024 * 1024;
}
