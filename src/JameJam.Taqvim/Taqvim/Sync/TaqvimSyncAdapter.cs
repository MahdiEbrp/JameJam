using System.Text.Json;

using JameJam.Sync;

namespace JameJam.Taqvim.Sync;

/// <summary>
/// Bridges the Taqvim calendar into the shared sync engine. The wire payload is sync-id-keyed,
/// so two devices merge without seeing each other's local row ids. Conflicts resolve
/// deterministically on both sides: last-write-wins on <see cref="TaqvimEvent.UpdatedAt"/>;
/// exact ties prefer tombstones, then the lexicographically greater serialized candidate.
/// </summary>
/// <param name="service">The calendar service (rails, snapshots).</param>
/// <param name="store">The calendar store (capture/apply at record level).</param>
public sealed class TaqvimSyncAdapter(TaqvimService service, ITaqvimStore store) : ISyncAdapter
{
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions WireReader = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <inheritdoc />
    public string Service => "taqvim";

    /// <inheritdoc />
    public Task<string> CaptureAsync(CancellationToken cancellationToken = default)
    {
        // Canonical order: by sync identity — never by local row id — so two converged pads
        // serialize byte-identically even though their local ids diverge.
        var payload = new SyncPayload(
            [.. store.ListEvents()
                .OrderBy(e => e.SyncId)
                .Select(e => new EventDto(
                    e.SyncId,
                    e.Calendar,
                    e.Title,
                    e.Location,
                    e.Notes,
                    e.Tags,
                    e.Start,
                    e.End,
                    e.IsAllDay,
                    e.Rule,
                    e.Reminders,
                    e.CreatedAt,
                    e.UpdatedAt))],
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

        // Events: union by sync id; last-write-wins, tombstone beats an equally old event.
        Dictionary<Guid, EventDto> events = [];
        foreach (var ev in local.Events.Concat(remote.Events))
        {
            if (ev.SyncId == Guid.Empty)
            {
                continue; // cannot merge safely — ignore rather than fork
            }

            events[ev.SyncId] = !events.TryGetValue(ev.SyncId, out var existing)
                ? ev
                : PickEvent(existing, ev);
        }

        // Tombstones: keep every live one, then reconcile against a live event of the same id.
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
            if (!events.TryGetValue(tombstone.SyncId, out var live))
            {
                continue;
            }

            if (tombstone.DeletedAt >= live.UpdatedAt)
            {
                _ = events.Remove(tombstone.SyncId); // the deletion is newer — it stands
            }
            else
            {
                _ = tombstones.Remove(tombstone.SyncId); // the edit is newer — resurrect
            }
        }

        var merged = new SyncPayload([.. events.Values], [.. tombstones.Values]);
        return Task.FromResult(JsonSerializer.Serialize(merged, WireOptions));
    }

    /// <inheritdoc />
    public Task<int> ApplyAsync(string mergedJson, CancellationToken cancellationToken = default)
    {
        var merged = Parse(mergedJson);
        service.PushUndoSnapshot(); // one `taqvim undo` reverts the whole apply

        var changed = 0;
        var localBySyncId = store.ListEvents()
            .Where(e => e.SyncId != Guid.Empty)
            .ToDictionary(e => e.SyncId, e => e);

        foreach (var dto in merged.Events)
        {
            if (localBySyncId.TryGetValue(dto.SyncId, out var existing))
            {
                var updated = existing with
                {
                    Calendar = dto.Calendar,
                    Title = dto.Title,
                    Location = dto.Location,
                    Notes = dto.Notes,
                    Tags = dto.Tags,
                    Start = dto.Start,
                    End = dto.End,
                    IsAllDay = dto.IsAllDay,
                    Rule = dto.Rule,
                    Reminders = dto.Reminders,
                    CreatedAt = dto.CreatedAt,
                    UpdatedAt = dto.UpdatedAt,
                };
                if (updated != existing)
                {
                    store.UpdateEvent(updated);
                    changed++;
                }

                _ = localBySyncId.Remove(dto.SyncId);
            }
            else
            {
                _ = store.AddEvent(new TaqvimEvent(
                    0, dto.Calendar, dto.Title, dto.Location, dto.Notes, dto.Tags,
                    dto.Start, dto.End, dto.IsAllDay, dto.Rule, dto.Reminders,
                    dto.CreatedAt, dto.UpdatedAt, dto.SyncId));
                changed++;
            }
        }

        // Local events the merge dropped are tombstoned remotely — delete them here too.
        var deleted = merged.Tombstones.Select(t => t.SyncId).ToHashSet();
        foreach (var orphan in localBySyncId.Values.Where(e => deleted.Contains(e.SyncId)))
        {
            _ = store.RemoveEvent(orphan.Id, merged.Tombstones.First(t => t.SyncId == orphan.SyncId).DeletedAt);
            changed++;
        }

        // Align tombstone timestamps with the payload — when both devices deleted the same
        // event, each recorded its own time; the merged time is canonical for everyone.
        var known = store.GetTombstones().ToDictionary(t => t.SyncId, t => t.DeletedAt);
        foreach (var tombstone in merged.Tombstones)
        {
            if (!known.TryGetValue(tombstone.SyncId, out var current) || current != tombstone.DeletedAt)
            {
                store.UpsertTombstone(new TaqvimTombstone(tombstone.SyncId, tombstone.DeletedAt));
                changed++;
            }
        }

        return Task.FromResult(changed);
    }

    private static SyncPayload Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<SyncPayload>(json, WireReader) ?? new SyncPayload([], []);
        }
        catch (JsonException ex)
        {
            throw new SyncException("The synced Taqvim payload is not valid.", innerException: ex);
        }
    }

    private static EventDto PickEvent(EventDto a, EventDto b)
    {
        if (a.UpdatedAt != b.UpdatedAt)
        {
            return a.UpdatedAt > b.UpdatedAt ? a : b;
        }

        // Exact time tie: deterministic on every device — the lexicographically greater
        // serialization wins, so no ping-pong.
        var left = JsonSerializer.Serialize(a, WireOptions);
        var right = JsonSerializer.Serialize(b, WireOptions);
        return string.CompareOrdinal(left, right) >= 0 ? a : b;
    }

    // ── Wire DTOs ──

    private sealed record SyncPayload(
        IReadOnlyList<EventDto> Events,
        IReadOnlyList<TombstoneDto> Tombstones);

    private sealed record EventDto(
        Guid SyncId,
        string Calendar,
        string Title,
        string Location,
        string Notes,
        string Tags,
        DateTimeOffset Start,
        DateTimeOffset End,
        bool IsAllDay,
        Recurrence? Rule,
        IReadOnlyList<int> Reminders,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    private sealed record TombstoneDto(Guid SyncId, DateTimeOffset DeletedAt);
}
