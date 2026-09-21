using JameJam.HaftKhan;

namespace JameJam.Tests.HaftKhan;

/// <summary>Tests for the in-memory task repository (same contract as the SQLite store).</summary>
public sealed class MemoryTaskRepositoryTests
{
    private readonly MemoryTaskRepository _repository = new();

    [Fact]
    public void Add_AssignsSequentialIds()
    {
        var first = _repository.Add(new NewTask("a", string.Empty, TaskPriority.Normal, null));
        var second = _repository.Add(new NewTask("b", string.Empty, TaskPriority.Normal, null));

        Assert.Equal(1, first.Id);
        Assert.Equal(2, second.Id);
    }

    [Fact]
    public void Update_UnknownTask_ThrowsNotFound() =>
        Assert.Throws<TaskNotFoundException>(() => _repository.Update(new HaftKhanTask(
            9, "t", string.Empty, TaskPriority.Normal, TaskState.Todo, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null)));

    [Fact]
    public void ListAll_GroupsByStateThenPriority()
    {
        var open = _repository.Add(new NewTask("open", string.Empty, TaskPriority.Low, null));
        var done = _repository.Add(new NewTask("done", string.Empty, TaskPriority.Critical, null));
        _repository.Update(done with { State = TaskState.Done, CompletedAt = DateTimeOffset.UtcNow });

        var all = _repository.ListAll();

        Assert.Equal([open.Id, done.Id], all.Select(task => task.Id)); // open (todo) sorts before done
    }
}
