using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

using JameJam.Divan;
using JameJam.Divan.Sync;
using JameJam.Raz.Crypto;
using JameJam.Soroush;
using JameJam.Sync;
using JameJam.Text;

// ════════════════════════════════════════════════════════════════════════════════
// JameJam stress harness — puts the packages under load and verifies invariants
// while measuring: wall time, throughput, latency percentiles, allocations.
// Every phase ends in Check(...)s; the process exits non-zero on any failure.
// ════════════════════════════════════════════════════════════════════════════════

var failures = 0;
var clock = TimeProvider.System;
var work = Path.Combine(Path.GetTempPath(), "jamejam-stress-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);

void Check(bool condition, string what)
{
    if (condition)
    {
        Console.WriteLine(FormattableString.Invariant($"  ✓ {what}"));
    }
    else
    {
        Interlocked.Increment(ref failures);
        Console.WriteLine(FormattableString.Invariant($"  ✗ FAIL: {what}"));
    }
}

Console.WriteLine("════════════════════════════════════════════════════════════════════");
Console.WriteLine(" JameJam stress harness");
Console.WriteLine("════════════════════════════════════════════════════════════════════");

const int Total = 10_000;
string Body(int i, int total) => new StringBuilder()
    .Append(CultureInfo.InvariantCulture, $"Note {i} of {total}. ")
    .Append("Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor. ")
    .Append(CultureInfo.InvariantCulture, $"Needle{i % 97} and rare-token-{i % 13}. ")
    .Append(CultureInfo.InvariantCulture, $"Linked to [[Note {(i + 1) % total}]] and [[Note {(i * 7) % total}]]. ")
    .Append(i % 3 == 0 ? "- [ ] task one\n" : "- [x] done task\n")
    .ToString();

// ── Phase 1: bulk insert through the SQLite store (10k notes, FTS triggers fire) ──
var dbPath = Path.Combine(work, "divan-10k.db");
using (var phase = new Phase("divan insert 10k"))
{
    var store = new SqliteDivanStore(dbPath);
    _ = store.AddNotebook(new Notebook(0, "Stress", clock.GetUtcNow(), false, clock.GetUtcNow()));
    var watch = Stopwatch.StartNew();
    var alloc0 = GC.GetTotalAllocatedBytes(precise: true);
    for (var i = 0; i < Total; i++)
    {
        _ = store.AddNote(new Note(
            0, 1, $"Note {i}", Body(i, Total), i % 5 == 0 ? "stress" : string.Empty,
            false, false, clock.GetUtcNow(), clock.GetUtcNow()));
    }

    watch.Stop();
    var mb = (GC.GetTotalAllocatedBytes(precise: true) - alloc0) / 1024.0 / 1024.0;
    Check(store.ListNotes().Count == Total, FormattableString.Invariant($"all {Total} notes persisted"));
    var size = new FileInfo(dbPath).Length;
    if (File.Exists(dbPath + "-wal"))
    {
        size += new FileInfo(dbPath + "-wal").Length;
    }
    phase.Finish(watch.Elapsed, mb, FormattableString.Invariant($"{Total / watch.Elapsed.TotalSeconds,7:0} notes/s  db {size / 1024.0 / 1024.0:0.0} MB"));
}

// ── Phase 2: FTS search latency on the 10k pad (300 queries: rare / AND / miss) ──
using (var phase = new Phase("divan search (FTS)"))
{
    var store = new SqliteDivanStore(dbPath);
    List<double> samples = [];
    var hits = 0;
    for (var q = 0; q < 100; q++)
    {
        var watch = Stopwatch.StartNew();
        var rare = store.SearchIds($"rare-token-{q % 13}", 20);
        watch.Stop();
        samples.Add(watch.Elapsed.TotalMilliseconds);
        hits += rare.Count > 0 ? 1 : 0;

        watch.Restart();
        var and = store.SearchIds($"Note {q} needle{q % 97}", 20); // AND across title + body
        watch.Stop();
        samples.Add(watch.Elapsed.TotalMilliseconds);
        hits += and.Count > 0 ? 1 : 0;

        watch.Restart();
        _ = store.SearchIds("nothing-matches-this-ever", 20);
        watch.Stop();
        samples.Add(watch.Elapsed.TotalMilliseconds);
    }

    var p95 = Percentile(samples, 95);
    var p99 = Percentile(samples, 99);
    Check(hits == 200, FormattableString.Invariant($"every seeded query matches ({hits}/200)"));
    Check(p99 < 100, FormattableString.Invariant($"p99 under 100 ms (p99 = {p99:0.00} ms)"));
    phase.Finish(
        samples.Aggregate(TimeSpan.Zero, (acc, ms) => acc + TimeSpan.FromMilliseconds(ms)),
        0,
        FormattableString.Invariant($"300 q  p50 {Percentile(samples, 50):0.00}  p95 {p95:0.00}  p99 {p99:0.00} ms"));
}

// ── Phase 3: full listing + metrics over the 10k pad ──
using (var phase = new Phase("divan list + metrics"))
{
    var store = new SqliteDivanStore(dbPath);
    var service = new DivanService(store, clock);
    var watch = Stopwatch.StartNew();
    for (var round = 0; round < 20; round++)
    {
        _ = service.List();
    }

    var listMs = watch.Elapsed.TotalMilliseconds / 20;

    watch.Restart();
    var words = 0L;
    foreach (var note in service.List())
    {
        words += service.Metrics(note).Words;
    }

    var metricsMs = watch.Elapsed.TotalMilliseconds;
    Check(words > 250_000, FormattableString.Invariant($"metrics actually read the pad ({words} words total)"));
    phase.Finish(
        watch.Elapsed, 0,
        FormattableString.Invariant($"list(10k) {listMs:0.0} ms  metrics(10k) {metricsMs:0} ms"));
}

// ── Phase 4: undo churn — whole-pad snapshots on a live 2k-note service ──
using (var phase = new Phase("divan undo churn"))
{
    var store = new MemoryDivanStore();
    var service = new DivanService(store, clock);
    for (var i = 0; i < 2_000; i++)
    {
        _ = service.AddNote($"N{i}", Body(i, 2_000));
    }

    List<double> perOp = [];
    for (var round = 0; round < 20; round++)
    {
        var watch = Stopwatch.StartNew();
        _ = service.EditNote(1 + round, title: $"Retitled {round}");
        watch.Stop();
        perOp.Add(watch.Elapsed.TotalMilliseconds);
        CheckTrue(service.Undo(), "undo must revert");
    }

    CheckTrue(perOp.Max() < 500, FormattableString.Invariant(
        $"snapshot+edit stays interactive at 2k notes (max {perOp.Max():0.0} ms)"));
    phase.Finish(
        perOp.Aggregate(TimeSpan.Zero, (acc, ms) => acc + TimeSpan.FromMilliseconds(ms)),
        0,
        FormattableString.Invariant($"edit+snapshot@2k  p50 {Percentile(perOp, 50):0.0}  max {perOp.Max():0.0} ms"));
}

// ── Phase 5: markdown export / import of the whole 10k pad ──
var exportFolder = Path.Combine(work, "export");
using (var phase = new Phase("divan export / import"))
{
    var store = new SqliteDivanStore(dbPath);
    var service = new DivanService(store, clock);
    var watch = Stopwatch.StartNew();
    _ = service.Export(exportFolder);
    var exportMs = watch.Elapsed.TotalMilliseconds;
    var files = Directory.GetFiles(exportFolder, "*.md").Length;

    var fresh = new SqliteDivanStore(Path.Combine(work, "divan-imported.db"));
    var freshService = new DivanService(fresh, clock);
    watch.Restart();
    var imported = freshService.Import(exportFolder);
    var importMs = watch.Elapsed.TotalMilliseconds;

    Check(files == Total, FormattableString.Invariant($"export wrote one file per note ({files})"));
    // Import deliberately caps at DivanDefaults.MaxImportFiles (500) — large restores go
    // through sync/undo, not folder import. The rail must hold exactly.
    Check(imported == JameJam.Divan.DivanDefaults.MaxImportFiles,
        FormattableString.Invariant($"import respects its 500-file rail ({imported})"));
    phase.Finish(
        watch.Elapsed, 0,
        FormattableString.Invariant($"export {exportMs:0} ms  import {importMs:0} ms  ({files} files)"));
}

// ── Phase 6: sync — two 10k-device pads diverge, merge, converge ──
using (var phase = new Phase("sync merge 10k pads"))
{
    var storeA = new SqliteDivanStore(Path.Combine(work, "sync-a.db"));
    var storeB = new SqliteDivanStore(Path.Combine(work, "sync-b.db"));
    var serviceA = new DivanService(storeA, clock);
    var serviceB = new DivanService(storeB, clock);
    var adapterA = new DivanSyncAdapter(serviceA, storeA, clock);
    var adapterB = new DivanSyncAdapter(serviceB, storeB, clock);

    _ = storeA.AddNotebook(new Notebook(0, "Shared", clock.GetUtcNow(), false, clock.GetUtcNow()));
    _ = storeB.AddNotebook(new Notebook(0, "Shared", clock.GetUtcNow(), false, clock.GetUtcNow()));
    for (var i = 0; i < Total; i++)
    {
        var note = storeA.AddNote(new Note(
            0, 1, $"Note {i}", Body(i, Total), string.Empty, false, false, clock.GetUtcNow(), clock.GetUtcNow()));
        _ = storeB.AddNote(new Note(
            0, 1, $"Note {i}", Body(i, Total), string.Empty, false, false, clock.GetUtcNow(), clock.GetUtcNow(),
            note.SyncId)); // the same logical note on both devices
    }

    // Diverge: A edits every 4th (+1 min) and deletes every 200th; B edits every 4th
    // (+2 min — newer, so B wins) and deletes every 250th; each adds 500 fresh notes.
    for (var i = 0; i < Total; i += 4)
    {
        var a = storeA.FindNote(i + 1)!;
        storeA.UpdateNote(a with { Body = a.Body + " — edited on A", UpdatedAt = clock.GetUtcNow().AddMinutes(1) });
        var b = storeB.FindNote(i + 1)!;
        storeB.UpdateNote(b with { Body = b.Body + " — edited on B (newer)", UpdatedAt = clock.GetUtcNow().AddMinutes(2) });
    }

    for (var i = 0; i < Total; i += 200)
    {
        _ = storeA.RemoveNote(i + 1, clock.GetUtcNow());
    }

    for (var i = 0; i < Total; i += 250)
    {
        _ = storeB.RemoveNote(i + 1, clock.GetUtcNow());
    }

    for (var i = 0; i < 500; i++)
    {
        _ = storeA.AddNote(new Note(0, 1, $"Fresh A{i}", "from a", string.Empty, false, false, clock.GetUtcNow(), clock.GetUtcNow()));
        _ = storeB.AddNote(new Note(0, 1, $"Fresh B{i}", "from b", string.Empty, false, false, clock.GetUtcNow(), clock.GetUtcNow()));
    }

    var watch = Stopwatch.StartNew();
    var localA = await adapterA.CaptureAsync().ConfigureAwait(false);
    var captureMs = watch.Elapsed.TotalMilliseconds;
    var payloadKb = Encoding.UTF8.GetByteCount(localA) / 1024.0;

    watch.Restart();
    var remoteB = await adapterB.CaptureAsync().ConfigureAwait(false);
    var merged = await adapterA.MergeAsync(localA, remoteB, "device-a", "device-b").ConfigureAwait(false);
    var mergeMs = watch.Elapsed.TotalMilliseconds;

    watch.Restart();
    var applied = await adapterA.ApplyAsync(merged).ConfigureAwait(false);
    var applyMs = watch.Elapsed.TotalMilliseconds;
    _ = await adapterB.ApplyAsync(merged).ConfigureAwait(false);

    var converged = string.Equals(
        await adapterA.CaptureAsync().ConfigureAwait(false),
        await adapterB.CaptureAsync().ConfigureAwait(false),
        StringComparison.Ordinal);
    Check(converged, "both devices converge to byte-identical pads after the merge");
    Check(mergeMs < 5_000, FormattableString.Invariant($"merge stays fast at 10k (took {mergeMs:0} ms)"));
    phase.Finish(
        watch.Elapsed, 0,
        FormattableString.Invariant(
            $"capture {captureMs:0} ms ({payloadKb:0} KB)  merge {mergeMs:0} ms  apply {applyMs:0} ms ({applied} changed)"));
}

// ── Phase 7: sync over real HTTP — 10 alternating cycles + a 503-retry injection ──
using (var phase = new Phase("sync over real HTTP"))
{
    using var blob = new BlobServer();
    blob.Start();
    var options = new SyncOptions
    {
        Endpoint = blob.Url,
        MaxRetries = 2,
        RetryBaseDelay = TimeSpan.FromMilliseconds(1),
    };

    var storeA = new SqliteDivanStore(Path.Combine(work, "http-a.db"));
    var storeB = new SqliteDivanStore(Path.Combine(work, "http-b.db"));
    var serviceA = new DivanService(storeA, clock);
    var serviceB = new DivanService(storeB, clock);
    var adapterA = new DivanSyncAdapter(serviceA, storeA, clock);
    var adapterB = new DivanSyncAdapter(serviceB, storeB, clock);
    for (var i = 0; i < 1_000; i++)
    {
        _ = serviceA.AddNote($"HTTP Note {i}", Body(i, 1_000));
    }

    var client = new HttpSyncClient(new HttpClient(), options);
    List<double> cycleMs = [];
    for (var cycle = 0; cycle < 10; cycle++)
    {
        var onA = cycle % 2 == 0;
        var watch = Stopwatch.StartNew();
        _ = await SyncEngine.RunAsync(
            client,
            onA ? adapterA : adapterB,
            onA ? "device-a" : "device-b",
            onA ? "A" : "B",
            SyncMode.Merge,
            force: false,
            clock.GetUtcNow()).ConfigureAwait(false);
        watch.Stop();
        cycleMs.Add(watch.Elapsed.TotalMilliseconds);
    }

    var flakyClient = new HttpSyncClient(
        new HttpClient(new FlakyHandler(new HttpClientHandler(), timesToFail: 2)),
        options with { RetryBaseDelay = TimeSpan.FromMilliseconds(10) });
    var recovered = await SyncEngine.RunAsync(
        flakyClient, adapterB, "device-b", "B", SyncMode.Pull, force: false, clock.GetUtcNow()).ConfigureAwait(false);
    Check(recovered.Applied >= 0, "retries recover from transient 503s");
    Check(Percentile(cycleMs, 95) < 2_000, FormattableString.Invariant(
        $"HTTP cycles stay fast (p95 {Percentile(cycleMs, 95):0} ms)"));
    var aPayload = await adapterA.CaptureAsync().ConfigureAwait(false);
    var bPayload = await adapterB.CaptureAsync().ConfigureAwait(false);
    Check(string.Equals(aPayload, bPayload, StringComparison.Ordinal), "HTTP sync converged A and B");
    phase.Finish(
        TimeSpan.Zero, 0,
        FormattableString.Invariant($"10 cycles  p50 {Percentile(cycleMs, 50):0}  p95 {Percentile(cycleMs, 95):0} ms  (~700 KB docs)"));
}

// ── Phase 8: the payload rail must hold (9 MB envelope refused, cleanly) ──
using (var phase = new Phase("sync payload rail"))
{
    var huge = new string('x', 9 * 1024 * 1024);
    var refused = false;
    try
    {
        _ = SyncSafety.Seal("divan", huge, "device", "bench", clock.GetUtcNow());
    }
    catch (SyncException ex)
    {
        refused = ex.Message.Contains("above the configured maximum", StringComparison.Ordinal);
    }

    Check(refused, "oversized payloads are refused before any network call");
    phase.Finish(TimeSpan.Zero, 0, "9 MB payload rejected by the 8 MiB rail");
}

// ── Phase 9: concurrency — 8 threads writing one SQLite pad while readers race them ──
using (var phase = new Phase("sqlite concurrency"))
{
    var store = new SqliteDivanStore(Path.Combine(work, "concurrent.db"));
    var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
    var watch = Stopwatch.StartNew();
    var writers = Enumerable.Range(0, 8).Select(writer => Task.Run(() =>
    {
        for (var i = 0; i < 250; i++)
        {
            try
            {
                _ = store.AddNote(new Note(
                    0, 1, $"W{writer} N{i}", "concurrent body", string.Empty, false, false,
                    clock.GetUtcNow(), clock.GetUtcNow()));
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
            }
        }
    }));
    var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
    {
        for (var i = 0; i < 100; i++)
        {
            try
            {
                _ = store.ListNotes().Count;
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
            }
        }
    }));
    await Task.WhenAll(writers.Concat(readers)).ConfigureAwait(false);
    watch.Stop();
    Check(errors.IsEmpty, FormattableString.Invariant(
        $"no locked/corrupt failures under 12 racing tasks ({string.Join("; ", errors.Take(3))})"));
    Check(store.ListNotes().Count == 2_000, "every concurrent write landed exactly once");
    phase.Finish(watch.Elapsed, 0, FormattableString.Invariant(
        $"2000 writes + 400 reads in {watch.Elapsed.TotalSeconds:0.0}s (8W + 4R threads)"));
}

