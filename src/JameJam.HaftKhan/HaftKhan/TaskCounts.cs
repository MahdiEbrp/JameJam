namespace JameJam.HaftKhan;

/// <summary>Row counts returned by a repository snapshot.</summary>
/// <param name="Todo">Tasks not started.</param>
/// <param name="Doing">Tasks in progress.</param>
/// <param name="Done">Completed tasks.</param>
/// <param name="Overdue">Open tasks past their due date (relative to the requested day).</param>
public sealed record TaskCounts(int Todo, int Doing, int Done, int Overdue)
{
    /// <summary>Every task regardless of state.</summary>
    public int Total => Todo + Doing + Done;
}
