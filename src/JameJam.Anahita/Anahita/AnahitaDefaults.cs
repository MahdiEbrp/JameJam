namespace JameJam.Anahita;

/// <summary>
/// Named defaults, safety rails, and endpoints for the Anahita weather service —
/// no magic numbers anywhere else. Named after the Persian goddess of water and wisdom.
/// </summary>
public static class AnahitaDefaults
{
    /// <summary>Human-facing service name used in help and errors.</summary>
    public const string ServiceName = "Anahita";

    // ── Endpoints (Open-Meteo, keyless) ──

    /// <summary>Default forecast endpoint (Open-Meteo-compatible; override for self-hosted mirrors).</summary>
    public const string ForecastEndpoint = "https://api.open-meteo.com/v1/forecast";

    /// <summary>Default geocoding endpoint (place name → coordinates).</summary>
    public const string GeocodingEndpoint = "https://geocoding-api.open-meteo.com/v1/search";

    /// <summary>How many geocoding candidates the service asks for (the first is used).</summary>
    public const int GeocodeResultLimit = 5;

    // ── Environment and settings ──

    /// <summary>Environment variable carrying the default location (flag and settings override it).</summary>
    public const string LocationEnvironmentVariable = "ANAHITA_LOCATION";

    /// <summary>Environment variable carrying a forecast endpoint override.</summary>
    public const string EndpointEnvironmentVariable = "ANAHITA_ENDPOINT";

    /// <summary>Environment variable carrying a geocoding endpoint override.</summary>
    public const string GeocodingEnvironmentVariable = "ANAHITA_GEOCODING_URL";

    /// <summary>
    /// Environment variable carrying an optional API key for private/proxied endpoints
    /// (sent as a bearer token; never accepted on the command line, never stored).
    /// </summary>
    public const string ApiKeyEnvironmentVariable = "ANAHITA_API_KEY";

    /// <summary>Environment variable carrying the default units (flag and settings override it).</summary>
    public const string UnitsEnvironmentVariable = "ANAHITA_UNITS";

    /// <summary>Settings key that stores the default location.</summary>
    public const string LocationSettingKey = "anahita.location";

    /// <summary>Settings key that stores the default units (metric|imperial).</summary>
    public const string UnitsSettingKey = "anahita.units";

    /// <summary>Guidance shown when no location can be resolved.</summary>
    public const string NoLocationMessage =
        "No location. Pass --at <place>, set ANAHITA_LOCATION, or save it once: JameJam weather set Berlin";

    // ── Transport rails ──

    /// <summary>Default per-attempt HTTP timeout for weather calls.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Default number of retries for transient weather failures.</summary>
    public const int MaxRetries = 2;

    /// <summary>Default base delay for exponential backoff between retries.</summary>
    public static readonly TimeSpan RetryBaseDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>Default maximum accepted response size (4 MiB).</summary>
    public const long MaxResponseBytes = 4 * 1024 * 1024;

    /// <summary>Default duration a weather report stays in the cache.</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);

    // ── Forecast shape rails ──

    /// <summary>Default number of forecast days to request.</summary>
    public const int ForecastDays = 7;

    /// <summary>Default number of hours shown by the hourly view.</summary>
    public const int HourlyWindow = 24;

    /// <summary>Days of the forecast (starting today) the alert engine watches.</summary>
    public const int AlertHorizonDays = 2;

    /// <summary>Days listed by the best-day ranking.</summary>
    public const int BestDayCount = 3;

    // ── Coordinate rails ──

    /// <summary>Absolute latitude bound (±90°).</summary>
    public const double LatitudeBound = 90.0;

    /// <summary>Absolute longitude bound (±180°).</summary>
    public const double LongitudeBound = 180.0;

    // ── Alert thresholds (customizable through <see cref="AnahitaOptions"/> where sensible) ──

    /// <summary>Daily maximum at or above which a heat alert fires (°C).</summary>
    public const double HeatCelsius = 35.0;

    /// <summary>Daily minimum at or below which a deep-freeze alert fires (°C).</summary>
    public const double ColdCelsius = -10.0;

    /// <summary>Daily minimum at or below which a frost watch fires (°C).</summary>
    public const double FrostCelsius = 0.0;

    /// <summary>Daily wind maximum at or above which a strong-wind alert fires (km/h).</summary>
    public const double StrongWindKmh = 60.0;

    /// <summary>Daily precipitation sum at or above which a heavy-rain alert fires (mm).</summary>
    public const double HeavyRainMm = 25.0;

    /// <summary>Daily UV maximum at or above which a high-UV watch fires.</summary>
    public const double HighUv = 8.0;

    /// <summary>Precipitation probability at or above which advice suggests rain gear (%).</summary>
    public const double RainAdvicePercent = 50.0;

    /// <summary>Hourly/daily precipitation total considered "rain actually fell" (mm).</summary>
    public const double RainTraceMm = 1.0;

    // ── Outdoor-day scoring ──

    /// <summary>Score of a perfect outdoor day; penalties subtract from it.</summary>
    public const int PerfectScore = 100;

    /// <summary>Penalty per degree Celsius between a day's mean temperature and comfort.</summary>
    public const int TemperaturePenaltyPerDegree = 2;

    /// <summary>Penalty applied to days with strong wind.</summary>
    public const int WindPenalty = 25;

    /// <summary>Penalty applied to days with a thunderstorm code.</summary>
    public const int ThunderPenalty = 60;

    /// <summary>Largest penalty a day's rain chance can contribute.</summary>
    public const int RainPenaltyMax = 40;

    /// <summary>Mean temperature considered most pleasant for outdoor plans (°C).</summary>
    public const double ComfortCelsius = 22.0;

    // ── AI prompt tunables (Soroush layer) ──

    /// <summary>Maximum characters of a user weather question included in AI prompts.</summary>
    public const int AiMaxQuestionChars = 400;

    /// <summary>Maximum open tasks included in the AI planning prompt.</summary>
    public const int AiMaxTaskCount = 30;

    /// <summary>Maximum hourly lines included in the AI weather context.</summary>
    public const int AiHourlyLines = 12;

    /// <summary>Maximum characters of a task title inside AI prompts.</summary>
    public const int AiTaskTitleChars = 80;

    // ── Safety rails (bounds) ──

    /// <summary>Upper rail for the AI question length.</summary>
    public const int AiMaxQuestionCharsBound = 2000;

    /// <summary>Upper rail for the AI task count.</summary>
    public const int AiMaxTaskCountBound = 100;

    /// <summary>Upper rail for AI hourly lines.</summary>
    public const int AiHourlyLinesBound = 48;

    /// <summary>Upper rail for task titles inside AI prompts.</summary>
    public const int AiTaskTitleCharsBound = 200;

    /// <summary>Upper rail for the request timeout.</summary>
    public static readonly TimeSpan RequestTimeoutBound = TimeSpan.FromMinutes(10);

    /// <summary>Upper rail for retries.</summary>
    public const int MaxRetriesBound = 10;

    /// <summary>Upper rail for the response size cap (64 MiB).</summary>
    public const long MaxResponseBytesBound = 64L * 1024 * 1024;

    /// <summary>Upper rail for the cache TTL.</summary>
    public static readonly TimeSpan CacheTtlBound = TimeSpan.FromHours(24);

    /// <summary>Upper rail for forecast days (Open-Meteo serves at most 16).</summary>
    public const int ForecastDaysBound = 16;

    /// <summary>Upper rail for the hourly window (the API serves 48 hours usefully).</summary>
    public const int HourlyWindowBound = 48;
}
