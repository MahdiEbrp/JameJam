using JameJam.Soroush;
using JameJam.Soroush.Providers;

namespace JameJam.Tests.Soroush.Providers;

/// <summary>Tests for the provider registry (maximum connectivity plug-in point).</summary>
public sealed class SoroushProvidersTests
{
    [Theory]
    [InlineData("openai")]
    [InlineData("OPENAI")]
    [InlineData("openai-compatible")]
    [InlineData("ollama")]
    [InlineData("groq")]
    [InlineData("anthropic")]
    [InlineData("claude")]
    public void Resolve_KnownNames_ReturnProvider(string name)
    {
        var provider = SoroushProviders.Resolve(name);

        Assert.Contains(provider.Name, new[] { SoroushProviders.OpenAiCompatible, SoroushProviders.Anthropic });
    }

    [Fact]
    public void Resolve_Blank_DefaultsToOpenAiCompatible()
    {
        var provider = SoroushProviders.Resolve(null);

        Assert.Equal(SoroushProviders.OpenAiCompatible, provider.Name);
        Assert.Equal(SoroushDefaults.OpenAiCompatibleEndpoint, provider.DefaultEndpoint);
        Assert.Equal(SoroushDefaults.OpenAiCompatibleModel, provider.DefaultModel);
    }

    [Fact]
    public void Resolve_Unknown_Throws()
    {
        var exception = Assert.Throws<SoroushException>(() => SoroushProviders.Resolve("skynet"));

        Assert.Contains("Unknown AI provider 'skynet'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnthropicProvider_ApiVersion_IsConfigurable()
    {
        var provider = new AnthropicProvider("2024-10-22");
        using var request = provider.BuildRequest(
            new SoroushOptions { ApiKey = "k" },
            new Uri(SoroushDefaults.AnthropicEndpoint),
            "hi");

        Assert.Equal("2024-10-22", request.Headers.GetValues("anthropic-version").Single());
    }

    [Fact]
    public void AnthropicProvider_BlankApiVersion_Throws() =>
        Assert.Throws<ArgumentException>(() => new AnthropicProvider("  "));

    [Fact]
    public void AnthropicProvider_MalformedBody_ProducesFriendlyError()
    {
        var provider = new AnthropicProvider();

        var exception = Assert.Throws<SoroushException>(() => provider.ParseResponse("""{"oops":1}"""));

        Assert.Contains("unexpected Anthropic response shape", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnthropicProvider_EmptyContentBlocks_ProducesFriendlyError()
    {
        var provider = new AnthropicProvider();

        var exception = Assert.Throws<SoroushException>(
            () => provider.ParseResponse("""{"content":[{"type":"tool_use"}]}"""));

        Assert.Contains("no text blocks", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnthropicProvider_EmptyText_ThrowsViaSafetyLayer()
    {
        var provider = new AnthropicProvider();

        Assert.Throws<SoroushException>(
            () => provider.ParseResponse("""{"content":[{"type":"text","text":"   "}]}"""));
    }

    [Fact]
    public void OpenAICompatibleProvider_MalformedBody_ProducesFriendlyError()
    {
        var provider = new OpenAICompatibleProvider();

        var exception = Assert.Throws<SoroushException>(() => provider.ParseResponse("""{"unexpected":1}"""));

        Assert.Contains("unexpected OpenAI-compatible response shape", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Names_IncludesCanonicalProviders()
    {
        Assert.Contains(SoroushProviders.OpenAiCompatible, SoroushProviders.Names);
        Assert.Contains(SoroushProviders.Anthropic, SoroushProviders.Names);
    }
}
