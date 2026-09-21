using JameJam.Raz;
using JameJam.Raz.Crypto;

namespace JameJam.Tests.Raz;

/// <summary>Vault lifecycle: init, unlock, entry CRUD, undo, audit, expiry, backup.</summary>
public sealed class VaultServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly MemoryVaultStore _store = new();
    private readonly VaultService _service;

    public VaultServiceTests() =>
        _service = new VaultService(_store, new FixedTimeProvider(Now), new RazOptions(Iterations: RazDefaults.MinIterations));

    private void Unlock()
    {
        if (!_store.IsInitialized)
        {
            _service.Init("correct-horse-battery");
        }

        _service.Unlock("correct-horse-battery");
    }

    [Fact]
    public void Init_CreatesTheVault_AndUnlocksIt()
    {
        _service.Init("correct-horse-battery");
        Assert.True(_store.IsInitialized);
        Assert.True(_service.IsUnlocked);
        Assert.Equal(RazDefaults.MinIterations, _store.GetIterations());
        Assert.Equal(RazDefaults.SaltSizeBytes, _store.GetSalt()!.Length);
    }

    [Fact]
    public void Init_Twice_Fails()
    {
        _service.Init("correct-horse-battery");
        Assert.Throws<RazException>(() => _service.Init("another"));
    }

    [Fact]
    public void Unlock_WrongPassphrase_IsRejected()
    {
        _service.Init("correct-horse-battery");
        var second = new VaultService(_store, new FixedTimeProvider(Now), new RazOptions(Iterations: RazDefaults.MinIterations));
        var exception = Assert.Throws<RazException>(() => second.Unlock("wrong"));
        Assert.Equal("Wrong passphrase or corrupted vault.", exception.Message);
        Assert.False(second.IsUnlocked);
    }

    [Fact]
    public void Operations_BeforeInit_AreGuided()
    {
        var exception = Assert.Throws<RazException>(() => _service.ListEntries());
        Assert.Contains("No vault yet", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddEntry_EncryptsSensitiveFields_AtRest()
    {
        Unlock();
        var entry = _service.AddEntry("GitHub", "hunter2!", username: "octocat", tags: "code,work");
        Assert.Equal(1, entry.Id);

        var stored = _store.FindEntry(1)!;
        Assert.NotEqual("GitHub", stored.Title);
        Assert.NotEqual("hunter2!", stored.Secret);
        Assert.NotEqual("octocat", stored.Username);

        var decrypted = _service.FindEntry(1)!;
        Assert.Equal(("GitHub", "hunter2!", "octocat"), (decrypted.Title, decrypted.Secret, decrypted.Username));
    }

    [Fact]
    public void AddEntry_WithoutSecret_IsRejected()
    {
        Unlock();
        var exception = Assert.Throws<RazException>(() => _service.AddEntry("Empty", ""));
        Assert.Contains("A secret is required", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ListEntries_Filters()
    {
        Unlock();
        _ = _service.AddEntry("A", "secret-a", tags: "work", favorite: true);
        _ = _service.AddEntry("B", "abc", tags: "personal");
        _ = _service.AddEntry("C", "secret-c", notes: "needle-in-notes", expiresOn: new DateOnly(2026, 1, 1));

        Assert.Equal(3, _service.ListEntries().Count);
        Assert.Single(_service.ListEntries(new VaultFilter(Tag: "work")));
        Assert.Single(_service.ListEntries(new VaultFilter(WeakOnly: true)));
        Assert.Single(_service.ListEntries(new VaultFilter(ExpiredOnly: true)));
        Assert.Single(_service.ListEntries(new VaultFilter(FavoritesOnly: true)));
        Assert.Single(_service.ListEntries(new VaultFilter(Query: "needle")));
    }

    [Fact]
    public void UpdateEntry_ReEncrypts_AndStampsTime()
    {
        Unlock();
        var entry = _service.AddEntry("Old", "secret-one");
        var updated = _service.UpdateEntry(entry.Id, entry with { Title = "New", Secret = "secret-two", Username = "u" });

        Assert.Equal("New", updated.Title);
        Assert.Equal("secret-two", updated.Secret);
        Assert.Equal(Now, updated.UpdatedAt);
        Assert.Equal("secret-two", _service.FindEntry(entry.Id)!.Secret);
    }

    [Fact]
    public void Update_MissingEntry_Fails()
    {
        Unlock();
        RazEntry entry = new(9, "T", "s", "", "", "", "", "", TotpAlgorithm.None, 0, 0, null, false, Now, Now);
        Assert.Throws<RazException>(() => _service.UpdateEntry(9, entry));
    }

    [Fact]
    public void Delete_ThenUndo_RestoresEverything()
    {
        Unlock();
        var entry = _service.AddEntry("Keep me", "secret");
        Assert.Equal(1, _store.UndoCount); // the add snapshot

        var deleted = _service.DeleteEntry(entry.Id);
        Assert.Equal("Keep me", deleted.Title);
        Assert.Null(_service.FindEntry(entry.Id));

        Assert.True(_service.Undo());
        Assert.Equal("Keep me", _service.FindEntry(entry.Id)!.Title);

        Assert.True(_service.Undo()); // the add snapshot: back to an empty vault
        Assert.Null(_service.FindEntry(entry.Id));

        Assert.False(_service.Undo()); // nothing left
    }

    [Fact]
    public void UndoDepth_TrimsOldest()
    {
        var tight = new VaultService(
            _store,
            new FixedTimeProvider(Now),
            new RazOptions(Iterations: RazDefaults.MinIterations, UndoDepth: 1));
        tight.Init("correct-horse-battery");
        _ = tight.AddEntry("A", "secret-a");
        Assert.Equal(1, _store.UndoCount);
        _ = tight.AddEntry("B", "secret-b");
        Assert.Equal(1, _store.UndoCount); // oldest trimmed

        Assert.True(tight.Undo()); // removes B
        Assert.Equal("A", tight.FindEntry(1)!.Title);
        Assert.Null(tight.FindEntry(2));
    }

    [Fact]
    public void Audit_SumsTheRightThings()
    {
        Unlock();
        _ = _service.AddEntry("weak", "abc", expiresOn: new DateOnly(2026, 1, 1));
        _ = _service.AddEntry("shared1", "Same-Secret-42");
        _ = _service.AddEntry("shared2", "Same-Secret-42");
        _ = _service.AddEntry("fresh", "Long-And-Unique-99", expiresOn: new DateOnly(2026, 10, 1));

        var stats = _service.Audit();
        Assert.Equal(4, stats.TotalEntries);
        Assert.Equal(1, stats.WeakCount);
        Assert.Equal(2, stats.ReusedCount);
        Assert.Equal(1, stats.ExpiredCount);
        Assert.Equal(1, stats.ExpiringSoonCount);
        Assert.Equal(3, stats.UniqueSecrets);
        Assert.Equal((3 + 14 + 14 + 17) / 4, stats.AverageSecretLength);
    }

    [Fact]
    public void ExpiringWithin_OrdersByDate_AndRejectsWildWindows()
    {
        Unlock();
        _ = _service.AddEntry("soon", "secret-soon", expiresOn: new DateOnly(2026, 10, 1));
        _ = _service.AddEntry("later", "secret-later", expiresOn: new DateOnly(2026, 11, 15));

        Assert.Single(_service.ExpiringWithin());
        Assert.Equal(2, _service.ExpiringWithin(90).Count);
        Assert.Empty(_service.ExpiringWithin(1));
        Assert.Throws<RazException>(() => _service.ExpiringWithin(0));
    }

    [Fact]
    public void TotpNow_ReturnsCodeAndWindow()
    {
        Unlock();
        var entry = _service.AddEntry("Authy", "its-a-password", totpSeed: "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", totpAlgorithm: TotpAlgorithm.Sha1);
        var totp = _service.TotpNow(entry.Id)!;
        Assert.Equal(6, totp.Value.Code.Length);
        Assert.InRange(totp.Value.SecondsRemaining, 1, 30);

        var plain = _service.AddEntry("No totp", "secret");
        Assert.Null(_service.TotpNow(plain.Id));
    }

    [Fact]
    public void Totp_NeedsASeed_WhenAlgorithmIsSet()
    {
        Unlock();
        var exception = Assert.Throws<RazException>(
            () => _service.AddEntry("Broken", "secret", totpAlgorithm: TotpAlgorithm.Sha256));
        Assert.Contains("TOTP seed is required", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TotpParameters_AreRailed()
    {
        Unlock();
        var seed = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
        Assert.Throws<RazException>(() => _service.AddEntry("X", "secret", totpSeed: seed, totpDigits: 5));
        Assert.Throws<RazException>(() => _service.AddEntry("X", "secret", totpSeed: seed, totpDigits: 9));
        Assert.Throws<RazException>(() => _service.AddEntry("X", "secret", totpSeed: seed, totpPeriodSeconds: 10));
        Assert.Throws<RazException>(() => _service.AddEntry("X", "secret", totpSeed: seed, totpPeriodSeconds: 121));

        var entry = _service.AddEntry("Ok", "secret", totpSeed: seed, totpDigits: 8, totpPeriodSeconds: 60);
        Assert.Equal((8, 60), (entry.TotpDigits, entry.TotpPeriodSeconds));
    }

    [Fact]
    public void ExpiringWithin_IncludesToday_AndExcludesBeyondTheWindow()
    {
        Unlock();
        _ = _service.AddEntry("today", "s1", expiresOn: new DateOnly(2026, 9, 20));       // today: inside
        _ = _service.AddEntry("edge", "s2", expiresOn: new DateOnly(2026, 10, 20));       // exactly +30: inside
        _ = _service.AddEntry("out", "s3", expiresOn: new DateOnly(2026, 10, 21));        // +31: outside

        var rows = _service.ExpiringWithin(30);
        Assert.Equal(2, rows.Count);
        Assert.Equal("today", rows[0].Title);
    }

    [Fact]
    public void Import_UnsupportedVersions_AreRejected()
    {
        Unlock();
        Assert.Throws<RazException>(() => _service.ImportJson("{\"version\":99,\"salt\":\"\",\"iterations\":1,\"keyCheck\":\"\",\"payload\":\"\"}"));
    }

    [Fact]
    public void Generate_UsesTheConfiguredLength()
    {
        Unlock();
        var password = _service.Generate(new PasswordPolicy(Length: 16, Symbol: false));
        Assert.Equal(16, password.Length);
    }

    [Fact]
    public void ExportImport_RoundTrips_AcrossVaults_WithFreshIds()
    {
        Unlock();
        _ = _service.AddEntry("GitHub", "hunter2!", username: "octocat");
        var bundle = _service.ExportJson();

        var second = NewService();
        second.Init("correct-horse-battery");
        Assert.Equal(1, second.ImportJson(bundle));
        Assert.Equal(1, second.ImportJson(bundle)); // importing again adds again (fresh ids)

        var imported = second.ListEntries();
        Assert.Equal(2, imported.Count);
        Assert.Contains(imported, e => e.Title == "GitHub" && e.Secret == "hunter2!" && e.Username == "octocat");
    }

    [Fact]
    public void Import_WithADifferentPassphrase_IsRejected()
    {
        Unlock();
        _ = _service.AddEntry("GitHub", "hunter2!");
        var bundle = _service.ExportJson();

        var other = NewService();
        other.Init("a-completely-different-pass");
        var exception = Assert.Throws<RazException>(() => other.ImportJson(bundle));
        Assert.Contains("different passphrase", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    public void Import_Garbage_IsRejected(string bundle)
    {
        Unlock();
        Assert.Throws<RazException>(() => _service.ImportJson(bundle));
    }

    [Fact]
    public void Undo_WithEmptyStack_SaysNothingToUndo()
    {
        Unlock();
        Assert.False(_service.Undo());
    }

    private static VaultService NewService() =>
        new(new MemoryVaultStore(), new FixedTimeProvider(Now), new RazOptions(Iterations: RazDefaults.MinIterations));
}
