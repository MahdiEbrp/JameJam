using System.Globalization;
using System.Text;
using JameJam.HaftKhan.Ai;
using JameJam.Sync;
using JameJam.Soroush;

namespace JameJam.HaftKhan;

/// <summary>
/// CLI surface for the Haft Khan to-do list. All heavy lifting lives in
/// <see cref="HaftKhanService"/> (logic), <see cref="Backup"/> (portability), and the AI layer
/// (prompts + safety); this class only parses arguments, formats output, and maps failures
/// to exit codes.
/// </summary>
/// <param name="repository">Task storage; null disables Haft Khan (storage unavailable).</param>
/// <param name="clock">Time source for the service.</param>
/// <param name="output">Standard output.</param>
/// <param name="error">Error output.</param>
/// <param name="aiCompletion">Routes a prompt through the Soroush safety layer; null when AI is unavailable.</param>
/// <param name="options">Customizable limits for tasks and AI prompts; defaults apply when null.</param>
/// <param name="syncClientFactory">Creates the sync transport; defaults to the hardened HTTP client.</param>
/// <param name="syncUrlProvider">Resolves the stored default sync URL (usually the settings store).</param>
public sealed class HaftKhanCommands(
    ITaskRepository? repository,
    TimeProvider clock,
    TextWriter output,
    TextWriter error,
    Func<AiRequest, CancellationToken, Task<SoroushResult>>? aiCompletion,
    HaftKhanOptions? options = null,
    Func<SyncOptions, ISyncClient>? syncClientFactory = null,
    Func<string?>? syncUrlProvider = null)
{
    private const int PriorityColumnWidth = 8;

    private readonly TextWriter _output = output ?? throw new ArgumentNullException(nameof(output));
    private readonly TextWriter _error = error ?? throw new ArgumentNullException(nameof(error));
    private readonly HaftKhanOptions _options = options ?? new HaftKhanOptions();
    private readonly HaftKhanService? _service = repository is null
        ? null
        : new HaftKhanService(repository, clock, options);
    private readonly AiTaskAssistant _assistant = new(options);
    private readonly Func<AiRequest, CancellationToken, Task<SoroushResult>>? _aiCompletion = aiCompletion;
    private readonly Func<SyncOptions, ISyncClient> _syncClientFactory =
        syncClientFactory ?? (syncOptions => new HttpSyncClient(SoroushHttp.CreateClient(), syncOptions));
    private readonly Func<string?>? _syncUrlProvider = syncUrlProvider;

    /// <summary>The validated options in effect.</summary>
    public HaftKhanOptions Options => _options;

    /// <summary>
    /// Open tasks that carry a due date — feeds Anahita's weather-for-your-plans view.
    /// Null when task storage is unavailable.
    /// </summary>
    public IReadOnlyList<HaftKhanTask>? DueTasks() => _service?
        .List(TaskView.Open)
        .Where(static t => t.DueDate is not null)
        .ToList();

    /// <summary>
    /// Every open task in priority order — feeds Anahita's AI planning view.
    /// Null when task storage is unavailable.
    /// </summary>
    public IReadOnlyList<HaftKhanTask>? OpenTasks() => _service?.List(TaskView.Open);

    /// <summary>Runs a <c>haftkhan</c> subcommand. Returns the process exit code.</summary>
    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        _options.Validate();

        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            Help();
            return 0;
        }

        try
        {
            return args[0] switch
            {
                "add" => Add(args[1..]),
                "list" => List(args[1..]),
                "find" => Find(args[1..]),
                "show" => Show(args[1..]),
                "start" => Start(args[1..]),
                "done" => Done(args[1..]),
                "remove" => Remove(args[1..]),
                "link" => Link(args[1..]),
                "unlink" => Unlink(args[1..]),
                "clear-done" => ClearDone(),
                "stats" => Stats(),
                "board" => Board(),
                "matrix" => Matrix(),
                "focus" => Focus(),
                "review" => Review(),
                "undo" => Undo(),
                "export" => Export(args[1..]),
                "import" => Import(args[1..]),
                "sync" => await RunSyncAsync(args[1..], cancellationToken).ConfigureAwait(false),
                "ai" => await RunAiAsync(args[1..], cancellationToken).ConfigureAwait(false),
                _ => Fail($"Unknown haftkhan command '{args[0]}'. Run 'JameJam haftkhan help'."),
            };
        }
        catch (TaskNotFoundException ex)
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
        catch (SyncException ex)
        {
            return Fail(ex.Message);
        }
    }

    private int Add(string[] args)
    {
        string? notes = null;
        string? due = null;
        string? priority = null;
        string? project = null;
        string? tags = null;
        string? effort = null;
        string? recurrence = null;
        string? interval = null;
        string? blockedBy = null;
        List<string> titleParts = [];

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--notes" when i + 1 < args.Length:
                    notes = args[++i];
                    break;
                case "--due" when i + 1 < args.Length:
                    due = args[++i];
                    break;
                case "--priority" when i + 1 < args.Length:
                    priority = args[++i];
                    break;
                case "--project" when i + 1 < args.Length:
                    project = args[++i];
                    break;
                case "--tags" when i + 1 < args.Length:
                    tags = args[++i];
                    break;
                case "--effort" when i + 1 < args.Length:
                    effort = args[++i];
                    break;
                case "--every" when i + 1 < args.Length:
                    recurrence = args[++i];
                    break;
                case "--interval" when i + 1 < args.Length:
                    interval = args[++i];
                    break;
                case "--after" when i + 1 < args.Length:
                    blockedBy = args[++i];
                    break;
                default:
                    titleParts.Add(args[i]);
                    break;
            }
        }

        var title = string.Join(' ', titleParts);
        if (title.Trim().Length == 0)
        {
            _error.WriteLine("Usage: JameJam haftkhan add <title> [--notes \"...\"] [--due <date|natural>] [--priority low|normal|high|critical] [--project name] [--tags a,b] [--effort s|m|l|xl] [--every daily|weekly|monthly] [--interval N] [--after ids]");
            return 1;
        }

        var task = Service().AddTask(
            title, notes, priority, due, project, tags, effort, recurrence, interval,
            ParseBlockers(blockedBy));
        _output.WriteLine(FormattableString.Invariant($"Added #{task.Id}: {task.Title}"));
        return 0;
    }

    private int List(string[] args)
    {
        var positional = new List<string>();
        string? tag = null;
        string? project = null;
        string? priority = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--tag" when i + 1 < args.Length:
                    tag = args[++i];
                    break;
                case "--project" when i + 1 < args.Length:
                    project = args[++i];
                    break;
                case "--priority" when i + 1 < args.Length:
                    priority = args[++i];
                    break;
                default:
                    positional.Add(args[i]);
                    break;
            }
        }

        var tasks = Service().ListFiltered(
            TaskGuard.ParseView(positional.Count > 0 ? positional[0] : null), tag, project, priority);
        if (tasks.Count == 0)
        {
            _output.WriteLine("No tasks in this view.");
            return 0;
        }

        var today = Today();
        foreach (var task in tasks)
        {
            _output.WriteLine(Format(task, today, BlockedMap()));
        }

        return 0;
    }

    private int Find(string[] args)
    {
        if (args.Length == 0)
        {
            _error.WriteLine("Usage: JameJam haftkhan find <text>  (searches titles, notes, projects, and tags)");
            return 1;
        }

        var tasks = Service().Search(string.Join(' ', args));
        if (tasks.Count == 0)
        {
            _output.WriteLine("No matching tasks.");
            return 0;
        }

        var today = Today();
        var blocked = BlockedMap();
        foreach (var task in tasks)
        {
            _output.WriteLine(Format(task, today, blocked));
        }

        return 0;
    }

    private int Show(string[] args)
    {
        var task = Service().Find(args.Length > 0 ? args[0] : null);
        var today = Today();
        _output.WriteLine(Format(task, today, BlockedMap()));
        if (task.Notes.Length > 0)
            _output.WriteLine(task.Notes);
        return 0;
    }

    private int Start(string[] args)
    {
        var task = Service().Start(args.Length > 0 ? args[0] : null);
        _output.WriteLine(FormattableString.Invariant($"Started #{task.Id}: {task.Title}"));
        return 0;
    }

    private int Done(string[] args)
    {
        var force = args.Contains("--force", StringComparer.Ordinal);
        var positional = args.Where(arg => arg != "--force").ToArray();
        var result = Service().Complete(positional.Length > 0 ? positional[0] : null, force);
        _output.WriteLine(FormattableString.Invariant($"Conquered #{result.Completed.Id}: {result.Completed.Title}"));
        if (result.Next is not null)
        {
            _output.WriteLine(FormattableString.Invariant(
                $"Respawned #{result.Next.Id}: {result.Next.Title} (due {result.Next.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)})"));
        }

        return 0;
    }

    private int Remove(string[] args)
    {
        var task = Service().Find(args.Length > 0 ? args[0] : null);
        Service().Remove(args.Length > 0 ? args[0] : null);
        _output.WriteLine(FormattableString.Invariant($"Removed #{task.Id}: {task.Title}"));
        return 0;
    }

    private int Link(string[] args)
    {
        if (args.Length < 3 || args[1] != "--after")
        {
            _error.WriteLine("Usage: JameJam haftkhan link <id> --after <blocker-ids e.g. 2,3>");
            return 1;
        }

        Service().AddLink(args[0], args[2]);
        _output.WriteLine(FormattableString.Invariant($"#{args[0]} is now blocked by {args[2]}."));
        return 0;
    }

    private int Unlink(string[] args)
    {
        var task = Service().Find(args.Length > 0 ? args[0] : null);
        Service().RemoveLinks(args.Length > 0 ? args[0] : null);
        _output.WriteLine(FormattableString.Invariant($"Removed all links of #{task.Id}."));
        return 0;
    }

    private int ClearDone()
    {
        var count = Service().ClearCompleted();
        _output.WriteLine(count == 1 ? "Cleared 1 completed task." : FormattableString.Invariant($"Cleared {count} completed tasks."));
        return 0;
    }

    private int Stats()
    {
        var report = Service().Report();
        _output.WriteLine(FormattableString.Invariant(
            $"Haft Khan — {report.Todo + report.Doing + report.Done} task(s): {report.Todo} to do, {report.Doing} doing, {report.Done} done, {report.Overdue} overdue."));
        _output.WriteLine(FormattableString.Invariant(
            $"Completions — today: {report.DoneToday}, last 7 days: {report.DoneLast7Days} | streak: {report.CurrentStreak} day(s) (best {report.BestStreak})."));
        return 0;
    }

    private int Board()
    {
        var today = Today();
        var blocked = BlockedMap();
        foreach (var column in Service().Board())
        {
            _output.WriteLine(FormattableString.Invariant($"── {column.Title} {'─',-4}"));
            if (column.Tasks.Count == 0)
            {
                _output.WriteLine("  (empty)");
                continue;
            }

            foreach (var task in column.Tasks)
            {
                _output.WriteLine($"  {Format(task, today, blocked)}");
            }
        }

        return 0;
    }

    private int Matrix()
    {
        var today = Today();
        var blocked = BlockedMap();
        foreach (var quadrant in Service().Matrix())
        {
            _output.WriteLine(FormattableString.Invariant($"── {quadrant.Title} ──"));
            if (quadrant.Tasks.Count == 0)
            {
                _output.WriteLine("  (empty)");
                continue;
            }

            foreach (var task in quadrant.Tasks)
            {
                _output.WriteLine($"  {Format(task, today, blocked)}");
            }
        }

        return 0;
    }

    private int Focus()
    {
        var focus = Service().NextFocus();
        if (focus is null)
        {
            _output.WriteLine("Nothing focusable — every open task is blocked, or the list is empty.");
            return 0;
        }

        _output.WriteLine(FormattableString.Invariant($"FOCUS → #{focus.Id} [{focus.Priority.ToString().ToLowerInvariant()}] {focus.Title}"));
        if (focus.Notes.Length > 0)
            _output.WriteLine(focus.Notes);

        _output.WriteLine("Ask the AI for a plan: JameJam haftkhan ai breakdown " + focus.Id);
        return 0;
    }

    private int Review()
    {
        var report = Service().Report();
        _output.WriteLine("── Haft Khan weekly review ──");
        _output.WriteLine(FormattableString.Invariant(
            $"Open: {report.Todo + report.Doing} ({report.Overdue} overdue) | done today: {report.DoneToday} | done this week: {report.DoneLast7Days}"));
        _output.WriteLine(FormattableString.Invariant(
            $"Streak: {report.CurrentStreak} day(s) (best {report.BestStreak})"));
        if (report.Focus.Count > 0)
        {
            _output.WriteLine("Focus next:");
            foreach (var task in report.Focus)
            {
                _output.WriteLine($"  #{task.Id} [{task.Priority.ToString().ToLowerInvariant()}] {task.Title}");
            }
        }
        else
        {
            _output.WriteLine("Nothing focusable — enjoy the peace.");
        }

        return 0;
    }

    private int Undo()
    {
        var result = Service().Undo();
        var message = result.RestoredTasks == 1
            ? FormattableString.Invariant($"Reverted '{result.Operation}' (1 task affected).")
            : FormattableString.Invariant($"Reverted '{result.Operation}' ({result.RestoredTasks} tasks affected).");
        _output.WriteLine(message);
        return 0;
    }

    private int Export(string[] args)
    {
        if (args.Length != 1)
        {
            _error.WriteLine("Usage: JameJam haftkhan export <file.json | file.md>");
            return 1;
        }

        var path = args[0];
        if (string.IsNullOrWhiteSpace(path))
            return Fail("Export path must not be empty.");

        var (tasks, dependencies) = Service().ExportData();
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var backup = new Backup.BackupFile(
                Backup.CurrentVersion,
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                [.. tasks.Select(Backup.ToDto)],
                [.. dependencies.Select(link => new Backup.DependencyDto(link.TaskId, link.DependsOnId))]);
            File.WriteAllText(path, Backup.ToJson(backup));
        }
        else if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllText(path, Backup.ToMarkdown(tasks, Today()));
        }
        else
        {
            return Fail("Export format is picked by the extension: use .json or .md.");
        }

        _output.WriteLine(FormattableString.Invariant($"Exported {tasks.Count} task(s) to {path}."));
        return 0;
    }

    private int Import(string[] args)
    {
        var replace = args.Contains("--replace", StringComparer.Ordinal);
        var positional = args.Where(arg => arg != "--replace").ToArray();
        if (positional.Length != 1)
        {
            _error.WriteLine("Usage: JameJam haftkhan import <backup.json> [--replace]");
            return 1;
        }

        var path = positional[0];
        if (!File.Exists(path))
            return Fail($"File not found: {path}.");

        Backup.BackupFile backup;
        try
        {
            backup = Backup.FromJson(File.ReadAllText(path));
        }
        catch (ArgumentException ex)
        {
            return Fail(ex.Message);
        }

        var (tasks, links) = Service().Import(backup, replace);
        _output.WriteLine(FormattableString.Invariant($"Imported {tasks} task(s), {links} link(s)."));
        return 0;
    }

    private async Task<int> RunSyncAsync(string[] args, CancellationToken cancellationToken)
    {
        if (_service is null)
            return Fail("Haft Khan storage is not available in this context.");

        string? urlFlag = null;
        string? modeText = null;
        var force = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--url" when i + 1 < args.Length:
                    urlFlag = args[++i];
                    break;
                case "--mode" when i + 1 < args.Length:
                    modeText = args[++i];
                    break;
                case "--force":
                    force = true;
                    break;
                default:
                    _error.WriteLine("Usage: JameJam haftkhan sync [--url <custom-url>] [--mode sync|pull|push] [--force]");
                    return 1;
            }
        }

        SyncMode? mode = modeText?.Trim().ToLowerInvariant() switch
        {
            null or "" or "sync" or "merge" => SyncMode.Merge,
            "pull" => SyncMode.Pull,
            "push" => SyncMode.Push,
            _ => (SyncMode?)null,
        };
        if (mode is null)
            return Fail($"Unknown sync mode '{modeText}'. Use sync (default), pull, or push.");

        // URL precedence: --flag → environment → stored setting.
        var url = urlFlag
            ?? Environment.GetEnvironmentVariable(SyncDefaults.UrlEnvironmentVariable)
            ?? _syncUrlProvider?.Invoke();
        if (string.IsNullOrWhiteSpace(url))
        {
            _error.WriteLine(
                "No sync URL. Pass --url <custom-url>, set JAMEJAM_SYNC_URL, or save it once: "
                + $"JameJam settings set {SyncDefaults.UrlSettingKey} <url>");
            return 1;
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

        try
        {
            var report = await _service.SyncAsync(_syncClientFactory(options), mode.Value, force, cancellationToken)
                .ConfigureAwait(false);
            _output.WriteLine(report.Describe());
            return 0;
        }
        catch (SyncException ex)
        {
            return Fail(ex.Message);
        }
    }

    private async Task<int> RunAiAsync(string[] args, CancellationToken cancellationToken)
    {
        if (_service is null)
            return Fail("Haft Khan storage is not available in this context.");
        if (_aiCompletion is null)
            return Fail("AI is not available in this context.");

        var subcommand = args.Length > 0 ? args[0] : "help";
        switch (subcommand)
        {
            case "breakdown" when args.Length >= 2:
            {
                var task = _service.Find(args[1]);
                var result = await _aiCompletion(
                    new AiRequest(_assistant.BuildBreakdownPrompt(task)), cancellationToken).ConfigureAwait(false);
                var plan = AiTaskAssistant.ParsePlan(result.Content);
                _output.WriteLine(FormattableString.Invariant($"Plan for #{task.Id} — {task.Title}"));
                foreach (var (step, index) in plan.Steps.Select((step, index) => (step, index)))
                {
                    _output.WriteLine(FormattableString.Invariant($"  {index + 1}. {step}"));
                }

                return 0;
            }

            case "summary":
            {
                var openTasks = _service.List(TaskView.Open);
                if (openTasks.Count == 0)
                {
                    _output.WriteLine("Nothing open — enjoy the peace.");
                    return 0;
                }

                var result = await _aiCompletion(
                    new AiRequest(_assistant.BuildSummaryPrompt(openTasks)), cancellationToken).ConfigureAwait(false);
                _output.WriteLine(result.Content);
                return 0;
            }

            default:
                _error.WriteLine("Usage: JameJam haftkhan ai breakdown <id> | ai summary");
                return 1;
        }
    }

    private HaftKhanService Service() =>
        _service ?? throw new InvalidOperationException("Haft Khan storage is not available in this context.");

    private DateOnly Today() => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    private Dictionary<long, List<long>>? _blockedMapCache;

    private Dictionary<long, List<long>> BlockedMap() =>
        _blockedMapCache ??= BuildBlockedMap();

    private Dictionary<long, List<long>> BuildBlockedMap()
    {
        var openIds = Service().List(TaskView.Open).Select(task => task.Id).ToHashSet();
        Dictionary<long, List<long>> map = [];
        foreach (var link in Service().ExportData().Dependencies)
        {
            if (!openIds.Contains(link.TaskId))
                continue;

            if (!map.TryGetValue(link.TaskId, out var blockers))
                map[link.TaskId] = blockers = [];

            blockers.Add(link.DependsOnId);
        }

        return map;
    }

    private static IReadOnlyList<long> ParseBlockers(string? csv) => TaskGuard.ParseIdList(csv);

    private static string Format(HaftKhanTask task, DateOnly today, Dictionary<long, List<long>> blocked)
    {
        StringBuilder line = new();
        line.Append(CultureInfo.InvariantCulture, $"#{task.Id} [{StateGlyph(task.State)}] ");
        line.Append(CultureInfo.InvariantCulture, $"{task.Priority.ToString().ToLowerInvariant(),-PriorityColumnWidth} ");
        line.Append(task.Title);

        if (task.DueDate.HasValue)
        {
            var due = task.DueDate.Value;
            var label = due < today
                ? FormattableString.Invariant($"  [overdue since {due:yyyy-MM-dd}]")
                : due == today
                    ? "  [due today]"
                    : FormattableString.Invariant($"  [due {due:yyyy-MM-dd}]");
            line.Append(label);
        }

        if (task.Project.Length > 0)
            line.Append(CultureInfo.InvariantCulture, $"  (@{task.Project})");

        if (task.Tags.Count > 0)
        {
            line.Append(CultureInfo.InvariantCulture, $"  {string.Join(' ', task.Tags.Select(tag => $"#{tag}"))}");
        }

        if (task.Effort != TaskEffort.None)
            line.Append(CultureInfo.InvariantCulture, $"  [effort: {task.Effort.ToString().ToLowerInvariant()}]");

        if (task.Recurrence != RecurrenceKind.None)
        {
            line.Append(task.RecurrenceInterval == 1
                ? FormattableString.Invariant($"  [every {task.Recurrence.ToString().ToLowerInvariant()}]")
                : FormattableString.Invariant($"  [every {task.RecurrenceInterval} {task.Recurrence.ToString().ToLowerInvariant()}]"));
        }

        if (blocked.TryGetValue(task.Id, out var blockers) && blockers.Count > 0)
            line.Append(CultureInfo.InvariantCulture, $"  [blocked by {string.Join(',', blockers.Select(id => $"#{id}"))}]");

        return line.ToString();
    }

    private static string StateGlyph(TaskState state) => state switch
    {
        TaskState.Todo => " ",
        TaskState.Doing => "~",
        TaskState.Done => "x",
        _ => "?",
    };

    private void Help()
    {
        _output.WriteLine("Haft Khan — advanced to-do list (every task is a labour to conquer)");
        _output.WriteLine();
        _output.WriteLine("Tasks:");
        _output.WriteLine("  add <title> [--notes \"...\"] [--due <date|natural>] [--priority low|normal|high|critical]");
        _output.WriteLine("       [--project name] [--tags a,b] [--effort s|m|l|xl] [--every daily|weekly|monthly]");
        _output.WriteLine("       [--interval N] [--after <ids>]          (dates: 2026-10-01, tomorrow, next monday, in 3 days)");
        _output.WriteLine("  list [open|all|done|today|overdue] [--tag t] [--project p] [--priority min]");
        _output.WriteLine("  find <text>                                   Search titles, notes, projects, tags");
        _output.WriteLine("  show <id>");
        _output.WriteLine("  start <id> / done <id> [--force] / remove <id>");
        _output.WriteLine("  link <id> --after <ids> / unlink <id>         Dependencies (a blocked task refuses done)");
        _output.WriteLine("  clear-done / undo");
        _output.WriteLine("Views:");
        _output.WriteLine("  board / matrix / focus / stats / review");
        _output.WriteLine("Data:");
        _output.WriteLine("  export <file.json | file.md> / import <file.json> [--replace]");
        _output.WriteLine("  sync [--url <custom-url>] [--mode sync|pull|push] [--force]");
        _output.WriteLine("       Remote sync over HTTPS (loopback HTTP allowed). Token: JAMEJAM_SYNC_TOKEN env var.");
        _output.WriteLine("AI:");
        _output.WriteLine("  ai breakdown <id> / ai summary");
    }

    private int Fail(string message)
    {
        _error.WriteLine(message);
        return 1;
    }
}
