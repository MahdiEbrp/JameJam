using JameJam.HaftKhan;
using JameJam.Sync;

namespace JameJam.Tests.HaftKhan.Sync;

/// <summary>Tests for the sync engine: uid merge, last-write-wins, dependency union, push safety, undo.</summary>
public sealed class SyncServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Earlier = Now.AddHours(-5);
    private static readonly DateTimeOffset Later = Now.AddHours(5);

    private readonly MemoryTaskRepository _repository = new();
    private readonly HaftKhanService _service;

    public SyncServiceTests() => _service = new HaftKhanService(_repository, new FixedTimeProvider(Now));

    private HaftKhanTask MakeLocal(string title, string uid, DateTimeOffset updatedAt, TaskState state = TaskState.Todo)
    {
        var added = _repository.Add(new NewTask(title, string.Empty, TaskPriority.Normal, null) { Uid = uid });
        var updated = added with
        {
            State = state,
            UpdatedAt = updatedAt,
            CompletedAt = state == TaskState.Done ? updatedAt : null,
        };
        _repository.Update(updated);
        return updated;
    }

    private static Backup.BackupFile RemoteBackup(params Backup.TaskDto[] tasks) => new(
        Backup.CurrentVersion,
        "2026-09-19T00:00:00.0000000+00:00",
        tasks,
        []);

    private static Backup.TaskDto RemoteDto(long id, string title, string uid, DateTimeOffset updatedAt) => new(
        id, title, string.Empty, 1, 0, null,
        updatedAt.ToString("O"), updatedAt.ToString("O"), null, "", [], 0, 0, 1, null, uid);

    [Fact]
    public async Task FirstSync_IntoEmptyLocal_PullsRemote_AndPushesBack()
    {
        var client = new StubSyncClient(RemoteBackup(RemoteDto(1, "from remote", "uid-a", Earlier)));
        _ = _service.AddTask("local only");

        var report = await _service.SyncAsync(client);

        Assert.Equal(SyncMode.Merge, report.Mode);
        Assert.Equal(1, report.Pulled);
        Assert.Equal(2, report.Pushed);
        Assert.Equal(2, report.Total);
        Assert.Equal("local only", _repository.ListAll().First(task => task.Uid != "uid-a").Title);
        var pulled = _repository.FindByUid("uid-a")!;
        Assert.Equal("from remote", pulled.Title);
        Assert.Equal(Earlier, pulled.UpdatedAt); // remote timestamps preserved
    }

    [Fact]
    public async Task Merge_LastWriteWins_RemoteNewerUpdatesLocal()
    {
        _ = MakeLocal("local version", "uid-a", Earlier);
        var client = new StubSyncClient(RemoteBackup(RemoteDto(9, "remote version", "uid-a", Later)));

        var report = await _service.SyncAsync(client);

        Assert.Equal(1, report.Pulled);
        Assert.Equal("remote version", _repository.FindByUid("uid-a")!.Title);
    }

    [Fact]
    public async Task Merge_LocalNewerWins_RemoteSkipped()
    {
        _ = MakeLocal("local version", "uid-a", Later);
        var client = new StubSyncClient(RemoteBackup(RemoteDto(9, "remote version", "uid-a", Earlier)));

        var report = await _service.SyncAsync(client);

        Assert.Equal(0, report.Pulled);
        Assert.Equal("local version", _repository.FindByUid("uid-a")!.Title);
    }

    [Fact]
    public async Task Merge_TieKeepsLocal()
    {
        _ = MakeLocal("local version", "uid-a", Now);
        var client = new StubSyncClient(RemoteBackup(RemoteDto(9, "remote version", "uid-a", Now)));

        await _service.SyncAsync(client);

        Assert.Equal("local version", _repository.FindByUid("uid-a")!.Title);
    }

    [Fact]
    public async Task Merge_UnionsDependencies_ByUid()
    {
        // Local: a ← b (b blocked by a). Remote: a ← c.
        var a = _repository.Add(new NewTask("a", string.Empty, TaskPriority.Normal, null) { Uid = "uid-a" });
        var b = _repository.Add(new NewTask("b", string.Empty, TaskPriority.Normal, null) { Uid = "uid-b" });
        _repository.AddDependency(b.Id, a.Id);
        var backup = new Backup.BackupFile(
            Backup.CurrentVersion, "2026-09-19T00:00:00.0000000+00:00",
            [RemoteDto(1, "a", "uid-a", Earlier), RemoteDto(3, "c", "uid-c", Earlier)],
            [new Backup.DependencyDto(3, 1, "uid-c", "uid-a")]);
        var client = new StubSyncClient(backup);

        await _service.SyncAsync(client);

        var links = _repository.ListDependencies();
        Assert.Equal(2, links.Count); // uid-b→uid-a preserved + uid-c→uid-a added
        Assert.Contains(links, link => _repository.Find(link.DependsOnId)!.Uid == "uid-a"
            && _repository.Find(link.TaskId)!.Uid is "uid-b" or "uid-c");
    }

    [Fact]
    public async Task Sync_IsUndoable()
    {
        _ = MakeLocal("local", "uid-a", Earlier);
        var client = new StubSyncClient(RemoteBackup(RemoteDto(1, "remote", "uid-b", Earlier)));

        await _service.SyncAsync(client);
        Assert.Equal(2, _repository.ListAll().Count);

        _service.Undo();

        var tasks = _repository.ListAll();
        Assert.Single(tasks);
        Assert.Equal("local", tasks[0].Title);
    }

    [Fact]
    public async Task Push_WithNonEmptyRemote_RequiresForce()
    {
        _ = MakeLocal("local", "uid-a", Now);
        var client = new StubSyncClient(RemoteBackup(RemoteDto(1, "remote", "uid-z", Earlier)));

        var refused = await Assert.ThrowsAsync<SyncException>(() => _service.SyncAsync(client, SyncMode.Push));
        Assert.Contains("Use --force", refused.Message, StringComparison.Ordinal);

        var report = await _service.SyncAsync(client, SyncMode.Push, force: true);
        Assert.Equal(1, report.Pushed);
        Assert.NotNull(client.LastUpload);
    }

    [Fact]
    public async Task Pull_DoesNotPush()
    {
        var client = new StubSyncClient(RemoteBackup(RemoteDto(1, "remote", "uid-a", Earlier)));

        var report = await _service.SyncAsync(client, SyncMode.Pull);

        Assert.Equal(0, report.Pushed);
        Assert.Null(client.LastUpload);
    }

    [Fact]
    public async Task EmptyRemote_FullSync_ConvergesBothWays()
    {
        _ = MakeLocal("local", "uid-a", Now);
        var client = new StubSyncClient(); // GET → 404

        var report = await _service.SyncAsync(client);

        Assert.Equal(0, report.Pulled);
        Assert.Equal(1, report.Pushed);
        Assert.NotNull(client.LastUpload);
    }

    [Fact]
    public async Task Import_PreservesUids_AndLinksByUid()
    {
        var backup = new Backup.BackupFile(
            Backup.CurrentVersion, "2026-09-19T00:00:00.0000000+00:00",
            [RemoteDto(1, "first", "uid-a", Earlier), RemoteDto(2, "second", "uid-b", Earlier)],
            [new Backup.DependencyDto(2, 1, "uid-b", "uid-a")]);

        var (imported, links) = _service.Import(backup, replace: false);

        Assert.Equal(2, imported);
        Assert.Equal(1, links);
        Assert.Equal("uid-a", _repository.ListAll().First(task => task.Title == "first").Uid);
        var link = Assert.Single(_repository.ListDependencies());
        Assert.Equal("uid-b", _repository.Find(link.TaskId)!.Uid);
        Assert.Equal("uid-a", _repository.Find(link.DependsOnId)!.Uid);
    }

    [Fact]
    public async Task Sync_InvalidRemotePayload_FailsFriendly()
    {
        var client = new StubSyncClient { RemoteJson = "{\"hello\":true}" };
        var exception = await Assert.ThrowsAsync<SyncException>(
            () => _service.SyncAsync(client, SyncMode.Merge));
        Assert.Contains("did not return a valid Haft Khan backup", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>In-memory sync transport with scriptable remote state.</summary>
    private sealed class StubSyncClient : ISyncClient
    {
        public StubSyncClient(Backup.BackupFile? remote = null) =>
            RemoteJson = remote is null ? null : Backup.ToJson(remote);

        public string? RemoteJson { get; set; }

        public string? LastUpload { get; private set; }

        public Task<string?> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(RemoteJson);

        public Task PutAsync(string json, CancellationToken cancellationToken = default)
        {
            LastUpload = json;
            RemoteJson = json; // the remote now holds what was pushed
            return Task.CompletedTask;
        }
    }
}
