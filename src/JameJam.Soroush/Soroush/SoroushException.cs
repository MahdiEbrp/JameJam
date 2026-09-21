namespace JameJam.Soroush;

/// <summary>
/// Thrown when an AI provider returns a non-retryable failure, an invalid response,
/// or when the safety layer rejects a request configuration. Messages never contain secrets.
/// </summary>
/// <param name="message">Safe, redacted description of the failure.</param>
/// <param name="statusCode">HTTP status code from the provider, when applicable.</param>
/// <param name="innerException">Original exception, when applicable.</param>
public sealed class SoroushException(string message, int? statusCode = null, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>HTTP status code from the provider, when applicable.</summary>
    public int? StatusCode { get; } = statusCode;
}
