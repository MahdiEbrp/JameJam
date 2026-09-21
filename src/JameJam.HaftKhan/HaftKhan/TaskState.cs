namespace JameJam.HaftKhan;

/// <summary>Lifecycle state of a Haft Khan task (a "labour").</summary>
public enum TaskState
{
    /// <summary>Not started yet.</summary>
    Todo = 0,

    /// <summary>In progress.</summary>
    Doing = 1,

    /// <summary>Conquered.</summary>
    Done = 2,
}
