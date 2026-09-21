using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JameJam.HaftKhan;

/// <summary>JSON/Markdown (de)serialization for backups, imports, and undo snapshots.</summary>
public static class Backup
{
    /// <summary>One task, serialized. All values are culture-invariant strings.</summary>
    /// <param name="Id">Task id.</param>
    /// <param name="Title">Title.</param>
    /// <param name="Notes">Notes.</param>
    /// <param name="Priority">Priority as integer.</param>
    /// <param name="State">State as integer.</param>
    /// <param name="DueDate">ISO date or null.</param>
    /// <param name="CreatedAt">ISO timestamp.</param>
    /// <param name="UpdatedAt">ISO timestamp.</param>
    /// <param name="CompletedAt">ISO timestamp or null.</param>
    /// <param name="Project">Project name (empty = none).</param>
    /// <param name="Tags">Tags.</param>
    /// <param name="Effort">Effort as integer.</param>
    /// <param name="Recurrence">Recurrence kind as integer.</param>
    /// <param name="RecurrenceInterval">Recurrence interval.</param>
    /// <param name="StartedAt">ISO timestamp or null.</param>
    /// <param name="Uid">Stable sync identity (v2 files; null in v1 files).</param>
    public sealed record TaskDto(
        long Id,
        string Title,
        string Notes,
        int Priority,
        int State,
        string? DueDate,
        string CreatedAt,
        string UpdatedAt,
        string? CompletedAt,
        string Project,
        IReadOnlyList<string> Tags,
        int Effort,
        int Recurrence,
        int RecurrenceInterval,
        string? StartedAt,
        string? Uid = null);

    /// <summary>One dependency edge, serialized.</summary>
    /// <param name="TaskId">The blocked task id.</param>
    /// <param name="DependsOnId">The blocker id.</param>
    /// <param name="TaskUid">The blocked task's stable uid (v2 files).</param>
    /// <param name="DependsOnUid">The blocker's stable uid (v2 files).</param>
    public sealed record DependencyDto(
        long TaskId,
        long DependsOnId,
        string? TaskUid = null,
        string? DependsOnUid = null);

    /// <summary>A full backup file model.</summary>
    /// <param name="Version">Schema version.</param>
    /// <param name="ExportedAt">When the backup was taken (ISO).</param>
    /// <param name="Tasks">All tasks.</param>
    /// <param name="Dependencies">All dependency edges.</param>
    public sealed record BackupFile(
        int Version,
        string ExportedAt,
        IReadOnlyList<TaskDto> Tasks,
        IReadOnlyList<DependencyDto> Dependencies);

    /// <summary>Shared JSON settings (indented, case-sensitive, strict).</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Current backup schema version.</summary>
    public const int CurrentVersion = 2;

    /// <summary>Oldest backup schema version this build accepts.</summary>
    public const int OldestSupportedVersion = 1;

    /// <summary>Converts a task to its serializable form.</summary>
    public static TaskDto ToDto(HaftKhanTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        return new TaskDto(
            task.Id,
            task.Title,
            task.Notes,
            (int)task.Priority,
            (int)task.State,
            task.DueDate?.ToString("O", CultureInfo.InvariantCulture)[..10],
            task.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            task.UpdatedAt.ToString("O", CultureInfo.InvariantCulture),
            task.CompletedAt?.ToString("O", CultureInfo.InvariantCulture),
            task.Project,
            [.. task.Tags],
            (int)task.Effort,
            (int)task.Recurrence,
            task.RecurrenceInterval,
            task.StartedAt?.ToString("O", CultureInfo.InvariantCulture),
            string.IsNullOrEmpty(task.Uid) ? null : task.Uid);
    }

    /// <summary>Converts a serialized task back to the domain (throws on malformed values).</summary>
    public static HaftKhanTask FromDto(TaskDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var createdAt = DateTimeOffset.Parse(dto.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var updatedAt = DateTimeOffset.Parse(dto.UpdatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var completedAt = dto.CompletedAt is null
            ? (DateTimeOffset?)null
            : DateTimeOffset.Parse(dto.CompletedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var startedAt = dto.StartedAt is null
            ? (DateTimeOffset?)null
            : DateTimeOffset.Parse(dto.StartedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        return new HaftKhanTask(
            dto.Id,
            dto.Title,
            dto.Notes,
            (TaskPriority)dto.Priority,
            (TaskState)dto.State,
            string.IsNullOrEmpty(dto.DueDate)
                ? null
                : DateOnly.ParseExact(dto.DueDate, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            createdAt,
            updatedAt,
            completedAt)
        {
            Project = dto.Project,
            Tags = [.. dto.Tags],
            Effort = (TaskEffort)dto.Effort,
            Recurrence = (RecurrenceKind)dto.Recurrence,
            RecurrenceInterval = dto.RecurrenceInterval,
            StartedAt = startedAt,
            Uid = string.IsNullOrEmpty(dto.Uid) ? NewUid() : dto.Uid,
        };
    }

    /// <summary>Serializes a backup to JSON text.</summary>
    public static string ToJson(BackupFile backup)
    {
        ArgumentNullException.ThrowIfNull(backup);

        return JsonSerializer.Serialize(backup, JsonOptions);
    }

    /// <summary>Parses a backup from JSON text (schema versions 1 and 2 are accepted).</summary>
    /// <exception cref="ArgumentException">Malformed JSON or an unsupported schema version.</exception>
    public static BackupFile FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        BackupFile? backup;
        try
        {
            backup = JsonSerializer.Deserialize<BackupFile>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Not a valid Haft Khan backup file.", ex);
        }

        return backup is null
            ? throw new ArgumentException("Not a valid Haft Khan backup file.")
            : backup.Version is < OldestSupportedVersion or > CurrentVersion
                ? throw new ArgumentException(
                    $"Unsupported backup version {backup.Version} (supported: {OldestSupportedVersion}–{CurrentVersion}).")
                : backup;
    }

    /// <summary>Generates a new stable task identity.</summary>
    public static string NewUid() => Guid.NewGuid().ToString("D");

    /// <summary>Serializes tasks to a human-readable Markdown checklist.</summary>
    public static string ToMarkdown(IReadOnlyList<HaftKhanTask> tasks, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        var lines = new List<string> { "# Haft Khan export", string.Empty };
        foreach (var task in tasks)
        {
            var checkbox = task.State == TaskState.Done ? "x" : " ";
            var bits = new List<string>();
            if (task.DueDate.HasValue)
            {
                bits.Add(task.DueDate.Value < today
                    ? $"overdue: {task.DueDate.Value:yyyy-MM-dd}"
                    : $"due: {task.DueDate.Value:yyyy-MM-dd}");
            }

            if (task.Project.Length > 0)
                bits.Add($"project: {task.Project}");

            if (task.Tags.Count > 0)
                bits.Add(string.Join(' ', task.Tags.Select(tag => $"#{tag}")));

            if (task.Effort != TaskEffort.None)
                bits.Add($"effort: {task.Effort.ToString().ToLowerInvariant()}");

            if (task.Recurrence != RecurrenceKind.None)
                bits.Add($"every: {task.RecurrenceInterval} {task.Recurrence.ToString().ToLowerInvariant()}");

            var suffix = bits.Count > 0 ? $"  ({string.Join(", ", bits)})" : string.Empty;
            var notes = task.Notes.Length > 0 ? $" — {task.Notes.Replace('\n', ' ')}" : string.Empty;
            lines.Add($"- [{checkbox}] {task.Title}{suffix}{notes}");
        }

        return string.Join('\n', lines) + "\n";
    }
}
