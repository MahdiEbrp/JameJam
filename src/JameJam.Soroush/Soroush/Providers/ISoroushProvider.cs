using JameJam.Soroush;

namespace JameJam.Soroush.Providers;

/// <summary>
/// Translates prompts and responses for a specific AI API shape.
/// Implement this (and register it in <see cref="SoroushProviders"/>) to support more APIs.
/// </summary>
public interface ISoroushProvider
{
    /// <summary>Canonical provider name (e.g. <c>"openai"</c>, <c>"anthropic"</c>).</summary>
    string Name { get; }

    /// <summary>Endpoint used when the user did not override it.</summary>
    string DefaultEndpoint { get; }

    /// <summary>Model used when the user did not override it.</summary>
    string DefaultModel { get; }

    /// <summary>Builds the HTTP request for one chat completion.</summary>
    /// <param name="options">Resolved options (endpoint, key, model, limits).</param>
    /// <param name="endpoint">Validated endpoint URI.</param>
    /// <param name="prompt">Already-sanitized prompt.</param>
    HttpRequestMessage BuildRequest(SoroushOptions options, Uri endpoint, string prompt);

    /// <summary>Extracts the completion text from a successful JSON response body.</summary>
    /// <param name="json">Raw response body.</param>
    /// <exception cref="SoroushException">Unexpected response shape or empty completion.</exception>
    string ParseResponse(string json);
}
