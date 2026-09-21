using System.Text;
using System.Text.Json;
using JameJam.Soroush;

namespace JameJam.Soroush.Providers;

/// <summary>Anthropic messages API (Claude models). The API version header is configurable.</summary>
/// <param name="apiVersion">Value of the <c>anthropic-version</c> header.</param>
public sealed class AnthropicProvider(string apiVersion = AnthropicProvider.DefaultApiVersion) : ISoroushProvider
{
    private const string JsonMediaType = "application/json";
    private const string ApiVersionHeader = "anthropic-version";
    private const string ApiKeyHeader = "x-api-key";

    /// <summary>API version sent when no override is configured.</summary>
    public const string DefaultApiVersion = "2023-06-01";

    private readonly string _apiVersion = string.IsNullOrWhiteSpace(apiVersion)
        ? throw new ArgumentException("API version must not be empty.", nameof(apiVersion))
        : apiVersion;

    /// <inheritdoc />
    public string Name => SoroushProviders.Anthropic;

    /// <inheritdoc />
    public string DefaultEndpoint => SoroushDefaults.AnthropicEndpoint;

    /// <inheritdoc />
    public string DefaultModel => SoroushDefaults.AnthropicModel;

    /// <inheritdoc />
    public HttpRequestMessage BuildRequest(SoroushOptions options, Uri endpoint, string prompt)
    {
        var payload = JsonSerializer.Serialize(new
        {
            model = options.Model ?? DefaultModel,
            max_tokens = options.MaxTokens,
            messages = new[] { new { role = "user", content = prompt } },
        });

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, JsonMediaType),
        };
        request.Headers.Add(ApiKeyHeader, options.ApiKey);
        request.Headers.Add(ApiVersionHeader, _apiVersion);
        return request;
    }

    /// <inheritdoc />
    public string ParseResponse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            foreach (var block in document.RootElement.GetProperty("content").EnumerateArray())
            {
                if (block.TryGetProperty("text", out var text))
                    return SoroushGuard.ValidateResponse(text.GetString());
            }

            throw new SoroushException("The Anthropic response contained no text blocks.");
        }
        catch (Exception ex) when (
            ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new SoroushException(
                "The AI provider returned an unexpected Anthropic response shape.", innerException: ex);
        }
    }
}
