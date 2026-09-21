namespace JameJam.Soroush;

/// <summary>Outcome of a successful AI completion.</summary>
/// <param name="Content">The completion text (sanitized).</param>
/// <param name="Provider">Canonical provider name that served the request.</param>
/// <param name="Model">Model identifier used for the request.</param>
/// <param name="Attempts">HTTP attempts consumed (1 = no retry needed).</param>
/// <param name="Duration">Total wall-clock time including retries.</param>
public sealed record SoroushResult(
    string Content,
    string Provider,
    string? Model,
    int Attempts,
    TimeSpan Duration);
