using System.Text;
using JameJam.Divan;

namespace JameJam.Tests.Divan;

/// <summary>Edge-path tests closing the coverage gaps: alternate verbs, guards, stdin modes, import quirks.</summary>
public sealed class DivanEdgeTests
{
    private static readonly FixedTimeProvider Clock =
        new(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));

    private static DivanCommands Build(
        out StringWriter output,
        out StringWriter error,
        string? stdin = null,
        MemoryDivanStore? store = null,
        Func<JameJam.Soroush.AiRequest, CancellationToken, Task<JameJam.Soroush.SoroushResult>>? ai = null) =>
        new(
            store ?? new MemoryDivanStore(),
            Clock,
            output = new StringWriter(),
            error = new StringWriter(),
            stdin: stdin is null ? null : new StringReader(stdin),
            aiCompletion: ai);

    [Fact]
    public async Task AlternateVerbs_EndToEnd()
    {
        var store = new MemoryDivanStore();
        var commands = Build(out var output, out var error, stdin: "first line\n", store: store);

        Assert.Equal(0, await commands.RunAsync(["notebook", "add", "Vault"]));
        Assert.Equal(0, await commands.RunAsync(["new", "Pinned Note", "--stdin"])); // consumes the reader
        Assert.Equal(0, await commands.RunAsync(["pin", "1"]));

        // append with an explicit --stdin flag (fresh reader on the same store)
        var flagged = Build(out var flaggedOut, out _, stdin: "second line\n", store: store);
        Assert.Equal(0, await flagged.RunAsync(["append", "1", "--stdin"]));
        // append without the flag: injected stdin counts as redirected and is still read
        var bare = Build(out _, out _, stdin: "third line\n", store: store);
        Assert.Equal(0, await bare.RunAsync(["append", "1"]));
        Assert.Contains("first line\nsecond line\nthird line", store.FindNote(1)!.Body, StringComparison.Ordinal);

        Assert.Equal(0, await commands.RunAsync(["unpin", "1"]));
        Assert.Contains("#1 unpinned.", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await commands.RunAsync(["unpin"]));
        Assert.Contains("Usage: JameJam divan unpin", error.ToString(), StringComparison.Ordinal);

        Assert.Equal(0, await commands.RunAsync(["notebook", "archive", "1"]));
        Assert.Equal(0, await commands.RunAsync(["notebook", "unarchive", "1"]));
        Assert.Contains("Unarchived Vault.", output.ToString(), StringComparison.Ordinal);

        Assert.Equal(0, await commands.RunAsync(["archive", "1"]));
        Assert.Equal(0, await commands.RunAsync(["unarchive", "1"]));
        Assert.Contains("#1 unarchived.", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await commands.RunAsync(["unarchive"]));
        Assert.Contains("Usage: JameJam divan unarchive", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Show_AndList_RenderEveryFacet()
    {
        var store = new MemoryDivanStore();
        var commands = Build(out var output, out _, stdin: "- [ ] open task\nSee [[Other]]\n", store: store);
        await commands.RunAsync(["new", "Facets", "--stdin"]); // consumes the reader
        Assert.Equal(0, await commands.RunAsync(["pin", "1"]));
        output.GetStringBuilder().Clear();
        Assert.Equal(0, await commands.RunAsync(["show", "1"]));
        var text = output.ToString();
        Assert.Contains("★ pinned", text, StringComparison.Ordinal);
        Assert.Contains("Checklist: 0/1 done", text, StringComparison.Ordinal);
        Assert.Contains("~1 min read", text, StringComparison.Ordinal);

        Assert.Equal(0, await commands.RunAsync(["archive", "1"]));
        output.GetStringBuilder().Clear();
        Assert.Equal(0, await commands.RunAsync(["list", "--archived"]));
        Assert.Contains("[archived]", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("☑ 0/1", output.ToString(), StringComparison.Ordinal); // checklist progress column

        var active = Build(out _, out _, stdin: "- [ ] live one\n", store: store);
        Assert.Equal(0, await active.RunAsync(["new", "Active", "--stdin"])); // checklists view skips archived
        output.GetStringBuilder().Clear();
        Assert.Equal(0, await commands.RunAsync(["list", "--checklists"]));
        Assert.Contains("Active", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MoneyInt_RejectsJunkIds()
    {
        var commands = Build(out _, out var error);

        foreach (var junk in new[] { "abc", "0", "-3" })
        {
            Assert.Equal(1, await commands.RunAsync(["show", junk]));
            Assert.Contains("must be a positive number", error.ToString(), StringComparison.Ordinal);
            error.GetStringBuilder().Clear();
        }

    }

    [Fact]
    public async Task AiAsk_EmptyQuestion_FailsFriendly()
    {
        var commands = Build(out _, out var error, stdin: "b\n", ai: (_, _) =>
            Task.FromResult(new JameJam.Soroush.SoroushResult("x", "stub", "m", 1, TimeSpan.Zero)));
        await commands.RunAsync(["new", "T", "--stdin"]);

        Assert.Equal(1, await commands.RunAsync(["ai", "ask", "--json"]));
        Assert.Contains("Ask a question:", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownNotebook_AndMissingEntities_FailFriendly()
    {
        var commands = Build(out _, out var error, stdin: "body\n");
        await commands.RunAsync(["notebook", "add", "Real"]);

        Assert.Equal(1, await commands.RunAsync(["new", "T", "--notebook", "ghost"]));
        Assert.Contains("No notebook named 'ghost'", error.ToString(), StringComparison.Ordinal);
        error.GetStringBuilder().Clear();

        Assert.Equal(1, await commands.RunAsync(["notebook", "rename", "ghost", "X"]));
        Assert.Contains("No notebook named 'ghost'", error.ToString(), StringComparison.Ordinal);
        error.GetStringBuilder().Clear();

        Assert.Equal(0, await commands.RunAsync(["new", "Held", "--stdin"])); // lands in Real (first)
        Assert.Equal(1, await commands.RunAsync(["notebook", "remove", "1"]));
        Assert.Contains("move them or pass --force", error.ToString(), StringComparison.Ordinal);
        error.GetStringBuilder().Clear();
        Assert.Equal(1, await commands.RunAsync(["edit", "42", "--title", "X"]));

        Assert.Equal(1, await commands.RunAsync(["edit", "42", "--title", "X"]));
        Assert.Contains("No note #42", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_SkipsUnparseableFiles_ButImportsGoodOnes()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"divan-edge-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "a-good.md"), "---\ntitle: Good One\nnotebook: Real\ntags: x\npinned: true\n---\n\nbody here\n", Encoding.UTF8);
            File.WriteAllText(Path.Combine(folder, "b-plain.md"), "no front matter at all\n", Encoding.UTF8);
            File.WriteAllText(Path.Combine(folder, "c-unterminated.md"), "---\ntitle: Never Closed\nbody continues forever\n", Encoding.UTF8);
            File.WriteAllText(Path.Combine(folder, "d-notitle.md"), "---\nnotebook: Real\n---\nbody\n", Encoding.UTF8);

            var commands = Build(out var output, out var error);
            await commands.RunAsync(["notebook", "add", "Real"]);

            Assert.Equal(0, await commands.RunAsync(["import", folder]));
            Assert.Contains("Imported 1 note(s)", output.ToString(), StringComparison.Ordinal);

            Assert.Equal(1, await commands.RunAsync(["import", Path.Combine(folder, "no-such-sub")]));
            Assert.Contains("No such folder", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Import_EmptyFolder_FailsFriendly()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"divan-edge-empty-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(folder);
        try
        {
            var commands = Build(out _, out var error);
            Assert.Equal(1, await commands.RunAsync(["import", folder]));
            Assert.Contains("No .md files found", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Constructors_NullGuards_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new DivanCommands(null, null!, new StringWriter(), new StringWriter()));
        Assert.Throws<ArgumentNullException>(() => new DivanCommands(null, Clock, null!, new StringWriter()));
        Assert.Throws<ArgumentNullException>(() => new DivanCommands(null, Clock, new StringWriter(), null!));
        Assert.Throws<ArgumentNullException>(() => new DivanService(null!, Clock));
        Assert.Throws<ArgumentNullException>(() => new DivanService(new MemoryDivanStore(), null!));
    }
}

/// <summary>Service-level guards that the CLI never reaches on its own.</summary>
public sealed class DivanServiceEdgeTests
{
    private static readonly FixedTimeProvider Clock =
        new(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void NotebookName_Guards()
    {
        DivanService service = new(new MemoryDivanStore(), Clock);
        _ = service.CreateNotebook("Alpha");

        var clash = Assert.Throws<DivanException>(() => service.CreateNotebook("alpha"));
        Assert.Contains("already exists", clash.Message, StringComparison.Ordinal);

        _ = service.CreateNotebook("Beta");
        var renameClash = Assert.Throws<DivanException>(() => service.RenameNotebook("1", "beta"));
        Assert.Contains("already exists", renameClash.Message, StringComparison.Ordinal);

        // renaming to the same name (self) is allowed
        Assert.Equal("Beta", service.RenameNotebook("2", "Beta").Name);
    }

    [Fact]
    public void Append_JoinsPerBodyShape()
    {
        DivanService service = new(new MemoryDivanStore(), Clock);
        var bare = service.AddNote("Bare", "text");
        Assert.Equal("text\nmore", service.Append(bare.Id, "more").Body);

        var trailing = service.AddNote("Trailing", "text\n");
        Assert.Equal("text\nmore", service.Append(trailing.Id, "more").Body);

        var empty = service.AddNote("Empty", "");
        Assert.Equal("more", service.Append(empty.Id, "more").Body);

        Assert.Throws<DivanException>(() => service.Append(999, "x"));
        _ = Assert.ThrowsAny<ArgumentException>(() => service.Append(1, "   "));
    }

    [Fact]
    public void AddNote_TrimsLongTitles_AndRejectsBlank()
    {
        DivanService service = new(new MemoryDivanStore(), Clock);
        var longTitle = new string('x', DivanDefaults.MaxTitleLength + 50);
        Assert.Equal(DivanDefaults.MaxTitleLength, service.AddNote(longTitle, "").Title.Length);
        _ = Assert.ThrowsAny<ArgumentException>(() => service.AddNote("   ", ""));
    }

    [Fact]
    public void Store_Update_MissingEntities_Throw()
    {
        MemoryDivanStore store = new();
        var notebook = new Notebook(99, "Ghost", Clock.GetUtcNow(), IsArchived: false);
        var ghost = Assert.Throws<DivanException>(() => store.UpdateNotebook(notebook));
        Assert.Contains("No notebook #99", ghost.Message, StringComparison.Ordinal);

        var note = new Note(99, 1, "Ghost", "", "", Pinned: false, Archived: false, Clock.GetUtcNow(), Clock.GetUtcNow());
        Assert.Throws<DivanException>(() => store.UpdateNote(note));
    }

    [Fact]
    public async Task Sqlite_Update_MissingEntities_Throw()
    {
        var path = Path.Combine(Path.GetTempPath(), $"divan-edge-{Guid.NewGuid():N}.db");
        try
        {
            SqliteDivanStore store = new(path);
            var notebook = new Notebook(99, "Ghost", Clock.GetUtcNow(), IsArchived: false);
            Assert.Throws<DivanException>(() => store.UpdateNotebook(notebook));
            var note = new Note(99, 1, "Ghost", "", "", Pinned: false, Archived: false, Clock.GetUtcNow(), Clock.GetUtcNow());
            Assert.Throws<DivanException>(() => store.UpdateNote(note));
            await Task.CompletedTask;
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Undo_TrimsToTheConfiguredDepth()
    {
        DivanService service = new(new MemoryDivanStore(), Clock, new DivanOptions(UndoDepth: 5));
        for (var i = 0; i < 8; i++)
        {
            _ = service.AddNote($"N{i}", "");
        }

        for (var i = 0; i < 5; i++)
        {
            Assert.True(service.Undo());
        }

        Assert.False(service.Undo()); // snapshots older than depth 5 are dropped
        Assert.Equal(3, service.List().Count); // N0..N2 remain
        await Task.CompletedTask;
    }
}
