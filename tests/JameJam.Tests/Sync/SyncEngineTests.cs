using JameJam.Sync;

namespace JameJam.Tests.Sync;

/// <summary>
/// The engine over in-memory fakes: modes, first-sync, convergence in both orders,
/// safety gates, and the identical-state fast path.
/// </summary>
public sealed class SyncEngineTests
{
    /// <summary>A shared blob that any server can implement: GET returns it, PUT replaces it.</summary>
    private sealed class BlobClient
    {
        public string? Document;

        public readonly List<string> Uploads = [];

        public ISyncClient AsClient() => new Impl(this);

        private sealed class Impl(BlobClient owner) : ISyncClient
        {
            public Task<string?> GetAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(owner.Document);

            public Task PutAsync(string json, CancellationToken cancellationToken = default)
            {
                owner.Uploads.Add(json);
                owner.Document = json;
                return Task.CompletedTask;
            }
        }
    }

    /// <summary>A minimal state: one counter merged by taking the max.</summary>
    private sealed class CounterAdapter(string service = "test") : ISyncAdapter
    {
        public int Value;

        public string Service { get; } = service;

        public Task<string> CaptureAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult($$"""{"value":{{Value}}}""");

        public Task<string> MergeAsync(string localJson, string remoteJson, string localDeviceId, string remoteDeviceId, CancellationToken cancellationToken = default)
        {
            var merged = Math.Max(Read(localJson), Read(remoteJson));
            return Task.FromResult($$"""{"value":{{merged}}}""");
        }

        public Task<int> ApplyAsync(string mergedJson, CancellationToken cancellationToken = default)
        {
            var target = Read(mergedJson);
            var changed = target == Value ? 0 : 1;
            Value = target;
            return Task.FromResult(changed);
        }

        private static int Read(string json) =>
            int.Parse(json.Replace("{\"value\":", string.Empty).TrimEnd('}'), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task FirstSync_SeedsTheRemote()
    {
        var blob = new BlobClient();
        var adapter = new CounterAdapter { Value = 3 };

        var run = await SyncEngine.RunAsync(blob.AsClient(), adapter, "device-a", "A", now: Fixed.Now);

        Assert.True(run.FirstSync);
        Assert.True(run.Pushed);
        Assert.Equal(0, run.Applied);
        Assert.Contains("device-a", blob.Document, StringComparison.Ordinal);
        Assert.Contains("\"service\":\"test\"", blob.Document, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Merge_Converges_FromBothOrders()
    {
        // Device A and B both changed; whichever syncs second converges the remote —
        // and the state each ends with is identical.
        var blob = new BlobClient();
        var a = new CounterAdapter { Value = 5 };
        var b = new CounterAdapter { Value = 7 };

        await SyncEngine.RunAsync(blob.AsClient(), a, "device-a", "A", now: Fixed.Now);
        var runB = await SyncEngine.RunAsync(blob.AsClient(), b, "device-b", "B", now: Fixed.Now);
        Assert.Equal(0, runB.Applied); // b already held the winning value…
        Assert.True(runB.Pushed);      // …but it published the merged state

        // device A pulls the merged result back — both ends converge on 7
        var runA = await SyncEngine.RunAsync(blob.AsClient(), a, "device-a", "A", now: Fixed.Now);
        Assert.Equal(1, runA.Applied);
        Assert.False(runA.Pushed);
        Assert.Equal(7, a.Value);
        Assert.Equal(7, b.Value);
    }

    [Fact]
    public async Task Merge_NoOpRun_SkipsTheRemoteWrite()
    {
        var blob = new BlobClient();
        var a = new CounterAdapter { Value = 2 };

        await SyncEngine.RunAsync(blob.AsClient(), a, "a", "A", now: Fixed.Now);
        blob.Uploads.Clear();

        var second = await SyncEngine.RunAsync(blob.AsClient(), a, "a", "A", now: Fixed.Now);
        Assert.False(second.Pushed);
        Assert.Empty(blob.Uploads); // byte-identical → no write at all
    }

    [Fact]
    public async Task Pull_NeverWritesTheRemote()
    {
        var blob = new BlobClient();
        var a = new CounterAdapter { Value = 9 };
        await SyncEngine.RunAsync(blob.AsClient(), a, "a", "A", now: Fixed.Now);

        var b = new CounterAdapter { Value = 1 };
        blob.Uploads.Clear();
        var run = await SyncEngine.RunAsync(blob.AsClient(), b, "b", "B", SyncMode.Pull, now: Fixed.Now);

        Assert.Equal(1, run.Applied); // pulled 9 into b
        Assert.Empty(blob.Uploads);   // and never touched the remote
        Assert.Equal(9, b.Value);
    }

    [Fact]
    public async Task Push_OverDifferingRemote_RequiresForce()
    {
        var blob = new BlobClient();
        var a = new CounterAdapter { Value = 1 };
        await SyncEngine.RunAsync(blob.AsClient(), a, "a", "A", now: Fixed.Now);

        var b = new CounterAdapter { Value = 2 };
        var exception = await Assert.ThrowsAsync<SyncException>(
            () => SyncEngine.RunAsync(blob.AsClient(), b, "b", "B", SyncMode.Push, force: false, now: Fixed.Now));
        Assert.Contains("--force", exception.Message, StringComparison.Ordinal);
        Assert.Contains("A", exception.Message, StringComparison.Ordinal); // names the last writer

        var forced = await SyncEngine.RunAsync(blob.AsClient(), b, "b", "B", SyncMode.Push, force: true, now: Fixed.Now);
        Assert.True(forced.Pushed);
    }

    [Fact]
    public async Task ForeignServiceOnTheSameUrl_IsRejected()
    {
        var blob = new BlobClient();
        var divanish = new CounterAdapter("divan") { Value = 1 };
        await SyncEngine.RunAsync(blob.AsClient(), divanish, "a", "A", now: Fixed.Now);

        var haftkhanish = new CounterAdapter("haftkhan") { Value = 1 };
        var exception = await Assert.ThrowsAsync<SyncException>(
            () => SyncEngine.RunAsync(blob.AsClient(), haftkhanish, "b", "B", now: Fixed.Now));
        Assert.Contains("'haftkhan' cannot merge", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CorruptedRemote_FailsBeforeMerge()
    {
        var blob = new BlobClient { Document = """{"schema":"jamejam.sync/1","service":"test","payload":"x","checksum":"nope"}""" };
        var a = new CounterAdapter();

        var exception = await Assert.ThrowsAsync<SyncException>(
            () => SyncEngine.RunAsync(blob.AsClient(), a, "a", "A", now: Fixed.Now));
        Assert.Contains("integrity check", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pull_FromEmptyRemote_IsANoOp()
    {
        var blob = new BlobClient();
        var a = new CounterAdapter { Value = 4 };

        var run = await SyncEngine.RunAsync(blob.AsClient(), a, "a", "A", SyncMode.Pull, now: Fixed.Now);

        Assert.False(run.Pushed);
        Assert.Equal(0, run.Applied);
        Assert.Null(blob.Document);
    }

    [Fact]
    public async Task Describe_CoversEveryShape()
    {
        Assert.Contains("Pushed the local state", new SyncRun(SyncMode.Push, 0, true, false).Describe());
        Assert.Contains("Pulled: 3", new SyncRun(SyncMode.Pull, 3, false, false).Describe());
        Assert.Contains("First sync", new SyncRun(SyncMode.Merge, 0, true, true).Describe());
        Assert.Contains("Merged: 2 record(s)", new SyncRun(SyncMode.Merge, 2, true, false).Describe());
    }
}
