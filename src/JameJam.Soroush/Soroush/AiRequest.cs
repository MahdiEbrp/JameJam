namespace JameJam.Soroush;

/// <summary>One AI completion request routed through the safety layer.</summary>
/// <param name="Prompt">The prompt to complete (sanitized before sending).</param>
/// <param name="Provider">Provider override; falls back to settings, then the default provider.</param>
/// <param name="Model">Model override; falls back to settings, then the provider default.</param>
/// <param name="Endpoint">Endpoint override; falls back to settings, then the provider default.</param>
public sealed record AiRequest(
    string Prompt,
    string? Provider = null,
    string? Model = null,
    string? Endpoint = null);