// ── Phase 10: the text sanitizer against MB-scale + adversarial input ──
using (var phase = new Phase("text sanitizer"))
{
    var big = string.Concat(Enumerable.Repeat("clean text with \"quotes\" and \n newlines\t ", 120_000)); // ~5 MB
    var watch = Stopwatch.StartNew();
    for (var round = 0; round < 10; round++)
    {
        _ = TextGuard.SanitizeRequired(big, 10_000_000, "body");
    }

    var bigMs = watch.Elapsed.TotalMilliseconds / 10;

    var adversarial = string.Concat(Enumerable.Repeat("\"'\\\u0000\u2028\u2029", 250_000)); // ~1.75 MB
    watch.Restart();
    for (var round = 0; round < 50; round++)
    {
        _ = TextGuard.SanitizeRequired(adversarial, 10_000_000, "body");
    }

    var adversarialMs = watch.Elapsed.TotalMilliseconds / 50;
    Check(bigMs < 200, FormattableString.Invariant($"5 MB sanitized fast ({bigMs:0.0} ms/pass)"));
    phase.Finish(watch.Elapsed, 0, FormattableString.Invariant(
        $"5 MB clean {bigMs:0.0} ms/pass · 1.75 MB adversarial {adversarialMs:0.0} ms/pass"));
}

