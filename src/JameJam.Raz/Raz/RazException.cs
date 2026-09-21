namespace JameJam.Raz;

/// <summary>Domain error for the Raz vault, rendered as a friendly CLI message.</summary>
public sealed class RazException(string message) : Exception(message);
