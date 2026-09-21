namespace JameJam.HaftKhan;

/// <summary>Which slice of tasks a listing should return.</summary>
public enum TaskView
{
    /// <summary>Open tasks (to-do and doing) — the default view.</summary>
    Open = 0,

    /// <summary>Every task, done or not.</summary>
    All = 1,

    /// <summary>Only conquered tasks.</summary>
    Done = 2,

    /// <summary>Open tasks due today.</summary>
    Today = 3,

    /// <summary>Open tasks past their due date.</summary>
    Overdue = 4,
}