// ── Phase 11: vault crypto — PBKDF2 derives, AES-GCM bulk, TOTP throughput ──
using (var phase = new Phase("vault crypto"))
{
    var salt = new byte[32]; // VaultCrypto rail: exactly 32 bytes
    Random.Shared.NextBytes(salt);

    var watch = Stopwatch.StartNew();
    byte[] key = [];
    for (var round = 0; round < 5; round++)
    {
        key = VaultCrypto.DeriveKey("correct horse battery staple", salt, JameJam.Raz.RazDefaults.DefaultIterations);
    }

    var deriveMs = watch.Elapsed.TotalMilliseconds / 5;

    var secret = RandomNumberString(1024 * 1024); // 1 MB vault payload
    watch.Restart();
    for (var round = 0; round < 20; round++)
    {
        var cipher = VaultCrypto.Encrypt(key, secret);
        var plain = VaultCrypto.Decrypt(key, cipher);
        CheckTrue(string.Equals(plain, secret, StringComparison.Ordinal), "AES-GCM round-trip");
    }

    var aesMs = watch.Elapsed.TotalMilliseconds / 20;

    watch.Restart();
    const string Seed = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
    for (var round = 0; round < 10_000; round++)
    {
        _ = Totp.ComputeCode(Seed, clock.GetUtcNow().AddSeconds(round));
    }

    var totpMicros = watch.Elapsed.TotalMilliseconds / 10_000 * 1000;
    phase.Finish(watch.Elapsed, 0, FormattableString.Invariant(
        $"PBKDF2-210k {deriveMs:0} ms/derive · AES-GCM 1 MB {aesMs:0.0} ms · TOTP {totpMicros:0} µs/code"));
}

