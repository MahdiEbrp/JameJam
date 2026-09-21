using System.Globalization;
using System.Text.Json;

using JameJam.Sync;
using JameJam.Taqvim;
using JameJam.Taqvim.Sync;

namespace JameJam.Tests.Taqvim;

/// <summary>
/// Two-device sync: canonical captures, last-write-wins, tombstone rules, apply alignment —
/// the same convergence guarantees the Divan pad earned in the stress test.
/// </summary>
public sealed class TaqvimSyncAdapterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0.AddMinutes(10);
    private static readonly DateTimeOffset T2 = T0.AddMinutes(20);

    private static readonly TaqvimOptions Rails = new() { MaxEvents = 100 };

    private static readonly JsonSerializerOptions Wire = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static TaqvimEvent Ev(string title, Guid syncId, DateTimeOffset updated, string start = "2026-09-22T10:00:00+00:00") => new(
        0, "Work", title, "Room 4", string.Empty, "team",
        DateTimeOffset.Parse(start, CultureInfo.InvariantCulture),
        DateTimeOffset.Parse(start, CultureInfo.InvariantCulture).AddHours(1),
        false, null, [10], T0, updated, syncId);

    private static TaqvimService Service(MemoryTaqvimStore store, TimeProvider clock) => new(store, clock, Rails);

    private static TaqvimSyncAdapter Adapter(MemoryTaqvimStore store) =>
        new(Service(store, new FixedTimeProvider(T2)), store);

    [Fact]
    public async Task Capture_IsCanonicallyOrdered_TwoPadsWithDifferentRowIdsSerializeByteIdentically()
    {
        var syncId = Guid.NewGuid();
        var betaId = Guid.NewGuid();
        var storeA = new MemoryTaqvimStore();
        var storeB = new MemoryTaqvimStore();

        // Device A: the event arrives first; device B: another event arrives first.
        var a1 = storeA.AddEvent(Ev("Alpha", syncId, T1));
        _ = storeA.AddEvent(Ev("Beta", betaId, T1));
        var b1 = storeB.AddEvent(Ev("Beta", betaId, T1));
        _ = storeB.AddEvent(Ev("Alpha", syncId, T1));

        var captureA = await Adapter(storeA).CaptureAsync();
        var captureB = await Adapter(storeB).CaptureAsync();
        Assert.Equal(captureA, captureB, StringComparer.Ordinal); // byte-identical despite different row ids
        _ = (a1, b1);
    }

    [Fact]
    public async Task Merge_KeepsTheNewerEdit_LastWriteWins()
    {
        var store = new MemoryTaqvimStore();
        var adapter = Adapter(store);
        var id = Guid.NewGuid();

        var local = JsonSerializer.Serialize(
            new FakePayload([new FakeEvent(id, "Old title", T1)], []),
            Wire);
        var remote = JsonSerializer.Serialize(
            new FakePayload([new FakeEvent(id, "New title", T2)], []),
            Wire);

        var merged = await adapter.MergeAsync(local, remote, "a", "b");
        Assert.Contains("New title", merged, StringComparison.Ordinal);
        Assert.DoesNotContain("Old title", merged, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Merge_ExactTie_IsDeterministicOnBothDevices()
    {
        var store = new MemoryTaqvimStore();
        var adapter = Adapter(store);
        var id = Guid.NewGuid();

        var left = JsonSerializer.Serialize(
            new FakePayload([new FakeEvent(id, "Alpha", T1)], []),
            Wire);
        var right = JsonSerializer.Serialize(
            new FakePayload([new FakeEvent(id, "Beta", T1)], []),
            Wire);

        var ab = await adapter.MergeAsync(left, right, "a", "b");
        var ba = await adapter.MergeAsync(right, left, "b", "a");
        Assert.Equal(ab, ba); // no ping-pong
    }

    [Fact]
    public async Task Merge_Tombstone_BeatsAnEquallyOldEvent()
    {
        var store = new MemoryTaqvimStore();
        var adapter = Adapter(store);
        var id = Guid.NewGuid();

        var events = JsonSerializer.Serialize(
            new FakePayload([new FakeEvent(id, "Alive", T1)], []),
            Wire);
        var tombstoned = JsonSerializer.Serialize(
            new FakePayload([], [new FakeTombstone(id, T1)]),
            Wire);

        var merged = await adapter.MergeAsync(events, tombstoned, "a", "b");
        Assert.DoesNotContain("Alive", merged, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Merge_NewerEdit_ResurrectsFromTombstone()
    {
        var store = new MemoryTaqvimStore();
        var adapter = Adapter(store);
        var id = Guid.NewGuid();

        var events = JsonSerializer.Serialize(
            new FakePayload([new FakeEvent(id, "Revived", T2)], []),
            Wire);
        var tombstoned = JsonSerializer.Serialize(
            new FakePayload([], [new FakeTombstone(id, T1)]),
            Wire);

        var merged = await adapter.MergeAsync(events, tombstoned, "a", "b");
        Assert.Contains("Revived", merged, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Merge_IgnoresEmptySyncIds()
    {
        var store = new MemoryTaqvimStore();
        var adapter = Adapter(store);
        var empty = JsonSerializer.Serialize(
            new FakePayload([], []),
            Wire);
        var merged = await adapter.MergeAsync(empty, empty, "a", "b");
        Assert.Contains("\"events\":[]", merged.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Merge_InvalidJson_ThrowsSyncException()
    {
        await Assert.ThrowsAsync<SyncException>(() =>
            Adapter(new MemoryTaqvimStore()).MergeAsync("{bad", "{bad", "a", "b"));
    }

    [Fact]
    public async Task FullCycle_InsertUpdateDelete_Converges()
    {
        // Device A creates; both sync; B edits; both sync; B deletes; both sync.
        var clockA = new FixedTimeProvider(T0);
        var clockB = new FixedTimeProvider(T0.AddSeconds(1)); // different clocks per the LWW test recipe
        var storeA = new MemoryTaqvimStore();
        var storeB = new MemoryTaqvimStore();
        var serviceA = Service(storeA, clockA);
        var serviceB = Service(storeB, clockB);
        var blob = new FakeBlob();

        var created = serviceA.AddEvent(
            "Planning", DateTimeOffset.Parse("2026-09-22T10:00:00+00:00", CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-09-22T11:00:00+00:00", CultureInfo.InvariantCulture),
            reminders: [10]);

        await SyncOnce(storeA, serviceA, clockA, blob); // A pushes
        await SyncOnce(storeB, serviceB, clockB, blob); // B pulls the same blob
        Assert.Single(storeB.ListEvents());

        _ = serviceB.Reschedule(created.Id, DateTimeOffset.Parse("2026-09-22T14:00:00+00:00", CultureInfo.InvariantCulture));
        await SyncOnce(storeB, serviceB, clockB, blob);
        await SyncOnce(storeA, serviceA, clockA, blob);
        Assert.Equal("Planning", storeA.FindEvent(created.Id)!.Title);
        Assert.Equal(14, storeA.FindEvent(created.Id)!.Start.Hour);

        _ = serviceB.Delete(created.Id);
        await SyncOnce(storeB, serviceB, clockB, blob);
        await SyncOnce(storeA, serviceA, clockA, blob);
        Assert.Empty(storeA.ListEvents());
        Assert.Empty(storeB.ListEvents());

        // Both captures are byte-identical after convergence.
        var a = await new TaqvimSyncAdapter(serviceA, storeA).CaptureAsync();
        var b = await new TaqvimSyncAdapter(serviceB, storeB).CaptureAsync();
        Assert.Equal(a, b);
    }

    [Fact]
    public async Task FullCycle_BothDeleteTheSameEvent_TombstoneTimesConverge()
    {
        var clockA = new FixedTimeProvider(T0);
        var clockB = new FixedTimeProvider(T0.AddSeconds(1));
        var storeA = new MemoryTaqvimStore();
        var storeB = new MemoryTaqvimStore();
        var serviceA = Service(storeA, clockA);
        var serviceB = Service(storeB, clockB);
        var blob = new FakeBlob();

        var created = serviceA.AddEvent(
            "Shared", DateTimeOffset.Parse("2026-09-22T10:00:00+00:00", CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-09-22T11:00:00+00:00", CultureInfo.InvariantCulture));
        await SyncOnce(storeA, serviceA, clockA, blob);
        await SyncOnce(storeB, serviceB, clockB, blob);

        // Both devices delete the same event at (slightly) different times.
        _ = serviceA.Delete(created.Id);
        _ = serviceB.Delete(created.Id);
        await SyncOnce(storeA, serviceA, clockA, blob);
        await SyncOnce(storeB, serviceB, clockB, blob);
        await SyncOnce(storeA, serviceA, clockA, blob);

        var tombA = Assert.Single(storeA.GetTombstones());
        var tombB = Assert.Single(storeB.GetTombstones());
        Assert.Equal(tombA, tombB); // aligned deletion times — captures converge

        var a = await new TaqvimSyncAdapter(serviceA, storeA).CaptureAsync();
        var b = await new TaqvimSyncAdapter(serviceB, storeB).CaptureAsync();
        Assert.Equal(a, b);
    }

    [Fact]
    public async Task FullCycle_RemindersAndRules_Travel()
    {
        var clockA = new FixedTimeProvider(T0);
        var clockB = new FixedTimeProvider(T0.AddSeconds(1));
        var storeA = new MemoryTaqvimStore();
        var storeB = new MemoryTaqvimStore();
        var serviceA = Service(storeA, clockA);
        var serviceB = Service(storeB, clockB);
        var blob = new FakeBlob();

        _ = serviceA.AddEvent(
            "Yoga", DateTimeOffset.Parse("2026-09-23T08:00:00+00:00", CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-09-23T09:15:00+00:00", CultureInfo.InvariantCulture),
            tags: "health",
            rule: new Recurrence(RecurrenceKind.Weekly, 1, [DayOfWeek.Wednesday], 10, null),
            reminders: [30, 5]);

        await SyncOnce(storeA, serviceA, clockA, blob);
        await SyncOnce(storeB, serviceB, clockB, blob);

        var mirrored = Assert.Single(storeB.ListEvents());
        Assert.Equal("Yoga", mirrored.Title);
        Assert.Equal([5, 30], mirrored.Reminders);
        Assert.Equal("health", mirrored.Tags);
        Assert.Equal(RecurrenceKind.Weekly, mirrored.Rule!.Kind);
    }

    [Fact]
    public async Task Apply_PushesOneUndoSnapshot_AndCountsChanges()
    {
        var store = new MemoryTaqvimStore();
        var service = Service(store, new FixedTimeProvider(T2));
        var adapter = new TaqvimSyncAdapter(service, store);
        var payload = JsonSerializer.Serialize(
            new FakePayload(
            [new FakeEvent(Guid.NewGuid(), "From afar", T1, "2026-09-25T09:00:00+00:00")],
            []),
            Wire);

        var changed = await adapter.ApplyAsync(payload);
        Assert.Equal(1, changed);
        Assert.Equal(1, store.UndoCount); // one snapshot reverts the whole apply
        Assert.Single(store.ListEvents());
    }

    [Fact]
    public async Task Apply_LocalEventDroppedByMerge_GetsDeleted()
    {
        var store = new MemoryTaqvimStore();
        var service = Service(store, new FixedTimeProvider(T0));
        var doomed = service.AddEvent(
            "Doomed", DateTimeOffset.Parse("2026-09-22T10:00:00+00:00", CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-09-22T11:00:00+00:00", CultureInfo.InvariantCulture));

        var adapter = new TaqvimSyncAdapter(Service(store, new FixedTimeProvider(T2)), store);
        var payload = JsonSerializer.Serialize(
            new FakePayload([], [new FakeTombstone(doomed.SyncId, T1)]),
            Wire);

        var changed = await adapter.ApplyAsync(payload);
        Assert.Equal(1, changed);
        Assert.Empty(store.ListEvents());
        var tombstone = Assert.Single(store.GetTombstones());
        Assert.Equal(doomed.SyncId, tombstone.SyncId);
        Assert.Equal(T1, tombstone.DeletedAt);
    }

    [Fact]
    public async Task Apply_TombstoneTimestamps_AreAlignedWithThePayload()
    {
        var store = new MemoryTaqvimStore();
        var adapter = Adapter(store);
        var id = Guid.NewGuid();
        store.UpsertTombstone(new TaqvimTombstone(id, T0)); // stale local time

        var payload = JsonSerializer.Serialize(
            new FakePayload([], [new FakeTombstone(id, T2)]),
            Wire);
        _ = await adapter.ApplyAsync(payload);

        Assert.Equal(T2, Assert.Single(store.GetTombstones()).DeletedAt);
    }

    [Fact]
    public async Task ServiceName_IsTaqvim()
    {
        Assert.Equal("taqvim", Adapter(new MemoryTaqvimStore()).Service);
    }

    // ── Helpers ──

    /// <summary>One merge-pull-push exchange against a shared blob, exactly like the CLI flow.</summary>
    private static async Task SyncOnce(MemoryTaqvimStore store, TaqvimService service, TimeProvider clock, FakeBlob blob)
    {
        var adapter = new TaqvimSyncAdapter(service, store);
        var client = new BlobClient(blob);
        _ = await SyncEngine.RunAsync(
            client, adapter, "device-" + store.GetHashCode(), "Test", SyncMode.Merge, false, clock.GetUtcNow());
    }

    private sealed class FakeBlob
    {
        public string? Value { get; set; }
    }

    private sealed class BlobClient(FakeBlob blob) : ISyncClient
    {
        public Task<string?> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(blob.Value);

        public Task PutAsync(string json, CancellationToken cancellationToken = default)
        {
            blob.Value = json;
            return Task.CompletedTask;
        }
    }

    // Wire-shape records mirroring the adapter's private DTOs (camelCase JSON).
    internal sealed record FakePayload(IReadOnlyList<FakeEvent> Events, IReadOnlyList<FakeTombstone> Tombstones);

    internal sealed record FakeEvent(Guid SyncId, string Title, DateTimeOffset UpdatedAt, string Start = "2026-09-22T10:00:00+00:00")
    {
        public string Calendar { get; init; } = "Work";

        public string Location { get; init; } = "Room 4";

        public string Notes { get; init; } = string.Empty;

        public string Tags { get; init; } = string.Empty;

        public DateTimeOffset StartValue { get; init; } = DateTimeOffset.Parse(Start, CultureInfo.InvariantCulture);

        public DateTimeOffset End => StartValue.AddHours(1);

        // The positional parameter `Start` (string) is exposed as the wire start instant.
        public DateTimeOffset StartWire => StartValue;

        public bool IsAllDay { get; init; }

        public Recurrence? Rule { get; init; }

        public IReadOnlyList<int> Reminders { get; init; } = [10];

        public DateTimeOffset CreatedAt { get; init; } = T0;
    }

    internal sealed record FakeTombstone(Guid SyncId, DateTimeOffset DeletedAt);
}
