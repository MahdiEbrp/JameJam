namespace JameJam.HaftKhan;

/// <summary>Importance of a Haft Khan task. Higher values sort first.</summary>
public enum TaskPriority
{
    /// <summary>Can wait.</summary>
    Low = 0,

    /// <summary>Default importance.</summary>
    Normal = 1,

    /// <summary>Should be tackled soon.</summary>
    High = 2,

    /// <summary>Drop everything else.</summary>
    Critical = 3,
}