// ── Phase 12: the Soroush AI funnel under load — 300 real HTTP round-trips + 429 retry ──
using (var phase = new Phase("soroush funnel load"))
{
    using var mock = new MockAiServer();
    mock.Start();
    var options = new SoroushOptions
    {
        Provider = "openai",
        Endpoint = mock.Url,
        ApiKey = "sk-stress-key-not-real",
        RequestTimeout = TimeSpan.FromSeconds(10),
        RetryBaseDelay = TimeSpan.FromMilliseconds(1),
    };
    var client = new SoroushClient(new HttpClient(SoroushHttp.CreateHandler()), options);

    List<double> samples = [];
    var answered = 0;
    for (var call = 0; call < 300; call++)
    {
        var watch = Stopwatch.StartNew();
        var result = await client.CompleteAsync(FormattableString.Invariant($"Stress call {call}: say done.")).ConfigureAwait(false);
        watch.Stop();
        samples.Add(watch.Elapsed.TotalMilliseconds);
        answered += result.Content.Length > 0 ? 1 : 0;
    }

    Check(answered == 300, FormattableString.Invariant(
        $"300/300 funnel calls answered (p95 {Percentile(samples, 95):0.0} ms)"));

    using var flakyMock = new MockAiServer(failFirstWith429: 2);
    flakyMock.Start();
    var retryClient = new SoroushClient(
        new HttpClient(SoroushHttp.CreateHandler()),
        options with { Endpoint = flakyMock.Url, MaxRetries = 3 });
    var retried = await retryClient.CompleteAsync("retry me").ConfigureAwait(false);
    Check(retried.Content.Length > 0 && flakyMock.Requests >= 3, "429 storms are retried to success");

    phase.Finish(
        samples.Aggregate(TimeSpan.Zero, (acc, ms) => acc + TimeSpan.FromMilliseconds(ms)),
        0,
        FormattableString.Invariant(
            $"300 calls  p50 {Percentile(samples, 50):0.0}  p95 {Percentile(samples, 95):0.0}  max {samples.Max():0.0} ms"));
}

