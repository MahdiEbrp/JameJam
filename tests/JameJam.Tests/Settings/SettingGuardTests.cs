using JameJam.Settings;

namespace JameJam.Tests.Settings;

/// <summary>Tests for setting validation, including secret-key refusal.</summary>
public sealed class SettingGuardTests
{
    [Theory]
    [InlineData("soroush.apiKey")]
    [InlineData("AI_API_KEY")]
    [InlineData("db.password")]
    [InlineData("auth.token")]
    [InlineData("MY_SECRET")]
    public void EnsureNotSecretKey_RejectsSecretLookingKeys(string key) =>
        Assert.Throws<InvalidOperationException>(() => SettingGuard.EnsureNotSecretKey(key));

    [Theory]
    [InlineData("greeter.defaultName")]
    [InlineData("soroush.model")]
    [InlineData("soroush.endpoint")]
    [InlineData("tools.converter.ratio")]
    public void EnsureNotSecretKey_AllowsNormalKeys(string key)
    {
        SettingGuard.EnsureNotSecretKey(key); // must not throw
    }

    [Fact]
    public void ValidateKey_LongKey_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SettingGuard.ValidateKey(new string('k', 129)));

    [Fact]
    public void ValidateValue_LongValue_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SettingGuard.ValidateValue(new string('v', 8_193)));
}
