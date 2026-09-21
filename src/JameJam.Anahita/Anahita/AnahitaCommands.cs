using System.Globalization;

using JameJam.Anahita.Ai;
using JameJam.HaftKhan;
using JameJam.HaftKhan.Ai;
using JameJam.Soroush;

namespace JameJam.Anahita;

/// <summary>
/// CLI surface for the Anahita weather service: current conditions, forecasts, hourly
/// detail, alerts, best-day ranking, and the weather-for-your-plans view that pairs
/// the forecast with open Haft Khan tasks. Argument parsing and output only — all
/// logic lives in <see cref="AnahitaService"/>, <see cref="AnahitaInsights"/>, and
/// <see cref="AnahitaFormat"/>.
/// </summary>
/// <param name="output">Standard output.</param>
/// <param name="error">Error output.</param>
/// <param name="clock">Time source for the report cache.</param>
/// <param name="locationProvider">Resolves the stored default location (usually the settings store).</param>
/// <param name="clientFactory">Creates the weather transport; defaults to the hardened HTTP client.</param>
/// <param name="dueProvider">Resolves open dated Haft Khan tasks for the plan view; null disables it.</param>
/// <param name="unitsProvider">Resolves the stored default units (usually the settings store).</param>
/// <param name="locationSaver">Persists the default location (usually the settings store).</param>
/// <param name="options">Customizable weather options; environment-driven defaults apply when null.</param>
/// <param name="aiCompletion">Routes a prompt through the Soroush safety layer; null when AI is unavailable.</param>
/// <param name="openTasksProvider">Resolves open Haft Khan tasks for the AI planning view; null disables it.</param>
public sealed class AnahitaCommands(
    TextWriter output,
    TextWriter error,
    TimeProvider clock,
    Func<string?>? locationProvider = null,
    Func<AnahitaOptions, IAnahitaClient>? clientFactory = null,
    Func<IReadOnlyList<HaftKhanTask>>? dueProvider = null,
    Func<string?>? unitsProvider = null,
    Action<string>? locationSaver = null,
    AnahitaOptions? options = null,
    Func<AiRequest, CancellationToken, Task<SoroushResult>>? aiCompletion = null,
    Func<IReadOnlyList<HaftKhanTask>>? openTasksProvider = null)
{
    private readonly TextWriter _output = output ?? throw new ArgumentNullException(nameof(output));
    private readonly TextWriter _error = error ?? throw new ArgumentNullException(nameof(error));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly Func<string?>? _locationProvider = locationProvider;
    private readonly Func<AnahitaOptions, IAnahitaClient> _clientFactory =
        clientFactory ?? (o => new OpenMeteoClient(SoroushHttp.CreateClient(), o));
    private readonly Func<IReadOnlyList<HaftKhanTask>>? _dueProvider = dueProvider;
    private readonly Func<string?>? _unitsProvider = unitsProvider;
    private readonly Action<string>? _locationSaver = locationSaver;
    private readonly AnahitaOptions _options = options ?? AnahitaOptions.FromEnvironment();
    private readonly Func<AiRequest, CancellationToken, Task<SoroushResult>>? _aiCompletion = aiCompletion;
    private readonly Func<IReadOnlyList<HaftKhanTask>>? _openTasksProvider = openTasksProvider;

    /// <summary>The validated options in effect.</summary>
    public AnahitaOptions Options => _options;

    /// <summary>Runs a <c>weather</c> subcommand. Returns the process exit code.</summary>
    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        _options.Validate();

        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            Help();
            return 0;
        }

        try
        {
            // Flags without a subcommand configure the default command (now).
            if (args[0].StartsWith('-'))
            {
                return await RunNowAsync(args, cancellationToken).ConfigureAwait(false);
            }

            return args[0] switch
            {
                "now" => await RunNowAsync(args[1..], cancellationToken).ConfigureAwait(false),
                "forecast" => await RunForecastAsync(args[1..], cancellationToken).ConfigureAwait(false),
                "hourly" => await RunHourlyAsync(args[1..], cancellationToken).ConfigureAwait(false),
                "alerts" => await RunAlertsAsync(args[1..], cancellationToken).ConfigureAwait(false),
                "best" => await RunBestAsync(args[1..], cancellationToken).ConfigureAwait(false),
                "plan" => await RunPlanAsync(args[1..], cancellationToken).ConfigureAwait(false),
                "ai" => await RunAiAsync(args[1..], cancellationToken).ConfigureAwait(false),
                "set" => RunSet(args[1..]),
                _ => Fail($"Unknown weather command '{args[0]}'. Run 'JameJam weather help'."),
            };
        }
        catch (AnahitaException ex)
        {
            return Fail(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Fail(ex.Message);
        }
        catch (SoroushException ex)
        {
            return Fail(ex.Message);
        }
    }

    private async Task<int> RunAiAsync(string[] args, CancellationToken cancellationToken)
    {
        if (_aiCompletion is null)
            return Fail("AI is not available in this context.");

        var assistant = new WeatherAssistant(_options);
        switch (args.Length > 0 ? args[0] : "help")
        {
            case "explain":
            {
                var request = ParseRequest(args[1..]);
                var report = await NewService(_options).CurrentAsync(request.Place, cancellationToken).ConfigureAwait(false);
                var result = await _aiCompletion(
                    new AiRequest(assistant.BuildExplainPrompt(report)), cancellationToken).ConfigureAwait(false);
                _output.WriteLine(result.Content);
                return 0;
            }

            case "ask":
            {
                var (question, request) = ParseQuestion(args[1..]);
                if (question.Length == 0)
                {
                    return Fail("Ask a question: JameJam weather ai ask \"should I bike tomorrow?\"");
                }

                var report = await NewService(_options).CurrentAsync(request.Place, cancellationToken).ConfigureAwait(false);
                var result = await _aiCompletion(
                    new AiRequest(assistant.BuildAskPrompt(question, report)), cancellationToken).ConfigureAwait(false);
                _output.WriteLine(result.Content);
                return 0;
            }

            case "plan":
            {
                if (_openTasksProvider is null)
                {
                    return Fail("The AI planning view needs the Haft Khan task list (storage unavailable in this context).");
                }

                var request = ParseRequest(args[1..]);
                var report = await NewService(_options).CurrentAsync(request.Place, cancellationToken).ConfigureAwait(false);
                var tasks = _openTasksProvider.Invoke();
                if (tasks.Count == 0)
                {
                    _output.WriteLine("Nothing open — enjoy the peace.");
                    return 0;
                }

                var result = await _aiCompletion(
                    new AiRequest(assistant.BuildPlanPrompt(tasks, report)), cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(result.Content))
                {
                    return Fail("The AI returned an empty response.");
                }

                _output.WriteLine("Suggested schedule:");
                foreach (var (step, index) in AiTaskAssistant.ParsePlan(result.Content).Steps.Select((s, i) => (s, i)))
                {
                    _output.WriteLine(FormattableString.Invariant($"  {index + 1}. {step}"));
                }

                return 0;
            }

            default:
                _error.WriteLine("Usage: JameJam weather ai explain | ai ask <question...> | ai plan");
                return 1;
        }
    }

    /// <summary>
    /// Splits <c>ai ask</c> arguments into the free-form question and the known location
    /// flags; unknown flags are rejected so typos never silently change the question.
    /// </summary>
    private (string Question, ParsedRequest Request) ParseQuestion(string[] args)
    {
        string? place = null;
        string? unitsText = null;
        List<string> parts = [];

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--at" when i + 1 < args.Length:
                    place = args[++i];
                    break;
                case "--units" when i + 1 < args.Length:
                    unitsText = args[++i];
                    break;
                case var flag when flag.StartsWith('-'):
                    throw new AnahitaException($"Unknown weather option '{flag}'. Run 'JameJam weather help'.");
                default:
                    parts.Add(args[i]);
                    break;
            }
        }

        _ = ParseUnits(unitsText); // validate even though the AI answers in plain language
        return (string.Join(' ', parts), new ParsedRequest(place, WeatherUnits.Metric, null, null));
    }

    private async Task<int> RunNowAsync(string[] args, CancellationToken cancellationToken)
    {
        var request = ParseRequest(args);
        var report = await NewService(_options).CurrentAsync(request.Place, cancellationToken).ConfigureAwait(false);

        _output.WriteLine(AnahitaFormat.FormatNow(report, request.Units));
        WriteOptionalBlock(AnahitaFormat.FormatAlerts(AnahitaInsights.FindAlerts(report, request.Units)));
        return 0;
    }

    private async Task<int> RunForecastAsync(string[] args, CancellationToken cancellationToken)
    {
        var request = ParseRequest(args);
        var effective = _options;
        if (request.Days is not null)
        {
            effective = effective with { ForecastDays = request.Days.Value };
            effective.Validate();
        }

        var report = await NewService(effective).CurrentAsync(request.Place, cancellationToken).ConfigureAwait(false);
        _output.WriteLine(AnahitaFormat.FormatDaily(report, request.Units));
        return 0;
    }

    private async Task<int> RunHourlyAsync(string[] args, CancellationToken cancellationToken)
    {
        var request = ParseRequest(args);
        var report = await NewService(_options).CurrentAsync(request.Place, cancellationToken).ConfigureAwait(false);
        _output.WriteLine(AnahitaFormat.FormatHourly(report, request.Units, request.Hours ?? _options.HourlyWindow));
        return 0;
    }

    private async Task<int> RunAlertsAsync(string[] args, CancellationToken cancellationToken)
    {
        var request = ParseRequest(args);
        var report = await NewService(_options).CurrentAsync(request.Place, cancellationToken).ConfigureAwait(false);
        var alerts = AnahitaInsights.FindAlerts(report, request.Units);
        var block = AnahitaFormat.FormatAlerts(alerts);
        _output.WriteLine(block.Length > 0
            ? block
            : $"No weather alerts for the next {AnahitaDefaults.AlertHorizonDays} day(s).");
        return 0;
    }

    private async Task<int> RunBestAsync(string[] args, CancellationToken cancellationToken)
    {
        var request = ParseRequest(args);
        var report = await NewService(_options).CurrentAsync(request.Place, cancellationToken).ConfigureAwait(false);
        var ranked = AnahitaInsights.RankDays(report, request.Units, AnahitaDefaults.BestDayCount);
        _output.WriteLine(AnahitaFormat.FormatBest(ranked));
        return 0;
    }

    private async Task<int> RunPlanAsync(string[] args, CancellationToken cancellationToken)
    {
        if (_dueProvider is null)
        {
            return Fail("The plan view needs the Haft Khan task list (storage unavailable in this context).");
        }

        var request = ParseRequest(args);
        var report = await NewService(_options).CurrentAsync(request.Place, cancellationToken).ConfigureAwait(false);
        var tasks = _dueProvider.Invoke()
            .Where(static t => t.DueDate is not null)
            .Select(static t => (Due: t.DueDate!.Value, t.Title))
            .ToList();
        _output.WriteLine(AnahitaFormat.FormatPlan(report, request.Units, tasks));
        return 0;
    }

    private int RunSet(string[] args)
    {
        var place = args.FirstOrDefault(static a => !a.StartsWith('-'));
        if (string.IsNullOrWhiteSpace(place))
            return Fail("Give a place: JameJam weather set Berlin");

        if (_locationSaver is null)
            return Fail("Settings storage is not available in this context.");

        _locationSaver.Invoke(place);
        _output.WriteLine($"Location saved: {place} — 'JameJam weather now' uses it from now on.");
        return 0;
    }

    private ParsedRequest ParseRequest(string[] args)
    {
        string? place = null;
        string? unitsText = null;
        string? daysText = null;
        string? hoursText = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--at" when i + 1 < args.Length:
                    place = args[++i];
                    break;
                case "--units" when i + 1 < args.Length:
                    unitsText = args[++i];
                    break;
                case "--days" when i + 1 < args.Length:
                    daysText = args[++i];
                    break;
                case "--hours" when i + 1 < args.Length:
                    hoursText = args[++i];
                    break;
                default:
                    throw new AnahitaException($"Unknown weather option '{args[i]}'. Run 'JameJam weather help'.");
            }
        }

        return new ParsedRequest(
            place,
            ParseUnits(unitsText),
            ParseCount(daysText, "--days", AnahitaDefaults.ForecastDaysBound),
            ParseCount(hoursText, "--hours", AnahitaDefaults.HourlyWindowBound));
    }

    private WeatherUnits ParseUnits(string? text)
    {
        var effective = FirstNonEmpty(text, Environment.GetEnvironmentVariable(AnahitaDefaults.UnitsEnvironmentVariable), _unitsProvider?.Invoke());
        return effective?.Trim().ToLowerInvariant() switch
        {
            null or "" or "metric" or "c" or "celsius" => WeatherUnits.Metric,
            "imperial" or "f" or "fahrenheit" => WeatherUnits.Imperial,
            _ => throw new AnahitaException($"Unknown units '{effective}'. Use metric or imperial."),
        };
    }

    private static int? ParseCount(string? text, string flag, int bound)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value is < 1)
        {
            throw new AnahitaException($"{flag} must be a whole number between 1 and {bound}.");
        }

        if (value > bound)
        {
            throw new AnahitaException($"{flag} must be between 1 and {bound}.");
        }

        return value;
    }

    private AnahitaService NewService(AnahitaOptions effective) =>
        new(_clientFactory(effective), _clock, effective, _locationProvider);

    private void WriteOptionalBlock(string block)
    {
        if (block.Length > 0)
        {
            _output.WriteLine();
            _output.WriteLine(block);
        }
    }

    private static string? FirstNonEmpty(params string?[] candidates) =>
        candidates.FirstOrDefault(static c => !string.IsNullOrWhiteSpace(c));

    private int Fail(string message)
    {
        _error.WriteLine(message);
        return 1;
    }

    private sealed record ParsedRequest(
        string? Place,
        WeatherUnits Units,
        int? Days,
        int? Hours);

    private void Help()
    {
        _output.WriteLine($"{AnahitaDefaults.ServiceName} — professional weather, right in the terminal");
        _output.WriteLine();
        _output.WriteLine("Usage:");
        _output.WriteLine("  JameJam weather [now]             Conditions right now (the default command)");
        _output.WriteLine("  JameJam weather forecast [--days <1-16>]        Daily outlook with tips");
        _output.WriteLine("  JameJam weather hourly [--hours <1-48>]         Hour-by-hour detail");
        _output.WriteLine("  JameJam weather alerts                          Heat, frost, wind, rain, storm, UV warnings");
        _output.WriteLine("  JameJam weather best                            Rank upcoming days for outdoor plans");
        _output.WriteLine("  JameJam weather plan                            Your open Haft Khan tasks under each day's forecast");
        _output.WriteLine("  JameJam weather ai explain                       AI narration of the forecast");
        _output.WriteLine("  JameJam weather ai ask <question...>             Ask about the weather (forecast used as data)");
        _output.WriteLine("  JameJam weather ai plan                          AI matches your open Haft Khan tasks to the best days");
        _output.WriteLine("  JameJam weather set <place>                     Save the default location");
        _output.WriteLine();
        _output.WriteLine("Options:");
        _output.WriteLine("  --at <place>            City name or \"lat,lon\" (else ANAHITA_LOCATION or the anahita.location setting)");
        _output.WriteLine("  --units metric|imperial Units (else ANAHITA_UNITS or the anahita.units setting)");
        _output.WriteLine();
        _output.WriteLine("No API key is needed (Open-Meteo). For a private endpoint: ANAHITA_ENDPOINT / ANAHITA_GEOCODING_URL,");
        _output.WriteLine($"with an optional key in {AnahitaDefaults.ApiKeyEnvironmentVariable} (never the command line, never stored).");
    }
}
