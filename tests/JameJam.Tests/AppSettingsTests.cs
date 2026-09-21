using JameJam.Settings;

namespace JameJam.Tests;

/// <summary>Tests for the <c>settings</c> commands of <see cref="App"/>.</summary>
public sealed class AppSettingsTests
{
    [Fact]
    public async Task List_PrintsAllEntries()
    {
        var store = new MemorySettingsStore();
        store.SetValue("a.first", "1");
        store.SetValue("b.second", "2");
        using StringWriter output = new();
        using StringWriter error = new();
        var app = new App(output, error, store);

        var exitCode = await app.RunAsync(["settings", "list"]);

        Assert.Equal(0, exitCode);
        Assert.Equal($"a.first = 1{Environment.NewLine}b.second = 2{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public async Task Set_ThenGet_RoundTrips()
    {
        var store = new MemorySettingsStore();
        using StringWriter output = new();
        using StringWriter error = new();
        var app = new App(output, error, store);

        var setExit = await app.RunAsync(["settings", "set", "soroush.model", "test-model"]);
        var getExit = await app.RunAsync(["settings", "get", "soroush.model"]);

        Assert.Equal(0, setExit);
        Assert.Equal(0, getExit);
        Assert.Equal("test-model", store.GetValue("soroush.model"));
        Assert.Equal($"test-model{Environment.NewLine}", output.ToString().Split(Environment.NewLine).LastOrDefault(s => s.Length > 0) + Environment.NewLine);
    }

    [Fact]
    public async Task Get_MissingKey_Fails()
    {
        using StringWriter output = new();
        using StringWriter error = new();
        var app = new App(output, error, new MemorySettingsStore());

        var exitCode = await app.RunAsync(["settings", "get", "nope"]);

        Assert.Equal(1, exitCode);
        Assert.Contains("No setting 'nope'.", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_RemovesExistingKey()
    {
        var store = new MemorySettingsStore();
        store.SetValue("gone.soon", "x");
        using StringWriter output = new();
        using StringWriter error = new();
        var app = new App(output, error, store);

        var exitCode = await app.RunAsync(["settings", "remove", "gone.soon"]);

        Assert.Equal(0, exitCode);
        Assert.Null(store.GetValue("gone.soon"));
    }

    [Fact]
    public async Task Clear_RemovesEverything()
    {
        var store = new MemorySettingsStore();
        store.SetValue("one", "1");
        store.SetValue("two", "2");
        using StringWriter output = new();
        using StringWriter error = new();
        var app = new App(output, error, store);

        var exitCode = await app.RunAsync(["settings", "clear"]);

        Assert.Equal(0, exitCode);
        Assert.Empty(store.GetAll());
    }

    [Fact]
    public async Task WithoutStore_SettingsCommand_Fails()
    {
        using StringWriter output = new();
        using StringWriter error = new();
        var app = new App(output, error);

        var exitCode = await app.RunAsync(["settings", "list"]);

        Assert.Equal(1, exitCode);
        Assert.Contains("not available", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecretLookingKey_IsRefused()
    {
        var store = new MemorySettingsStore();
        using StringWriter output = new();
        using StringWriter error = new();
        var app = new App(output, error, store);

        var exitCode = await app.RunAsync(["settings", "set", "ai.apiKey", "super-secret"]);

        Assert.Equal(1, exitCode);
        Assert.Contains("looks like a secret", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(store.GetAll());
    }

    [Fact]
    public async Task Greet_UsesDefaultNameSetting_WhenNoArgs()
    {
        var store = new MemorySettingsStore();
        store.SetValue(SettingKeys.GreeterDefaultName, "Soroush");
        using StringWriter output = new();
        using StringWriter error = new();
        var app = new App(output, error, store);

        var exitCode = await app.RunAsync([]);

        Assert.Equal(0, exitCode);
        Assert.Equal($"Hello, Soroush!{Environment.NewLine}", output.ToString());
    }
}
