using JameJam.Toolbox;

namespace JameJam.Tests.Toolbox;

/// <summary>
/// Step 1 actual tests for <see cref="Greeter"/>.
/// </summary>
public sealed class GreeterTests
{
    [Theory]
    [InlineData(null, "Hello, World!")]
    [InlineData("", "Hello, World!")]
    [InlineData("   ", "Hello, World!")]
    [InlineData("JameJam", "Hello, JameJam!")]
    [InlineData("  Sara  ", "Hello, Sara!")]
    public void GetGreeting_ReturnsExpected(string? name, string expected)
    {
        // Act
        var actual = Greeter.GetGreeting(name);

        // Assert
        Assert.Equal(expected, actual);
    }
}
