using System.Globalization;

using JameJam.Anahita;
using JameJam.Divan;
using JameJam.Ganjoor;
using JameJam.Raz;
using JameJam.HaftKhan;
using JameJam.Settings;
using JameJam.Sync;
using JameJam.Soroush;
using JameJam.Taqvim;
using JameJam.Soroush.Providers;
using JameJam.Toolbox;

namespace JameJam;

/// <summary>
/// Application entry logic. Kept separate from <c>Program.cs</c> so it can be unit tested.
/// Routes to the toolbox tools: Greeter, Soroush AI, Settings, Haft Khan (to-do list),
/// Anahita (weather), and Ganjoor (wallet).
/// </summary>
public sealed class App
{
    /// <summary>Environment variable that carries the AI API key (never the command line).</summary>
    public const string ApiKeyEnvironmentVariable = "JAMEJAM_AI_API_KEY";

    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly ISettingsStore? _settings;
    private readonly Func<SoroushOptions, ISoroushClient> _soroushFactory;
    private readonly TimeProvider _clock;
    private readonly AiGreeter _aiGreeter = new();
    private readonly HaftKhanCommands _haftkhan;
    private readonly AnahitaCommands _anahita;
    private readonly GanjoorCommands _ganjoor;
    private readonly RazCommands _raz;
    private readonly DivanCommands _divan;
    private readonly TaqvimCommands _taqvim;

