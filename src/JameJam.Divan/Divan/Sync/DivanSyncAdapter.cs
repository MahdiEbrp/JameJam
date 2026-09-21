using System.Text.Json;
using System.Text.Json.Serialization;

using JameJam.Sync;

namespace JameJam.Divan.Sync;

/// <summary>
/// Bridges the Divan pad into the shared sync engine. The wire payload is name-keyed for
/// notebooks and sync-id-keyed (GUID) for notes, so two devices merge without ever seeing
/// each other's local row ids. Conflicts resolve deterministically on both sides:
/// last-write-wins on <see cref="Note.UpdatedAt"/>; equal timestamps prefer tombstones,
/// then the higher payload checksum — the same note always survives to the same value.
/// </summary>
/// <param name="service">The pad service (rails, snapshots).</param>
/// <param name="store">The pad store (capture/apply at record level).</param>
public sealed class DivanSyncAdapter(DivanService service, IDivanStore store, TimeProvider clock) : ISyncAdapter
{
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions WireReader = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <inheritdoc />
    public string Service => "divan";

    /// <inheritdoc />
    public Task<string> CaptureAsync(CancellationToken cancellationToken = default)
    {
        var notebooks = store.ListNotebooks();
        var notebookNames = notebooks.ToDictionary(n => n.Id, n => n.Name);

        // Canonical order: by sync identity (notes/tombstones) and name (notebooks) — never by
        // local row id, which is device-local. Two converged pads must serialize byte-identically
        // even though their local ids diverge.
        var payload = new SyncPayload(
            [.. notebooks
                .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .Select(n => new NotebookDto(n.Name, n.IsArchived, n.UpdatedAt == default ? n.CreatedAt : n.UpdatedAt))],
            [.. store.ListNotes()
                .OrderBy(n => n.SyncId)
                .Select(n => new NoteDto(
                    n.SyncId,
                    notebookNames.GetValueOrDefault(n.NotebookId, string.Empty),
                    n.Title,
                    n.Body,
                    n.Tags,
                    n.Pinned,
                    n.Archived,
                    n.CreatedAt,
                    n.UpdatedAt))],
            [.. store.GetTombstones().OrderBy(t => t.SyncId).Select(t => new TombstoneDto(t.SyncId, t.DeletedAt))]);
        return Task.FromResult(JsonSerializer.Serialize(payload, WireOptions));
    }

    /// <inheritdoc />
    public Task<string> MergeAsync(
        string localJson,
        string remoteJson,
        string localDeviceId,
        string remoteDeviceId,
        CancellationToken cancellationToken = default)
    {
        var local = Parse(localJson);
        var remote = Parse(remoteJson);

        // Notebooks: union by case-insensitive name; the archive flag goes to the record
        // that changed last (UpdatedAt), ties stay unarchived so data is never hidden.
        Dictionary<string, NotebookDto> notebooks = new(StringComparer.OrdinalIgnoreCase);
        foreach (var notebook in local.Notebooks.Concat(remote.Notebooks))
        {
            var key = notebook.Name.Trim();
            if (notebooks.TryGetValue(key, out var existing))
            {
                notebooks[key] = PickNotebook(existing, notebook);
            }
            else
            {
                notebooks[key] = notebook;
            }
        }

        // Notes: union by sync id; last-write-wins, tombstone beats an equally old note.
        Dictionary<Guid, NoteDto> notes = [];
        foreach (var note in local.Notes.Concat(remote.Notes))
        {
            if (notes.TryGetValue(note.SyncId, out var existing))
            {
                notes[note.SyncId] = PickNote(existing, note);
            }
            else
            {
                notes[note.SyncId] = note;
            }
        }

        // Tombstones: keep every live one, then reconcile each against a live note of the
        // same sync id — the newer operation wins (equal times: the deletion stands).
        Dictionary<Guid, TombstoneDto> tombstones = [];
        foreach (var tombstone in local.Tombstones.Concat(remote.Tombstones))
        {
            if (tombstone.SyncId == Guid.Empty)
            {
                continue;
            }

            tombstones[tombstone.SyncId] = !tombstones.TryGetValue(tombstone.SyncId, out var existing)
                || tombstone.DeletedAt > existing.DeletedAt
                ? tombstone
                : existing;
        }

        foreach (var tombstone in tombstones.Values.ToList())
        {
            if (!notes.TryGetValue(tombstone.SyncId, out var live))
            {
                continue;
            }

            if (tombstone.DeletedAt >= live.UpdatedAt)
            {
                _ = notes.Remove(tombstone.SyncId); // the deletion is newer — it stands
            }
            else
            {
                _ = tombstones.Remove(tombstone.SyncId); // the edit is newer — resurrect
            }
        }

        var merged = new SyncPayload([.. notebooks.Values], [.. notes.Values], [.. tombstones.Values]);
        return Task.FromResult(JsonSerializer.Serialize(merged, WireOptions));
    }

