using JameJam.Settings;

namespace JameJam.Tests.Settings;

/// <summary>Tests for the in-memory settings store (same contract as the SQLite store).</summary>
public sealed class MemorySettingsStoreTests
{
    private readonly MemorySettingsStore _store = new();

    [Fact]
    public void Set_ThenGet_RoundTrips()
    {
        _store.SetValue("key", "value");

        Assert.Equal("value", _store.GetValue("key"));
    }

    [Fact]
    public void Set_OverwritesExistingValue()
    {
        _store.SetValue("key", "old");
        _store.SetValue("key", "new");

        Assert.Equal("new", _store.GetValue("key"));
    }

    [Fact]
    public void Get_MissingKey_ReturnsNull() =>
        Assert.Null(_store.GetValue("nope"));

    [Fact]
    public void GetAll_IsSortedByKey()
    {
        _store.SetValue("b", "2");
        _store.SetValue("a", "1");

        Assert.Equal(["a", "b"], _store.GetAll().Select(entry => entry.Key));
    }

    [Fact]
    public void Remove_RemovesAndReportsExistence()
    {
        _store.SetValue("key", "x");

        Assert.True(_store.Remove("key"));
        Assert.False(_store.Remove("key"));
    }

    [Fact]
    public void Clear_RemovesEverything()
    {
        _store.SetValue("one", "1");
        _store.SetValue("two", "2");

        Assert.Equal(2, _store.Clear());
        Assert.Empty(_store.GetAll());
    }

    [Fact]
    public void CustomValueLimit_IsEnforced()
    {
        var store = new MemorySettingsStore(new SettingsOptions { MaxValueLength = 5 });

        store.SetValue("k", "12345");

        Assert.Throws<ArgumentOutOfRangeException>(() => store.SetValue("k", "123456"));
        Assert.Equal("12345", store.GetValue("k"));
    }

    [Fact]
    public void InvalidOptions_AreRejectedAtConstruction() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MemorySettingsStore(new SettingsOptions { MaxValueLength = 0 }));

    [Fact]
    public void SecretLookingKey_IsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => _store.SetValue("auth.token", "jwt-value"));
        Assert.Null(_store.GetValue("auth.token"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void InvalidKey_Throws(string? key) =>
        Assert.ThrowsAny<ArgumentException>(() => _store.GetValue(key!));
}
