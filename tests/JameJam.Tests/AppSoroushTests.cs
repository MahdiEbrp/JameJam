using JameJam.Settings;
using JameJam.Soroush;

namespace JameJam.Tests;

/// <summary>Tests for the <c>soroush</c> command of <see cref="App"/> (safety gating, options flow).</summary>
[Collection("EnvSequential")]
public sealed class AppSoroushTests
{
    private const string OkJson = """{"choices":[{"message":{"content":"fake answer"}}]}""";

    [Fact]
    public async Task MissingKey_OnNonLoopbackEndpoint_Fails()
    {
        Environment.SetEnvironmentVariable(App.ApiKeyEnvironmentVariable, null);
        try
        {
            var factory = new RecordingFactory();
            using StringWriter output = new();
            using StringWriter error = new();
            var app = new App(output, error, new MemorySettingsStore(), factory.Create);

            var exitCode = await app.RunAsync(["soroush", "hi"]);

            Assert.Equal(1, exitCode);
            Assert.Contains(App.ApiKeyEnvironmentVariable, error.ToString(), StringComparison.Ordinal);
            Assert.Empty(factory.Options);
        }
        finally
        {
            Environment.SetEnvironmentVariable(App.ApiKeyEnvironmentVariable, null);
        }
    }

    [Fact]
    public async Task LoopbackEndpoint_AllowsMissingKey()
    {
        Environment.SetEnvironmentVariable(App.ApiKeyEnvironmentVariable, null);
        try
        {
            var factory = new RecordingFactory(new StubSoroushClient());
            using StringWriter output = new();
            using StringWriter error = new();
            var app = new App(output, error, new MemorySettingsStore(), factory.Create);

            var exitCode = await app.RunAsync([
                "soroush", "--endpoint", "http://127.0.0.1:11434/v1/chat/completions", "hi"]);

            Assert.Equal(0, exitCode);
            Assert.Equal($"fake answer{Environment.NewLine}", output.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(App.ApiKeyEnvironmentVariable, null);
        }
    }

    [Fact]
    public async Task Flags_FlowIntoOptions_AndPromptIsJoined()
    {
        Environment.SetEnvironmentVariable(App.ApiKeyEnvironmentVariable, "test-key");
        try
        {
            var factory = new RecordingFactory(new StubSoroushClient());
            using StringWriter output = new();
            using StringWriter error = new();
            var app = new App(output, error, new MemorySettingsStore(), factory.Create);

            var exitCode = await app.RunAsync([
                "soroush", "--provider", "anthropic", "--model", "my-model", "hello", "world"]);

            Assert.Equal(0, exitCode);
            var options = Assert.Single(factory.Options);
            Assert.Equal("anthropic", options.Provider);
            Assert.Equal("my-model", options.Model);
            Assert.Equal("https://api.anthropic.com/v1/messages", options.Endpoint);
            Assert.Equal("test-key", options.ApiKey);
            Assert.Equal("hello world", Assert.Single(factory.Client.Prompts));
        }
        finally
        {
            Environment.SetEnvironmentVariable(App.ApiKeyEnvironmentVariable, null);
        }
    }

    [Fact]
    public async Task Settings_ProvideDefaults()
    {
        Environment.SetEnvironmentVariable(App.ApiKeyEnvironmentVariable, "test-key");
        try
        {
            var store = new MemorySettingsStore();
            store.SetValue(SettingKeys.SoroushModel, "saved-model");
            store.SetValue(SettingKeys.SoroushProvider, "anthropic");
            var factory = new RecordingFactory(new StubSoroushClient());
            using StringWriter output = new();
            using StringWriter error = new();
            var app = new App(output, error, store, factory.Create);

            await app.RunAsync(["soroush", "hi"]);

            var options = Assert.Single(factory.Options);
            Assert.Equal("anthropic", options.Provider);
            Assert.Equal("saved-model", options.Model);
            Assert.Equal("https://api.anthropic.com/v1/messages", options.Endpoint);
        }
        finally
        {
            Environment.SetEnvironmentVariable(App.ApiKeyEnvironmentVariable, null);
        }
    }

    [Fact]
    public async Task EmptyPrompt_Fails()
    {
        var factory = new RecordingFactory(new StubSoroushClient());
        using StringWriter output = new();
        using StringWriter error = new();
        var app = new App(output, error, new MemorySettingsStore(), factory.Create);

        var exitCode = await app.RunAsync(["soroush"]);

        Assert.Equal(1, exitCode);
        Assert.Contains("Usage:", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(factory.Options);
    }

    [Fact]
    public async Task TooLongPrompt_FailsWithoutHttpCall()
    {
        Environment.SetEnvironmentVariable(App.ApiKeyEnvironmentVariable, "test-key");
        try
        {
            var factory = new RecordingFactory(new StubSoroushClient());
            using StringWriter output = new();
            using StringWriter error = new();
            var app = new App(output, error, new MemorySettingsStore(), factory.Create);

            var exitCode = await app.RunAsync(["soroush", new string('a', 8_001)]);

            Assert.Equal(1, exitCode);
            Assert.Contains("maximum length", error.ToString(), StringComparison.Ordinal);
            Assert.Empty(factory.Options);
        }
        finally
        {
            Environment.SetEnvironmentVariable(App.ApiKeyEnvironmentVariable, null);
        }
    }

    [Fact]
    public async Task UnknownProvider_Fails()
    {
        Environment.SetEnvironmentVariable(App.ApiKeyEnvironmentVariable, "test-key");
        try
        {
            var factory = new RecordingFactory(new StubSoroushClient());
            using StringWriter output = new();
            using StringWriter error = new();
            var app = new App(output, error, new MemorySettingsStore(), factory.Create);

            var exitCode = await app.RunAsync(["soroush", "--provider", "skynet", "hi"]);

            Assert.Equal(1, exitCode);
            Assert.Contains("Unknown AI provider 'skynet'", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(App.ApiKeyEnvironmentVariable, null);
        }
    }

    [Fact]
    public async Task ClientFailure_SurfacesMessage()
    {
        Environment.SetEnvironmentVariable(App.ApiKeyEnvironmentVariable, "test-key");
        try
        {
            var factory = new RecordingFactory(
                new StubSoroushClient { Throw = new SoroushException("provider exploded") });
            using StringWriter output = new();
            using StringWriter error = new();
            var app = new App(output, error, new MemorySettingsStore(), factory.Create);

            var exitCode = await app.RunAsync(["soroush", "hi"]);

            Assert.Equal(1, exitCode);
            Assert.Contains("provider exploded", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(App.ApiKeyEnvironmentVariable, null);
        }
    }

    private sealed class StubSoroushClient : ISoroushClient
    {
        public SoroushException? Throw { get; init; }

        public List<string> Prompts { get; } = [];

        public Task<SoroushResult> CompleteAsync(string prompt, CancellationToken cancellationToken = default)
        {
            Prompts.Add(prompt);
            return Throw is null
                ? Task.FromResult(new SoroushResult("fake answer", "stub", "stub-model", 1, TimeSpan.Zero))
                : Task.FromException<SoroushResult>(Throw);
        }
    }

    private sealed class RecordingFactory(ISoroushClient? client = null)
    {
        public List<SoroushOptions> Options { get; } = [];

        public StubSoroushClient Client { get; } = client as StubSoroushClient ?? new StubSoroushClient();

        public StubSoroushClient Create(SoroushOptions options)
        {
            Options.Add(options);
            return Client;
        }
    }
}
