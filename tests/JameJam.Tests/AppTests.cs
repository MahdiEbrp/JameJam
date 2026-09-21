namespace JameJam.Tests;

/// <summary>Tests for <see cref="App"/> — greeting and routing.</summary>
public sealed class AppTests
{
    [Fact]
    public void Constructor_WithNullOutput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new App(null!));
    }

    [Fact]
    public async Task RunAsync_WithNullArgs_Throws()
    {
        using StringWriter writer = new();
        var app = new App(writer);

        await Assert.ThrowsAsync<ArgumentNullException>(() => app.RunAsync(null!));
    }

    [Fact]
    public async Task RunAsync_WithNoArgs_GreetsWorldAndReturnsZero()
    {
        using StringWriter writer = new();
        var app = new App(writer);

        var exitCode = await app.RunAsync([]);

        Assert.Equal(0, exitCode);
        Assert.Equal($"Hello, World!{Environment.NewLine}", writer.ToString());
    }

    [Fact]
    public async Task RunAsync_WithName_GreetsNameAndReturnsZero()
    {
        using StringWriter writer = new();
        var app = new App(writer);

        var exitCode = await app.RunAsync(["Sara"]);

        Assert.Equal(0, exitCode);
        Assert.Equal($"Hello, Sara!{Environment.NewLine}", writer.ToString());
    }

    [Fact]
    public async Task RunAsync_WithAnyOtherFirstArg_TreatsItAsName()
    {
        using StringWriter writer = new();
        var app = new App(writer);

        await app.RunAsync(["toolbox"]);

        Assert.Equal($"Hello, toolbox!{Environment.NewLine}", writer.ToString());
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("-?")]
    public async Task RunAsync_WithHelp_PrintsUsageAndReturnsZero(string flag)
    {
        using StringWriter writer = new();
        var app = new App(writer);

        var exitCode = await app.RunAsync([flag]);
        var output = writer.ToString();

        Assert.Equal(0, exitCode);
        Assert.Contains("Usage:", output, StringComparison.Ordinal);
        Assert.Contains("JameJam", output, StringComparison.Ordinal);
        Assert.Contains("soroush", output, StringComparison.Ordinal);
        Assert.Contains("settings", output, StringComparison.Ordinal);
    }
}
