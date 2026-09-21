using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JameJam.Divan;

/// <summary>One open (`- [ ]`) checklist item with the note it belongs to.</summary>
/// <param name="Note">The containing note.</param>
/// <param name="Item">The parsed item.</param>
public sealed record OpenTodo(Note Note, ChecklistItem Item);

/// <summary>
/// Business logic for the Divan pad: notebooks, notes, wiki-links, backlinks, checklists,
/// daily journaling, full-text search, stats, markdown export/import, and undo.
/// </summary>
public sealed class DivanService
{
    private readonly IDivanStore _store;
    private readonly TimeProvider _clock;
    private readonly DivanOptions _options;

    /// <summary>Initializes the service over a store with validated options.</summary>
    public DivanService(IDivanStore store, TimeProvider clock, DivanOptions? options = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? new DivanOptions();
        _options.Validate();
        _store.UndoDepth = _options.UndoDepth;
    }

    /// <summary>The validated options in effect.</summary>
    public DivanOptions Options => _options;

    /// <summary>Today according to the injected clock.</summary>
    public DateOnly Today => DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);

    // ── Notebooks ──

    /// <summary>Creates a notebook (unique name, case-insensitive).</summary>
    public Notebook CreateNotebook(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var clean = Clean(name, DivanDefaults.MaxNotebookNameLength);
        if (_store.FindNotebookByName(clean) is not null)
        {
            throw new DivanException($"A notebook named '{clean}' already exists.");
        }

        if (_store.ListNotebooks().Count >= DivanDefaults.MaxNotebooks)
        {
            throw new DivanException($"At most {DivanDefaults.MaxNotebooks} notebooks are allowed.");
        }

        var now = _clock.GetUtcNow();
        PushSnapshot();
        return _store.AddNotebook(new Notebook(0, clean, now, IsArchived: false, now));
    }

    /// <summary>
    /// Resolves a notebook by id or name, creating the default one when the pad is empty
    /// and no name was given.
    /// </summary>
    public Notebook ResolveNotebook(string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            var clean = Clean(text, DivanDefaults.MaxNotebookNameLength);
            return _store.FindNotebookByName(clean)
                ?? (long.TryParse(clean, out var id) ? _store.FindNotebook(id) : null)
                ?? throw new DivanException($"No notebook named '{clean}'. Create one: JameJam divan notebook add {clean}");
        }

        var notebooks = _store.ListNotebooks();
        if (notebooks.Count > 0)
        {
            return notebooks[0];
        }

        return _store.FindNotebookByName(DivanDefaults.DefaultNotebook) ?? _store.CreateNotebookProxy(this);
    }

    /// <summary>Renames a notebook.</summary>
    public Notebook RenameNotebook(string text, string newName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        var notebook = ResolveNotebook(text);
        PushSnapshot();
        var clean = Clean(newName, DivanDefaults.MaxNotebookNameLength);
        if (_store.FindNotebookByName(clean) is { } clash && clash.Id != notebook.Id)
        {
            throw new DivanException($"A notebook named '{clean}' already exists.");
        }

        var renamed = notebook with { Name = clean, UpdatedAt = _clock.GetUtcNow() };
        _store.UpdateNotebook(renamed);
        return renamed;
    }

    /// <summary>Archives or unarchives a notebook.</summary>
    public Notebook ArchiveNotebook(string text, bool archived)
    {
        var notebook = ResolveNotebook(text);
        PushSnapshot();
        var updated = notebook with { IsArchived = archived, UpdatedAt = _clock.GetUtcNow() };
        _store.UpdateNotebook(updated);
        return updated;
    }

    /// <summary>Removes an empty notebook; with <paramref name="force"/> its notes go too.</summary>
    public int RemoveNotebook(string text, bool force)
    {
        var notebook = ResolveNotebook(text);
        var notes = _store.ListNotes().Where(n => n.NotebookId == notebook.Id).ToList();
        if (notes.Count > 0 && !force)
        {
            throw new DivanException(
                $"Notebook '{notebook.Name}' holds {notes.Count} note(s) — move them or pass --force.");
        }

        PushSnapshot();
        var now = _clock.GetUtcNow();
        foreach (var note in notes)
        {
            _ = _store.RemoveNote(note.Id, now);
        }

        _ = _store.RemoveNotebook(notebook.Id);
        return notes.Count;
    }

    // ── Notes ──

    /// <summary>Creates a note with an undo snapshot.</summary>
    public Note AddNote(string title, string body, string? notebookText = null, string tags = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (_store.ListNotes().Count >= DivanDefaults.MaxNotes)
        {
            throw new DivanException($"At most {DivanDefaults.MaxNotes} notes are allowed.");
        }

        PushSnapshot();
        var now = _clock.GetUtcNow();
        var notebook = ResolveNotebook(notebookText);
        return _store.AddNote(new Note(
            0,
            notebook.Id,
            Clean(title, DivanDefaults.MaxTitleLength),
            CleanBody(body),
            CleanTags(tags),
            Pinned: false,
            Archived: false,
            now,
            now));
    }

    /// <summary>Appends markdown text to a note's body.</summary>
    public Note Append(long id, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var note = RequireNote(id);
        PushSnapshot();
        var addition = CleanBody(text);
        var updated = note with
        {
            Body = note.Body.Length == 0
                ? addition
                : note.Body.EndsWith('\n') ? note.Body + addition : note.Body + "\n" + addition,
            UpdatedAt = _clock.GetUtcNow(),
        };
        _store.UpdateNote(updated);
        return updated;
    }

    /// <summary>Overwrites a note's title, body, or tags (null keeps the current value).</summary>
    public Note EditNote(long id, string? title = null, string? body = null, string? tags = null, bool? pinned = null)
    {
        var note = RequireNote(id);
        PushSnapshot();
        var updated = note with
        {
            Title = title is null ? note.Title : Clean(title, DivanDefaults.MaxTitleLength),
            Body = body is null ? note.Body : CleanBody(body),
            Tags = tags is null ? note.Tags : CleanTags(tags),
            Pinned = pinned ?? note.Pinned,
            UpdatedAt = _clock.GetUtcNow(),
        };
        _store.UpdateNote(updated);
        return updated;
    }

    /// <summary>Moves a note to another notebook.</summary>
    public Note Move(long id, string notebookText)
    {
        var note = RequireNote(id);
        var notebook = ResolveNotebook(notebookText);
        PushSnapshot();
        var updated = note with { NotebookId = notebook.Id, UpdatedAt = _clock.GetUtcNow() };
        _store.UpdateNote(updated);
        return updated;
    }

    /// <summary>Sets or clears a note's pin.</summary>
    public Note Pin(long id, bool pinned)
    {
        var note = RequireNote(id);
        PushSnapshot();
        var updated = note with { Pinned = pinned, UpdatedAt = _clock.GetUtcNow() };
        _store.UpdateNote(updated);
        return updated;
    }

    /// <summary>Archives or unarchives a note.</summary>
    public Note Archive(long id, bool archived)
    {
        var note = RequireNote(id);
        PushSnapshot();
        var updated = note with { Archived = archived, UpdatedAt = _clock.GetUtcNow() };
        _store.UpdateNote(updated);
        return updated;
    }

    /// <summary>Pushes a whole-pad undo snapshot — sync calls this once before applying a merge.</summary>
    public void PushUndoSnapshot() => PushSnapshot();

    /// <summary>Deletes a note (undo brings it back).</summary>
    public Note Delete(long id)
    {
        var note = RequireNote(id);
        PushSnapshot();
        _ = _store.RemoveNote(id, _clock.GetUtcNow());
        return note;
    }

    /// <summary>Gets one note.</summary>
    public Note? GetNote(long id) => _store.FindNote(id);

    /// <summary>Lists notes (pinned first, then most recently updated) under the filter.</summary>
    public IReadOnlyList<Note> List(DivanFilter? filter = null)
    {
        var f = filter ?? new DivanFilter();
        IEnumerable<Note> notes = f.Query is { } q && q.Trim().Length > 0
            ? RankedSearch(q.Trim(), f)
            : _store.ListNotes();

        var today = Today;
        return notes
            .Where(n => f.ArchivedOnly ? n.Archived : !n.Archived)
            .Where(n => f.NotebookId is null || n.NotebookId == f.NotebookId)
            .Where(n => f.Tag is null || TagsOf(n).Contains(f.Tag, StringComparer.OrdinalIgnoreCase))
            .Where(n => !f.PinnedOnly || n.Pinned)
            .Where(n => !f.ChecklistsOnly || DivanText.Checklist(n.Body).Count > 0)
            .OrderByDescending(n => n.Pinned)
            .ThenByDescending(n => n.UpdatedAt)
            .ToList();
    }

    /// <summary>Notes that link to the given note via `[[Title]]`.</summary>
    public IReadOnlyList<Note> Backlinks(long id)
    {
        var target = RequireNote(id);
        return _store.ListNotes()
            .Where(n => n.Id != id)
            .Where(n => DivanText.ExtractLinks(n.Body)
                .Any(l => string.Equals(l, target.Title, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>All open `- [ ]` items across active notes (optionally within a notebook).</summary>
    public IReadOnlyList<OpenTodo> OpenTodos(long? notebookId = null)
    {
        List<OpenTodo> todos = [];
        foreach (var note in List(new DivanFilter(NotebookId: notebookId)))
        {
            foreach (var item in DivanText.Checklist(note.Body).Where(i => !i.Done))
            {
                todos.Add(new OpenTodo(note, item));
            }
        }

        return todos;
    }

    /// <summary>
    /// Today's journal note: creates it (with a date heading) in the Journal notebook on
    /// first call of the day, returns the same note afterwards.
    /// </summary>
    public Note Daily()
    {
        var journal = _store.FindNotebookByName(DivanDefaults.JournalNotebook)
            ?? _store.FindNotebookByName(DivanDefaults.JournalNotebook)
            ?? CreateJournal();
        var title = Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var existing = _store.ListNotes().FirstOrDefault(n =>
            n.NotebookId == journal.Id && string.Equals(n.Title, title, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        return AddNote(
            title,
            $"# {Today.ToString("dddd, yyyy-MM-dd", CultureInfo.InvariantCulture)}\n\n- [ ] ",
            journal.Id.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Derived metrics for one note (words, reading time, checklist, links).</summary>
    public NoteMetrics Metrics(Note note)
    {
        ArgumentNullException.ThrowIfNull(note);
        var words = DivanText.WordCount(note.Body);
        var checklist = DivanText.Checklist(note.Body);
        return new NoteMetrics(
            words,
            note.Body.Length,
            (int)Math.Ceiling(words * 60.0 / _options.ReadingWordsPerMinute),
            checklist.Count,
            checklist.Count(i => i.Done),
            DivanText.ExtractLinks(note.Body));
    }

    /// <summary>Lists all notebooks in id order.</summary>
    public IReadOnlyList<Notebook> ListNotebooks() => _store.ListNotebooks();

    /// <summary>Distinct tags across active notes (case-insensitive, id order).</summary>
    public IReadOnlyList<string> AllTags() =>
        _store.ListNotes()
            .Where(n => !n.Archived)
            .SelectMany(TagsOf)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Aggregate statistics over the pad.</summary>
    public DivanStats Stats()
    {
        var notebooks = _store.ListNotebooks();
        var notes = _store.ListNotes();
        var active = notes.Where(n => !n.Archived).ToList();
        return new DivanStats(
            notebooks.Count,
            active.Count,
            notes.Count - active.Count,
            active.Count(n => n.Tags.Length > 0),
            active.Sum(n => DivanText.WordCount(n.Body)),
            OpenTodos().Count,
            active.Sum(n => DivanText.ExtractLinks(n.Body).Count));
    }

    // ── Undo ──

    /// <summary>Reverts the last change (notebook + note state together).</summary>
    public bool Undo()
    {
        var payload = _store.PopUndo();
        if (payload is null)
        {
            return false;
        }

        PadSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<PadSnapshot>(payload);
        }
        catch (JsonException)
        {
            throw new DivanException("The undo snapshot is unreadable.");
        }

        if (snapshot is null)
        {
            return false;
        }

        _store.ReplaceNotebooks(snapshot.Notebooks.Select(FromDto).ToList());
        _store.ReplaceNotes(snapshot.Notes.Select(FromDto).ToList());
        return true;
    }

    // ── Export / import (folder of markdown files with front matter) ──

    /// <summary>Writes every note as a markdown file (front matter + body) into a folder.</summary>
    public int Export(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        Directory.CreateDirectory(folder);
        var notes = _store.ListNotes();
        var notebooks = _store.ListNotebooks().ToDictionary(n => n.Id, n => n.Name);
        foreach (var note in notes)
        {
            var fileName = $"{FileNameSafe(note.Title)}-{note.Id}.md";
            File.WriteAllText(
                Path.Combine(folder, fileName),
                SerializeNote(note, notebooks.GetValueOrDefault(note.NotebookId, DivanDefaults.DefaultNotebook)),
                System.Text.Encoding.UTF8);
        }

        return notes.Count;
    }

    /// <summary>Imports every `*.md` file from a folder, creating notebooks as needed.</summary>
    public int Import(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (!Directory.Exists(folder))
        {
            throw new DivanException($"No such folder: {folder}");
        }

        var files = Directory.GetFiles(folder, "*.md").OrderBy(f => f).ToList();
        if (files.Count == 0)
        {
            throw new DivanException("No .md files found in that folder.");
        }

        if (files.Count > DivanDefaults.MaxImportFiles)
        {
            files = files.Take(DivanDefaults.MaxImportFiles).ToList();
        }

        PushSnapshot();
        var imported = 0;
        foreach (var file in files)
        {
            var parsed = ParseNoteFile(File.ReadAllText(file, System.Text.Encoding.UTF8));
            if (parsed is null)
            {
                continue;
            }

            var (title, notebookName, tags, pinned, body) = parsed.Value;
            var notebook = _store.FindNotebookByName(notebookName) ?? CreateNotebook(notebookName);
            var now = _clock.GetUtcNow();
            _ = _store.AddNote(new Note(
                0, notebook.Id, title, body, tags, pinned, Archived: false, now, now));
            imported++;
        }

        return imported;
    }

    // ── Internals ──

    private Notebook CreateJournal()
    {
        if (_store.FindNotebookByName(DivanDefaults.JournalNotebook) is { } journal)
        {
            return journal;
        }

        PushSnapshot();
        return _store.AddNotebook(new Notebook(0, DivanDefaults.JournalNotebook, _clock.GetUtcNow(), IsArchived: false));
    }

    private IEnumerable<Note> RankedSearch(string query, DivanFilter f)
    {
        var ids = new HashSet<long>(_store.SearchIds(query, _options.SearchLimit));
        return _store.ListNotes().Where(n => ids.Contains(n.Id));
    }

    private Note RequireNote(long id) =>
        _store.FindNote(id) ?? throw new DivanException($"No note #{id}.");

    private static IReadOnlyList<string> TagsOf(Note note) =>
        note.Tags.Length == 0 ? [] : note.Tags.Split(',');

    private static string Clean(string? value, int maxLength) => DivanText.Clip(value, maxLength);

    private static string CleanBody(string? body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var normalized = body.Replace("\r\n", "\n").Trim();
        return normalized.Length <= DivanDefaults.MaxBodyLength ? normalized : normalized[..DivanDefaults.MaxBodyLength];
    }

    private static string CleanTags(string? tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var split = tags
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(t => t.Length <= DivanDefaults.MaxTagLength)
            .Take(DivanDefaults.MaxTagsPerNote)
            .ToList();
        var joined = string.Join(',', split);
        return joined.Length <= DivanDefaults.MaxTagsLength ? joined : joined[..DivanDefaults.MaxTagsLength];
    }

    private static string FileNameSafe(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(title.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim();
        return safe.Length == 0 ? "note" : safe.Length > 40 ? safe[..40].Trim() : safe;
    }

    private void PushSnapshot()
    {
        var snapshot = new PadSnapshot(
            [.. _store.ListNotebooks().Select(ToDto)],
            [.. _store.ListNotes().Select(ToDto)]);
        _store.PushUndo(JsonSerializer.Serialize(snapshot));
    }

    private static NotebookDto ToDto(Notebook notebook) => new(
        notebook.Id, notebook.Name, notebook.CreatedAt.ToString("O", CultureInfo.InvariantCulture), notebook.IsArchived,
        notebook.UpdatedAt == default ? null : notebook.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));

    private static NoteDto ToDto(Note note) => new(
        note.Id, note.NotebookId, note.Title, note.Body, note.Tags, note.Pinned, note.Archived,
        note.CreatedAt.ToString("O", CultureInfo.InvariantCulture), note.UpdatedAt.ToString("O", CultureInfo.InvariantCulture),
        note.SyncId);

    private static Notebook FromDto(NotebookDto dto) => new(
        dto.Id, dto.Name, DateTimeOffset.Parse(dto.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), dto.IsArchived,
        dto.UpdatedAt is null ? default : DateTimeOffset.Parse(dto.UpdatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    private static Note FromDto(NoteDto dto) => new(
        dto.Id, dto.NotebookId, dto.Title, dto.Body, dto.Tags, dto.Pinned, dto.Archived,
        DateTimeOffset.Parse(dto.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        DateTimeOffset.Parse(dto.UpdatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        dto.SyncId);

    private static string SerializeNote(Note note, string notebookName)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("---");
        builder.AppendLine(CultureInfo.InvariantCulture, $"title: {note.Title.ReplaceLineEndings(" ")}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"notebook: {notebookName}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"tags: {note.Tags}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"pinned: {note.Pinned.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"created: {note.CreatedAt:O}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"updated: {note.UpdatedAt:O}");
        builder.AppendLine("---");
        builder.AppendLine(note.Body);
        return builder.ToString();
    }

    private static (string Title, string Notebook, string Tags, bool Pinned, string Body)? ParseNoteFile(string content)
    {
        if (!content.StartsWith("---", StringComparison.Ordinal))
        {
            return null;
        }

        var end = content.IndexOf("\n---", StringComparison.Ordinal);
        if (end < 0)
        {
            return null;
        }

        var frontMatter = content[3..end];
        var body = content[(end + 4)..].TrimStart('\n');
        string title = string.Empty;
        var notebook = DivanDefaults.DefaultNotebook;
        var tags = string.Empty;
        var pinned = false;
        foreach (var line in frontMatter.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            switch (key.ToLowerInvariant())
            {
                case "title" when value.Length > 0:
                    title = value;
                    break;
                case "notebook" when value.Length > 0:
                    notebook = value;
                    break;
                case "tags":
                    tags = value;
                    break;
                case "pinned":
                    pinned = value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    break;
            }
        }

        return title.Length == 0 ? null : (title, notebook, tags, pinned, body);
    }
}

/// <summary>Extension point used by <see cref="DivanService.ResolveNotebook"/> for default creation.</summary>
internal static class DivanServiceExtensions
{
    /// <summary>Creates the default notebook through the owning service (snapshot included).</summary>
    public static Notebook CreateNotebookProxy(this IDivanStore store, DivanService service) =>
        service.CreateNotebook(DivanDefaults.DefaultNotebook);
}

/// <summary>Undo snapshot: the whole pad (notebooks + notes).</summary>
/// <param name="Notebooks">All notebooks.</param>
/// <param name="Notes">All notes.</param>
internal sealed record PadSnapshot(
    [property: JsonPropertyName("notebooks")] IReadOnlyList<NotebookDto> Notebooks,
    [property: JsonPropertyName("notes")] IReadOnlyList<NoteDto> Notes);

internal sealed record NotebookDto(
    long Id, string Name, string CreatedAt, bool IsArchived, string? UpdatedAt = null);

internal sealed record NoteDto(
    long Id, long NotebookId, string Title, string Body, string Tags,
    bool Pinned, bool Archived, string CreatedAt, string UpdatedAt, Guid SyncId = default);
