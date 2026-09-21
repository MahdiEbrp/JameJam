using JameJam.Settings;

namespace JameJam.Tests.Settings;

/// <summary>Tests for the SQLite settings store (real database in a temp file, parameterized + safe).</summary>
public sealed class SqliteSettingsStoreTests : IDisposable
{
    private readonly TempDatabase _database = new();
    private readonly SqliteSettingsStore _store;

    public SqliteSettingsStoreTests() => _store = new SqliteSettingsStore(_database.DbPath);

    public void Dispose() => _database.Dispose();

    [Fact]
    public void Set_ThenGet_RoundTrips()
    {
        _store.SetValue("soroush.model", "test-model");

        Assert.Equal("test-model", _store.GetValue("soroush.model"));
    }

    [Fact]
    public void Set_OverwritesExistingValue()
    {
        _store.SetValue("key", "old");
        _store.SetValue("key", "new");

        Assert.Equal("new", _store.GetValue("key"));
        var entry = Assert.Single(_store.GetAll());
        Assert.Equal("new", entry.Value);
    }

    [Fact]
    public void Set_NullValue_StoresEmptyString()
    {
        _store.SetValue("key", null);

        Assert.Equal(string.Empty, _store.GetValue("key"));
    }

    [Fact]
    public void Get_MissingKey_ReturnsNull() =>
        Assert.Null(_store.GetValue("nope"));

    [Fact]
    public void GetAll_IsSortedByKey_WithTimestamps()
    {
        _store.SetValue("b.key", "2");
        _store.SetValue("a.key", "1");

        var entries = _store.GetAll();

        Assert.Equal(["a.key", "b.key"], entries.Select(entry => entry.Key));
        Assert.All(entries, entry => Assert.True(entry.UpdatedAt <= DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Remove_RemovesAndReportsExistence()
    {
        _store.SetValue("key", "x");

        Assert.True(_store.Remove("key"));
        Assert.False(_store.Remove("key"));
        Assert.Null(_store.GetValue("key"));
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
    public void Data_PersistsAcrossStoreInstances()
    {
        _store.SetValue("durable", "yes");

        var reopened = new SqliteSettingsStore(_database.DbPath);

        Assert.Equal("yes", reopened.GetValue("durable"));
    }

    [Fact]
    public void EmptyValue_RoundTrips()
    {
        _store.SetValue("empty", string.Empty);

        Assert.Equal(string.Empty, _store.GetValue("empty"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void InvalidKey_Throws(string? key) =>
        Assert.ThrowsAny<ArgumentException>(() => _store.SetValue(key!, "value"));

    [Fact]
    public void TooLongKey_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => _store.SetValue(new string('k', 129), "value"));

    [Fact]
    public void TooLongValue_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => _store.SetValue("key", new string('v', 8_193)));

    [Fact]
    public void SecretLookingKey_IsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => _store.SetValue("db.password", "hunter2"));
        Assert.Null(_store.GetValue("db.password"));
    }

    [Fact]
    public void DatabaseFile_IsOwnerOnly_OnUnix()
    {
        _store.SetValue("touch", "database"); // forces file creation

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return; // Unix-only hardening check

        var forbidden = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

        Assert.True(
            (File.GetUnixFileMode(Path.GetFullPath(_database.DbPath)) & forbidden) == 0,
            "The settings database must be readable/writable by the owner only.");
    }

    [Fact]
    public void CustomValueLimit_IsEnforced()
    {
        var store = new SqliteSettingsStore(_database.DbPath, new SettingsOptions { MaxValueLength = 5 });

        store.SetValue("k", "12345");

        Assert.Throws<ArgumentOutOfRangeException>(() => store.SetValue("k", "123456"));
        Assert.Equal("12345", store.GetValue("k"));
    }

    [Fact]
    public void CustomSecretNeedles_AreEnforced()
    {
        var store = new SqliteSettingsStore(_database.DbPath, new SettingsOptions
        {
            SecretKeyNeedles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "hidden" },
        });

        Assert.Throws<InvalidOperationException>(() => store.SetValue("something.hidden", "x"));
        store.SetValue("something.visible", "x"); // default needles do not apply anymore
    }

    [Fact]
    public void InvalidOptions_AreRejectedAtConstruction() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SqliteSettingsStore(_database.DbPath, new SettingsOptions { MaxKeyLength = 0 }));

    /// <summary>A unique temp SQLite file that cleans up after itself (including WAL sidecars).</summary>
    private sealed class TempDatabase : IDisposable
    {
        public string DbPath { get; } = Path.Combine(
            Path.GetTempPath(), $"jamejam-tests-{Guid.NewGuid():N}.db");

        public void Dispose()
        {
            foreach (var path in new[] { DbPath, DbPath + "-wal", DbPath + "-shm" })
            {
                try
                {
                    File.Delete(path);
                }
                catch (FileNotFoundException)
                {
                    // Already gone — nothing to clean up.
                }
            }
        }
    }
}
