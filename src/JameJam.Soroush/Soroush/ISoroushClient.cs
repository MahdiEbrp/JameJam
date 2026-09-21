namespace JameJam.Soroush;

/// <summary>A client that safely completes prompts against an AI provider.</summary>
public interface ISoroushClient
{
    /// <summary>Sends <paramref name="prompt"/> through the safety layer to the configured provider.</summary>
    /// <param name="prompt">The user prompt. Sanitized before sending.</param>
    /// <param name="cancellationToken">Cancels the whole operation.</param>
    /// <returns>The provider's completion plus metadata.</returns>
    Task<SoroushResult> CompleteAsync(string prompt, CancellationToken cancellationToken = default);
}
