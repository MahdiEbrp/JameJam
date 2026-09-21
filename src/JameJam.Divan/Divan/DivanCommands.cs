using System.Globalization;
using JameJam.Divan.Ai;
using JameJam.Divan.Sync;
using JameJam.Settings;
using JameJam.Soroush;
using JameJam.Sync;

namespace JameJam.Divan;

/// <summary>
/// The <c>divan</c> CLI: a markdown pad with notebooks, wiki-links, full-text search,
/// checklists, a daily journal, and Soroush-powered AI.
/// </summary>
/// <param name="store">Pad storage; null disables Divan (storage unavailable).</param>
/// <param name="clock">Time source for daily notes and timestamps.</param>
/// <param name="output">Standard output.</param>
/// <param name="error">Error output.</param>
/// <param name="stdin">Piped input for note bodies; defaults to the console.</param>
/// <param name="aiCompletion">Routes a prompt through the Soroush safety layer; null when AI is unavailable.</param>
/// <param name="options">Customizable rails; defaults apply when null.</param>
/// <param name="defaultNotebookProvider">Resolves the preferred default notebook name.</param>
/// <param name="syncClientFactory">Builds the sync transport for a URL; null disables sync.</param>
/// <param name="syncUrlProvider">Resolves a stored default sync URL (settings).</param>
/// <param name="deviceIdProvider">Resolves this device's stable sync identity (a GUID string).</param>
/// <param name="deviceNameProvider">Resolves this device's friendly name.</param>
public sealed class DivanCommands(
    IDivanStore? store,
    TimeProvider clock,
    TextWriter output,
    TextWriter error,
    TextReader? stdin = null,
    Func<AiRequest, CancellationToken, Task<SoroushResult>>? aiCompletion = null,
    DivanOptions? options = null,
    Func<string?>? defaultNotebookProvider = null,
    Func<SyncOptions, ISyncClient>? syncClientFactory = null,
    Func<string?>? syncUrlProvider = null,
    Func<string>? deviceIdProvider = null,
    Func<string>? deviceNameProvider = null)
{
    private const string NoStorageMessage = "Pad storage is not available in this context.";

    private readonly IDivanStore? _store = store;
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly TextWriter _output = output ?? throw new ArgumentNullException(nameof(output));
    private readonly TextWriter _error = error ?? throw new ArgumentNullException(nameof(error));
    private readonly TextReader _stdin = stdin ?? Console.In;
    private readonly Func<AiRequest, CancellationToken, Task<SoroushResult>>? _aiCompletion = aiCompletion;
    private readonly DivanOptions _options = options ?? new DivanOptions();
    private readonly Func<string?>? _defaultNotebookProvider = defaultNotebookProvider;
    private readonly Func<SyncOptions, ISyncClient>? _syncClientFactory = syncClientFactory;
    private readonly Func<string?>? _syncUrlProvider = syncUrlProvider;
    private readonly Func<string>? _deviceIdProvider = deviceIdProvider;
    private readonly Func<string>? _deviceNameProvider = deviceNameProvider;
    private readonly PadAssistant _assistant = new(options);

    /// <summary>Runs a <c>divan</c> command; returns the process exit code.</summary>
    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
        {
            return Help();
        }

        try
        {
            return args[0] switch
            {
                "help" => Help(),
                "notebook" => RunNotebook(args[1..]),
                "new" => RunNew(args[1..]),
                "append" => RunAppend(args[1..]),
                "list" => RunList(args[1..]),
                "show" => RunShow(args[1..]),
                "edit" => RunEdit(args[1..]),
                "delete" => RunDelete(args[1..]),
                "search" => RunSearch(args[1..]),
                "backlinks" => RunBacklinks(args[1..]),
                "todos" => RunTodos(args[1..]),
                "daily" => RunDaily(),
                "pin" => RunPin(args[1..], true),
                "unpin" => RunPin(args[1..], false),
                "move" => RunMove(args[1..]),
                "archive" => RunArchive(args[1..], true),
                "unarchive" => RunArchive(args[1..], false),
                "export" => RunExport(args[1..]),
                "import" => RunImport(args[1..]),
                "undo" => RunUndo(),
                "stats" => RunStats(),
                "sync" when args.Length >= 1 => await RunSyncAsync(args[1..], cancellationToken).ConfigureAwait(false),
                "sync" => await RunSyncAsync([], cancellationToken).ConfigureAwait(false),
                "ai" when args.Length >= 2 => await RunAiAsync(args[1..], cancellationToken).ConfigureAwait(false),
                "ai" => Fail("Usage: JameJam divan ai summarize <id> | ai title <id> [--apply] | ai tags <id> [--apply] | ai ask <question...>"),
                _ => Fail($"Unknown divan command '{args[0]}'. Run 'JameJam divan help'."),
            };
        }
        catch (DivanException ex)
        {
            return Fail(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Fail(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Fail(ex.Message);
        }
        catch (SoroushException ex)
        {
            return Fail(ex.Message);
        }
        catch (IOException ex)
        {
            return Fail($"File error: {ex.Message}");
        }
    }

    // ── Notebooks ──

    private int RunNotebook(string[] args)
    {
        var service = NewService();
        var sub = args.Length > 0 ? args[0] : "list";
        switch (sub)
        {
            case "add" when args.Length >= 2:
            {
                var notebook = service.CreateNotebook(args[1]);
                _output.WriteLine($"Added notebook #{notebook.Id}: {notebook.Name}.");
                return 0;
            }

            case "rename" when args.Length >= 3:
            {
                var notebook = service.RenameNotebook(args[1], args[2]);
                _output.WriteLine($"Renamed to {notebook.Name}.");
                return 0;
            }

            case "archive" when args.Length >= 2:
            {
                var notebook = service.ArchiveNotebook(args[1], archived: true);
                _output.WriteLine($"Archived {notebook.Name}.");
                return 0;
            }

            case "unarchive" when args.Length >= 2:
            {
                var notebook = service.ArchiveNotebook(args[1], archived: false);
                _output.WriteLine($"Unarchived {notebook.Name}.");
                return 0;
            }

            case "remove" when args.Length >= 2:
            {
                var removed = service.RemoveNotebook(args[1], args.Contains("--force"));
                _output.WriteLine($"Notebook removed ({removed} note(s) deleted). divan undo brings them back.");
                return 0;
            }

            case "list":
            {
                var notebooks = service.ListNotebooks();
                if (notebooks.Count == 0)
                {
                    _output.WriteLine("No notebooks yet. JameJam divan notebook add <name> creates one.");
                    return 0;
                }

                foreach (var notebook in notebooks)
                {
                    _output.WriteLine(FormattableString.Invariant(
                        $"#{notebook.Id}  {notebook.Name}{(notebook.IsArchived ? "  [archived]" : string.Empty)}"));
                }

                return 0;
            }

            default:
                _error.WriteLine("Usage: JameJam divan notebook add <name> | list | rename <id|name> <new> | archive <id> | unarchive <id> | remove <id> [--force]");
                return 1;
        }
    }

    // ── Notes ──

    private int RunNew(string[] args)
    {
        if (args.Length < 1)
        {
            return Fail("Usage: JameJam divan new <title> [--notebook n] [--tags a,b] [--stdin]  (body via stdin, or starts empty)");
        }

        var parsed = ParseFlags(args[1..], "--notebook", "--tags", "--stdin");
        var service = NewService();
        var body = parsed.Has("--stdin") ? ReadStdin("Note body") : string.Empty;
        var preferred = parsed.Get("--notebook") ?? _defaultNotebookProvider?.Invoke();
        var note = service.AddNote(args[0], body, preferred, parsed.Get("--tags") ?? string.Empty);
        _output.WriteLine(FormattableString.Invariant($"Added #{note.Id} {note.Title} — {DivanText.WordCount(note.Body)} words."));
        return 0;
    }

    private int RunAppend(string[] args)
    {
        if (args.Length < 1)
        {
            return Fail("Usage: JameJam divan append <id> [--stdin]  (text via stdin, or the interactive prompt)");
        }

        var parsed = ParseFlags(args[1..], "--stdin");
        var service = NewService();
        var text = parsed.Has("--stdin") || InputIsRedirected()
            ? ReadStdin("Text to append")
            : ReadInteractive("Text to append (end with an empty line):");
        var note = service.Append(MoneyInt(args[0], "Note id"), text);
        _output.WriteLine(FormattableString.Invariant($"Appended — #{note.Id} is now {DivanText.WordCount(note.Body)} words."));
        return 0;
    }

    private int RunList(string[] args)
    {
        var parsed = ParseFlags(args, "--notebook", "--tag", "--q", "--pinned", "--archived", "--checklists");
        var service = NewService();
        var filter = new DivanFilter(
            NotebookId: parsed.Get("--notebook") is { } nb ? service.ResolveNotebook(nb).Id : null,
            Tag: parsed.Get("--tag"),
            Query: parsed.Get("--q"),
            PinnedOnly: parsed.Has("--pinned"),
            ArchivedOnly: parsed.Has("--archived"),
            ChecklistsOnly: parsed.Has("--checklists"));
        var rows = service.List(filter);
        if (rows.Count == 0)
        {
            _output.WriteLine("No notes match.");
            return 0;
        }

        foreach (var note in rows)
        {
            _output.WriteLine(ListLine(note));
        }

        return 0;
    }

    private int RunShow(string[] args)
    {
        if (args.Length < 1)
        {
            return Fail("Usage: JameJam divan show <id>");
        }

        var service = NewService();
        var note = service.GetNote(MoneyInt(args[0], "Note id"))
            ?? throw new DivanException($"No note #{MoneyInt(args[0], "Note id")}.");
        var metrics = service.Metrics(note);
        var notebook = service.ResolveNotebook(note.NotebookId.ToString(CultureInfo.InvariantCulture));
        _output.WriteLine(FormattableString.Invariant($"#{note.Id} — {note.Title}"));
        _output.WriteLine(FormattableString.Invariant(
            $"  Notebook: {notebook.Name} · Tags: {(note.Tags.Length == 0 ? "—" : note.Tags)} · {(note.Pinned ? "★ pinned" : "unpinned")}{(note.Archived ? " · [archived]" : string.Empty)}"));
        _output.WriteLine(FormattableString.Invariant(
            $"  {metrics.Words} words · {metrics.Characters} chars · ~{(metrics.ReadingSeconds + 59) / 60} min read · updated {note.UpdatedAt:yyyy-MM-dd HH:mm} UTC"));
        if (metrics.ChecklistTotal > 0)
        {
            _output.WriteLine(FormattableString.Invariant($"  Checklist: {metrics.ChecklistDone}/{metrics.ChecklistTotal} done"));
        }

        if (metrics.Links.Count > 0)
        {
            _output.WriteLine($"  Links: {string.Join(", ", metrics.Links)}");
        }

        var backlinks = service.Backlinks(note.Id);
        if (backlinks.Count > 0)
        {
            _output.WriteLine($"  Backlinks: {string.Join(", ", backlinks.Select(b => $"#{b.Id} {b.Title}"))}");
        }

        if (note.Body.Length > 0)
        {
            _output.WriteLine();
            _output.WriteLine(note.Body);
        }

        return 0;
    }

    private int RunEdit(string[] args)
    {
        if (args.Length < 1)
        {
            return Fail("Usage: JameJam divan edit <id> [--title t] [--tags a,b] [--stdin]  (replaces the body via stdin)");
        }

        var parsed = ParseFlags(args[1..], "--title", "--tags", "--stdin");
        var service = NewService();
        var body = parsed.Has("--stdin") ? ReadStdin("Replacement body") : null;
        var note = service.EditNote(
            MoneyInt(args[0], "Note id"),
            parsed.Get("--title"),
            body,
            parsed.Get("--tags"));
        _output.WriteLine(FormattableString.Invariant($"Updated #{note.Id} {note.Title}."));
        return 0;
    }

    private int RunDelete(string[] args)
    {
        if (args.Length < 1)
        {
            return Fail("Usage: JameJam divan delete <id>");
        }

        var note = NewService().Delete(MoneyInt(args[0], "Note id"));
        _output.WriteLine($"Deleted #{note.Id} ({note.Title}). divan undo brings it back.");
        return 0;
    }

    private int RunSearch(string[] args)
    {
        if (args.Length < 1)
        {
            return Fail("Usage: JameJam divan search <query...>");
        }

        var service = NewService();
        var rows = service.List(new DivanFilter(Query: string.Join(' ', args)));
        if (rows.Count == 0)
        {
            _output.WriteLine("No notes match.");
            return 0;
        }

        foreach (var note in rows)
        {
            _output.WriteLine(FormattableString.Invariant($"#{note.Id}  {note.Title} — {DivanText.Snippet(note.Body, 90)}"));
        }

        return 0;
    }

    private int RunBacklinks(string[] args)
    {
        if (args.Length < 1)
        {
            return Fail("Usage: JameJam divan backlinks <id>");
        }

        var service = NewService();
        var links = service.Backlinks(MoneyInt(args[0], "Note id"));
        if (links.Count == 0)
        {
            _output.WriteLine("No backlinks.");
            return 0;
        }

        foreach (var note in links)
        {
            _output.WriteLine($"#{note.Id}  {note.Title}");
        }

        return 0;
    }

    private int RunTodos(string[] args)
    {
        var parsed = ParseFlags(args, "--notebook");
        var service = NewService();
        var todos = service.OpenTodos(parsed.Get("--notebook") is { } nb ? service.ResolveNotebook(nb).Id : null);
        if (todos.Count == 0)
        {
            _output.WriteLine("No open checklist items. ☑");
            return 0;
        }

        foreach (var (note, item) in todos)
        {
            _output.WriteLine(FormattableString.Invariant($"#{note.Id} L{item.LineNumber}  {item.Text}  ({note.Title})"));
        }

        return 0;
    }

    private int RunDaily()
    {
        var note = NewService().Daily();
        _output.WriteLine($"Journal #{note.Id}: {note.Title}");
        return 0;
    }

    private int RunPin(string[] args, bool pinned)
    {
        if (args.Length < 1)
        {
            return Fail($"Usage: JameJam divan {(pinned ? "pin" : "unpin")} <id>");
        }

        var note = NewService().Pin(MoneyInt(args[0], "Note id"), pinned);
        _output.WriteLine(FormattableString.Invariant($"#{note.Id} {(pinned ? "pinned ★" : "unpinned")}."));
        return 0;
    }

    private int RunMove(string[] args)
    {
        if (args.Length < 2)
        {
            return Fail("Usage: JameJam divan move <id> <notebook>");
        }

        var note = NewService().Move(MoneyInt(args[0], "Note id"), args[1]);
        _output.WriteLine($"Moved #{note.Id} {note.Title} to {args[1]}.");
        return 0;
    }

    private int RunArchive(string[] args, bool archived)
    {
        if (args.Length < 1)
        {
            return Fail($"Usage: JameJam divan {(archived ? "archive" : "unarchive")} <id>");
        }

        var note = NewService().Archive(MoneyInt(args[0], "Note id"), archived);
        _output.WriteLine(FormattableString.Invariant($"#{note.Id} {(archived ? "archived" : "unarchived")}."));
        return 0;
    }

    private int RunExport(string[] args)
    {
        if (args.Length < 1)
        {
            return Fail("Usage: JameJam divan export <folder>  (markdown files with front matter)");
        }

        var count = NewService().Export(args[0]);
        _output.WriteLine(FormattableString.Invariant($"Exported {count} note(s) to {args[0]}."));
        return 0;
    }

    private int RunImport(string[] args)
    {
        if (args.Length < 1)
        {
            return Fail("Usage: JameJam divan import <folder>  (divan undo reverts it)");
        }

        var count = NewService().Import(args[0]);
        _output.WriteLine(FormattableString.Invariant($"Imported {count} note(s). divan undo reverts it."));
        return 0;
    }

    private int RunUndo()
    {
        var undone = NewService().Undo();
        _output.WriteLine(undone ? "Undone — the last change is reverted." : "Nothing to undo.");
        return 0;
    }

    private int RunStats()
    {
        var stats = NewService().Stats();
        _output.WriteLine(FormattableString.Invariant(
            $"Divan — {stats.Notebooks} notebook(s), {stats.Notes} active note(s), {stats.ArchivedNotes} archived"));
        _output.WriteLine(FormattableString.Invariant(
            $"  {stats.Words} words · {stats.TaggedNotes} tagged note(s) · {stats.OpenChecklistItems} open checklist item(s) · {stats.Links} wiki-link(s)"));
        return 0;
    }

    // ── AI ──

    private async Task<int> RunSyncAsync(string[] args, CancellationToken cancellationToken)
    {
        if (_store is null)
        {
            return Fail(NoStorageMessage);
        }

        if (_syncClientFactory is null)
        {
            return Fail("Sync is not available in this context.");
        }

        string? urlFlag = null;
        string? modeText = null;
        var force = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--url" && i + 1 < args.Length)
            {
                urlFlag = args[++i];
            }
            else if (args[i] is "--force")
            {
                force = true;
            }
            else if (modeText is null && !args[i].StartsWith('-'))
            {
                modeText = args[i];
            }
            else
            {
                return Fail("Usage: JameJam divan sync [merge|pull|push] [--url <url>] [--force]");
            }
        }

        var mode = modeText?.Trim().ToLowerInvariant() switch
        {
            null or "" or "merge" or "sync" => SyncMode.Merge,
            "pull" => SyncMode.Pull,
            "push" => SyncMode.Push,
            _ => (SyncMode?)null,
        };
        if (mode is null)
        {
            return Fail($"Unknown sync mode '{modeText}'. Use merge (default), pull, or push.");
        }

        // URL precedence: --flag → environment → stored setting.
        var url = urlFlag
            ?? Environment.GetEnvironmentVariable(SyncDefaults.UrlEnvironmentVariable)
            ?? _syncUrlProvider?.Invoke();
        if (string.IsNullOrWhiteSpace(url))
        {
            return Fail(
                "No sync URL. Pass --url <url>, set JAMEJAM_SYNC_URL, or save it once: "
                + $"JameJam settings set {SettingKeys.DivanSyncUrl} <url>");
        }

        var options = new SyncOptions
        {
            Endpoint = url,
            BearerToken = Environment.GetEnvironmentVariable(SyncDefaults.TokenEnvironmentVariable),
        };
        try
        {
            options.Validate();
        }
        catch (SyncException ex)
        {
            return Fail(ex.Message);
        }

        var deviceId = _deviceIdProvider?.Invoke();
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            deviceId = Guid.CreateVersion7().ToString(); // ephemeral identity when no provider exists
        }

        var deviceName = _deviceNameProvider?.Invoke() ?? Environment.MachineName;
        var service = NewService();
        var adapter = new DivanSyncAdapter(service, _store, _clock);
        try
        {
            var run = await SyncEngine.RunAsync(
                _syncClientFactory(options),
                adapter,
                deviceId,
                DivanText.Clip(deviceName, DivanDefaults.MaxNotebookNameLength),
                mode.Value,
                force,
                _clock.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            _output.WriteLine(run.Describe());
            return 0;
        }
        catch (SyncException ex)
        {
            return Fail(ex.Message);
        }
    }

    private async Task<int> RunAiAsync(string[] args, CancellationToken cancellationToken)
    {
        if (_aiCompletion is null)
        {
            return Fail("AI is not available in this context.");
        }

        var service = NewService();
        switch (args[0])
        {
            case "summarize" when args.Length >= 2:
            {
                var note = service.GetNote(MoneyInt(args[1], "Note id"))
                    ?? throw new DivanException($"No note #{MoneyInt(args[1], "Note id")}.");
                var response = await _aiCompletion(
                    new AiRequest(_assistant.BuildSummarizePrompt(note)), cancellationToken).ConfigureAwait(false);
                _output.WriteLine(response.Content);
                return 0;
            }

            case "title" when args.Length >= 2:
            {
                var note = service.GetNote(MoneyInt(args[1], "Note id"))
                    ?? throw new DivanException($"No note #{MoneyInt(args[1], "Note id")}.");
                var response = await _aiCompletion(
                    new AiRequest(_assistant.BuildTitlePrompt(note)), cancellationToken).ConfigureAwait(false);
                var title = PadAssistant.ParseTitle(response.Content);
                if (title is null)
                {
                    return Fail("The AI proposed no usable title — try again or write one yourself.");
                }

                if (!args.Contains("--apply"))
                {
                    _output.WriteLine($"Suggestion: {title}  (re-run with --apply to set it)");
                    return 0;
                }

                _ = service.EditNote(note.Id, title: title);
                _output.WriteLine(FormattableString.Invariant($"#{note.Id} retitled: {title}. divan undo reverts it."));
                return 0;
            }

            case "tags" when args.Length >= 2:
            {
                var note = service.GetNote(MoneyInt(args[1], "Note id"))
                    ?? throw new DivanException($"No note #{MoneyInt(args[1], "Note id")}.");
                var known = service.AllTags();
                var response = await _aiCompletion(
                    new AiRequest(_assistant.BuildTagsPrompt(note, known)), cancellationToken).ConfigureAwait(false);
                var tags = PadAssistant.ParseTags(response.Content);
                if (tags.Count == 0)
                {
                    return Fail("The AI proposed no usable tags — try again or tag it yourself.");
                }

                if (!args.Contains("--apply"))
                {
                    _output.WriteLine($"Suggestion: {string.Join(", ", tags)}  (re-run with --apply to set them)");
                    return 0;
                }

                _ = service.EditNote(note.Id, tags: string.Join(',', tags));
                _output.WriteLine($"#{note.Id} tagged: {string.Join(", ", tags)}. divan undo reverts it.");
                return 0;
            }

            case "ask":
            {
                var question = ParseQuestion(args[1..]);
                if (question.Length == 0)
                {
                    return Fail("Ask a question: JameJam divan ai ask \"what did I write about the thesis?\"");
                }

                var context = service.List().Take(DivanDefaults.MaxAiContextNotes)
                    .Select(n => (n, DivanText.Snippet(n.Body, 200)))
                    .ToList();
                var response = await _aiCompletion(
                    new AiRequest(PadAssistant.BuildAskPrompt(question, context)), cancellationToken).ConfigureAwait(false);
                _output.WriteLine(response.Content);
                return 0;
            }

            default:
                return Fail("Usage: JameJam divan ai summarize <id> | ai title <id> [--apply] | ai tags <id> [--apply] | ai ask <question...>");
        }
    }

    // ── Helpers ──

    private int Help()
    {
        _output.WriteLine("Divan — your markdown pad, a registry of everything you write");
        _output.WriteLine();
        _output.WriteLine("  divan notebook add <name> | list | rename <id|name> <new> | archive <id> | remove <id> [--force]");
        _output.WriteLine("  divan new <title> [--notebook n] [--tags a,b] [--stdin]");
        _output.WriteLine("  divan list [--notebook n] [--tag t] [--q text] [--pinned] [--archived] [--checklists]");
        _output.WriteLine("  divan show <id> | edit <id> [--title t] [--tags a,b] [--stdin] | delete <id> | undo");
        _output.WriteLine("  divan append <id> [--stdin] | search <query...> | backlinks <id>");
        _output.WriteLine("  divan todos [--notebook n] | daily | pin <id> | unpin <id> | move <id> <notebook>");
        _output.WriteLine("  divan archive <id> | unarchive <id>");
        _output.WriteLine("  divan export <folder> | import <folder> | stats");
        _output.WriteLine("  divan sync [merge|pull|push] [--url <url>] [--force]   two-device sync (any server)");
        _output.WriteLine("  divan ai summarize <id> | ai title <id> [--apply] | ai tags <id> [--apply] | ai ask <question...>");
        return 0;
    }

    private DivanService NewService()
    {
        if (_store is null)
        {
            throw new DivanException(NoStorageMessage);
        }

        return new DivanService(_store, _clock, _options);
    }

    private bool InputIsRedirected() =>
        !ReferenceEquals(_stdin, Console.In) || Console.IsInputRedirected;

    private string ReadStdin(string label)
    {
        var text = _stdin.ReadToEnd();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new DivanException($"No {label} arrived on stdin.");
        }

        return text.TrimEnd();
    }

    private static string ReadInteractive(string label)
    {
        Console.Write(label);
        List<string> lines = [];
        while (Console.ReadLine() is { } line && line.Length > 0)
        {
            lines.Add(line);
        }

        if (lines.Count == 0)
        {
            throw new DivanException("No text arrived — type it, or pipe it with --stdin.");
        }

        return string.Join('\n', lines);
    }

    private static string ListLine(Note note)
    {
        var pin = note.Pinned ? "★ " : "  ";
        var archived = note.Archived ? "  [archived]" : string.Empty;
        var tags = note.Tags.Length == 0 ? string.Empty : $"  [{note.Tags}]";
        var checklist = DivanText.Checklist(note.Body);
        var progress = checklist.Count == 0
            ? string.Empty
            : FormattableString.Invariant($"  ☑ {checklist.Count(i => i.Done)}/{checklist.Count}");
        return FormattableString.Invariant($"#{note.Id} {pin}{note.Title}{tags}{progress}{archived}");
    }

    private static string ParseQuestion(string[] args)
    {
        List<string> parts = [];
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith('-'))
            {
                i++; // skip the flag's value; AI questions carry no flags today
                continue;
            }

            parts.Add(args[i]);
        }

        return string.Join(' ', parts);
    }

    private static int MoneyInt(string text, string label) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : throw new DivanException($"{label} must be a positive number.");

    private static FlagParser ParseFlags(string[] args, params string[] known)
    {
        var parser = new FlagParser(args, known);
        parser.Collect();
        return parser;
    }

    private int Fail(string message)
    {
        _error.WriteLine(message);
        return 1;
    }

    /// <summary>Strict flag parser: unknown options fail, flags consume the next non-dash token.</summary>
    private sealed class FlagParser(string[] args, string[] known)
    {
        private readonly Dictionary<string, string?> _values = [];

        public string? Get(string flag) => _values.GetValueOrDefault(flag);

        public bool Has(string flag) => _values.ContainsKey(flag);

        public void Collect()
        {
            for (var i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith('-'))
                {
                    continue;
                }

                if (!known.Contains(args[i]))
                {
                    throw new DivanException($"Unknown option '{args[i]}'.");
                }

                if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                {
                    _values[args[i]] = args[i + 1];
                    i++;
                }
                else
                {
                    _values[args[i]] = null;
                }
            }
        }
    }
}
