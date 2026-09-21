using System.Globalization;

using JameJam.Divan;

namespace JameJam.Tests.Divan;

/// <summary>Option rails and the pure markdown analytics.</summary>
public sealed class DivanOptionsTests
{
    [Fact]
    public void Defaults_AreValid() => Assert.Null(Record.Exception(() => new DivanOptions().Validate()));

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void UndoDepth_HasRails(int depth) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new DivanOptions(UndoDepth: depth).Validate());

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void SearchLimit_HasRails(int limit) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new DivanOptions(SearchLimit: limit).Validate());

    [Theory]
    [InlineData(49)]
    [InlineData(1001)]
    public void ReadingSpeed_HasRails(int wpm) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new DivanOptions(ReadingWordsPerMinute: wpm).Validate());

    [Theory]
    [InlineData(99)]
    [InlineData(20_001)]
    public void AiBodyClip_HasRails(int clip) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new DivanOptions(MaxAiBodyChars: clip).Validate());
}

public sealed class DivanTextTests
{
    [Fact]
    public void WordCount_CountsWhitespaceRuns()
    {
        Assert.Equal(0, DivanText.WordCount(""));
        Assert.Equal(1, DivanText.WordCount("one"));
        Assert.Equal(3, DivanText.WordCount("  two  words\nhere\t "));
    }

    [Fact]
    public void Checklist_ParsesTheThreeListMarkers_AndBothStates()
    {
        var body = "# Plan\n- [ ] alpha\n  * [X] beta\n+ [x] gamma\nplain line\n- [not a box]\n";
        var items = DivanText.Checklist(body);
        Assert.Equal(3, items.Count);
        Assert.Equal(("alpha", false), (items[0].Text, items[0].Done));
        Assert.Equal(("beta", true), (items[1].Text, items[1].Done));
        Assert.Equal(("gamma", true), (items[2].Text, items[2].Done));
        Assert.Equal(2, items[0].LineNumber);
    }

    [Fact]
    public void Checklist_IgnoresNonBoxes()
    {
        Assert.Empty(DivanText.Checklist("- plain bullet\n- [missing space\n-- [] weird\n"));
    }

    [Fact]
    public void ExtractLinks_FindsAndDeduplicates()
    {
        var links = DivanText.ExtractLinks("See [[Alpha]] then [[beta]] and [[Alpha]] again. [[Unclosed stays");
        Assert.Equal(["Alpha", "beta"], links);
    }

    [Fact]
    public void ExtractLinks_SkipsEmptyAndMultiline()
    {
        Assert.Empty(DivanText.ExtractLinks("[[]] [[multi\nline]] [[pip|ed]]"));
    }

    [Fact]
    public void Snippet_FlattensAndClips()
    {
        var snippet = DivanText.Snippet("# Title\n\nfirst line\nsecond line", 22);
        Assert.Equal("# Title first line sec…", snippet);
        Assert.DoesNotContain("…", DivanText.Snippet("short", 22), StringComparison.Ordinal);
    }
}