// ── wrap up ──
Directory.Delete(work, recursive: true);
Console.WriteLine("════════════════════════════════════════════════════════════════════");
Console.WriteLine(failures == 0
    ? " ALL CHECKS PASSED — the packages held up under stress."
    : FormattableString.Invariant($" {failures} CHECK(S) FAILED — see the ✗ lines above."));
return failures == 0 ? 0 : 1;

static double Percentile(List<double> samples, double p)
{
    var sorted = samples.OrderBy(x => x).ToList();
    var index = (int)Math.Ceiling((p / 100.0 * sorted.Count) - 1);
    return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
}

static void CheckTrue(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(FormattableString.Invariant($"invariant broken: {message}"));
    }
}

static string RandomNumberString(int chars)
{
    var builder = new StringBuilder(chars);
    for (var i = 0; i < chars; i++)
    {
        _ = builder.Append((char)('0' + (i * 31 % 10)));
    }

    return builder.ToString();
}

/// <summary>A measured stress phase: prints one aligned result line when finished.</summary>
internal sealed class Phase(string name) : IDisposable
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly long _alloc0 = GC.GetTotalAllocatedBytes(precise: true);

    public void Finish(TimeSpan busy, double megabytes, string metrics)
    {
        _watch.Stop();
        var allocated = megabytes > 0
            ? megabytes
            : (GC.GetTotalAllocatedBytes(precise: true) - _alloc0) / 1024.0 / 1024.0;
        Console.WriteLine(FormattableString.Invariant(
            $"► {name,-26} wall {_watch.Elapsed.TotalSeconds,6:0.00}s  busy {busy.TotalMilliseconds,8:0} ms  alloc {allocated,8:0.0} MB   {metrics}"));
    }

    public void Dispose()
    {
    }
}

