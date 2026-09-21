using JameJam.Divan;
using JameJam.Divan.Sync;
using JameJam.Sync;

namespace JameJam.Tests.Divan;

/// <summary>
/// Edge paths of the sync stack: determinism corners, apply diffs, guard rails, and
/// the transport's defensive construction.
/// </summary>
public sealed class DivanSyncEdgeTests
{
    private static readonly FixedTimeProvider Clock =
        new(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));

    private static (DivanService Service, MemoryDivanStore Store) Pad(TimeProvider? clock = null)
    {
        var store = new MemoryDivanStore();
        return (new DivanService(store, clock ?? Clock), store);
    }

    [Fact]
    public async Task PickNote_ExactTie_IsDeterministicOnBothDevices()
    {
        var (_, store) = Pad();
        var adapter = new DivanSyncAdapter(new DivanService(store, Clock), store, Clock);
        var stamp = "2026-09-20T11:00:00.0000000+00:00";
        var id = Guid.NewGuid();

        // Same sync id, same timestamps, different titles — a perfect tie.
        static string Side(Guid id, string title, string stamp) =>
            $$"""{"notebooks":[],"notes":[{"syncId":"{{id}}","notebook":"N","title":"{{title}}","body":"","tags":"","pinned":false,"archived":false,"createdAt":"{{stamp}}","updatedAt":"{{stamp}}"}],"tombstones":[]}""";

        var ab = await adapter.MergeAsync(Side(id, "Alpha", stamp), Side(id, "Beta", stamp), "a", "b");
        var ba = await adapter.MergeAsync(Side(id, "Beta", stamp), Side(id, "Alpha", stamp), "b", "a");

        Assert.Equal(ab, ba); // both devices resolve the tie to the same bytes
        var winner = ab.Contains("Alpha", StringComparison.Ordinal) ? "Alpha" : "Beta";
        var loser = winner == "Alpha" ? "Beta" : "Alpha";
        Assert.Contains(winner, ab, StringComparison.Ordinal); // exactly one survives, deterministically
        Assert.DoesNotContain(loser, ab, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PickNotebook_ExactTie_WithBothArchived_StaysArchived()
    {
        var (_, store) = Pad();
        var adapter = new DivanSyncAdapter(new DivanService(store, Clock), store, Clock);
        var stamp = "2026-09-20T11:00:00.0000000+00:00";
        var a = $$"""{"notebooks":[{"name":"N","archived":true,"updatedAt":"{{stamp}}"}],"notes":[],"tombstones":[]}""";

        var merged = await adapter.MergeAsync(a, a, "a", "b");
        Assert.Contains("\"archived\":true", merged, StringComparison.Ordinal); // AND of ties keeps the archive
    }

    [Fact]
    public async Task Merge_NullPayload_YieldsEmptyState()
    {
        var (_, store) = Pad();
        var adapter = new DivanSyncAdapter(new DivanService(store, Clock), store, Clock);

        var merged = await adapter.MergeAsync("null", """{"notebooks":[],"notes":[],"tombstones":[]}""", "a", "b");
        Assert.Contains("notebooks", merged, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_ArchiveFlagDiff_AndTitleEdit_UpdateInPlace()
    {
        var (service, store) = Pad();
        var adapter = new DivanSyncAdapter(service, store, Clock);
        _ = service.CreateNotebook("Research");
        var note = service.AddNote("Original", "body");

        var stamp = "2026-09-20T12:30:00.0000000+00:00";
        var payload = $$"""
            {"notebooks":[{"name":"Research","archived":true,"updatedAt":"{{stamp}}"}],
             "notes":[{"syncId":"{{note.SyncId}}","notebook":"Research","title":"Renamed remotely","body":"body","tags":"","pinned":false,"archived":false,"createdAt":"{{stamp}}","updatedAt":"{{stamp}}"}],
             "tombstones":[]}
            """;

        Assert.Equal(2, await adapter.ApplyAsync(payload)); // one notebook flag + one note edit

        Assert.True(store.FindNotebookByName("Research")!.IsArchived);
        var edited = service.GetNote(note.Id)!;
        Assert.Equal("Renamed remotely", edited.Title);
        Assert.Equal(note.Id, edited.Id); // local id preserved
        Assert.Equal(note.SyncId, edited.SyncId);

        // Re-applying the same payload is a no-op (idempotent).
        Assert.Equal(0, await adapter.ApplyAsync(payload));
    }

    [Fact]
    public async Task Apply_RemoteTombstone_RemovesTheLocalCopy()
    {
        var (service, store) = Pad();
        var adapter = new DivanSyncAdapter(service, store, Clock);
        var note = service.AddNote("Doomed remotely", "body");
        var stamp = "2026-09-20T12:30:00.0000000+00:00";
        var payload = $$"""{"notebooks":[],"notes":[],"tombstones":[{"syncId":"{{note.SyncId}}","deletedAt":"{{stamp}}"}]}""";

        Assert.Equal(1, await adapter.ApplyAsync(payload));
        Assert.Null(service.GetNote(note.Id));
        _ = Assert.Single(store.GetTombstones()); // deletion recorded locally too
    }

    [Fact]
    public async Task Apply_LongNotebookNames_AreClipped_ToTheRail()
    {
        var (service, store) = Pad();
        var adapter = new DivanSyncAdapter(service, store, Clock);
        var longName = new string('x', DivanDefaults.MaxNotebookNameLength + 40);
        var payload = $$"""{"notebooks":[{"name":"{{longName}}","archived":false,"updatedAt":"2026-09-20T12:30:00.0000000+00:00"}],"notes":[],"tombstones":[]}""";

        _ = await adapter.ApplyAsync(payload);

        Assert.Equal(DivanDefaults.MaxNotebookNameLength, Assert.Single(store.ListNotebooks()).Name.Length);
    }

    [Fact]
    public async Task Apply_EmptyNotebookNames_AreSkipped()
    {
        var (_, store) = Pad();
        var adapter = new DivanSyncAdapter(new DivanService(store, Clock), store, Clock);
        var payload = """{"notebooks":[{"name":"   ","archived":false,"updatedAt":"2026-09-20T12:30:00.0000000+00:00"}],"notes":[],"tombstones":[]}""";

        Assert.Equal(0, await adapter.ApplyAsync(payload));
        Assert.Empty(store.ListNotebooks());
    }

    [Fact]
    public async Task Merge_LiveNoteVersusEqualAgeTombstone_DeletionWins()
    {
        var (service, store) = Pad();
        var adapter = new DivanSyncAdapter(service, store, Clock);
        var note = service.AddNote("Contested", "body");
        var stamp = "2026-09-20T12:00:00.0000000+00:00";

        // Remote: the same note deleted at the exact moment of its last edit.
        var remote = $$"""{"notebooks":[],"notes":[],"tombstones":[{"syncId":"{{note.SyncId}}","deletedAt":"{{stamp}}"}]}""";
        var local = await adapter.CaptureAsync();

        var merged = await adapter.MergeAsync(local, remote, "a", "b");
        Assert.DoesNotContain("Contested", merged, StringComparison.Ordinal); // deletion stands
        _ = await adapter.ApplyAsync(merged);
        Assert.Null(service.GetNote(note.Id));
    }

    [Fact]
    public async Task Merge_NewerUnarchive_BeatsOlderArchive()
    {
        var (service, store) = Pad();
        var adapter = new DivanSyncAdapter(service, store, Clock);
        _ = service.CreateNotebook("N");
        var older = Clock.GetUtcNow().AddMinutes(-5);
        var archived = $$"""{"notebooks":[{"name":"N","archived":true,"updatedAt":"{{older.ToString("O")}}"}],"notes":[],"tombstones":[]}""";
        var local = await adapter.CaptureAsync();

        var merged = await adapter.MergeAsync(local, archived, "a", "b");
        _ = await adapter.ApplyAsync(merged);

        Assert.False(store.FindNotebookByName("N")!.IsArchived); // the newer write (local, now) wins
    }

    [Fact]
    public async Task Capture_NotebooksWithoutTimestamps_FallBackToCreated()
    {
        var (_, store) = Pad();
        _ = store.AddNotebook(new Notebook(0, "Legacy", Clock.GetUtcNow(), IsArchived: false)); // UpdatedAt = default
        var adapter = new DivanSyncAdapter(new DivanService(store, Clock), store, Clock);

        var json = await adapter.CaptureAsync();

        Assert.Contains("2026-09-20T12:00:00", json, StringComparison.Ordinal); // created-at backed the missing timestamp
    }
}

/// <summary>Transport construction and device-identity fallbacks.</summary>
public sealed class DivanSyncTransportTests
{
    private static readonly FixedTimeProvider Clock =
        new(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void HttpSyncClient_Guards_ItsConstruction()
    {
        var options = new SyncOptions { Endpoint = "https://sync.test/pad.json" };
        Assert.Throws<ArgumentNullException>(() => new HttpSyncClient(null!, options));
        Assert.Throws<ArgumentNullException>(() => new HttpSyncClient(new HttpClient(), null!));
    }

    [Fact]
    public async Task Sync_WithoutProviders_GeneratesEphemeralIdentity_AndStillWorks()
    {
        var blob = new BlobClient();
        using StringWriter output = new();
        var commands = new DivanCommands(
            new MemoryDivanStore(),
            Clock,
            output,
            new StringWriter(),
            stdin: new StringReader("body\n"),
            syncClientFactory: _ => blob,
            syncUrlProvider: () => "https://sync.test/pad.json");

        // No deviceIdProvider / deviceNameProvider at all.
        Assert.Equal(0, await commands.RunAsync(["sync"]));
        Assert.NotNull(blob.Document);
        Assert.Contains("First sync", output.ToString(), StringComparison.Ordinal);
    }
}

/// <summary>The stand-in "any server": one shared JSON document.</summary>
internal sealed class BlobClient : ISyncClient
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
