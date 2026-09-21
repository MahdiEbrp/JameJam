namespace JameJam.Sync;

/// <summary>
/// The plug-in point that lets any JameJam service sync through the shared engine. An adapter
/// owns three pure-ish operations — capture the local state, merge two states, apply a merged
/// state — while the engine owns transport, safety, and mode semantics. This is the same
/// division Soroush uses for AI: one funnel, many services.
/// </summary>
public interface ISyncAdapter
{
    /// <summary>The service tag this adapter exchanges (must match the envelope's Service field).</summary>
    string Service { get; }

    /// <summary>Serializes the current local state into payload JSON.</summary>
    Task<string> CaptureAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Merges two payload JSON documents into one. Must be deterministic and commutative:
    /// merging (A, B) yields the same result as (B, A) on any device, so both sides converge
    /// without ever talking to each other — only to the shared URL.
    /// </summary>
    Task<string> MergeAsync(
        string localJson,
        string remoteJson,
        string localDeviceId,
        string remoteDeviceId,
        CancellationToken cancellationToken = default);

    /// <summary>Applies a merged payload locally. Returns how many records changed.</summary>
    Task<int> ApplyAsync(string mergedJson, CancellationToken cancellationToken = default);
}

/// <summary>Outcome of one sync run through the engine.</summary>
/// <param name="Mode">The mode that ran.</param>
/// <param name="Applied">Records changed locally by the merge (0 when local was already current).</param>
/// <param name="Pushed">True when the remote document was (re)written.</param>
/// <param name="FirstSync">True when the remote was empty and this run seeded it.</param>
public sealed record SyncRun(SyncMode Mode, int Applied, bool Pushed, bool FirstSync)
{
    /// <summary>Human-friendly summary (culture-invariant).</summary>
    public string Describe() => (Mode, FirstSync) switch
    {
        (SyncMode.Push, _) => "Pushed the local state — remote replaced.",
        (SyncMode.Pull, _) => FormattableString.Invariant($"Pulled: {Applied} record(s) changed locally."),
        (_, true) => "First sync — the remote was empty and now holds this device's state.",
        _ => FormattableString.Invariant($"Merged: {Applied} record(s) changed locally."),
    };
}

/// <summary>
/// The generic sync loop over any <see cref="ISyncClient"/> transport and any
/// <see cref="ISyncAdapter"/> service state. Stateless by design: there is no cursor, no
/// sequence number, nothing to corrupt — each run is pull-merge-(push-apply), and the
/// deterministic merge makes repeated runs converge to the same state on every device.
/// </summary>
public static class SyncEngine
{
    /// <summary>Runs one sync exchange.</summary>
    /// <param name="client">The transport (HTTP or a stub).</param>
    /// <param name="adapter">The service state bridge.</param>
    /// <param name="deviceId">This device's stable identity (a GUID string).</param>
    /// <param name="deviceName">Friendly name for reports.</param>
    /// <param name="mode">Merge (default), Pull (never writes the remote), or Push (overwrite, needs force).</param>
    /// <param name="force">Confirms a destructive push over a differing remote.</param>
    /// <param name="now">Clock for envelope timestamps (injected for testability).</param>
    public static async Task<SyncRun> RunAsync(
        ISyncClient client,
        ISyncAdapter adapter,
        string deviceId,
        string deviceName,
        SyncMode mode = SyncMode.Merge,
        bool force = false,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        var stamp = now ?? DateTimeOffset.UtcNow;
        var local = await adapter.CaptureAsync(cancellationToken).ConfigureAwait(false);

        var remoteJson = await client.GetAsync(cancellationToken).ConfigureAwait(false);
        if (remoteJson is null)
        {
            if (mode == SyncMode.Pull)
            {
                return new SyncRun(mode, 0, Pushed: false, FirstSync: false);
            }

            await PutAsync(client, adapter, local, deviceId, deviceName, stamp, cancellationToken).ConfigureAwait(false);
            return new SyncRun(mode, 0, Pushed: true, FirstSync: true);
        }

        var remote = SyncSafety.Open(remoteJson);
        if (!string.Equals(remote.Service, adapter.Service, StringComparison.OrdinalIgnoreCase))
        {
            throw new SyncException(
                $"That URL holds '{remote.Service}' data from device {remote.DeviceName ?? remote.DeviceId}; '{adapter.Service}' cannot merge with it. Choose another URL.");
        }

        if (mode == SyncMode.Push)
        {
            if (!string.Equals(remote.Payload, local, StringComparison.Ordinal) && !force)
            {
                throw new SyncException(
                    "The remote holds changes (last sealed by device "
                    + $"{remote.DeviceName ?? remote.DeviceId}). Pushing replaces them — rerun with --force to confirm.");
            }

            await PutAsync(client, adapter, local, deviceId, deviceName, stamp, cancellationToken).ConfigureAwait(false);
            return new SyncRun(mode, 0, Pushed: true, FirstSync: false);
        }

        // Nothing to do when the remote is byte-identical to local — the common case after
        // both devices have converged; avoids a merge pass and a write entirely.
        if (string.Equals(remote.Payload, local, StringComparison.Ordinal))
        {
            return new SyncRun(mode, 0, Pushed: false, FirstSync: false);
        }

        var merged = await adapter
            .MergeAsync(local, remote.Payload, deviceId, remote.DeviceId, cancellationToken)
            .ConfigureAwait(false);

        if (mode == SyncMode.Pull)
        {
            var applied = string.Equals(merged, local, StringComparison.Ordinal)
                ? 0
                : await adapter.ApplyAsync(merged, cancellationToken).ConfigureAwait(false);
            return new SyncRun(mode, applied, Pushed: false, FirstSync: false);
        }

        var changed = string.Equals(merged, local, StringComparison.Ordinal)
            ? 0
            : await adapter.ApplyAsync(merged, cancellationToken).ConfigureAwait(false);

        var pushed = !string.Equals(merged, remote.Payload, StringComparison.Ordinal);
        if (pushed)
        {
            await PutAsync(client, adapter, merged, deviceId, deviceName, stamp, cancellationToken).ConfigureAwait(false);
        }

        return new SyncRun(mode, changed, Pushed: pushed, FirstSync: false);
    }

    private static async Task PutAsync(
        ISyncClient client,
        ISyncAdapter adapter,
        string payload,
        string deviceId,
        string deviceName,
        DateTimeOffset stamp,
        CancellationToken cancellationToken)
    {
        var envelope = SyncSafety.Seal(adapter.Service, payload, deviceId, deviceName, stamp).ToJson();
        await client.PutAsync(envelope, cancellationToken).ConfigureAwait(false);
    }
}
