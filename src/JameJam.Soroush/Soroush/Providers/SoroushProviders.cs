using JameJam.Soroush;

namespace JameJam.Soroush.Providers;

/// <summary>
/// Registry of AI providers — the plug-in point for maximum connectivity.
/// Add an entry (optionally under several aliases) to support more APIs.
/// </summary>
public static class SoroushProviders
{
    /// <summary>Canonical name of the OpenAI-compatible provider.</summary>
    public const string OpenAiCompatible = "openai";

    /// <summary>Canonical name of the Anthropic provider.</summary>
    public const string Anthropic = "anthropic";

    private static readonly Dictionary<string, ISoroushProvider> Known = CreateKnown();

    /// <summary>All known provider names (canonical + aliases).</summary>
    public static IReadOnlyCollection<string> Names => [.. Known.Keys];

    /// <summary>Resolves a provider name (case-insensitive, alias-aware). Blank defaults to OpenAI-compatible.</summary>
    /// <param name="name">Provider name or alias, or null for the default.</param>
    /// <exception cref="SoroushException">Unknown provider.</exception>
    public static ISoroushProvider Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Known[OpenAiCompatible];

        return Known.TryGetValue(name.Trim(), out var provider)
            ? provider
            : throw new SoroushException(
                $"Unknown AI provider '{name}'. Known: {string.Join(", ", Known.Keys)}.");
    }

    private static Dictionary<string, ISoroushProvider> CreateKnown()
    {
        ISoroushProvider openAi = new OpenAICompatibleProvider();
        ISoroushProvider anthropic = new AnthropicProvider();

        return new Dictionary<string, ISoroushProvider>(StringComparer.OrdinalIgnoreCase)
        {
            // Canonical
            [OpenAiCompatible] = openAi,
            [Anthropic] = anthropic,

            // OpenAI-compatible aliases (they all speak the same wire format)
            ["openai-compatible"] = openAi,
            ["azure"] = openAi,
            ["groq"] = openAi,
            ["deepseek"] = openAi,
            ["mistral"] = openAi,
            ["openrouter"] = openAi,
            ["together"] = openAi,
            ["xai"] = openAi,
            ["ollama"] = openAi,
            ["lmstudio"] = openAi,

            // Anthropic aliases
            ["claude"] = anthropic,
        };
    }
}
