namespace JameJam.Ganjoor;

/// <summary>
/// Thrown when a wallet operation fails at the input, storage, or policy level.
/// Messages are safe to display — no secrets live in the wallet.
/// </summary>
/// <param name="message">Safe description of the failure.</param>
/// <param name="innerException">Original exception, when applicable.</param>
public sealed class GanjoorException(string message, Exception? innerException = null)
    : Exception(message, innerException)
{
}
