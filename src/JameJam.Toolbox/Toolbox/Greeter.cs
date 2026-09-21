namespace JameJam.Toolbox;

/// <summary>
/// Step 1 toolbox tool: builds friendly greetings.
/// Pure and null-safe so it is trivial to test.
/// </summary>
public static class Greeter
{
    /// <summary>
    /// Gets a friendly greeting for <paramref name="name"/>.
    /// </summary>
    /// <param name="name">Optional name. Null, empty, or whitespace greets the world.</param>
    /// <returns>A greeting such as <c>"Hello, Sara!"</c>.</returns>
    public static string GetGreeting(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? "Hello, World!"
            : $"Hello, {name.Trim()}!";
}
