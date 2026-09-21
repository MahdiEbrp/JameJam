namespace JameJam.Tests;

/// <summary>
/// Tests that mutate process environment variables must run sequentially —
/// xUnit runs collections one at a time, so everything touching env vars joins this one.
/// </summary>
[CollectionDefinition("EnvSequential")]
public sealed class EnvSequentialTests;
