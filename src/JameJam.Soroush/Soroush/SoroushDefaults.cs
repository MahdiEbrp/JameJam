namespace JameJam.Soroush;

/// <summary>Sensible defaults for the built-in providers.</summary>
public static class SoroushDefaults
{
    /// <summary>Canonical OpenAI chat-completions endpoint (works with most compatible APIs too).</summary>
    public const string OpenAiCompatibleEndpoint = "https://api.openai.com/v1/chat/completions";

    /// <summary>Canonical Anthropic messages endpoint.</summary>
    public const string AnthropicEndpoint = "https://api.anthropic.com/v1/messages";

    /// <summary>Default OpenAI-compatible model.</summary>
    public const string OpenAiCompatibleModel = "gpt-4o-mini";

    /// <summary>Default Anthropic model.</summary>
    public const string AnthropicModel = "claude-3-5-haiku-latest";
}
