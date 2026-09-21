using JameJam.Anahita;
using JameJam.HaftKhan;
using JameJam.Settings;
using JameJam.Tests.Anahita;

namespace JameJam.Tests;

/// <summary>Tests for the <c>weather</c> CLI commands and their wiring into the App.</summary>
[Collection("EnvSequential")]
public sealed class AppWeatherTests
{
    private static readonly DateOnly Saturday = new(2026, 9, 19);
    private static readonly DateOnly Sunday = new(2026, 9, 20);

    private readonly StubWeatherClient _client = new();

    private sealed class StubWeatherClient : IAnahitaClient
    {
        public WeatherReport Report { get; set; } = AnahitaTestSupport.MakeReport(
            AnahitaTestSupport.Day(Saturday),
            AnahitaTestSupport.Day(Sunday, code: 61, chance: 80));

        public List<string> GeocodeNames { get; } = [];

        public Task<GeoPlace?> GeocodeAsync(string name, CancellationToken cancellationToken = default)
        {
            GeocodeNames.Add(name);
            GeoPlace place = new(name, "Germany", 52.52, 13.41, "Europe/Berlin");
            return Task.FromResult<GeoPlace?>(place);
        }

        public Task<WeatherReport> GetForecastAsync(GeoPlace place, CancellationToken cancellationToken = default) =>
            Task.FromResult(Report);
    }

    private AnahitaCommands BuildCommands(
        out StringWriter output,
        out StringWriter error,
        MemorySettingsStore? store = null,
        Func<IReadOnlyList<HaftKhanTask>>? dueProvider = null)
    {
        output = new StringWriter();
        error = new StringWriter();
        return new AnahitaCommands(
            output,
            error,
            new FixedTimeProvider(AnahitaTestSupport.Now),
            locationProvider: store is null ? null : () => store.GetValue(SettingKeys.AnahitaLocation),
            clientFactory: _ => _client,
            dueProvider: dueProvider,
            unitsProvider: store is null ? null : () => store.GetValue(SettingKeys.AnahitaUnits),
            locationSaver: store is null ? null : value => store.SetValue(SettingKeys.AnahitaLocation, value),
            options: new AnahitaOptions());
    }

