namespace JameJam.Sync;

/// <summary>Sync mode.</summary>
public enum SyncMode
{
    /// <summary>Pull remote, merge into local, push the merged result (the default — both sides converge).</summary>
    Merge = 0,

    /// <summary>Pull remote and merge into local only (never touches the remote).</summary>
    Pull = 1,

    /// <summary>Replace the remote with the local state (requires --force when the remote is non-empty).</summary>
    Push = 2,
}
