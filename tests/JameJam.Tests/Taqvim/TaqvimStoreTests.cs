using JameJam.Taqvim;

namespace JameJam.Tests.Taqvim;

/// <summary>Both stores behave identically; SQLite adds FTS, persistence, permissions.</summary>
public sealed class TaqvimStoreTests
{
    private static readonly DateTimeOffset T1 = new(2026, 9, 21, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = new(2026, 9, 21, 15, 0, 0, TimeSpan.Zero);

    private static TaqvimEvent Ev(string title, long id = 0, string? notes = null, string? location = null, Recurrence? rule = null) => new(
        id, "Work", title, location ?? string.Empty, notes ?? string.Empty, string.Empty,
        T1, T2, false, rule, [10], T1, T1, Guid.NewGuid());

    [Fact]
    public void Memory_AddAssignsIds_AndNormalizesSyncId()
    {
        var store = new MemoryTaqvimStore();
        var first = store.AddEvent(Ev("One"));
        var second = store.AddEvent(Ev("Two"));
        Assert.Equal(1, first.Id);
        Assert.Equal(2, second.Id);
        Assert.NotEqual(Guid.Empty, first.SyncId);
        Assert.NotEqual(first.SyncId, second.SyncId);
    }

    [Fact]
    public void Memory_AddPreservesASuppliedSyncId()
    {
        var store = new MemoryTaqvimStore();
        var known = Guid.NewGuid();
        var ev = store.AddEvent(Ev("One") with { SyncId = known });
        Assert.Equal(known, ev.SyncId);
    }

    [Fact]
    public void Memory_Update_UnknownId_Throws()
    {
        Assert.Throws<TaqvimException>(() => new MemoryTaqvimStore().UpdateEvent(Ev("ghost", id: 42)));
    }

    [Fact]
    public void Memory_ListOrdersByStart()
    {
        var store = new MemoryTaqvimStore();
        _ = store.AddEvent(Ev("Late") with { Start = T2 });
        _ = store.AddEvent(Ev("Early") with { Start = T1.AddHours(-1) });
        Assert.Equal(["Early", "Late"], store.ListEvents().Select(e => e.Title));
    }

    [Fact]
    public void Memory_Search_AndLogic_ScoresTitleHigher()
    {
        var store = new MemoryTaqvimStore();
        _ = store.AddEvent(Ev("Sprint planning", notes: "nothing here"));
        _ = store.AddEvent(Ev("Retrospective", notes: "talk about the sprint"));
        Assert.Equal(2, store.SearchIds("sprint", 10).Count);
        Assert.Single(store.SearchIds("sprint planning", 10));
        Assert.Empty(store.SearchIds("nonexistent", 10));
    }

    [Fact]
    public void Memory_Search_RejectsEmpty()
    {
        Assert.ThrowsAny<ArgumentException>(() => new MemoryTaqvimStore().SearchIds("  ", 10));
    }

    [Fact]
    public void Memory_UndoStack_LIFO_WithDepthTrim()
    {
        var store = new MemoryTaqvimStore { UndoDepth = 2 };
        store.PushUndo("one");
        store.PushUndo("two");
        store.PushUndo("three");
        Assert.Equal(2, store.UndoCount);
        Assert.Equal("three", store.PopUndo());
        Assert.Equal("two", store.PopUndo());
        Assert.Null(store.PopUndo());
    }

    [Fact]
    public void Memory_UndoDepth_Zero_DropsEverything()
    {
        var store = new MemoryTaqvimStore { UndoDepth = 0 };
        store.PushUndo("one");
        Assert.Equal(0, store.UndoCount);
        Assert.Null(store.PopUndo());
    }

    [Fact]
    public void Memory_UndoDepth_Negative_Throws()
    {
        var store = new MemoryTaqvimStore();
        Assert.Throws<TaqvimException>(() => store.UndoDepth = -1);
    }

    [Fact]
    public void Memory_PushUndo_Empty_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => new MemoryTaqvimStore().PushUndo("   "));
    }

    [Fact]
    public void Memory_RemoveEvent_WritesTombstone_AndClearsOnRestore()
    {
        var store = new MemoryTaqvimStore();
        var ev = store.AddEvent(Ev("Doomed"));
        Assert.True(store.RemoveEvent(ev.Id, T2));
        Assert.False(store.RemoveEvent(ev.Id, T2));

        var tombstone = Assert.Single(store.GetTombstones());
        Assert.Equal(ev.SyncId, tombstone.SyncId);
        Assert.Equal(T2, tombstone.DeletedAt);

        _ = store.AddEvent(Ev("Doomed", 0) with { SyncId = ev.SyncId });
        Assert.Empty(store.GetTombstones()); // the live event retracts it
    }

    [Fact]
    public void Memory_UpsertTombstone_EmptySyncId_IsNoOp()
    {
        var store = new MemoryTaqvimStore();
        store.UpsertTombstone(new TaqvimTombstone(Guid.Empty, T1));
        Assert.Empty(store.GetTombstones());
    }

    [Fact]
    public void Memory_UpsertTombstone_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new MemoryTaqvimStore().UpsertTombstone(null!));
    }

    [Fact]
    public void Memory_ReplaceEvents_RestoresIds_AndClearsTombstones()
    {
        var store = new MemoryTaqvimStore();
        var ev = store.AddEvent(Ev("One"));
        _ = store.RemoveEvent(ev.Id, T2);
        store.ReplaceEvents([Ev("One") with { Id = ev.Id, SyncId = ev.SyncId }]);
        var restored = Assert.Single(store.ListEvents());
        Assert.Equal(ev.Id, restored.Id);
        Assert.Empty(store.GetTombstones());

        // The id counter moved past the restored ids.
        var next = store.AddEvent(Ev("Two"));
        Assert.True(next.Id > ev.Id);
    }

    [Fact]
    public void Memory_NullGuards()
    {
        var store = new MemoryTaqvimStore();
        Assert.Throws<ArgumentNullException>(() => store.AddEvent(null!));
        Assert.Throws<ArgumentNullException>(() => store.UpdateEvent(null!));
        Assert.Throws<ArgumentNullException>(() => store.ReplaceEvents(null!));
        Assert.Throws<ArgumentNullException>(() => store.UpsertTombstone(null!));
    }
}

