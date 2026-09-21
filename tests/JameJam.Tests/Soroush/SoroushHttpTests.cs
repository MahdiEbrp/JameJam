using JameJam.Soroush;

namespace JameJam.Tests.Soroush;

/// <summary>Tests for the hardened HTTP plumbing.</summary>
public sealed class SoroushHttpTests
{
    [Fact]
    public void CreateHandler_DisablesRedirects()
    {
        using var handler = SoroushHttp.CreateHandler();

        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public void CreateClient_ReturnsWorkingClient()
    {
        using var client = SoroushHttp.CreateClient();

        Assert.NotNull(client);
        Assert.Equal(TimeSpan.FromSeconds(100), client.Timeout); // framework default; per-attempt timeouts live in SoroushClient
    }
}
