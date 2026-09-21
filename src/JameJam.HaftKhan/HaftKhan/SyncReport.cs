namespace JameJam.HaftKhan;

/// <summary>Outcome of a Haft Khan sync run.</summary>
/// <param name="Mode">The mode that ran.</param>
/// <param name="Pulled">Tasks applied from the remote (added or updated by last-write-wins).</param>
/// <param name="Pushed">Tasks uploaded to the remote (0 outside merge/push modes).</param>
/// <param name="Total">Total local tasks after the sync.</param>
public sealed record SyncReport(JameJam.Sync.SyncMode Mode, int Pulled, int Pushed, int Total)
{
    /// <summary>Human-friendly summary line (culture-invariant).</summary>
    public string Describe() => Mode switch
    {
        JameJam.Sync.SyncMode.Pull => FormattableString.Invariant($"Pulled {Pulled} task(s) — {Total} local task(s) now."),
        JameJam.Sync.SyncMode.Push => FormattableString.Invariant($"Pushed {Pushed} task(s) — remote replaced."),
        _ => FormattableString.Invariant($"Merged: pulled {Pulled}, pushed {Pushed} — {Total} local task(s) now."),
    };
}
