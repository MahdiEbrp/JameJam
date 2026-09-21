namespace JameJam.Anahita;

/// <summary>
/// Thrown when a weather request fails at the transport, protocol, or input level.
/// Messages are secret-free: API keys are scrubbed before anything is surfaced.
/// </summary>
/// <param name="message">Safe, redacted description of the failure.</param>
/// <param name="statusCode">HTTP status code from the endpoint, when applicable.</param>
/// <param name="innerException">Original exception, when applicable.</param>
public sealed class AnahitaException(string message, int? statusCode = null, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>HTTP status code from the endpoint, when applicable.</summary>
    public int? StatusCode { get; } = statusCode;
}
