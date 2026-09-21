# JameJam — stress test report

The packages were put under load by `bench/JameJam.Bench` — a repeatable harness
(`dotnet run --project bench/JameJam.Bench`) that runs 12 phases against the **real** libraries
(SQLite stores, sync engine, hardened HTTP transport, crypto, sanitizer, the Soroush funnel)
and asserts invariants — convergence, zero data loss, rails holding — while measuring wall
time, throughput, latency percentiles, and allocations. Every run ends in `ALL CHECKS PASSED`
or a non-zero exit.

**Verdict: the packages held up — and the stress run paid for itself by catching 3 real bugs,
all fixed with regression tests (1068/1068 green).**

## Results (10k-note pad, sandbox CPU)

| Phase | Result | Invariants verified |
|---|---|---|
| **Divan insert 10k** | **2 065 notes/s** · 5 MB db · 88 MB alloc | every note persisted exactly once |
| **Divan search (FTS)** | p50 **0.87 ms** · p95 2.1 · p99 2.1 ms (300 queries) | 200/200 seeded queries match (rare + AND-across-title/body) |
| **Divan list + metrics** | list(10k) **50 ms** · metrics(10k) 91 ms | full-pad reads stay interactive at 10× the note rail |
| **Divan undo churn** | edit+snapshot(2k pad) p50 **2.0 ms** · max 3.2 ms | undo always reverts; documented O(pad) snapshot cost (below) |
| **Divan export / import** | export 10k files **169 ms** · import 406 ms | one file per note; the 500-file import rail holds exactly |
| **Sync merge, two 10k pads** | capture **138 ms** (4.3 MB) · merge **222 ms** · apply 5.4 s (10 481 changed) | 2 500 edits + 90 deletions + 1 000 inserts → **byte-identical pads on both devices** |
| **Sync over real HTTP** | 10 alternating cycles p50 **13 ms** · p95 543 ms (~700 KB docs) | converged; **503 storms recovered by retry** |
| **Sync payload rail** | 9 MB envelope **refused** before any network call | the 8 MiB rail holds |
| **SQLite concurrency** | 2 000 writes + 400 reads, **8 writers + 4 readers**, 1.6 s | zero lock/corruption failures; every write landed exactly once |
| **Text sanitizer** | 5 MB **4–5 ms/pass** · 1.75 MB adversarial ~15 ms/pass | vectorized hot path; adversarial input stays linear |
| **Vault crypto** | PBKDF2-210k **150 ms**/derive · AES-GCM 1 MB **3.6 ms** · TOTP 4 µs/code | 1 MB payloads round-trip intact |
| **Soroush funnel** | 300 real HTTP calls p50 **0.2 ms** · max 11 ms | 300/300 answered; **429 storms retried to success** |

## Bugs the stress run caught (and how they were fixed)

1. **Sync convergence: notebook timestamps never aligned.** `apply` only synced the archive
   flag, so two devices that created "the same" notebook microseconds apart could never agree
   on its last-write time — breaking byte-convergence forever. *Fix:* apply now aligns every
   payload field, timestamp included. Regression: `Apply_AlignsNotebookTimestamps_SoDevicesConverge`.
2. **Sync convergence: competing tombstones kept device-local times.** When both devices
   deleted the same note, each kept its own `deletedAt` forever. Notes/bodies converged;
   tombstone metadata never did. *Fix:* new `IDivanStore.UpsertTombstone`; apply re-times all
   tombstones from the merged (canonical) payload. Regression:
   `Merge_BothDevicesDeletedTheSameNote_TombstoneTimesConverge`.
3. **Sync convergence: captures serialized in local-row-id order.** After cross-device
   inserts, local ids diverge, so two *identical* pads serialized differently — defeating the
   engine's byte-equality fast path and convergence checks. *Fix:* canonical serialization —
   notes/tombstones ordered by sync id, notebooks by name. This also makes the "identical
   state → no remote write" fast path more effective.

All three are exactly the class of bug that unit tests with hand-built fixtures miss and
scale + byte-equality assertions catch.

## Known, documented costs (by design, not defects)

- **Undo snapshots are O(pad).** Every service mutation serializes the whole pad for undo:
  building a 2k-note pad through the service allocates ~2.4 GB cumulatively (sum of growing
  snapshots ≈ O(n²) bytes), though each individual edit stays at ~2 ms and 20-deep undo is
  capped by `DivanOptions.UndoDepth`. This is the price of "one `undo` reverts anything, in
  one keystroke" — bulk imports of 10k+ notes should go through the store-backed paths
  (`sync`, store API) rather than thousands of CLI mutations.
- **Sync `apply` is the expensive step** (5.4 s for a 10k changed-record apply through the
  service with rails + snapshots) — capture/merge are 100–250 ms. Merges on the 10k scale
  remain comfortably interactive; the apply path re-validates every record on purpose
  (the server is untrusted input).
- **Vault unlock is deliberately ~150 ms** — PBKDF2-HMAC-SHA512 at 210 000 iterations is the
  brute-force cost we *want* attackers to pay.

## Environment notes

Numbers come from the shared CI-class sandbox (2 vCPU-class container, temp-file SQLite, WSL
loopback). Absolute times will differ on your hardware; the **invariants are machine-independent**
and are what the harness gates on: byte-identical convergence, no lost/locked writes, rails
holding, and sub-100 ms interactive latencies at 10k scale.
