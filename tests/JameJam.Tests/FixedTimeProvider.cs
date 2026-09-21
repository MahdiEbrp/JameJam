namespace JameJam.Tests;

/// <summary>A TimeProvider frozen at a fixed instant — deterministic time for tests.</summary>
public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    /// <summary>The frozen instant.</summary>
    public DateTimeOffset Now { get; } = now;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => Now;
}
