using JameJam.Divan;

namespace JameJam.Tests.Divan;

/// <summary>The SQLite store: persistence, FTS search, undo trimming, permissions.</summary>
public sealed class SqliteDivanStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"divan-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        File.Delete(_path);
        File.Delete(_path + "-wal");
        File.Delete(_path + "-shm");
    }

    private static Notebook Notebook(long id, string name) => new(
        id, name, new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero), false);

    private static Note Note(long id, long notebookId, string title, string body) => new(
        id, notebookId, title, body, "work", true, false,
        new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 9, 20, 12, 30, 0, TimeSpan.Zero));

    [Fact]
    public void Persistence_RoundTrips_NotebooksAndNotes()
    {
        {
            var first = new SqliteDivanStore(_path);
            _ = first.AddNotebook(Notebook(0, "Research"));
            _ = first.AddNote(Note(0, 1, "Alpha", "some markdown body"));
        }

        {
            var second = new SqliteDivanStore(_path);
            var notebook = Assert.Single(second.ListNotebooks());
            Assert.Equal(("Research", false), (notebook.Name, notebook.IsArchived));

            var note = Assert.Single(second.ListNotes());
            Assert.Equal((1L, "Alpha", "some markdown body", true), (note.NotebookId, note.Title, note.Body, note.Pinned));
            Assert.NotNull(second.FindNote(1));
            Assert.NotNull(second.FindNotebookByName("research"));
            Assert.Null(second.FindNotebookByName("ghost"));
            Assert.Null(second.FindNote(99));
        }
    }

    [Fact]
    public void Updates_Removes_AndReplace_Work()
    {
        var store = new SqliteDivanStore(_path);
        _ = store.AddNotebook(Notebook(0, "A"));
        _ = store.AddNotebook(Notebook(0, "B"));
        store.UpdateNotebook(Notebook(1, "A2") with { IsArchived = true });
        Assert.True(store.FindNotebook(1)!.IsArchived);
        Assert.True(store.RemoveNotebook(2));
        Assert.False(store.RemoveNotebook(2));

        _ = store.AddNote(Note(0, 1, "Alpha", "the quick brown fox"));
        _ = store.AddNote(Note(0, 1, "Beta", "slow turtle"));
        store.UpdateNote(store.FindNote(1)! with { Title = "Alpha2" });
        Assert.Equal("Alpha2", store.FindNote(1)!.Title);

        store.ReplaceNotes([Note(9, 1, "Reset", "fresh body")]);
        Assert.Equal([9L], store.ListNotes().Select(n => n.Id));

        store.ReplaceNotebooks([Notebook(7, "Solo")]);
        Assert.Equal([7L], store.ListNotebooks().Select(n => n.Id));
    }

    [Fact]
    public void Search_FindsTheTerms()
    {
        var store = new SqliteDivanStore(_path);
        _ = store.AddNotebook(Notebook(0, "N"));
        _ = store.AddNote(Note(0, 1, "Alpha", "the quick brown fox"));
        _ = store.AddNote(Note(0, 1, "Beta", "quick timeline"));

        Assert.Equal([2L], store.SearchIds("quick timeline", 10)); // AND semantics
        Assert.Equal([1L, 2L], store.SearchIds("quick", 10).Order());
        Assert.Empty(store.SearchIds("zebra", 10));
    }

    [Fact]
    public void Search_SurvivesUpdateAndDelete()
    {
        var store = new SqliteDivanStore(_path);
        _ = store.AddNotebook(Notebook(0, "N"));
        _ = store.AddNote(Note(0, 1, "Alpha", "searchable words"));
        store.UpdateNote(store.FindNote(1)! with { Body = "different words" });
        Assert.Empty(store.SearchIds("searchable", 10));
        Assert.Equal([1L], store.SearchIds("different", 10));

        _ = store.RemoveNote(1, new DateTimeOffset(2026, 9, 20, 13, 0, 0, TimeSpan.Zero));
        Assert.Empty(store.SearchIds("different", 10));
    }

    [Fact]
    public void UndoStack_IsLifo_AndTrimsToDepth()
    {
        var store = new SqliteDivanStore(_path) { UndoDepth = 2 };
        Assert.Throws<ArgumentOutOfRangeException>(() => store.UndoDepth = -1);
        store.PushUndo("a");
        store.PushUndo("b");
        store.PushUndo("c");
        Assert.Equal(2, store.UndoCount);
        Assert.Equal("c", store.PopUndo());
        Assert.Equal("b", store.PopUndo());
        Assert.Null(store.PopUndo());
    }

    [Fact]
    public void DatabaseFile_IsOwnerOnly()
    {
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
        {
            return;
        }

        _ = new SqliteDivanStore(_path);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_path));
    }
}