    [Fact]
    public async Task Help_ListsAllCommands()
    {
        var commands = BuildCommands(out var output, out _);

        Assert.Equal(0, await commands.RunAsync(["help"]));

        var text = output.ToString();
        Assert.Contains("Anahita", text, StringComparison.Ordinal);
        Assert.Contains("now", text, StringComparison.Ordinal);
        Assert.Contains("forecast", text, StringComparison.Ordinal);
        Assert.Contains("hourly", text, StringComparison.Ordinal);
        Assert.Contains("alerts", text, StringComparison.Ordinal);
        Assert.Contains("best", text, StringComparison.Ordinal);
        Assert.Contains("plan", text, StringComparison.Ordinal);
        Assert.Contains("set", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownCommand_FailsWithHint()
    {
        var commands = BuildCommands(out _, out var error);

        Assert.Equal(1, await commands.RunAsync(["fly"]));

        Assert.Contains("Unknown weather command 'fly'", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownOption_FailsWithHint()
    {
        var commands = BuildCommands(out _, out var error);

        Assert.Equal(1, await commands.RunAsync(["now", "--teleport"]));

        Assert.Contains("Unknown weather option '--teleport'", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoLocationAnywhere_FailsWithGuidance()
    {
        Environment.SetEnvironmentVariable(AnahitaDefaults.LocationEnvironmentVariable, null);
        try
        {
            var commands = BuildCommands(out _, out var error);

            Assert.Equal(1, await commands.RunAsync(["now"]));

            Assert.Contains("No location", error.ToString(), StringComparison.Ordinal);
            Assert.Contains("JameJam weather set", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AnahitaDefaults.LocationEnvironmentVariable, null);
        }
    }

    [Fact]
    public async Task Now_WithPlace_ShowsConditionsAndAlerts()
    {
        var commands = BuildCommands(out var output, out var error);

        Assert.Equal(0, await commands.RunAsync(["now", "--at", "Berlin"]));

        var text = output.ToString();
        Assert.Contains("Berlin", text, StringComparison.Ordinal);
        Assert.Contains("Partly cloudy", text, StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Equal(["Berlin"], _client.GeocodeNames);
    }

    [Fact]
    public async Task Now_DefaultCommand_WhenArgsAreFlagsOnly()
    {
        var commands = BuildCommands(out var output, out _);

        Assert.Equal(0, await commands.RunAsync(["--at", "Berlin", "--units", "imperial"]));

        Assert.Contains("°F", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forecast_DaysOverride_FlowsThrough()
    {
        var commands = BuildCommands(out var output, out _);

        Assert.Equal(0, await commands.RunAsync(["forecast", "--at", "Berlin", "--days", "2"]));

        Assert.Contains("2-day forecast", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0", "must be a whole number between 1 and 16")]
    [InlineData("many", "must be a whole number between 1 and 16")]
    [InlineData("17", "must be between 1 and 16")]
    public async Task Forecast_InvalidDays_Fail(string days, string expected)
    {
        var commands = BuildCommands(out _, out var error);

        Assert.Equal(1, await commands.RunAsync(["forecast", "--at", "Berlin", "--days", days]));

        Assert.Contains("--days " + expected, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hourly_HoursOverride_FlowsThrough()
    {
        var commands = BuildCommands(out var output, out _);

        Assert.Equal(0, await commands.RunAsync(["hourly", "--at", "Berlin", "--hours", "1"]));

        Assert.Contains("Next 1 hour(s)", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0", "must be a whole number between 1 and 48")]
    [InlineData("49", "must be between 1 and 48")]
    public async Task Hourly_InvalidHours_Fail(string hours, string expected)
    {
        var commands = BuildCommands(out _, out var error);

        Assert.Equal(1, await commands.RunAsync(["hourly", "--at", "Berlin", "--hours", hours]));

        Assert.Contains("--hours " + expected, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Alerts_WhenCalm_SaySo()
    {
        var commands = BuildCommands(out var output, out _);
        _client.Report = AnahitaTestSupport.MakeReport(
            AnahitaTestSupport.Day(Saturday, min: 20, max: 24, chance: 0, wind: 10));

        Assert.Equal(0, await commands.RunAsync(["alerts", "--at", "Berlin"]));

        Assert.Contains("No weather alerts", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Alerts_ListWarnings()
    {
        var commands = BuildCommands(out var output, out _);
        _client.Report = AnahitaTestSupport.MakeReport(AnahitaTestSupport.Day(Saturday, max: 36));

        Assert.Equal(0, await commands.RunAsync(["alerts", "--at", "Berlin"]));

        Assert.Contains("Alerts", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Heat", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Best_RanksUpcomingDays()
    {
        var commands = BuildCommands(out var output, out _);

        Assert.Equal(0, await commands.RunAsync(["best", "--at", "Berlin"]));

        Assert.Contains("Best days outdoors", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("(score ", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plan_WithoutTaskStorage_Fails()
    {
        var commands = BuildCommands(out _, out var error);

        Assert.Equal(1, await commands.RunAsync(["plan", "--at", "Berlin"]));

        Assert.Contains("needs the Haft Khan task list", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plan_ShowsTasksUnderTheirForecastDays()
    {
        var commands = BuildCommands(out var output, out _, dueProvider: MakeDatedTasks);

        Assert.Equal(0, await commands.RunAsync(["plan", "--at", "Berlin"]));

        var text = output.ToString();
        Assert.Contains("Weather for your plans", text, StringComparison.Ordinal);
        Assert.Contains("· Water the garden", text, StringComparison.Ordinal);
        Assert.Contains("(nothing due)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Set_SavesTheLocation()
    {
        var store = new MemorySettingsStore();
        var commands = BuildCommands(out var output, out _, store);

        Assert.Equal(0, await commands.RunAsync(["set", "Tehran"]));

        Assert.Equal("Tehran", store.GetValue(SettingKeys.AnahitaLocation));
        Assert.Contains("Location saved: Tehran", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Set_WithoutSettingsStorage_Fails()
    {
        var commands = BuildCommands(out _, out var error); // no store → saver is null

        Assert.Equal(1, await commands.RunAsync(["set", "Berlin"]));

        Assert.Contains("Settings storage is not available", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Set_WithoutAPlace_Fails()
    {
        var commands = BuildCommands(out _, out var error);

        Assert.Equal(1, await commands.RunAsync(["set"]));

        Assert.Contains("Give a place", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Units_Flag_Beats_Setting_AndEnvironment()
    {
        Environment.SetEnvironmentVariable(AnahitaDefaults.UnitsEnvironmentVariable, "metric");
        try
        {
            var store = new MemorySettingsStore();
            store.SetValue(SettingKeys.AnahitaUnits, "metric");
            var commands = BuildCommands(out var output, out _, store);

            Assert.Equal(0, await commands.RunAsync(["now", "--at", "Berlin", "--units", "imperial"]));

            Assert.Contains("°F", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AnahitaDefaults.UnitsEnvironmentVariable, null);
        }
    }

    [Fact]
    public async Task EnvironmentLocation_BeatsSavedSetting()
    {
        Environment.SetEnvironmentVariable(AnahitaDefaults.LocationEnvironmentVariable, "Tokyo");
        try
        {
            var commands = BuildCommands(out _, out _);

            Assert.Equal(0, await commands.RunAsync(["now"]));

            Assert.Equal(["Tokyo"], _client.GeocodeNames);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AnahitaDefaults.LocationEnvironmentVariable, null);
        }
    }

    [Fact]
    public async Task App_RoutesWeather_AndAnahitaAlias()
    {
        var app = new App(
            new StringWriter(),
            new StringWriter(),
            new MemorySettingsStore(),
            anahitaClientFactory: _ => _client);

        Assert.Equal(0, await app.RunAsync(["weather", "now", "--at", "Berlin"]));
        Assert.Equal(0, await app.RunAsync(["anahita", "best", "--at", "Berlin"]));
    }

    [Fact]
    public async Task App_Help_MentionsWeather()
    {
        using StringWriter output = new();
        var app = new App(output, new StringWriter(), new MemorySettingsStore(), anahitaClientFactory: _ => _client);

        Assert.Equal(0, await app.RunAsync(["--help"]));

        Assert.Contains("JameJam weather", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Anahita", output.ToString(), StringComparison.Ordinal);
    }

    private static IReadOnlyList<HaftKhanTask> MakeDatedTasks()
    {
        var created = AnahitaTestSupport.Now;
        return
        [
            new(1, "Water the garden", string.Empty, TaskPriority.Normal, TaskState.Todo, Saturday, created, created, null),
            new(2, "Paint the shed", string.Empty, TaskPriority.Normal, TaskState.Todo, null, created, created, null), // undated → hidden
        ];
    }
}
