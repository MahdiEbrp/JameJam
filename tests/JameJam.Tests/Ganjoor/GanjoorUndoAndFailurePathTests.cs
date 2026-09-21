using JameJam.Ganjoor;

namespace JameJam.Tests.Ganjoor;

/// <summary>
/// Undo depth trimming, snapshot-before-import, account lifecycle, and the failure paths
/// of the CLI (exception-to-friendly-message translation).
/// </summary>
public sealed class GanjoorUndoAndFailurePathTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"gj-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        File.Delete(_path);
        File.Delete(_path + "-wal");
        File.Delete(_path + "-shm");
    }

    [Fact]
    public void MemoryStore_TrimsOldestSnapshots_BeyondTheDepth()
    {
        var store = new MemoryGanjoorStore { UndoDepth = 3 };
        Assert.Equal(3, store.UndoDepth);

        for (var i = 1; i <= 5; i++)
        {
            store.PushUndo($"s{i}");
            Assert.Equal(Math.Min(i, 3), store.UndoCount);
        }

        Assert.Equal("s5", store.PopUndo()); // newest first
        Assert.Equal("s4", store.PopUndo());
        Assert.Equal("s3", store.PopUndo());
        Assert.Null(store.PopUndo()); // s1 and s2 were trimmed away
    }

    [Fact]
    public void MemoryStore_RejectsNegativeDepth() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemoryGanjoorStore { UndoDepth = -1 });

    [Fact]
    public void SqliteStore_TrimsOldestSnapshots_BeyondTheDepth()
    {
        var store = new SqliteGanjoorStore(_path) { UndoDepth = 2 };
        store.PushUndo("old");
        store.PushUndo("mid");
        store.PushUndo("new");
        Assert.Equal(2, store.UndoCount);
        Assert.Equal("new", store.PopUndo());
        Assert.Equal("mid", store.PopUndo());
        Assert.Null(store.PopUndo());

        store.UndoDepth = 0; // disable undo entirely
        store.PushUndo("gone");
        Assert.Equal(0, store.UndoCount);
    }

    [Fact]
    public void Service_BindsOptionsUndoDepth_ToTheStore()
    {
        var store = new MemoryGanjoorStore();
        _ = new GanjoorService(store, TimeProvider.System, new GanjoorOptions { UndoDepth = 7 });
        Assert.Equal(7, store.UndoDepth);

        var options = new GanjoorOptions { UndoDepth = -5 };
        Assert.Throws<GanjoorException>(() => new GanjoorService(store, TimeProvider.System, options));
    }

    [Fact]
    public void Import_PushesASnapshot_SoUndoRevertsTheImport()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 9, 19, 10, 0, 0, TimeSpan.Zero));
        var service = new GanjoorService(new MemoryGanjoorStore(), clock, new GanjoorOptions());
        _ = service.AddAccount("Bank", "USD", "100");

        var snapshot = service.ExportJson();
        _ = service.AddAccount("Wreck", "USD", "999");
        Assert.Equal(2, service.Accounts().Count);

        service.PushUndoSnapshot();
        service.ImportJson(snapshot);

        Assert.Single(service.Accounts());
        Assert.True(service.Undo());
        Assert.Equal(2, service.Accounts().Count); // back to the wrecked state
    }

    [Fact]
    public async Task AccountLifecycle_RenamesArchivesAndRemoves()
    {
        var commands = Build(out var output, out _);

        Assert.Equal(0, await commands.RunAsync(["account", "add", "Bank"]));
        Assert.Equal(0, await commands.RunAsync(["account", "rename", "1", "Main"]));
        Assert.Equal(0, await commands.RunAsync(["account", "archive", "Main"]));
        Assert.Contains("Archived Main.", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, await commands.RunAsync(["account", "list"]));
        Assert.Contains("[archived]", output.ToString(), StringComparison.Ordinal);

        Assert.Equal(0, await commands.RunAsync(["account", "unarchive", "1"]));
        Assert.Contains("Unarchived Main.", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, await commands.RunAsync(["account", "remove", "1", "--force"]));
        Assert.Contains("Account removed (0 transaction(s) deleted).", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownAccountSubcommand_ShowsUsage()
    {
        var commands = Build(out _, out var error);

        Assert.Equal(1, await commands.RunAsync(["account", "gamble"]));

        Assert.Contains("Usage: JameJam ganjoor account", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_IoErrorsBecomeFriendly()
    {
        var commands = Build(out _, out var error);
        var badPath = Path.Combine(Path.GetTempPath(), "missing-dir-does-not-exist", "w.json");

        Assert.Equal(1, await commands.RunAsync(["export", badPath]));

        Assert.Contains("File error:", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ai_WithoutAiCompletion_IsUnavailable()
    {
        var commands = Build(out _, out var error);

        Assert.Equal(1, await commands.RunAsync(["ai", "insights"]));

        Assert.Contains("AI is not available in this context.", error.ToString(), StringComparison.Ordinal);
    }

    private static GanjoorCommands Build(out StringWriter output, out StringWriter error) =>
        new(
            new MemoryGanjoorStore(),
            new FixedTimeProvider(new DateTimeOffset(2026, 9, 19, 10, 0, 0, TimeSpan.Zero)),
            output = new StringWriter(),
            error = new StringWriter());
}
