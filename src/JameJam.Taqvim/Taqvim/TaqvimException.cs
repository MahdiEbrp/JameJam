namespace JameJam.Taqvim;

/// <summary>Thrown when a Taqvim operation fails; messages are user-facing and secret-free.</summary>
/// <param name="message">Safe description of the failure.</param>
public sealed class TaqvimException(string message) : Exception(message);
