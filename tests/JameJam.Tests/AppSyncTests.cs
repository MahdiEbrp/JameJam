using JameJam.HaftKhan;
using JameJam.Sync;
using JameJam.Settings;
using JameJam.Soroush;

namespace JameJam.Tests;

/// <summary>Tests for the <c>haftkhan sync</c> CLI command: URL resolution, modes, safety gates.</summary>
[Collection("EnvSequential")]
public sealed class AppSyncTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 10, 0, 0, TimeSpan.Zero);

    private readonly MemoryTaskRepository _tasks = new();
    private readonly StubSyncClient _client = new();

    [Fact]
    public async Task Sync_WithoutAnyUrl_FailsWithGuidance()
    {
        Environment.SetEnvironmentVariable(SyncDefaults.UrlEnvironmentVariable, null);
        try
        {
            using StringWriter output = new();
            using StringWriter error = new();
            var app = new App(output, error, new MemorySettingsStore(), null, _tasks);

            Assert.Equal(1, await app.RunAsync(["haftkhan", "sync"]));

            Assert.Contains("No sync URL", error.ToString(), StringComparison.Ordinal);
            Assert.Contains(SyncDefaults.UrlSettingKey, error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SyncDefaults.UrlEnvironmentVariable, null);
        }
    }

    [Fact]
    public async Task Sync_InsecureUrl_FailsBeforeAnyCall()
    {
        using StringWriter output = new();
        using StringWriter error = new();
        var app = new App(output, error, new MemorySettingsStore(), null, _tasks);

        Assert.Equal(1, await app.RunAsync(["haftkhan", "sync", "--url", "http://sync.example.com/x"]));

        Assert.Contains("Insecure sync endpoint", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_UnknownMode_Fails()
    {
        using StringWriter output = new();
        using StringWriter error = new();
        var app = new App(output, error, new MemorySettingsStore(), null, _tasks);

        Assert.Equal(1, await app.RunAsync(["haftkhan", "sync", "--url", "https://x.test", "--mode", "teleport"]));

        Assert.Contains("Unknown sync mode 'teleport'", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_WithCustomUrl_ResolvesPrecedence_AndSyncs()
    {
        var store = new MemorySettingsStore();
        store.SetValue(SyncDefaults.UrlSettingKey, "https://from-setting.test/x");
        _tasks.Add(new NewTask("local", string.Empty, TaskPriority.Normal, null));
        var commands = BuildCommands(store, out var output, out var error);

        Assert.Equal(0, await commands.RunAsync(["sync", "--url", "https://custom.test/x"]));

        Assert.Equal("Merged: pulled 0, pushed 1 — 1 local task(s) now.", output.ToString().Trim());
        Assert.Equal("https://custom.test/x", _client.Endpoints.Single());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task Sync_UsesSettingUrl_WhenNoFlag()
    {
        var store = new MemorySettingsStore();
        store.SetValue(SyncDefaults.UrlSettingKey, "https://from-setting.test/x");
        var commands = BuildCommands(store, out var output, out _);

        Assert.Equal(0, await commands.RunAsync(["sync"]));

        Assert.Equal("https://from-setting.test/x", _client.Endpoints.Single());
    }

    [Fact]
    public async Task Sync_EnvironmentUrl_OverridesSetting()
    {
        Environment.SetEnvironmentVariable(SyncDefaults.UrlEnvironmentVariable, "https://from-env.test/x");
        try
        {
            var store = new MemorySettingsStore();
            store.SetValue(SyncDefaults.UrlSettingKey, "https://from-setting.test/x");
            var commands = BuildCommands(store, out var output, out _);

            Assert.Equal(0, await commands.RunAsync(["sync"]));

            Assert.Equal("https://from-env.test/x", _client.Endpoints.Single());
        }
        finally
        {
            Environment.SetEnvironmentVariable(SyncDefaults.UrlEnvironmentVariable, null);
        }
    }

    [Fact]
    public async Task Sync_TokenFlowsFromEnvironment()
    {
        Environment.SetEnvironmentVariable(SyncDefaults.TokenEnvironmentVariable, "env-token-4321");
        try
        {
            var commands = BuildCommands(new MemorySettingsStore(), out var output, out _);
            _tasks.Add(new NewTask("t", string.Empty, TaskPriority.Normal, null));

            Assert.Equal(0, await commands.RunAsync(["sync", "--url", "https://x.test", "--mode", "push"]));

            Assert.Equal("env-token-4321", _client.Tokens.Single());
        }
        finally
        {
            Environment.SetEnvironmentVariable(SyncDefaults.TokenEnvironmentVariable, null);
        }
    }

    [Fact]
    public async Task Sync_PushRefusedWithoutForce()
    {
        var client = new StubSyncClient();
        client.Remote = Backup.ToJson(MakeRemoteBackup()); // non-empty remote
        var commands = new HaftKhanCommands(
            _tasks, TimeProvider.System, StringWriter.Null, new StringWriter(),
            aiCompletion: null, syncClientFactory: _ => client);

        Assert.Equal(1, await commands.RunAsync(["sync", "--url", "https://x.test", "--mode", "push"]));
        Assert.Null(client.LastUpload);
    }

    [Fact]
    public async Task Sync_TransportFailure_SurfacesMessage()
    {
        using StringWriter output = new();
        using StringWriter error = new();
        var commands = new HaftKhanCommands(
            _tasks, TimeProvider.System, output, error,
            aiCompletion: null, syncClientFactory: _ => new ThrowingSyncClient());

        Assert.Equal(1, await commands.RunAsync(["sync", "--url", "https://x.test"]));

        Assert.Contains("remote is down", error.ToString(), StringComparison.Ordinal);
    }

    private static Backup.BackupFile MakeRemoteBackup() => new(
        Backup.CurrentVersion, "2026-09-19T00:00:00.0000000+00:00",
        [new Backup.TaskDto(1, "remote", "", 1, 0, null, Now.ToString("O"), Now.ToString("O"), null, "", [], 0, 0, 1, null, "uid-remote")],
        []);

    private HaftKhanCommands BuildCommands(MemorySettingsStore store, out StringWriter output, out StringWriter error)
    {
        output = new StringWriter();
        error = new StringWriter();
        _lastError = error;
        _client.Endpoints.Clear();
        _client.Tokens.Clear();
        return new HaftKhanCommands(
            _tasks,
            new FixedTimeProvider(Now),
            output,
            error,
            aiCompletion: null,
            syncClientFactory: options =>
            {
                _client.Endpoints.Add(options.Endpoint);
                _client.Tokens.Add(options.BearerToken);
                return _client;
            },
            syncUrlProvider: () => store.GetValue(SettingKeys.SyncUrl));
    }

    private StringWriter? _lastError;

    /// <summary>In-memory transport recording what the CLI sent.</summary>
    private sealed class StubSyncClient : ISyncClient
    {
        public string? Remote { get; set; }

        public string? LastUpload { get; private set; }

        public List<string> Endpoints { get; } = [];

        public List<string?> Tokens { get; } = [];

        public Task<string?> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Remote);

        public Task PutAsync(string json, CancellationToken cancellationToken = default)
        {
            LastUpload = json;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingSyncClient : ISyncClient
    {
        public Task<string?> GetAsync(CancellationToken cancellationToken = default) =>
            throw new SyncException("The sync remote is down.");

        public Task PutAsync(string json, CancellationToken cancellationToken = default) =>
            throw new SyncException("The sync remote is down.");
    }
}
