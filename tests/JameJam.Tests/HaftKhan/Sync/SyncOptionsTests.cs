using System.Net;
using System.Text;
using JameJam.HaftKhan;
using JameJam.Sync;

namespace JameJam.Tests.HaftKhan.Sync;

/// <summary>Tests for sync option validation (rails, endpoint policy).</summary>
public sealed class SyncOptionsTests
{
    [Fact]
    public void ValidOptions_Pass() =>
        new SyncOptions { Endpoint = "https://sync.example.com/todos.json" }.Validate(); // must not throw

    [Fact]
    public void LoopbackHttp_IsAllowed() =>
        new SyncOptions { Endpoint = "http://127.0.0.1:8080/todos.json" }.Validate();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyEndpoint_Throws(string endpoint) =>
        Assert.Throws<SyncException>(() => new SyncOptions { Endpoint = endpoint }.Validate());

    [Theory]
    [InlineData("http://api.example.com/todos.json")]
    [InlineData("ftp://sync.example.com")]
    [InlineData("not-a-url")]
    public void InsecureOrInvalidEndpoint_Throws(string endpoint) =>
        Assert.Throws<SyncException>(() => new SyncOptions { Endpoint = endpoint }.Validate());

    [Fact]
    public void EmptyToken_Throws()
    {
        var exception = Assert.Throws<SyncException>(
            () => new SyncOptions { Endpoint = "https://x.test", BearerToken = string.Empty }.Validate());
        Assert.Contains("Bearer token", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ZeroTimeout_Throws() =>
        Assert.Throws<SyncException>(
            () => new SyncOptions { Endpoint = "https://x.test", RequestTimeout = TimeSpan.Zero }.Validate());

    [Fact]
    public void TooManyRetries_Throws() =>
        Assert.Throws<SyncException>(
            () => new SyncOptions { Endpoint = "https://x.test", MaxRetries = 11 }.Validate());

    [Fact]
    public void OversizedResponseCap_Throws() =>
        Assert.Throws<SyncException>(
            () => new SyncOptions { Endpoint = "https://x.test", MaxResponseBytes = SyncDefaults.MaxResponseBytesBound + 1 }.Validate());
}
