namespace JameJam.Sync;

/// <summary>
/// Thrown when a sync operation fails at the transport or protocol level.
/// Messages are secret-free: bearer tokens are scrubbed before anything is surfaced.
/// </summary>
/// <param name="message">Safe, redacted description of the failure.</param>
/// <param name="statusCode">HTTP status code from the remote, when applicable.</param>
/// <param name="innerException">Original exception, when applicable.</param>
public sealed class SyncException(string message, int? statusCode = null, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>HTTP status code from the remote, when applicable.</summary>
    public int? StatusCode { get; } = statusCode;
}
