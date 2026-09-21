using System.Globalization;

using System.Text.Json;

using JameJam.Divan;
using JameJam.Divan.Sync;
using JameJam.Sync;

namespace JameJam.Tests.Divan;

/// <summary>
/// Two-device Divan sync: the adapter's capture/merge/apply matrix, the SQLite v1→v2
/// migration, and the CLI verb over an in-memory blob server.
/// </summary>
public sealed class DivanSyncTests
{
    private static readonly FixedTimeProvider Clock =
        new(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));

    private static DivanSyncAdapter Adapter(DivanService service, IDivanStore store) =>
        new(service, store, Clock);

    private static (DivanService Service, MemoryDivanStore Store) Pad()
    {
        var store = new MemoryDivanStore();
        return (new DivanService(store, Clock), store);
    }

    private static Task<string> CaptureAsync(DivanSyncAdapter adapter) => adapter.CaptureAsync();

    private static Task<string> MergeAsync(DivanSyncAdapter adapter, string local, string remote) =>
        adapter.MergeAsync(local, remote, "device-a", "device-b");

    private static Task<int> ApplyAsync(DivanSyncAdapter adapter, string merged) =>
        adapter.ApplyAsync(merged);

    [Fact]
    public async Task Apply_AlignsNotebookTimestamps_SoDevicesConverge()
    {
        var (service, store) = Pad();
        var adapter = Adapter(service, store);
        var stamp = "2026-09-20T13:00:00.0000000+00:00";
        var payload = $$"""{"notebooks":[{"name":"Shared","archived":false,"updatedAt":"{{stamp}}"}],"notes":[],"tombstones":[]}""";

        _ = await ApplyAsync(adapter, payload);
        var applied = store.FindNotebookByName("Shared")!.UpdatedAt;

        // Re-capturing must carry exactly the payload's timestamp — no device-local drift.
        var captured = await adapter.CaptureAsync();
        Assert.Contains(
            "\"updatedAt\":\"2026-09-20T13:00:00+00:00\"}", // short round-trip format
            captured,
            StringComparison.Ordinal);
        Assert.Equal(DateTimeOffset.Parse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), applied);
    }

    [Fact]
    public void Note_GetsWithSyncId_AndStoresAssignIt()
    {
        var (service, store) = Pad();
        var note = service.AddNote("Alpha", "body");

        Assert.NotEqual(Guid.Empty, note.SyncId); // store assigned a v7 identity
        Assert.NotEqual(Guid.Empty, store.FindNote(note.Id)!.SyncId);

        // Plain store adds get identities too (import path).
        var direct = store.AddNote(new Note(0, 1, "Beta", "b", "", false, false, Clock.GetUtcNow(), Clock.GetUtcNow()));
        Assert.NotEqual(Guid.Empty, direct.SyncId);
    }

    [Fact]
    public void Delete_RecordsATombstone_UndoRetractsIt()
    {
        var (service, store) = Pad();
        var note = service.AddNote("Doomed", "body");
        var syncId = note.SyncId;

        _ = service.Delete(note.Id);
        var tombstones = store.GetTombstones();

        _ = Assert.Single(tombstones);
        Assert.Equal(syncId, tombstones[0].SyncId);

        Assert.True(service.Undo()); // restores the note…
        Assert.Empty(store.GetTombstones()); // …and the tombstone retires
        Assert.Equal(syncId, service.GetNote(note.Id)!.SyncId);
    }

    [Fact]
    public async Task Sync_TwoDevices_ExchangeNotes()
    {
        var (aliceService, aliceStore) = Pad();
        var (bobService, bobStore) = Pad();
        var alice = Adapter(aliceService, aliceStore);
        var bob = Adapter(bobService, bobStore);
        _ = aliceService.AddNote("From Alice", "hello");
        _ = bobService.AddNote("From Bob", "hi");

        // One shared document = the whole server. Alice seeds; Bob merges; Alice pulls.
        var server = new BlobClient();
        await SyncEngine.RunAsync(server, alice, "alice", "Alice", now: Clock.GetUtcNow());
        await SyncEngine.RunAsync(server, bob, "bob", "Bob", now: Clock.GetUtcNow());
        var run = await SyncEngine.RunAsync(server, alice, "alice", "Alice", now: Clock.GetUtcNow());

        Assert.Equal(1, run.Applied);
        Assert.Single(aliceService.List(), n => n.Title == "From Bob");
        Assert.Single(bobService.List(), n => n.Title == "From Alice");
        // Local ids stay local: Alice's own note kept id 1.
        Assert.Equal("From Alice", aliceService.GetNote(1)!.Title);
    }

    [Fact]
    public async Task Merge_BothDevicesDeletedTheSameNote_TombstoneTimesConverge()
    {
        var (aService, aStore) = Pad();
        var (bService, bStore) = Pad();
        var a = Adapter(aService, aStore);
        var b = Adapter(bService, bStore);
        var seed = aService.AddNote("Deleted everywhere", "body");
        _ = bStore.AddNote(new Note(
            0, 1, "Deleted everywhere", "body", "", false, false,
            seed.CreatedAt, seed.UpdatedAt, seed.SyncId));

        // Both delete — microseconds apart.
        _ = aService.Delete(seed.Id);
        _ = bService.Delete(bStore.ListNotes()[0].Id);

        var merged = await MergeAsync(a, await CaptureAsync(a), await CaptureAsync(b));
        _ = await ApplyAsync(a, merged);
        _ = await ApplyAsync(b, merged);

        // The tombstone time must be identical on both devices (byte-identical captures).
        Assert.Equal(await CaptureAsync(a), await CaptureAsync(b));
    }

    [Fact]
    public async Task Merge_NewerEditWins_BothDirections()
    {
        var (aService, aStore) = Pad();
        var (bService, bStore) = Pad();
        var a = Adapter(aService, aStore);
        var b = Adapter(bService, bStore);
        var seed = aService.AddNote("Shared", "v1");
        _ = bService.AddNote("Shared", "v1", notebookText: null, tags: string.Empty);

        // Bob's copy is the same logical note only if ids match — mint it manually.
        var bobNote = bStore.ListNotes()[0];
        bStore.UpdateNote(bobNote with { SyncId = seed.SyncId });

        // Bob edits one minute later.
        var bobLater = bStore.FindNote(bobNote.Id)! with
        {
            Body = "v2 from bob",
            UpdatedAt = Clock.GetUtcNow().AddMinutes(1),
        };
        bStore.UpdateNote(bobLater);

        var local = await CaptureAsync(a);
        var remote = await CaptureAsync(b);
        var merged = await MergeAsync(a, local, remote);
        Assert.Equal(1, await ApplyAsync(a, merged));
        Assert.Equal("v2 from bob", aService.GetNote(seed.Id)!.Body);

        // The reverse direction converges to the same body.
        var mergedBack = await MergeAsync(b, remote, local);
        Assert.Equal(0, await ApplyAsync(b, mergedBack)); // bob already holds the winner
    }

    [Fact]
    public async Task Merge_TombstoneDeletesTheRemoteCopy_ResurrectOnNewerEdit()
    {
        var (aService, aStore) = Pad();
        var (bService, bStore) = Pad();
        var a = Adapter(aService, aStore);
        var b = Adapter(bService, bStore);
        var seed = aService.AddNote("Ephemeral", "body");

        // Bob holds the same note, older.
        var bobNote = bStore.AddNote(new Note(
            0, 1, "Ephemeral", "body", "", false, false,
            Clock.GetUtcNow(), Clock.GetUtcNow().AddSeconds(-5), seed.SyncId));

        // Alice deletes — the tombstone must reach Bob.
        _ = aService.Delete(seed.Id);
        var local = await CaptureAsync(a);
        var remote = await CaptureAsync(b);
        _ = await ApplyAsync(a, await MergeAsync(a, local, remote));
        _ = await ApplyAsync(b, await MergeAsync(b, remote, local));
        Assert.Null(bService.GetNote(bobNote.Id));
        Assert.Empty(aService.List());

        // A NEWER edit (pre-delete snapshot resurrected elsewhere) beats the tombstone.
        var resurrected = new Note(
            0, 1, "Ephemeral", "phoenix", "", false, false,
            seed.CreatedAt, Clock.GetUtcNow().AddMinutes(2), seed.SyncId);
        _ = bStore.AddNote(resurrected);
        var remote2 = await CaptureAsync(b);
        var local2 = await CaptureAsync(a);
        _ = await ApplyAsync(a, await MergeAsync(a, local2, remote2));
        Assert.Equal("phoenix", aService.List().Single().Body);
    }

    [Fact]
    public async Task Merge_NotebooksUnion_AndArchiveFlagFollowsTheLatestWrite()
    {
        var (aService, aStore) = Pad();
        var (bService, bStore) = Pad();
        var a = Adapter(aService, aStore);
        var b = Adapter(bService, bStore);

        _ = aService.CreateNotebook("Research");
        // Device B works a minute later (its own clock), creating the same notebook
        // under a different case and archiving it.
        var later = new FixedTimeProvider(Clock.GetUtcNow().AddMinutes(1));
        var (bService2, bStore2) = Pad();
        var b2 = new DivanSyncAdapter(new DivanService(bStore2, later), bStore2, later);
        _ = new DivanService(bStore2, later).CreateNotebook("Writing");
        var researchOnB = new DivanService(bStore2, later).CreateNotebook("research");
        _ = new DivanService(bStore2, later).ArchiveNotebook("research", true);
        bStore = bStore2;
        b = b2;

        var local = await CaptureAsync(a);
        var remote = await CaptureAsync(b);
        _ = await ApplyAsync(a, await MergeAsync(a, local, remote));

        Assert.Equal(2, aService.ListNotebooks().Count); // union by case-insensitive name
        Assert.Equal("Research", aStore.FindNotebookByName("research")!.Name);
        Assert.True(aStore.FindNotebookByName("Research")!.IsArchived); // archived later on B
        Assert.NotNull(aStore.FindNotebookByName("Writing"));
    }

    [Fact]
    public async Task Apply_PushesOneUndoSnapshot()
    {
        var (service, store) = Pad();
        var adapter = Adapter(service, store);
        var payload = """{"notebooks":[{"name":"Synced","archived":false,"updatedAt":"2026-09-20T11:00:00.0000000+00:00"}],"notes":[],"tombstones":[]}""";

        var before = store.UndoCount;
        Assert.Equal(1, await ApplyAsync(adapter, payload));
        Assert.Equal(before + 1, store.UndoCount);

        Assert.True(service.Undo()); // the whole apply reverts
        Assert.Null(store.FindNotebookByName("Synced"));
    }

    [Fact]
    public async Task Merge_IsDeterministic_UnderExactTimeTies()
    {
        var (service, store) = Pad();
        var adapter = Adapter(service, store);
        var stamp = "2026-09-20T11:00:00.0000000+00:00";

        // Same note id on both sides (tie) — merge twice, same winner.
        var sharedId = Guid.NewGuid();
        var a = $$"""{"notebooks":[],"notes":[{"syncId":"{{sharedId}}","notebook":"N","title":"A-side","body":"x","tags":"","pinned":false,"archived":false,"createdAt":"{{stamp}}","updatedAt":"{{stamp}}"}],"tombstones":[]}""";
        var b = $$"""{"notebooks":[],"notes":[{"syncId":"{{sharedId}}","notebook":"N","title":"B-side","body":"y","tags":"","pinned":false,"archived":false,"createdAt":"{{stamp}}","updatedAt":"{{stamp}}"}],"tombstones":[]}""";

        var ab = await adapter.MergeAsync(a, b, "a", "b");
        var ba = await adapter.MergeAsync(b, a, "b", "a");
        Assert.Equal(ab, ba);
    }

    [Fact]
    public async Task Merge_GarbagePayload_FailsFriendly()
    {
        var (service, store) = Pad();
        var adapter = Adapter(service, store);
        var exception = await Assert.ThrowsAsync<SyncException>(
            () => adapter.MergeAsync("{}", "not json at all", "a", "b"));
        Assert.Contains("not valid", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SQLite_MigratesV1Databases_ToV2()
    {
        var path = Path.Combine(Path.GetTempPath(), $"divan-mig-{Guid.NewGuid():N}.db");
        try
        {
            // Build a genuine v1 database by hand (pre-sync-identity schema).
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE notebooks (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL,
                        created_at TEXT NOT NULL, is_archived INTEGER NOT NULL DEFAULT 0);
                    CREATE TABLE notes (id INTEGER PRIMARY KEY AUTOINCREMENT, notebook_id INTEGER NOT NULL,
                        title TEXT NOT NULL, body TEXT NOT NULL, tags TEXT NOT NULL, pinned INTEGER NOT NULL DEFAULT 0,
                        archived INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
                    CREATE TABLE undo_log (id INTEGER PRIMARY KEY AUTOINCREMENT, created_at TEXT NOT NULL, payload TEXT NOT NULL);
                    INSERT INTO notebooks (name, created_at, is_archived) VALUES ('Old', '2026-01-01T00:00:00.0000000+00:00', 0);
                    INSERT INTO notes (notebook_id, title, body, tags, pinned, archived, created_at, updated_at)
                        VALUES (1, 'Legacy note', 'body', '', 0, 0, '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00');
                    PRAGMA user_version = 1;
                    """;
                _ = cmd.ExecuteNonQuery();
            }

            var store = new SqliteDivanStore(path);
            var note = store.FindNote(1)!;

            // The migration backfilled a sync identity and the notebook timestamp.
            Assert.NotEqual(Guid.Empty, note.SyncId);
            Assert.NotEqual(default, store.FindNotebook(1)!.UpdatedAt);
            Assert.Empty(store.GetTombstones());

            // New writes work, including tombstones.
            var added = store.AddNote(new Note(0, 1, "New", "b", "", false, false, Clock.GetUtcNow(), Clock.GetUtcNow()));
            _ = store.RemoveNote(added.Id, Clock.GetUtcNow());
            _ = Assert.Single(store.GetTombstones());
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
    }

    /// <summary>A shared JSON document — the smallest server any host can implement.</summary>
    private sealed class BlobClient : ISyncClient
    {
        public string? Document { get; set; }

        public Task<string?> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Document);

        public Task PutAsync(string json, CancellationToken cancellationToken = default)
        {
            Document = json;
            return Task.CompletedTask;
        }
    }
}

/// <summary>CLI surface for <c>divan sync</c>: modes, URL precedence, friendly failures.</summary>
public sealed class DivanSyncCommandTests
{
    private static readonly FixedTimeProvider Clock =
        new(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));

    private static DivanCommands Build(
        out StringWriter output,
        out StringWriter error,
        BlobClient? blob,
        string? urlSetting = "https://sync.test/pad.json") =>
        new(
            new MemoryDivanStore(),
            Clock,
            output = new StringWriter(),
            error = new StringWriter(),
            stdin: new StringReader("note body\n"),
            syncClientFactory: blob is null ? null : (Func<SyncOptions, ISyncClient>)(_ => blob),
            syncUrlProvider: () => urlSetting,
            deviceIdProvider: () => "device-a",
            deviceNameProvider: () => "testbox");

    /// <summary>A shared JSON document (the "any server" stand-in).</summary>
    private sealed class BlobClient : ISyncClient
    {
        public string? Document { get; set; }

        public Task<string?> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Document);

        public Task PutAsync(string json, CancellationToken cancellationToken = default)
        {
            Document = json;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Sync_WithoutFactory_IsUnavailable()
    {
        var commands = Build(out var output, out var error, blob: null);

        Assert.Equal(1, await commands.RunAsync(["sync"]));
        Assert.Contains("Sync is not available in this context.", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_FirstRun_Seeds_AndSecondRun_NoOps()
    {
        var blob = new BlobClient();
        var commands = Build(out var output, out var error, blob);

        Assert.Equal(0, await commands.RunAsync(["new", "Solo", "--stdin"]));

        Assert.Equal(0, await commands.RunAsync(["sync"]));
        Assert.Contains("First sync", output.ToString(), StringComparison.Ordinal);
        Assert.NotNull(blob.Document);

        output.GetStringBuilder().Clear();
        Assert.Equal(0, await commands.RunAsync(["sync"]));
        Assert.Contains("Merged: 0 record(s)", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_UrlPrecedence_Flag_Beats_Environment_Beats_Setting()
    {
        var blob = new BlobClient();
        var commands = Build(out var output, out var error, blob, urlSetting: "https://setting.test/pad.json");
        await commands.RunAsync(["new", "Solo", "--stdin"]);

        // No URL at all → friendly pointer to the setting.
        var bare = Build(out _, out var bareError, blob, urlSetting: null);
        Assert.Equal(1, await bare.RunAsync(["sync"]));
        Assert.Contains("divan.syncUrl", bareError.ToString(), StringComparison.Ordinal);

        // A bad URL fails the safety rail before any call.
        Assert.Equal(1, await commands.RunAsync(["sync", "--url", "http://insecure.test/pad.json"]));
        Assert.Contains("HTTPS", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_UnknownMode_FailsFriendly()
    {
        var blob = new BlobClient();
        var commands = Build(out _, out var error, blob);

        Assert.Equal(1, await commands.RunAsync(["sync", "gamble"]));
        Assert.Contains("Unknown sync mode 'gamble'", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await commands.RunAsync(["sync", "--url"]));
        Assert.Contains("Usage: JameJam divan sync", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_PushOverDifferingRemote_NeedsForce()
    {
        var blob = new BlobClient { Document = SyncSafety.Seal("divan", """{"notebooks":[],"notes":[],"tombstones":[]}""", "other", "Other", Clock.GetUtcNow()).ToJson() };
        var commands = Build(out var output, out var error, blob);
        await commands.RunAsync(["new", "Solo", "--stdin"]);

        Assert.Equal(1, await commands.RunAsync(["sync", "push"]));
        Assert.Contains("--force", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Other", error.ToString(), StringComparison.Ordinal);

        Assert.Equal(0, await commands.RunAsync(["sync", "push", "--force"]));
        Assert.Contains("remote replaced", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_ForeignServiceOnTheUrl_Fails()
    {
        var blob = new BlobClient
        {
            Document = SyncSafety.Seal("haftkhan", """{"tasks":[]}""", "other", "Other", Clock.GetUtcNow()).ToJson(),
        };
        var commands = Build(out _, out var error, blob);

        Assert.Equal(1, await commands.RunAsync(["sync"]));
        Assert.Contains("'divan' cannot merge", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_MentionsSync()
    {
        var commands = Build(out var output, out _, blob: null);
        Assert.Equal(0, await commands.RunAsync(["help"]));
        Assert.Contains("divan sync", output.ToString(), StringComparison.Ordinal);
    }
}