/// <summary>Business logic over the in-memory store.</summary>
public sealed class DivanServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly MemoryDivanStore _store = new();
    private readonly DivanService _service;

    public DivanServiceTests() =>
        _service = new DivanService(_store, new FixedTimeProvider(Now));

    private Note Add(string title, string body, string? notebook = null, string tags = "")
    {
        Thread.Sleep(0); // keep timestamps identical — updates are explicit
        return _service.AddNote(title, body, notebook, tags);
    }

    [Fact]
    public void CreateNotebook_IsUnique_AndResolvable()
    {
        var notebook = _service.CreateNotebook("Research");
        Assert.Equal(1, notebook.Id);
        var exception = Assert.Throws<DivanException>(() => _service.CreateNotebook("RESEARCH"));
        Assert.Contains("already exists", exception.Message, StringComparison.Ordinal);
        Assert.Equal(notebook.Id, _service.ResolveNotebook("research").Id);
        Assert.Equal(notebook.Id, _service.ResolveNotebook(notebook.Id.ToString(CultureInfo.InvariantCulture)).Id);
    }

    [Fact]
    public void ResolveNotebook_CreatesTheDefault_OnAnEmptyPad()
    {
        var resolved = _service.ResolveNotebook(null);
        Assert.Equal(DivanDefaults.DefaultNotebook, resolved.Name);
    }

    [Fact]
    public void AddNote_LandsInTheRightNotebook_WithCleanTags()
    {
        _service.CreateNotebook("Research");
        var note = _service.AddNote("  Trimmed title  ", "body", "research", "work, WORK, , x");
        Assert.Equal("Trimmed title", note.Title);
        Assert.Equal(1, note.NotebookId);
        Assert.Equal("work,x", note.Tags);
    }

    [Fact]
    public void AppendAndEdit_UpdateTheBody_Partially()
    {
        var note = Add("Note", "first");
        var appended = _service.Append(note.Id, "second");
        Assert.Equal("first\nsecond", appended.Body);

        var edited = _service.EditNote(note.Id, title: "Renamed", tags: "tag1");
        Assert.Equal("Renamed", edited.Title);
        Assert.Equal("first\nsecond", edited.Body);
        Assert.Equal("tag1", edited.Tags);
        Assert.Equal(Now, edited.UpdatedAt);

        Assert.Throws<DivanException>(() => _service.EditNote(99, title: "Ghost"));
    }

    [Fact]
    public void PinArchiveMove_Delete_Work()
    {
        _service.CreateNotebook("Other");
        var note = Add("Note", "body", "Other");
        Assert.True(_service.Pin(note.Id, true).Pinned);
        Assert.True(_service.Archive(note.Id, true).Archived);
        var moved = _service.Move(note.Id, "Other");
        Assert.Equal(1, moved.NotebookId);

        var deleted = _service.Delete(note.Id);
        Assert.Equal("Note", deleted.Title);
        Assert.Null(_service.GetNote(note.Id));
    }

    [Fact]
    public void List_Filters_AndSortsPinnedFirst()
    {
        _service.CreateNotebook("B2");
        var a = Add("Alpha", "body", null, "work");
        var b = Add("Beta", "body", "B2", "home");
        _ = _service.Pin(b.Id, true);
        var c = Add("Gamma", "- [ ] task", null);
        _ = _service.Archive(c.Id, true);

        Assert.Equal([b.Id, a.Id], _service.List().Select(n => n.Id));
        Assert.Equal([b.Id, a.Id], _service.List(new DivanFilter(NotebookId: b.NotebookId)).Select(n => n.Id)); // null resolves to first notebook
        Assert.Equal([a.Id], _service.List(new DivanFilter(Tag: "work")).Select(n => n.Id));
        Assert.Equal([b.Id], _service.List(new DivanFilter(PinnedOnly: true)).Select(n => n.Id));
        Assert.Equal([c.Id], _service.List(new DivanFilter(ArchivedOnly: true)).Select(n => n.Id));
        var d = Add("Delta", "- [ ] another"); // checklists view only shows active notes
        Assert.Equal([d.Id], _service.List(new DivanFilter(ChecklistsOnly: true)).Select(n => n.Id));
    }

    [Fact]
    public void Search_FindsEveryTerm_AndNothingElse()
    {
        Add("Alpha", "the quick brown fox");
        Add("Beta", "quick timeline");
        Add("Gamma", "slow turtle");

        Assert.Equal([2L], _service.List(new DivanFilter(Query: "quick timeline")).Select(n => n.Id));
        Assert.Equal([1L, 2L], _service.List(new DivanFilter(Query: "quick")).Select(n => n.Id));
        Assert.Empty(_service.List(new DivanFilter(Query: "zebra")));
    }

    [Fact]
    public void Backlinks_AreBidirectional()
    {
        var a = Add("Alpha", "see [[Beta]]");
        var b = Add("Beta", "back to [[alpha]]");
        _ = Add("Gamma", "no links");

        Assert.Equal([b.Id], _service.Backlinks(a.Id).Select(n => n.Id));
        Assert.Equal([a.Id], _service.Backlinks(b.Id).Select(n => n.Id));
        Assert.Empty(_service.Backlinks(3));
    }

    [Fact]
    public void OpenTodos_AggregatesAcrossNotes()
    {
        var a = Add("A", "- [ ] one\n- [x] two");
        var b = Add("B", "* [ ] three");
        var todos = _service.OpenTodos();
        Assert.Equal(2, todos.Count);
        Assert.Equal((a.Id, "one"), (todos[0].Note.Id, todos[0].Item.Text));
        Assert.Equal((b.Id, "three"), (todos[1].Note.Id, todos[1].Item.Text));
    }

    [Fact]
    public void Daily_IsIdempotent_AndLandsInJournal()
    {
        var first = _service.Daily();
        Assert.Equal(DivanDefaults.JournalNotebook, _service.ListNotebooks().Single(n => n.Id == first.NotebookId).Name);
        Assert.Equal("2026-09-20", first.Title);
        Assert.StartsWith("# Sunday, 2026-09-20", first.Body, StringComparison.Ordinal);

        var again = _service.Daily();
        Assert.Equal(first.Id, again.Id);
    }

    [Fact]
    public void Metrics_CountEverything()
    {
        var note = Add("T", "# Heading\n\nsome words here\n- [x] done\n- [ ] open\nSee [[Other]]");
        var metrics = _service.Metrics(note);
        var words = DivanText.WordCount(note.Body);
        Assert.Equal(words, metrics.Words);
        Assert.Equal(2, metrics.ChecklistTotal);
        Assert.Equal(1, metrics.ChecklistDone);
        Assert.Equal(["Other"], metrics.Links);
        Assert.Equal((int)Math.Ceiling(words * 60.0 / DivanDefaults.ReadingWordsPerMinute), metrics.ReadingSeconds);
    }

    [Fact]
    public void Stats_SumThePad()
    {
        _service.CreateNotebook("B2");
        Add("A", "words here", tags: "t1");
        var b = Add("B", "- [ ] task", "B2");
        _ = _service.Archive(b.Id, true);

        var stats = _service.Stats();
        Assert.Equal((1, 1, 1, 1), (stats.Notebooks, stats.Notes, stats.ArchivedNotes, stats.TaggedNotes));
        Assert.Equal(2, stats.Words);
    }

    [Fact]
    public void Undo_RestoresNotebooksAndNotes_Together()
    {
        _service.CreateNotebook("Keeper");
        var note = Add("Note", "body", "Keeper");
        Assert.Equal(2, _store.UndoCount); // notebook + note snapshots

        _service.Delete(note.Id);
        Assert.Null(_service.GetNote(note.Id));

        Assert.True(_service.Undo());
        Assert.Equal("Note", _service.GetNote(note.Id)!.Title);

        Assert.True(_service.Undo()); // pre-add snapshot: Keeper remains, the note is gone
        Assert.Single(_service.ListNotebooks());
        Assert.Null(_service.GetNote(note.Id));

        Assert.True(_service.Undo()); // pre-notebook snapshot: empty pad
        Assert.Empty(_service.ListNotebooks());
        Assert.False(_service.Undo());
    }

    [Fact]
    public void UndoDepth_TrimsOldest()
    {
        var tight = new DivanService(
            _store,
            new FixedTimeProvider(Now),
            new DivanOptions(UndoDepth: 1));
        tight.CreateNotebook("N");
        Assert.Equal(1, _store.UndoCount);
        _ = tight.AddNote("A", "body");
        Assert.Equal(1, _store.UndoCount);
        _ = tight.AddNote("B", "body");

        Assert.True(tight.Undo());
        Assert.Null(tight.GetNote(2));
        Assert.NotNull(tight.GetNote(1));
    }

    [Fact]
    public void RemoveNotebook_GuardsNotes_ThenForceClears()
    {
        _service.CreateNotebook("Doomed");
        Add("Note", "body", "Doomed");

        var exception = Assert.Throws<DivanException>(() => _service.RemoveNotebook("Doomed", force: false));
        Assert.Contains("--force", exception.Message, StringComparison.Ordinal);

        Assert.Equal(1, _service.RemoveNotebook("Doomed", force: true));
        Assert.Empty(_service.ListNotebooks());
        Assert.Empty(_service.List());
    }

    [Fact]
    public void ExportImport_RoundTripsThroughMarkdown()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"divan-{Guid.NewGuid():N}");
        try
        {
            _service.CreateNotebook("Research");
            Add("Alpha", "body one", "Research", "work");
            Add("Beta", "- [ ] x", "Research");
            _ = _service.Pin(1, true);

            Assert.Equal(2, _service.Export(folder));
            Assert.Equal(2, Directory.GetFiles(folder, "*.md").Length);

            var fresh = new DivanService(new MemoryDivanStore(), new FixedTimeProvider(Now));
            Assert.Equal(2, fresh.Import(folder));
            var imported = fresh.List();
            Assert.Equal(["Alpha", "Beta"], imported.Select(n => n.Title));
            Assert.True(imported.Single(n => n.Title == "Alpha").Pinned);
            Assert.Equal("Research", fresh.ResolveNotebook(null).Name);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public void Import_FailsFriendlyOnMissingOrEmptyFolders()
    {
        Assert.Throws<DivanException>(() => _service.Import(Path.Combine(Path.GetTempPath(), $"ghost-{Guid.NewGuid():N}")));
        var empty = Path.Combine(Path.GetTempPath(), $"divan-empty-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(empty);
        try
        {
            Assert.Throws<DivanException>(() => _service.Import(empty));
        }
        finally
        {
            Directory.Delete(empty);
        }
    }
}
