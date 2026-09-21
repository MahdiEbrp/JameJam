# JameJam Sync — the two-device guide (guid.md)

How to sync JameJam between two devices — laptop and desktop, phone terminal and home server —
through **any server that can store and serve one JSON document**. No JameJam code on the server,
no database, no plugin: Node.js, PHP, Python, nginx+WebDAV, an S3 bucket — if it can `GET` and
`PUT` a file behind a bearer token, it is a JameJam sync server.

```
┌──────────────┐        GET / PUT          ┌─────────────────────┐        GET / PUT        ┌──────────────┐
│   Device A   │ ────────────────────────► │   Any server        │ ◄────────────────────── │   Device B   │
│  (laptop)    │   one JSON envelope       │  node / php / …     │   one JSON envelope     │  (desktop)   │
└──────────────┘ ◄──────────────────────── │  (dumb blob store)  │ ────────────────────────┴──────────────┘
      │                pull                └─────────────────────┘            ▲      │
      │                                                                       │      │
      └────────────── deterministic merge (same result on both sides) ────────┘──────┘
```

---

## 1. One-time setup (5 minutes)

**On any host** — deploy a dumb blob endpoint. The complete server contract is:

| Call | Meaning |
|---|---|
| `GET  <url>` with `Authorization: Bearer <token>` | `200` + the stored document, or `404` if nothing stored yet |
| `PUT  <url>` with the same header + JSON body | `2xx` to accept (it becomes what `GET` returns), anything else to reject |

A reference Node.js server (~15 lines) and PHP server (~15 lines) are at the bottom of this file.

**On each device** — point JameJam at the URL and share the token:

```bash
# the token lives in the environment only — never argv, never stored
export JAMEJAM_SYNC_TOKEN="a-long-random-shared-secret"

# save the sync URL once (or pass --url on every sync)
JameJam settings set divan.syncUrl "https://your-host.example/pad.json"
```

Each device generates its own stable **device identity** (a GUID, stored in settings under
`sync.deviceId`) on first sync. Nothing else to configure.

## 2. Everyday use

```bash
JameJam divan sync                 # merge (default): pull → merge → push the merged state
JameJam divan sync pull            # only update this device, never touch the remote
JameJam divan sync push --force    # overwrite the remote with this device (safety: --force required)
```

A normal two-device day looks like this:

```bash
# On the laptop (morning): you write, then sync before leaving
JameJam divan new "Thesis Plan" --stdin < thesis.md
JameJam divan sync

# On the desktop (evening): pull everything, add notes, push
JameJam divan sync                  # → "Merged: 2 record(s) changed locally."
printf 'chapter one done\n' | JameJam divan append 1 --stdin
JameJam divan sync                  # publishes your changes

# Back on the laptop: one sync brings it all together
JameJam divan sync                  # → "Merged: 1 record(s) changed locally."
```

Sync anytime — there is no cursor, no sequence, no state to corrupt. Every run is independent:
pull the remote document, merge it into the local pad, push the merged result. Running it twice
in a row is a harmless no-op (`Merged: 0 record(s) changed locally.`).

## 3. How conflicts resolve (the algorithm)

Both devices only ever talk to the shared URL — never to each other. Convergence is guaranteed
because the merge is **deterministic, commutative, and idempotent** (a state-based CRDT merge):

1. **Identity**: every note carries a `SyncId` — a version-7 GUID minted at creation. It is the
   note's identity across devices; local row numbers (`divan show 1`) stay device-local.
2. **Notebooks** merge by (case-insensitive) **name** — a natural key. Whichever side last
   changed a notebook (created/renamed/archived — tracked by `UpdatedAt`) decides its archive
   flag; an exact tie resolves to *unarchived* (never hide data).
3. **Notes** merge by `SyncId` with **last-write-wins** on `UpdatedAt` (UTC):
   - edited on A at 10:00, edited on B at 10:05 → B's text wins **on both devices**;
   - an edit **newer than a deletion** resurrects the note; a deletion **newer than an edit**
     (a tombstone with `deletedAt`) removes it everywhere — equal times favor deletion;
   - a perfect tie (same timestamp, same sync id, different text) resolves by comparing the
     serialized candidates — both devices derive the *same* winner, so no ping-pong.
4. **Deletions** are tombstones (`syncId` + `deletedAt`), stored until the note is re-created
   (a fresh note mints a fresh `SyncId`). `divan undo` after a sync reverts the whole merge —
   and undoing a delete retracts the tombstone.

The guarantee: **if both devices run sync after any sequence of offline changes, they end on
byte-identical pads.** Verified in the test suite (both merge orders, tie matrices, delete-vs-edit
races) and live against a real HTTP server.

> ⚠ **Keep clocks honest.** LWW trusts device clocks. If a device's clock is minutes ahead, its
> older edit can still win. Use NTP on both devices; the tie rules only guarantee
> *determinism*, not clock correction.

## 4. Safety layer (what the transport refuses)