/// <summary>SQLite store: persistence, FTS with LIKE fallback, permissions, tombstones, undo log.</summary>
public sealed class SqliteTaqvimStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"taqvim-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        File.Delete(_path);
        File.Delete(_path + "-wal");
        File.Delete(_path + "-shm");
    }

    private static readonly DateTimeOffset S1 = new(2026, 9, 21, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset S2 = new(2026, 9, 21, 15, 0, 0, TimeSpan.Zero);

    private static TaqvimEvent Ev(string title, long id = 0, string? notes = null, string? location = null, Recurrence? rule = null) => new(
        id, "Work", title, location ?? "Room 4", notes ?? string.Empty, "team,work",
        S1, S2, false, rule, [10, 20], S1, S1, Guid.NewGuid());

    [Fact]
    public void Persistence_RoundTrips_Everything()
    {
        var rule = new Recurrence(RecurrenceKind.Weekly, 2, [DayOfWeek.Monday, DayOfWeek.Friday], 10, null);
        {
            var first = new SqliteTaqvimStore(_path);
            _ = first.AddEvent(Ev("Standup", notes: "sync notes", rule: rule));
        }

        var second = new SqliteTaqvimStore(_path);
        var ev = Assert.Single(second.ListEvents());
        Assert.Equal("Standup", ev.Title);
        Assert.Equal("Room 4", ev.Location);
        Assert.Equal("sync notes", ev.Notes);
        Assert.Equal("team,work", ev.Tags);
        Assert.Equal([10, 20], ev.Reminders);
        Assert.NotNull(ev.Rule);
        Assert.Equal(RecurrenceKind.Weekly, ev.Rule.Kind);
        Assert.Equal(2, ev.Rule.Interval);
        Assert.Equal(2, ev.Rule.Weekdays.Count);
        Assert.Equal(10, ev.Rule.Count);
        Assert.NotEqual(Guid.Empty, ev.SyncId);
    }

    [Fact]
    public void AddUpdateDelete_AndFind()
    {
        var store = new SqliteTaqvimStore(_path);
        var ev = store.AddEvent(Ev("One"));
        Assert.Equal(1, ev.Id);

        ev = ev with { Title = "One edited", UpdatedAt = ev.UpdatedAt.AddMinutes(5) };
        store.UpdateEvent(ev);
        Assert.Equal("One edited", store.FindEvent(ev.Id)!.Title);

        Assert.True(store.RemoveEvent(ev.Id, ev.UpdatedAt.AddMinutes(10)));
        Assert.Null(store.FindEvent(ev.Id));
        Assert.False(store.RemoveEvent(ev.Id, ev.UpdatedAt));
    }

    [Fact]
    public void Update_UnknownId_Throws()
    {
        var store = new SqliteTaqvimStore(_path);
        Assert.Throws<TaqvimException>(() => store.UpdateEvent(Ev("ghost", id: 99)));
    }

    [Fact]
    public void Search_FindsByTitleNotesAndLocation()
    {
        var store = new SqliteTaqvimStore(_path);
        _ = store.AddEvent(Ev("Sprint review", notes: "demo day"));
        _ = store.AddEvent(Ev("Retrospective", notes: "review the sprint"));
        _ = store.AddEvent(Ev("Offsite", notes: "elsewhere", location: "review terrace"));

        Assert.Equal(3, store.SearchIds("review", 10).Count);
        Assert.Single(store.SearchIds("sprint demo", 10));
        Assert.Single(store.SearchIds("terrace", 10));
    }

    [Fact]
    public void Search_QuotesInTheQuery_DoNotBreakMatch()
    {
        var store = new SqliteTaqvimStore(_path);
        _ = store.AddEvent(Ev("The \"great\" escape"));
        Assert.Single(store.SearchIds("\"great\"", 10));
    }

    [Fact]
    public void Search_RejectsEmpty()
    {
        Assert.ThrowsAny<ArgumentException>(() => new SqliteTaqvimStore(_path).SearchIds(" ", 10));
    }

    [Fact]
    public void UndoLog_PersistsAndTrims()
    {
        var store = new SqliteTaqvimStore(_path) { UndoDepth = 2 };
        store.PushUndo("one");
        store.PushUndo("two");
        store.PushUndo("three");
        Assert.Equal(2, store.UndoCount);

        // A fresh instance sees the same log.
        var reopened = new SqliteTaqvimStore(_path);
        Assert.Equal(2, reopened.UndoCount);
        Assert.Equal("three", reopened.PopUndo());
        Assert.Equal(1, reopened.UndoCount);
    }

    [Fact]
    public void Tombstones_PersistAndAlign()
    {
        var store = new SqliteTaqvimStore(_path);
        var ev = store.AddEvent(Ev("Doomed"));
        _ = store.RemoveEvent(ev.Id, S1);

        var tombstone = Assert.Single(store.GetTombstones());
        Assert.Equal(ev.SyncId, tombstone.SyncId);

        // UpsertTombstone can re-time an existing deletion.
        store.UpsertTombstone(new TaqvimTombstone(ev.SyncId, S2));
        Assert.Equal(S2, Assert.Single(store.GetTombstones()).DeletedAt);
    }

    [Fact]
    public void Tombstones_HideLiveEvents()
    {
        var store = new SqliteTaqvimStore(_path);
        var ev = store.AddEvent(Ev("Alive"));
        store.UpsertTombstone(new TaqvimTombstone(ev.SyncId, S1));
        Assert.Empty(store.GetTombstones()); // the event exists — no live tombstone
    }

    [Fact]
    public void ReplaceEvents_ClearsAndRetracts()
    {
        var store = new SqliteTaqvimStore(_path);
        var ev = store.AddEvent(Ev("One"));
        _ = store.RemoveEvent(ev.Id, S1);
        store.ReplaceEvents([Ev("One") with { Id = ev.Id, SyncId = ev.SyncId }]);
        Assert.Empty(store.GetTombstones());
        Assert.Single(store.ListEvents());
    }

    [Fact]
    public void Database_HasOwnerOnlyPermissions()
    {
        _ = new SqliteTaqvimStore(_path);
        Assert.True(File.Exists(_path));
        if (OperatingSystem.IsWindows())
        {
            return; // Unix permission bits do not apply
        }

        var mode = File.GetUnixFileMode(_path);
        Assert.Equal(System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite,
            mode & (System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite));
        Assert.False(mode.HasFlag(System.IO.UnixFileMode.GroupRead));
        Assert.False(mode.HasFlag(System.IO.UnixFileMode.OtherRead));
    }

    [Fact]
    public void NullGuards()
    {
        var store = new SqliteTaqvimStore(_path);
        Assert.Throws<ArgumentNullException>(() => store.AddEvent(null!));
        Assert.Throws<ArgumentNullException>(() => store.UpdateEvent(null!));
        Assert.Throws<ArgumentNullException>(() => store.ReplaceEvents(null!));
        Assert.Throws<ArgumentNullException>(() => store.UpsertTombstone(null!));
    }
}
