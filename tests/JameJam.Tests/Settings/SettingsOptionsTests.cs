using JameJam.Settings;

namespace JameJam.Tests.Settings;

/// <summary>Proves the settings layer is fully configurable through validated options.</summary>
public sealed class SettingsOptionsTests
{
    [Fact]
    public void Defaults_AreValid()
    {
        new SettingsOptions().Validate(); // must not throw
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4_097)]
    public void Validate_RejectsOutOfRangeKeyLimit(int maxKeyLength) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SettingsOptions { MaxKeyLength = maxKeyLength }.Validate());

    [Theory]
    [InlineData(0)]
    [InlineData(1_000_001)]
    public void Validate_RejectsOutOfRangeValueLimit(int maxValueLength) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SettingsOptions { MaxValueLength = maxValueLength }.Validate());

    [Fact]
    public void Validate_RejectsNullNeedles() =>
        Assert.Throws<ArgumentException>(
            () => new SettingsOptions { SecretKeyNeedles = null! }.Validate());
}