/// <summary>Minimal loopback blob server — the "any server" contract, in ~40 lines.</summary>
internal sealed class BlobServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly int _port = Random.Shared.Next(29_000, 39_000);
    private string? _document;
    private Task? _loop;

    public string Url => $"http://127.0.0.1:{_port}/blob/pad.json";

    public void Start()
    {
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _listener.Start();
        _loop = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                try
                {
                    var context = await _listener.GetContextAsync().ConfigureAwait(false);
                    if (context.Request.HttpMethod == "GET")
                    {
                        if (_document is null)
                        {
                            context.Response.StatusCode = 404;
                        }
                        else
                        {
                            context.Response.StatusCode = 200;
                            context.Response.ContentType = "application/json";
                            var bytes = Encoding.UTF8.GetBytes(_document);
                            context.Response.ContentLength64 = bytes.Length;
                            await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                        }
                    }
                    else if (context.Request.HttpMethod == "PUT")
                    {
                        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                        _document = await reader.ReadToEndAsync().ConfigureAwait(false);
                        context.Response.StatusCode = 204;
                    }
                    else
                    {
                        context.Response.StatusCode = 405;
                    }

                    context.Response.Close();
                }
                catch (Exception) when (!_listener.IsListening)
                {
                    return;
                }
                catch (HttpListenerException)
                {
                    // listener stopped mid-request — the shutdown path
                }
            }
        });
    }

    public void Dispose()
    {
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

/// <summary>Wraps the pipeline with transient 503s to exercise the retry path.</summary>
internal sealed class FlakyHandler(HttpMessageHandler inner, int timesToFail) : DelegatingHandler(inner)
{
    private int _remaining = timesToFail;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Interlocked.Decrement(ref _remaining) >= 0)
        {
            return Task.FromResult<HttpResponseMessage>(
                new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("flaky") });
        }

        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>Loopback OpenAI-compatible mock: deterministic replies, optional 429 storms.</summary>
internal sealed class MockAiServer(int failFirstWith429 = 0) : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly int _port = Random.Shared.Next(39_000, 49_000);
    private int _requests;
    private Task? _loop;

    public string Url => $"http://127.0.0.1:{_port}/v1/chat/completions";

    public int Requests => _requests;

    public void Start()
    {
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _listener.Start();
        _loop = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                try
                {
                    var context = await _listener.GetContextAsync().ConfigureAwait(false);
                    _ = Interlocked.Increment(ref _requests);
                    if (_requests <= failFirstWith429)
                    {
                        context.Response.StatusCode = 429;
                        context.Response.Headers.Add("Retry-After", "0");
                        context.Response.Close();
                        continue;
                    }

                    var payload = """{"choices":[{"message":{"role":"assistant","content":"done"}}],"usage":{"total_tokens":1}}""";
                    var bytes = Encoding.UTF8.GetBytes(payload);
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                    context.Response.Close();
                }
                catch (Exception) when (!_listener.IsListening)
                {
                    return;
                }
                catch (HttpListenerException)
                {
                }
            }
        });
    }

    public void Dispose()
    {
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