- **HTTPS enforced** — plain `http://` only on loopback (for local testing). Redirects are not
  followed, so a URL can't be hijacked mid-sync.
- **Bearer token from the environment only** (`JAMEJAM_SYNC_TOKEN`) — never argv, never the
  settings database, scrubbed from every error message.
- **Integrity**: every envelope carries a SHA-256 checksum of its payload; a corrupted or
  tampered document is rejected before anything merges.
- **Protocol gate**: envelopes are stamped `jamejam.sync/1`; a server or client speaking another
  version fails loudly with an upgrade hint instead of guessing.
- **Service isolation**: a Divan pad refuses to merge with a Haft Khan document that happens to
  live at the same URL — the envelope's service tag must match.
- **Rails on both sides**: payloads are size-capped (8 MiB default), re-validated and re-sanitized
  on receipt — the server is *untrusted input* too.
- **Raz never syncs.** There is no adapter for the vault by design: secrets have no code path to
  any network transport.

## 5. The wire format

One JSON document at the URL, sealed by the sender:

```json
{
  "schema": "jamejam.sync/1",
  "service": "divan",
  "deviceId": "0198f3a2-…",
  "deviceName": "laptop",
  "createdAt": "2026-09-20T12:00:00.0000000+00:00",
  "payload": "{ …service-specific JSON… }",
  "checksum": "sha256-hex-of-payload"
}
```

For Divan, `payload` (a JSON string) is:

```json
{
  "notebooks": [ { "name": "Research", "archived": false, "updatedAt": "…" } ],
  "notes":     [ { "syncId": "…", "notebook": "Research", "title": "…", "body": "…",
                   "tags": "a,b", "pinned": false, "archived": false,
                   "createdAt": "…", "updatedAt": "…" } ],
  "tombstones":[ { "syncId": "…", "deletedAt": "…" } ]
}
```

## 6. Reference servers

**Node.js** (no dependencies):

```javascript
const http = require("http");
const TOKEN = "a-long-random-shared-secret";
let doc = null;                                   // the whole "database"
http.createServer((req, res) => {
  if (req.headers.authorization !== `Bearer ${TOKEN}`)
    return res.writeHead(401).end();
  if (req.method === "GET")
    return doc ? res.writeHead(200, {"content-type": "application/json"}).end(doc)
               : res.writeHead(404).end();
  if (req.method === "PUT") {
    let chunks = [];
    req.on("data", c => chunks.push(c));
    return req.on("end", () => { doc = Buffer.concat(chunks); res.writeHead(204).end(); });
  }
  res.writeHead(405).end();
}).listen(8443);
```

**PHP** (drop into any host):

```php
<?php
header("Content-Type: application/json");
if (($_SERVER["HTTP_AUTHORIZATION"] ?? "") !== "Bearer a-long-random-shared-secret")
  http_response_code(401) & exit;
$file = __DIR__ . "/pad.json";
if ($_SERVER["REQUEST_METHOD"] === "GET") {
  clearstatcache();
  if (!file_exists($file)) http_response_code(404) & exit;
  header("Content-Length: " . filesize($file));
  readfile($file); exit;
}
if ($_SERVER["REQUEST_METHOD"] === "PUT") {
  file_put_contents($file, fopen("php://input", "rb")) or http_response_code(500);
  http_response_code(204); exit;
}
http_response_code(405);
```

Both implement the whole contract. Serve them behind HTTPS (Caddy/nginx terminate TLS) and
you are done.

## 7. Troubleshooting

| Symptom | Meaning / fix |
|---|---|
| `No sync URL` | Pass `--url` or save it: `JameJam settings set divan.syncUrl <url>` |
| `Insecure sync endpoint` | Use HTTPS; plain HTTP is only allowed on loopback |
| `401` in the message | The token differs between device and server — check `JAMEJAM_SYNC_TOKEN` |
| `failed its integrity check` | The document at the URL is corrupted (old sync crashed a naive server?) — `sync push --force` reseals it |
| `speaks sync protocol … upgrade` | Server/client version skew — update JameJam on both devices |
| `holds 'haftkhan' data … cannot merge` | Two services share one URL — give each service its own URL |
| `The remote holds changes … --force` | You asked for `push` over a changed remote — that guard is doing its job; use `sync` (merge) instead, or `--force` deliberately |
| A note appears twice | Rare clock-skew artifact of an old upgrade — delete one copy; tombstones make the removal stick |

## 8. Current scope & notes

- **Divan (notes)** syncs today; the adapter contract (`ISyncAdapter` in `JameJam.Sync`) is how
  any service joins — capture, merge, apply — and Haft Khan's task backup sync already rides the
  same hardened transport.
- `divan undo` right after a sync reverts the local merge; re-running sync deterministically
  rebuilds it.
- Notebooks that were `remove --force`d on one device may reappear empty after a merge (notes are
  tombstoned, the notebook shell is not). Delete it again locally if it bothers you — deletes of
  *notes* always propagate.
