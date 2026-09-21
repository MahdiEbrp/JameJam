using JameJam.Text;

namespace JameJam.Tests.Text;

/// <summary>Tests for the shared text safety layer.</summary>
public sealed class TextGuardTests
{
    [Fact]
    public void SanitizeRequired_StripsControlChars_Trims_AndKeepsFormatting()
    {
        Assert.Equal("line1\nline2\tend", TextGuard.SanitizeRequired("  line1\nline2\u0007\tend  ", 100, "input"));
        Assert.Equal("ab", TextGuard.SanitizeRequired("a\u007fb", 100, "input"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\0\u0001")]
    public void SanitizeRequired_EmptyOrUnreadable_Throws(string? value) =>
        Assert.Throws<ArgumentException>(() => TextGuard.SanitizeRequired(value, 100, "input"));

    [Fact]
    public void SanitizeRequired_TooLong_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TextGuard.SanitizeRequired("abcdef", 5, "input"));

    [Fact]
    public void SanitizeRequired_CleanValue_ReturnsUnchanged_NoAllocations()
    {
        var value = new string('x', 5_000);

        Assert.Equal(value, TextGuard.SanitizeRequired(value, 5_000, "value"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SanitizeOptional_EmptyBecomesEmptyString(string? value) =>
        Assert.Equal(string.Empty, TextGuard.SanitizeOptional(value, 100, "input"));

    [Fact]
    public void SanitizeOptional_StripsControlChars() =>
        Assert.Equal("note text", TextGuard.SanitizeOptional(" note\u0002 text ", 100, "input"));

    [Fact]
    public void SanitizeOptional_TooLong_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TextGuard.SanitizeOptional("abcdef", 5, "input"));

    [Fact]
    public void SanitizeRequired_ControlCharsAndTooLong_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TextGuard.SanitizeRequired("ab\u0002cdef", 5, "input")); // slow path + limit

    [Fact]
    public void SanitizeOptional_ControlCharsAndTooLong_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TextGuard.SanitizeOptional("ab\u0002cdef", 5, "input"));
}