    /// <summary>
    /// Initializes the application.
    /// </summary>
    /// <param name="output">Standard output. Injected for testability.</param>
    /// <param name="error">Error output. Defaults to <paramref name="output"/>.</param>
    /// <param name="settings">Settings store; null disables the settings commands.</param>
    /// <param name="soroushFactory">Creates an AI client from options; defaults to the real HTTP client.</param>
    /// <param name="tasks">Haft Khan task storage; null disables the Haft Khan commands.</param>
    /// <param name="clock">Time source for Haft Khan; defaults to the system clock.</param>
    /// <param name="haftKhanOptions">Customizable Haft Khan limits; defaults apply when null.</param>
    /// <param name="anahitaClientFactory">Creates the weather transport; defaults to the hardened HTTP client.</param>
    /// <param name="wallet">Ganjoor wallet storage; null disables the wallet commands.</param>
    /// <param name="razStore">Raz vault storage; null disables the vault commands.</param>
    /// <param name="divanStore">Divan pad storage; null disables the pad commands.</param>
    /// <param name="divanSyncClientFactory">Builds the Divan sync transport; defaults to the hardened HTTP client.</param>
    /// <param name="taqvimStore">Taqvim calendar storage; null disables the calendar commands.</param>
    /// <param name="taqvimSyncClientFactory">Builds the Taqvim sync transport; defaults to the hardened HTTP client.</param>
    public App(
        TextWriter output,
        TextWriter? error = null,
        ISettingsStore? settings = null,
        Func<SoroushOptions, ISoroushClient>? soroushFactory = null,
        ITaskRepository? tasks = null,
        TimeProvider? clock = null,
        HaftKhanOptions? haftKhanOptions = null,
        Func<AnahitaOptions, IAnahitaClient>? anahitaClientFactory = null,
        IGanjoorStore? wallet = null,
        IVaultStore? razStore = null,
        IDivanStore? divanStore = null,
        Func<SyncOptions, ISyncClient>? divanSyncClientFactory = null,
        ITaqvimStore? taqvimStore = null,
        Func<SyncOptions, ISyncClient>? taqvimSyncClientFactory = null)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _error = error ?? _output;
        _clock = clock ?? TimeProvider.System;
        _settings = settings;
        _soroushFactory = soroushFactory ?? (options => new SoroushClient(SoroushHttp.CreateClient(), options));
        _haftkhan = new HaftKhanCommands(
            tasks,
            _clock,
            _output,
            _error,
            CompleteAiRequestAsync,
            haftKhanOptions,
            syncUrlProvider: settings is null ? null : () => settings.GetValue(SettingKeys.SyncUrl));
        _anahita = new AnahitaCommands(
            _output,
            _error,
            _clock,
            locationProvider: settings is null ? null : () => settings.GetValue(SettingKeys.AnahitaLocation),
            clientFactory: anahitaClientFactory,
            dueProvider: tasks is null ? null : () => _haftkhan.DueTasks() ?? [],
            unitsProvider: settings is null ? null : () => settings.GetValue(SettingKeys.AnahitaUnits),
            locationSaver: settings is null ? null : value => settings.SetValue(SettingKeys.AnahitaLocation, value),
            aiCompletion: CompleteAiRequestAsync,
            openTasksProvider: tasks is null ? null : () => _haftkhan.OpenTasks() ?? []);
        _ganjoor = new GanjoorCommands(
            wallet,
            _clock,
            _output,
            _error,
            aiCompletion: CompleteAiRequestAsync,
            currencyProvider: settings is null ? null : () => settings.GetValue(SettingKeys.GanjoorCurrency));
        _raz = new RazCommands(
            razStore,
            _clock,
            _output,
            _error,
            aiCompletion: CompleteAiRequestAsync,
            passphraseProvider: () => Environment.GetEnvironmentVariable(RazCommands.PassphraseEnvironmentVariable),
            defaultLengthProvider: settings is null ? null : () => ParseLength(settings.GetValue(SettingKeys.RazDefaultLength)));
        _divan = new DivanCommands(
            divanStore,
            _clock,
            _output,
            _error,
            aiCompletion: CompleteAiRequestAsync,
            defaultNotebookProvider: settings is null ? null : () => settings.GetValue(SettingKeys.DivanNotebook),
            syncClientFactory: divanSyncClientFactory ?? (options => new HttpSyncClient(SoroushHttp.CreateClient(), options)),
            syncUrlProvider: settings is null ? null : () => settings.GetValue(SettingKeys.DivanSyncUrl),
            deviceIdProvider: settings is null ? null : () => EnsureDeviceId(settings),
            deviceNameProvider: settings is null ? null : () =>
                string.IsNullOrWhiteSpace(settings.GetValue(SettingKeys.SyncDeviceName))
                    ? Environment.MachineName
                    : settings.GetValue(SettingKeys.SyncDeviceName)!);
        _taqvim = new TaqvimCommands(
            taqvimStore,
            _clock,
            _output,
            _error,
            aiCompletion: CompleteAiRequestAsync,
            syncClientFactory: taqvimSyncClientFactory ?? (options => new HttpSyncClient(SoroushHttp.CreateClient(), options)),
            syncUrlProvider: settings is null ? null : () => settings.GetValue(SettingKeys.TaqvimSyncUrl),
            deviceIdProvider: settings is null ? null : () => EnsureDeviceId(settings),
            deviceNameProvider: settings is null ? null : () =>
                string.IsNullOrWhiteSpace(settings.GetValue(SettingKeys.SyncDeviceName))
                    ? Environment.MachineName
                    : settings.GetValue(SettingKeys.SyncDeviceName)!);
        _taqvim = new TaqvimCommands(
            taqvimStore,
            _clock,
            _output,
            _error,
            aiCompletion: CompleteAiRequestAsync,
            syncClientFactory: taqvimSyncClientFactory ?? (options => new HttpSyncClient(SoroushHttp.CreateClient(), options)),
            syncUrlProvider: settings is null ? null : () => settings.GetValue(SettingKeys.TaqvimSyncUrl),
            deviceIdProvider: settings is null ? null : () => EnsureDeviceId(settings),
            deviceNameProvider: settings is null ? null : () =>
                string.IsNullOrWhiteSpace(settings.GetValue(SettingKeys.SyncDeviceName))
                    ? Environment.MachineName
                    : settings.GetValue(SettingKeys.SyncDeviceName)!,
            dueTasksProvider: tasks is null ? null : () => _haftkhan.DueTasks()?.Select(DescribeDueTask).ToList() ?? [],
            forecastProvider: BuildForecastProvider(anahitaClientFactory, settings),
            journalProvider: divanStore is null ? null : day => DescribeJournalEntry(divanStore, day));
    }

    /// <summary>Formats one due Haft Khan task for the calendar agenda.</summary>
    private static string DescribeDueTask(HaftKhanTask task) =>
        task.DueDate is { } due
            ? FormattableString.Invariant($"{task.Title} (due {due:yyyy-MM-dd})")
            : task.Title;

    /// <summary>
    /// Builds the Anahita forecast line for a day (keystone integration: weather on outdoor
    /// events). Weather is a bonus — every failure returns null and never blocks the agenda.
    /// Results are memoized per location/day/units for the life of the process.
    /// </summary>
    private static Func<DateOnly, Task<string?>>? BuildForecastProvider(
        Func<AnahitaOptions, IAnahitaClient>? factory,
        ISettingsStore? settings)
    {
        if (settings is null)
        {
            return null;
        }

        var clientFactory = factory ?? (options => new OpenMeteoClient(SoroushHttp.CreateClient(), options));
        var client = clientFactory(AnahitaOptions.FromEnvironment());
        Dictionary<string, string?> memo = new(StringComparer.Ordinal);
        return async day =>
        {
            try
            {
                var location = settings.GetValue(SettingKeys.AnahitaLocation);
                if (string.IsNullOrWhiteSpace(location))
                {
                    return null;
                }

                var unitsText = settings.GetValue(SettingKeys.AnahitaUnits);
                var units = unitsText?.Trim().ToLowerInvariant() is "imperial" or "f" or "fahrenheit"
                    ? WeatherUnits.Imperial
                    : WeatherUnits.Metric;
                var key = FormattableString.Invariant($"{location}|{day:yyyy-MM-dd}|{units}");
                if (memo.TryGetValue(key, out var cached))
                {
                    return cached;
                }

                var place = await client.GeocodeAsync(location).ConfigureAwait(false);
                if (place is null)
                {
                    memo[key] = null;
                    return null;
                }

                var report = await client.GetForecastAsync(place).ConfigureAwait(false);
                var point = report.Daily.FirstOrDefault(p => p.Date == day);
                if (point is null)
                {
                    memo[key] = null;
                    return null;
                }

                var conditions = WeatherCode.Icon(point.Code) + " " + WeatherCode.Describe(point.Code) + ", "
                    + UnitMath.Temperature(point.MinC, units) + "–" + UnitMath.Temperature(point.MaxC, units);
                var line = point.PrecipProbabilityPercent is { } rain
                    ? conditions + FormattableString.Invariant($", {rain}% rain")
                    : conditions;
                memo[key] = line;
                return line;
            }
            catch (Exception ex) when (ex is AnahitaException or System.Net.Http.HttpRequestException
                or InvalidOperationException or TaskCanceledException)
            {
                return null; // weather is a bonus — never blocks the agenda
            }
        };
    }

    /// <summary>Divan daily-journal link for a day (keystone integration); null when no entry exists.</summary>
    private static string? DescribeJournalEntry(IDivanStore store, DateOnly day)
    {
        try
        {
            var journal = store.FindNotebookByName(DivanDefaults.JournalNotebook);
            if (journal is null)
            {
                return null;
            }

            var title = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var exists = store.ListNotes().Any(note =>
                note.NotebookId == journal.Id && string.Equals(note.Title, title, StringComparison.OrdinalIgnoreCase));
            return exists
                ? FormattableString.Invariant(
                    $"journal entry for {title} exists — open it with: JameJam divan daily (or divan open)")
                : null;
        }
        catch (Exception)
        {
            return null; // the pad is optional context for the calendar
        }
    }

    /// <summary>Gets (or lazily creates and stores) this installation's sync identity.</summary>
    private static string EnsureDeviceId(ISettingsStore settings)
    {
        var existing = settings.GetValue(SettingKeys.SyncDeviceId);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        var created = Guid.CreateVersion7().ToString();
        settings.SetValue(SettingKeys.SyncDeviceId, created);
        return created;
    }

    private static int? ParseLength(string? text) =>
        int.TryParse(text, out var length) ? length : null;

    /// <summary>Runs the application.</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="cancellationToken">Cancels long-running work (e.g. an AI call).</param>
    /// <returns>Process exit code: 0 success, 1 failure.</returns>
    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
            return Greet(null);

        return args[0] switch
        {
            "--help" or "-h" or "-?" => Help(),
            "greet" => await RunGreetAsync(args[1..], cancellationToken).ConfigureAwait(false),
            "soroush" => await RunSoroushAsync(args[1..], cancellationToken).ConfigureAwait(false),
            "settings" => RunSettings(args[1..]),
            "haftkhan" => await _haftkhan.RunAsync(args[1..], cancellationToken).ConfigureAwait(false),
            "weather" or "anahita" => await _anahita.RunAsync(args[1..], cancellationToken).ConfigureAwait(false),
            "ganjoor" or "wallet" => await _ganjoor.RunAsync(args[1..], cancellationToken).ConfigureAwait(false),
            "raz" or "vault" => await _raz.RunAsync(args[1..], cancellationToken).ConfigureAwait(false),
            "divan" or "pad" => await _divan.RunAsync(args[1..], cancellationToken).ConfigureAwait(false),
            "taqvim" or "cal" => await _taqvim.RunAsync(args[1..], cancellationToken).ConfigureAwait(false),
            _ => Greet(args[0]),
        };
    }

    private int Greet(string? name)
    {
        name ??= _settings?.GetValue(SettingKeys.GreeterDefaultName);
        _output.WriteLine(Greeter.GetGreeting(name));
        return 0;
    }

    private async Task<int> RunGreetAsync(string[] args, CancellationToken cancellationToken)
    {
        string? name = null;
        var ai = false;
        foreach (var arg in args)
        {
            if (arg.StartsWith('-'))
            {
                if (arg == "--ai")
                {
                    ai = true;
                    continue;
                }

                _error.WriteLine("Usage: JameJam greet [--ai] [name]");
                return 1;
            }

            name ??= arg;
        }

        if (!ai)
        {
            return Greet(name);
        }

        try
        {
            name ??= _settings?.GetValue(SettingKeys.GreeterDefaultName);
            var prompt = _aiGreeter.BuildPrompt(name, _clock.GetUtcNow());
            var response = await CompleteAiRequestAsync(new AiRequest(prompt), cancellationToken).ConfigureAwait(false);
            _output.WriteLine(_aiGreeter.ParseGreeting(response.Content));
            return 0;
        }
        catch (SoroushException ex)
        {
            _error.WriteLine(ex.Message);
            return 1;
        }
        catch (ArgumentException ex)
        {
            _error.WriteLine(ex.Message);
            return 1;
        }
    }

    private int Help()
    {
        _output.WriteLine("JameJam Toolbox");
        _output.WriteLine();
        _output.WriteLine("Usage:");
        _output.WriteLine("  JameJam [name]                    Greet <name> (default: World, or the greeter.defaultName setting)");
        _output.WriteLine("  JameJam greet [--ai] [name]       Local greeting — or let Soroush AI craft a time-aware one");
        _output.WriteLine("  JameJam haftkhan <command>        Advanced to-do list: tags, projects, recurrence, dependencies,");
        _output.WriteLine("                                    search, board, matrix, focus, review, undo, import/export, AI.");
        _output.WriteLine("                                    Full command list: JameJam haftkhan help");
        _output.WriteLine("  JameJam raz <command>             Encrypted vault: passwords, TOTP, expiry tracking, audits. Full list: JameJam raz help");
        _output.WriteLine("  JameJam divan <command>           Markdown pad: notebooks, wiki-links, search, daily journal, AI. Full list: JameJam divan help");
        _output.WriteLine("  JameJam taqvim <command>          Calendar: events, recurrence, reminders, agendas, free time, ICS,");
        _output.WriteLine("                                    sync, AI. Full command list: JameJam taqvim help");
        _output.WriteLine("  JameJam ganjoor <command>         Personal wallet: accounts, budgets, bills, goals, debts,");
        _output.WriteLine("                                    reports, CSV import, undo, AI insights. Full list: JameJam ganjoor help");
        _output.WriteLine("  JameJam weather <command>         Anahita weather: now, forecast, hourly, alerts, best,");
        _output.WriteLine("                                    plan (your tasks under each day's forecast), set.");
        _output.WriteLine("                                    Full command list: JameJam weather help");
        _output.WriteLine("  JameJam soroush [options] <prompt...>");
        _output.WriteLine("                                    Ask an AI through the Soroush safety layer");
        _output.WriteLine("      --provider <name>             openai (default; any OpenAI-compatible API) | anthropic");
        _output.WriteLine("      --model <model-id>            Model override (default: provider default or the soroush.model setting)");
        _output.WriteLine("      --endpoint <url>              Endpoint override (HTTPS; plain HTTP only on loopback for local models)");
        _output.WriteLine("  JameJam settings [list | get <key> | set <key> <value> | remove <key> | clear]");
        _output.WriteLine("                                    Manage toolbox settings (safely stored in SQLite)");
        _output.WriteLine("  JameJam --help                    Show this help");
        _output.WriteLine();
        _output.WriteLine($"The AI API key is read from the {ApiKeyEnvironmentVariable} environment variable — never the command line.");
        _output.WriteLine("Settings live in ~/.jamejam/settings.db, tasks in ~/.jamejam/haftkhan.db, the wallet in ~/.jamejam/ganjoor.db (JAMEJAM_SETTINGS_DB / JAMEJAM_HAFTKHAN_DB / JAMEJAM_GANJOOR_DB override).");
        return 0;
    }

    private async Task<int> RunSoroushAsync(string[] args, CancellationToken cancellationToken)
    {
        string? providerName = null;
        string? model = null;
        string? endpoint = null;
        List<string> promptParts = [];

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--provider" when i + 1 < args.Length:
                    providerName = args[++i];
                    break;
                case "--model" when i + 1 < args.Length:
                    model = args[++i];
                    break;
                case "--endpoint" when i + 1 < args.Length:
                    endpoint = args[++i];
                    break;
                default:
                    promptParts.Add(args[i]);
                    break;
            }
        }

        if (promptParts.Count == 0)
        {
            _error.WriteLine("Usage: JameJam soroush [--provider <name>] [--model <id>] [--endpoint <url>] <prompt...>");
            return 1;
        }

        try
        {
            var result = await CompleteAiRequestAsync(
                new AiRequest(string.Join(' ', promptParts), providerName, model, endpoint),
                cancellationToken).ConfigureAwait(false);

            _output.WriteLine(result.Content);
            return 0;
        }
        catch (ArgumentException ex)
        {
            _error.WriteLine(ex.Message);
            return 1;
        }
        catch (SoroushException ex)
        {
            _error.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// The single AI path for every feature: resolves provider/options (flags → settings → defaults),
    /// enforces the API-key safety gate, sanitizes the prompt, and dispatches through the factory.
    /// </summary>
    /// <exception cref="SoroushException">Missing key, unknown provider, insecure endpoint, or provider failure.</exception>
    private async Task<SoroushResult> CompleteAiRequestAsync(AiRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var provider = SoroushProviders.Resolve(request.Provider ?? _settings?.GetValue(SettingKeys.SoroushProvider));
        var options = new SoroushOptions
        {
            Provider = provider.Name,
            Endpoint = request.Endpoint ?? _settings?.GetValue(SettingKeys.SoroushEndpoint) ?? provider.DefaultEndpoint,
            Model = request.Model ?? _settings?.GetValue(SettingKeys.SoroushModel) ?? provider.DefaultModel,
            ApiKey = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable),
        };

        // Safety layer: a key is mandatory for non-loopback endpoints (local servers may skip it).
        if (string.IsNullOrWhiteSpace(options.ApiKey) && !SoroushGuard.ValidateEndpoint(options.Endpoint).IsLoopback)
        {
            throw new SoroushException(
                $"Missing API key. Set the {ApiKeyEnvironmentVariable} environment variable "
                + "(keys are never accepted on the command line for safety; loopback endpoints may omit it).");
        }

        // Safety layer: sanitize before anything leaves the app, with the same
        // configurable cap the client uses (the client enforces it too).
        var prompt = SoroushGuard.SanitizePrompt(request.Prompt, options.MaxPromptLength);

        return await _soroushFactory(options).CompleteAsync(prompt, cancellationToken).ConfigureAwait(false);
    }

    private int RunSettings(string[] args)
    {
        if (_settings is null)
        {
            _error.WriteLine("Settings storage is not available in this context.");
            return 1;
        }

        try
        {
            var command = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
            switch (command)
            {
                case "list":
                    foreach (var entry in _settings.GetAll())
                        _output.WriteLine($"{entry.Key} = {entry.Value}");
                    return 0;

                case "get" when args.Length >= 2:
                    var value = _settings.GetValue(args[1]);
                    if (value is null)
                        return Fail($"No setting '{args[1]}'.");
                    _output.WriteLine(value);
                    return 0;

                case "set" when args.Length >= 3:
                    _settings.SetValue(args[1], args[2]);
                    _output.WriteLine($"{args[1]} saved.");
                    return 0;

                case "remove" when args.Length >= 2:
                    return _settings.Remove(args[1]) ? 0 : Fail($"No setting '{args[1]}'.");

                case "clear":
                    _settings.Clear();
                    _output.WriteLine("All settings removed.");
                    return 0;

                default:
                    _error.WriteLine("Usage: JameJam settings [list | get <key> | set <key> <value> | remove <key> | clear]");
                    return 1;
            }
        }
        catch (ArgumentException ex)
        {
            return Fail(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Fail(ex.Message);
        }
    }

    private int Fail(string message)
    {
        _error.WriteLine(message);
        return 1;
    }
}