    /// <inheritdoc />
    public Task<int> ApplyAsync(string mergedJson, CancellationToken cancellationToken = default)
    {
        var merged = Parse(mergedJson);
        service.PushUndoSnapshot(); // one `divan undo` reverts the whole apply

        var changed = 0;
        var now = clock.GetUtcNow();

        // 1) Notebooks by name — existing ones keep their ids.
        Dictionary<string, long> notebookIds = new(StringComparer.OrdinalIgnoreCase);
        foreach (var notebook in merged.Notebooks)
        {
            var name = DivanText.Clip(notebook.Name.Trim(), DivanDefaults.MaxNotebookNameLength);
            if (name.Length == 0)
            {
                continue;
            }

            if (store.FindNotebookByName(name) is { } existing)
            {
                notebookIds[name] = existing.Id;
                // Align EVERYTHING the payload carries — the archive flag AND the timestamp —
                // otherwise two devices never converge on the notebook's last-write time.
                if (existing.IsArchived != notebook.Archived || existing.UpdatedAt != notebook.UpdatedAt)
                {
                    store.UpdateNotebook(existing with { IsArchived = notebook.Archived, UpdatedAt = notebook.UpdatedAt });
                    changed++;
                }
            }
            else
            {
                var created = store.AddNotebook(
                    new Notebook(0, name, notebook.UpdatedAt == default ? now : notebook.UpdatedAt, notebook.Archived, notebook.UpdatedAt));
                notebookIds[name] = created.Id;
                changed++;
            }
        }

        var fallbackNotebook = notebookIds.Values.Count > 0 ? notebookIds.Values.Min() : 0;
        var localBySyncId = store.ListNotes()
            .Where(n => n.SyncId != Guid.Empty)
            .ToDictionary(n => n.SyncId, n => n);

        // 2) Notes by sync id — local rows keep their ids; foreign rows are inserted.
        foreach (var note in merged.Notes)
        {
            if (note.SyncId == Guid.Empty)
            {
                continue; // cannot be merged safely — ignore rather than fork
            }

            if (fallbackNotebook == 0 && !notebookIds.ContainsKey(note.Notebook.Trim()))
            {
                continue; // no notebook to attach to — leave for the next sync rather than strand it
            }

            var notebookId = ResolveNotebookId(note.Notebook, notebookIds, fallbackNotebook);
            if (localBySyncId.TryGetValue(note.SyncId, out var existing))
            {
                var updated = existing with
                {
                    NotebookId = notebookId,
                    Title = note.Title,
                    Body = note.Body,
                    Tags = note.Tags,
                    Pinned = note.Pinned,
                    Archived = note.Archived,
                    CreatedAt = note.CreatedAt,
                    UpdatedAt = note.UpdatedAt,
                };
                if (updated != existing)
                {
                    store.UpdateNote(updated);
                    changed++;
                }

                _ = localBySyncId.Remove(note.SyncId);
            }
            else
            {
                _ = store.AddNote(new Note(
                    0,
                    notebookId,
                    note.Title,
                    note.Body,
                    note.Tags,
                    note.Pinned,
                    note.Archived,
                    note.CreatedAt,
                    note.UpdatedAt,
                    note.SyncId));
                changed++;
            }
        }

        // 3) Local notes the merge dropped are tombstoned remotely — delete them here too.
        var live = merged.Tombstones.Select(t => t.SyncId).ToHashSet();
        foreach (var orphan in localBySyncId.Values.Where(n => live.Contains(n.SyncId)))
        {
            _ = store.RemoveNote(orphan.Id, merged.Tombstones.First(t => t.SyncId == orphan.SyncId).DeletedAt);
            changed++;
        }

        // 4) Align tombstone timestamps with the payload — when both devices deleted the same
        // note, each recorded its own time; the merged time is canonical for everyone.
        var known = store.GetTombstones().ToDictionary(t => t.SyncId, t => t.DeletedAt);
        foreach (var tombstone in merged.Tombstones)
        {
            if (!known.TryGetValue(tombstone.SyncId, out var current) || current != tombstone.DeletedAt)
            {
                store.UpsertTombstone(new DivanTombstone(tombstone.SyncId, tombstone.DeletedAt));
            }
        }

        return Task.FromResult(changed);
    }

    private static SyncPayload Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<SyncPayload>(json, WireReader) ?? new SyncPayload([], [], []);
        }
        catch (JsonException ex)
        {
            throw new SyncException("The synced Divan payload is not valid.", innerException: ex);
        }
    }

    private static NotebookDto PickNotebook(NotebookDto a, NotebookDto b)
    {
        if (a.UpdatedAt != b.UpdatedAt)
        {
            return a.UpdatedAt > b.UpdatedAt ? a : b;
        }

        return a with { Archived = a.Archived && b.Archived }; // exact tie: never hide data
    }

    private static NoteDto PickNote(NoteDto a, NoteDto b)
    {
        if (a.UpdatedAt != b.UpdatedAt)
        {
            return a.UpdatedAt > b.UpdatedAt ? a : b;
        }

        // Exact time tie: a deletion beats an equally old edit; two live versions resolve by
        // a checksum of their JSON — deterministic on every device, no ping-pong.
        if (a.Archived == false && b.Archived == false)
        {
            return string.CompareOrdinal(JsonSerializer.Serialize(a, WireOptions), JsonSerializer.Serialize(b, WireOptions)) >= 0
                ? a
                : b;
        }

        return a.Archived ? a : b;
    }

    private static long ResolveNotebookId(string name, Dictionary<string, long> ids, long fallback) =>
        ids.TryGetValue(name.Trim(), out var id) ? id : fallback;

    // ── Wire DTOs ──

    private sealed record SyncPayload(
        IReadOnlyList<NotebookDto> Notebooks,
        IReadOnlyList<NoteDto> Notes,
        IReadOnlyList<TombstoneDto> Tombstones);

    private sealed record NotebookDto(string Name, bool Archived, DateTimeOffset UpdatedAt);

    private sealed record NoteDto(
        Guid SyncId,
        string Notebook,
        string Title,
        string Body,
        string Tags,
        bool Pinned,
        bool Archived,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    private sealed record TombstoneDto(Guid SyncId, DateTimeOffset DeletedAt);
}
