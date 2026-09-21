namespace JameJam.Divan;

/// <summary>Domain error for the Divan pad, rendered as a friendly CLI message.</summary>
public sealed class DivanException(string message) : Exception(message);
