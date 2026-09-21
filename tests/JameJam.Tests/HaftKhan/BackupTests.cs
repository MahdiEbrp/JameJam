using JameJam.HaftKhan;

namespace JameJam.Tests.HaftKhan;

/// <summary>Tests for backup (de)serialization: JSON roundtrips, validation, and Markdown export.</summary>
public sealed class BackupTests
{
    private static readonly HaftKhanTask Sample = new(
        3, "Slay the dragon", "bring Rakhsh", TaskPriority.Critical, TaskState.Todo,
        new DateOnly(2026, 9, 25), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null)
    {
        Project = "labours",
        Tags = ["myth", "hero"],
        Effort = TaskEffort.Large,
        Recurrence = RecurrenceKind.Weekly,
        RecurrenceInterval = 2,
        StartedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void ToJson_FromJson_RoundTripsEveryField()
    {
        var backup = new Backup.BackupFile(
            Backup.CurrentVersion, "2026-09-19T10:00:00.0000000+00:00",
            [Backup.ToDto(Sample)], [new Backup.DependencyDto(3, 1)]);
        var json = Backup.ToJson(backup);

        var parsed = Backup.FromJson(json);
        var restored = Backup.FromDto(Assert.Single(parsed.Tasks));

        Assert.Equal(Sample.Id, restored.Id);
        Assert.Equal(Sample.Title, restored.Title);
        Assert.Equal(Sample.Notes, restored.Notes);
        Assert.Equal(Sample.Priority, restored.Priority);
        Assert.Equal(Sample.State, restored.State);
        Assert.Equal(Sample.DueDate, restored.DueDate);
        Assert.Equal(Sample.CreatedAt, restored.CreatedAt);
        Assert.Equal(Sample.CompletedAt, restored.CompletedAt);
        Assert.Equal(Sample.Project, restored.Project);
        Assert.Equal(Sample.Tags, restored.Tags);
        Assert.Equal(Sample.Effort, restored.Effort);
        Assert.Equal(Sample.Recurrence, restored.Recurrence);
        Assert.Equal(Sample.RecurrenceInterval, restored.RecurrenceInterval);
        Assert.Equal(Sample.StartedAt, restored.StartedAt);

        var link = Assert.Single(parsed.Dependencies);
        Assert.Equal(new TaskLink(3, 1), new TaskLink(link.TaskId, link.DependsOnId));
    }

    [Fact]
    public void FromJson_WrongVersion_Throws()
    {
        var json = """{"version":99,"exportedAt":"x","tasks":[],"dependencies":[]}""";

        Assert.Throws<ArgumentException>(() => Backup.FromJson(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("""{"version":1,"exportedAt":"x","tasks":"oops","dependencies":[]}""")]
    public void FromJson_Malformed_Throws(string json) =>
        Assert.Throws<ArgumentException>(() => Backup.FromJson(json));

    [Fact]
    public void ToDto_NullTask_Throws() =>
        Assert.Throws<ArgumentNullException>(() => Backup.ToDto(null!));

    [Fact]
    public void ToMarkdown_ShowsCheckboxesAndMetadata()
    {
        var markdown = Backup.ToMarkdown([Sample, Sample with { Id = 4, State = TaskState.Done, CompletedAt = DateTimeOffset.UtcNow }], new DateOnly(2026, 9, 19));

        Assert.Contains("# Haft Khan export", markdown, StringComparison.Ordinal);
        Assert.Contains("- [ ] Slay the dragon", markdown, StringComparison.Ordinal);
        Assert.Contains("- [x] Slay the dragon", markdown, StringComparison.Ordinal);
        Assert.Contains("due: 2026-09-25", markdown, StringComparison.Ordinal);
        Assert.Contains("project: labours", markdown, StringComparison.Ordinal);
        Assert.Contains("#myth #hero", markdown, StringComparison.Ordinal);
        Assert.Contains("effort: large", markdown, StringComparison.Ordinal);
        Assert.Contains("every: 2 weekly", markdown, StringComparison.Ordinal);
    }
}
