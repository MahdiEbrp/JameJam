using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using JameJam.Soroush;

namespace JameJam.Soroush.Providers;

/// <summary>
/// OpenAI-compatible chat completions. Works with OpenAI and most compatible APIs:
/// Groq, DeepSeek, Mistral, OpenRouter, Together, xAI, Ollama, LM Studio, llama.cpp, ...
/// </summary>
public sealed class OpenAICompatibleProvider : ISoroushProvider
{
    private const string JsonMediaType = "application/json";

    /// <inheritdoc />
    public string Name => SoroushProviders.OpenAiCompatible;

    /// <inheritdoc />
    public string DefaultEndpoint => SoroushDefaults.OpenAiCompatibleEndpoint;

    /// <inheritdoc />
    public string DefaultModel => SoroushDefaults.OpenAiCompatibleModel;

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
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        return request;
    }

    /// <inheritdoc />
    public string ParseResponse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var content = document.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            return SoroushGuard.ValidateResponse(content);
        }
        catch (Exception ex) when (
            ex is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
        {
            throw new SoroushException(
                "The AI provider returned an unexpected OpenAI-compatible response shape.", innerException: ex);
        }
    }
}
