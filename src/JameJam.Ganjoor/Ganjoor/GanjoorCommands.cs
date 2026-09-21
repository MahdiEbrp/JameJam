using System.Globalization;

using JameJam.Ganjoor.Ai;
using JameJam.Soroush;

namespace JameJam.Ganjoor;

/// <summary>
/// CLI surface for the Ganjoor wallet. Argument parsing, formatting, exit codes —
/// all logic lives in <see cref="GanjoorService"/>, <see cref="GanjoorBackup"/>,
/// and the AI layer (<see cref="FinanceAssistant"/>).
/// </summary>
/// <param name="store">Wallet storage; null disables Ganjoor (storage unavailable).</param>
/// <param name="clock">Time source.</param>
/// <param name="output">Standard output.</param>
/// <param name="error">Error output.</param>
/// <param name="aiCompletion">Routes a prompt through the Soroush safety layer; null when AI is unavailable.</param>
/// <param name="options">Customizable rails; defaults apply when null.</param>
/// <param name="currencyProvider">Resolves the stored base currency (usually the settings store).</param>
public sealed class GanjoorCommands(
    IGanjoorStore? store,
    TimeProvider clock,
    TextWriter output,
    TextWriter error,
    Func<AiRequest, CancellationToken, Task<SoroushResult>>? aiCompletion = null,
    GanjoorOptions? options = null,
    Func<string?>? currencyProvider = null)
{
    private const string NoStorageMessage = "Wallet storage is not available in this context.";

    private readonly TextWriter _output = output ?? throw new ArgumentNullException(nameof(output));
    private readonly TextWriter _error = error ?? throw new ArgumentNullException(nameof(error));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly Func<AiRequest, CancellationToken, Task<SoroushResult>>? _aiCompletion = aiCompletion;
    private readonly Func<string?>? _currencyProvider = currencyProvider;
    private readonly GanjoorOptions _options = options ?? new GanjoorOptions();

    /// <summary>Runs a <c>ganjoor</c> subcommand. Returns the process exit code.</summary>
    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        var effective = EffectiveOptions();
        effective.Validate();
        if (store is null && args.Length > 0 && args[0] is not ("help" or "--help" or "-h"))
            return Fail(NoStorageMessage);

        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            Help();
            return 0;
        }

        try
        {
            return args[0] switch
            {
                "account" => RunAccount(args[1..], effective),
                "spend" => RunRecord(args[1..], effective, income: false),
                "earn" => RunRecord(args[1..], effective, income: true),
                "transfer" => RunTransfer(args[1..], effective),
                "list" => RunList(args[1..], effective),
                "show" => RunShow(args[1..], effective),
                "delete" => RunDelete(args[1..], effective),
                "budget" => RunBudget(args[1..], effective),
                "bill" => RunBill(args[1..], effective),
                "goal" => RunGoal(args[1..], effective),
                "debt" => RunDebt(args[1..], effective),
                "report" => RunReport(args[1..], effective),
                "networth" => RunNetWorth(effective),
                "undo" => RunUndo(effective),
                "export" => RunExport(args[1..], effective),
                "import" => RunImport(args[1..], effective),
                "import-csv" => RunImportCsv(args[1..], effective),
                "ai" when args.Length >= 2 => await RunAiAsync(args[1..], effective, cancellationToken).ConfigureAwait(false),
                "ai" => Fail("Usage: JameJam ganjoor ai insights [--month yyyy-MM] | ai categorize <id> [--apply] | ai ask <question...>"),
                _ => Fail($"Unknown ganjoor command '{args[0]}'. Run 'JameJam ganjoor help'."),
            };
        }
        catch (GanjoorException ex)
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

    // ── Command implementations ──

    private int RunAccount(string[] args, GanjoorOptions effective)
    {
        var service = NewService(effective);
        var sub = args.Length > 0 ? args[0] : "list";
        switch (sub)
        {
            case "add" when args.Length >= 2:
            {
                var parsed = ParseFlags(args[2..], "--currency", "--start");
                var account = service.AddAccount(args[1], parsed.Get("--currency"), parsed.Get("--start"));
                _output.WriteLine(FormattableString.Invariant(
                    $"Added account #{account.Id}: {account.Name} ({account.Currency}), starting {MoneyGuard.Money(account.InitialBalance, account.Currency)}"));
                return 0;
            }

            case "list":
                foreach (var account in service.Accounts(includeArchived: true))
                {
                    _output.WriteLine(FormattableString.Invariant(
                        $"#{account.Id}  {account.Name} ({account.Currency}) — {MoneyGuard.Money(service.Balance(account), account.Currency)}{(account.IsArchived ? "  [archived]" : string.Empty)}"));
                }

                return 0;

            case "rename" when args.Length >= 3:
            {
                var account = service.RenameAccount(args[1], args[2]);
                _output.WriteLine($"Renamed to {account.Name}.");
                return 0;
            }

            case "archive" when args.Length >= 2:
            {
                var account = service.ArchiveAccount(args[1], archived: true);
                _output.WriteLine($"Archived {account.Name}.");
                return 0;
            }

            case "unarchive" when args.Length >= 2:
            {
                var account = service.ArchiveAccount(args[1], archived: false);
                _output.WriteLine($"Unarchived {account.Name}.");
                return 0;
            }

            case "remove" when args.Length >= 2:
            {
                var removed = service.RemoveAccount(args[1], args.Contains("--force"));
                _output.WriteLine($"Account removed ({removed} transaction(s) deleted).");
                return 0;
            }

            default:
                _error.WriteLine("Usage: JameJam ganjoor account add <name> [--currency XYZ] [--start <amount>] | "
                    + "list | rename <id> <name> | archive <id> | unarchive <id> | remove <id> [--force]");
                return 1;
        }
    }

    private int RunRecord(string[] args, GanjoorOptions effective, bool income)
    {
        if (args.Length < 3)
        {
            _error.WriteLine($"Usage: JameJam ganjoor {(income ? "earn" : "spend")} <account> <amount> <category> "
                + "[--tags a,b] [--notes text] [--date yyyy-MM-dd]");
            return 1;
        }

        var parsed = ParseFlags(args[3..], "--tags", "--notes", "--date");
        var service = NewService(effective);
        var tx = service.Record(args[0], args[1], args[2], income, parsed.Get("--tags"), parsed.Get("--notes"), parsed.Get("--date"));
        var account = RequireAccount(tx.AccountId, service);
        _output.WriteLine(FormattableString.Invariant(
            $"{(income ? '+' : '−')}{MoneyGuard.Money(tx.Amount)} {tx.Category} — {account.Name} · balance {MoneyGuard.Money(service.Balance(account), account.Currency)}"));

        WarnOnBudget(service, tx);
        return 0;
    }

    private int RunTransfer(string[] args, GanjoorOptions effective)
    {
        if (args.Length < 3)
        {
            _error.WriteLine("Usage: JameJam ganjoor transfer <from> <to> <amount> [--notes text] [--date yyyy-MM-dd]");
            return 1;
        }

        var parsed = ParseFlags(args[3..], "--notes", "--date");
        var service = NewService(effective);
        var tx = service.Transfer(args[0], args[1], args[2], parsed.Get("--notes"), parsed.Get("--date"));
        _output.WriteLine(FormattableString.Invariant(
            $"Transferred {MoneyGuard.Money(tx.Amount, RequireAccount(tx.AccountId, service).Currency)} — {RequireAccount(tx.AccountId, service).Name} → {RequireAccount(tx.TransferToAccountId ?? 0, service).Name}"));
        return 0;
    }

    private int RunList(string[] args, GanjoorOptions effective)
    {
        var parsed = ParseFlags(args, "--account", "--category", "--tag", "--month", "--kind", "--q");
        var service = NewService(effective);
        var filter = new GanjoorFilter(
            AccountId: parsed.Get("--account") is { } accountText ? ResolveAccountId(service, accountText) : null,
            Category: parsed.Get("--category"),
            Tag: parsed.Get("--tag"),
            Month: parsed.Get("--month") is { } monthText ? MoneyGuard.Month(monthText) : null,
            Kind: parsed.Get("--kind") is { } kindText ? MoneyGuard.Kind(kindText) : null,
            Query: parsed.Get("--q"));
        var rows = service.Transactions(filter);
        if (rows.Count == 0)
        {
            _output.WriteLine("No transactions match.");
            return 0;
        }

        foreach (var tx in rows)
        {
            var account = RequireAccount(tx.AccountId, service);
            var label = tx.Kind switch
            {
                GanjoorTxKind.Income => FormattableString.Invariant($"+{MoneyGuard.Money(tx.Amount)}"),
                GanjoorTxKind.Expense => FormattableString.Invariant($"−{MoneyGuard.Money(tx.Amount)}"),
                _ => FormattableString.Invariant(
                    $"⇄ {MoneyGuard.Money(tx.Amount)} → {RequireAccount(tx.TransferToAccountId ?? 0, service).Name}"),
            };
            var tags = tx.Tags.Count > 0 ? $"  #{string.Join(" #", tx.Tags)}" : string.Empty;
            var notes = tx.Notes.Length > 0 ? $"  ({tx.Notes})" : string.Empty;
            _output.WriteLine(FormattableString.Invariant(
                $"#{tx.Id} {tx.Date:yyyy-MM-dd} {label} {tx.Category} [{account.Name}]{tags}{notes}"));
        }

        return 0;
    }

    private int RunShow(string[] args, GanjoorOptions effective)
    {
        if (args.Length < 1)
            return Fail("Usage: JameJam ganjoor show <id>");

        var service = NewService(effective);
        var tx = service.Transactions().FirstOrDefault(t => t.Id == MoneyGuard.Id(args[0], "Transaction id"))
            ?? throw new GanjoorException($"No transaction #{MoneyGuard.Id(args[0], "Transaction id")}.");
        var account = RequireAccount(tx.AccountId, service);
        _output.WriteLine(FormattableString.Invariant(
            $"#{tx.Id} — {tx.Kind} {MoneyGuard.Money(tx.Amount, account.Currency)} in {tx.Category}"));
        _output.WriteLine(FormattableString.Invariant(
            $"  Account: {account.Name} · Date: {tx.Date:yyyy-MM-dd} · Recorded: {tx.CreatedAt:yyyy-MM-dd HH:mm} UTC"));
        if (tx.TransferToAccountId is { } to)
            _output.WriteLine($"  → into {RequireAccount(to, service).Name}");

        if (tx.Tags.Count > 0)
            _output.WriteLine($"  Tags: {string.Join(", ", tx.Tags)}");

        if (tx.Notes.Length > 0)
            _output.WriteLine($"  Notes: {tx.Notes}");

        if (tx.FromBillId is { } bill)
            _output.WriteLine($"  From bill #{bill}");

        return 0;
    }

    private int RunDelete(string[] args, GanjoorOptions effective)
    {
        if (args.Length < 1)
            return Fail("Usage: JameJam ganjoor delete <id>");

        var tx = NewService(effective).DeleteTransaction(args[0]);
        _output.WriteLine($"Deleted #{tx.Id} ({tx.Category}). ganjoor undo brings it back.");
        return 0;
    }

    private int RunBudget(string[] args, GanjoorOptions effective)
    {
        var service = NewService(effective);
        var sub = args.Length > 0 ? args[0] : "list";
        switch (sub)
        {
            case "set" when args.Length >= 3:
            {
                var budget = service.SetBudget(args[1], args[2]);
                _output.WriteLine(FormattableString.Invariant(
                    $"Budget set: {budget.Category} ≤ {MoneyGuard.Money(budget.MonthlyLimit)} per month."));
                return 0;
            }

            case "remove" when args.Length >= 2:
            {
                var removed = service.RemoveBudget(args[1]);
                _output.WriteLine($"Budget removed: {removed.Category}.");
                return 0;
            }

            case "list":
            {
                var parsed = ParseFlags(args.Length > 1 ? args[1..] : [], "--month");
                var month = parsed.Get("--month") is { } text ? (DateOnly?)MoneyGuard.Month(text) : null;
                var statuses = service.BudgetStatuses(month);
                if (statuses.Count == 0)
                {
                    _output.WriteLine("No budgets set. ganjoor budget set <category> <limit> creates one.");
                    return 0;
                }

                foreach (var status in statuses)
                {
                    _output.WriteLine(FormattableString.Invariant(
                        $"{status.Category}: {MoneyGuard.Money(status.Spent)} / {MoneyGuard.Money(status.Limit)} ({status.PercentUsed}%){(status.Over ? "  ⚠ OVER" : status.PercentUsed >= GanjoorDefaults.BudgetWarnPercent ? "  ⚠ close" : string.Empty)}"));
                }

                return 0;
            }

            default:
                _error.WriteLine("Usage: JameJam ganjoor budget set <category> <limit> | list [--month yyyy-MM] | remove <category>");
                return 1;
        }
    }

    private int RunBill(string[] args, GanjoorOptions effective)
    {
        var service = NewService(effective);
        var sub = args.Length > 0 ? args[0] : "list";
        switch (sub)
        {
            case "add" when args.Length >= 4:
            {
                var parsed = ParseFlags(args[4..], "--category", "--every", "--interval", "--next", "--account");
                var bill = service.AddBill(
                    args[1], args[2], args[3],
                    parsed.Get("--category") ?? throw new GanjoorException("Bills need a category: --category <name>"),
                    parsed.Get("--every") ?? "monthly",
                    parsed.Get("--interval"),
                    parsed.Get("--next"),
                    parsed.Get("--account"));
                _output.WriteLine(FormattableString.Invariant(
                    $"Added bill #{bill.Id}: {bill.Name} {MoneyGuard.Money(bill.Amount)} {bill.Kind} {bill.Frequency} (next {bill.NextDue:yyyy-MM-dd})."));
                return 0;
            }

            case "list":
                foreach (var bill in service.DueBills(DateOnly.MaxValue))
                {
                    _output.WriteLine(FormattableString.Invariant(
                        $"#{bill.Id} {bill.Name} {MoneyGuard.Money(bill.Amount)} {bill.Kind} {bill.Category} — {bill.Frequency} ×{bill.Interval}, next {bill.NextDue:yyyy-MM-dd}"));
                }

                return 0;

            case "due":
            {
                var due = service.DueBills();
                if (due.Count == 0)
                {
                    _output.WriteLine($"No bills due in the next {GanjoorDefaults.BillHorizonDays} day(s).");
                    return 0;
                }

                foreach (var bill in due)
                {
                    _output.WriteLine(FormattableString.Invariant(
                        $"⚠ #{bill.Id} {bill.Name} {MoneyGuard.Money(bill.Amount)} was due {bill.NextDue:yyyy-MM-dd}"));
                }

                return 0;
            }

            case "apply":
            {
                var result = service.ApplyDueBills();
                if (result.Transactions.Count == 0)
                {
                    _output.WriteLine("Nothing was due.");
                    return 0;
                }

                foreach (var tx in result.Transactions)
                {
                    _output.WriteLine(FormattableString.Invariant(
                        $"Recorded #{tx.Id}: {(tx.Kind == GanjoorTxKind.Income ? '+' : '−')}{MoneyGuard.Money(tx.Amount)} {tx.Category} ({tx.Date:yyyy-MM-dd})"));
                }

                return 0;
            }

            case "remove" when args.Length >= 2:
            {
                var bill = service.RemoveBill(args[1]);
                _output.WriteLine($"Removed bill #{bill.Id} ({bill.Name}).");
                return 0;
            }

            default:
                _error.WriteLine("Usage: JameJam ganjoor bill add <name> <amount> <in|out> --category <c> --every daily|weekly|monthly|yearly "
                    + "[--interval N] [--next yyyy-MM-dd] [--account <id>] | list | due | apply | remove <id>");
                return 1;
        }
    }

    private int RunGoal(string[] args, GanjoorOptions effective)
    {
        var service = NewService(effective);
        var sub = args.Length > 0 ? args[0] : "list";
        switch (sub)
        {
            case "add" when args.Length >= 3:
            {
                var parsed = ParseFlags(args[3..], "--by");
                var goal = service.AddGoal(args[1], args[2], parsed.Get("--by"));
                _output.WriteLine(FormattableString.Invariant(
                    $"Goal #{goal.Id}: {goal.Name} — save {MoneyGuard.Money(goal.Target)}{(goal.Deadline is { } by ? $" by {by:yyyy-MM-dd}" : string.Empty)}."));
                return 0;
            }

            case "contribute" when args.Length >= 3:
            {
                var goal = service.Contribute(args[1], args[2]);
                _output.WriteLine(FormattableString.Invariant(
                    $"{goal.Name}: {MoneyGuard.Money(goal.Contributed)} / {MoneyGuard.Money(goal.Target)} ({Percent(goal.Contributed, goal.Target)}%){(Percent(goal.Contributed, goal.Target) >= GanjoorDefaults.GoalNearlyDonePercent ? "  ✨ almost there" : string.Empty)}"));
                return 0;
            }

            case "withdraw" when args.Length >= 3:
            {
                var goal = service.Withdraw(args[1], args[2]);
                _output.WriteLine(FormattableString.Invariant(
                    $"{goal.Name}: {MoneyGuard.Money(goal.Contributed)} / {MoneyGuard.Money(goal.Target)}"));
                return 0;
            }

            case "remove" when args.Length >= 2:
            {
                var goal = service.RemoveGoal(args[1]);
                _output.WriteLine($"Removed goal '{goal.Name}'.");
                return 0;
            }

            case "list":
                if (service.Goals().Count == 0)
                {
                    _output.WriteLine("No goals yet. ganjoor goal add <name> <target> starts one.");
                    return 0;
                }

                foreach (var goal in service.Goals())
                {
                    var bar = new string('█', Percent(goal.Contributed, goal.Target) / 10)
                        + new string('░', 10 - (Percent(goal.Contributed, goal.Target) / 10));
                    _output.WriteLine(FormattableString.Invariant(
                        $"#{goal.Id} {goal.Name} [{bar}] {Percent(goal.Contributed, goal.Target)}% — {MoneyGuard.Money(goal.Contributed)} of {MoneyGuard.Money(goal.Target)}{(goal.Deadline is { } by ? $" by {by:yyyy-MM-dd}" : string.Empty)}"));
                }

                return 0;

            default:
                _error.WriteLine("Usage: JameJam ganjoor goal add <name> <target> [--by yyyy-MM-dd] | list | "
                    + "contribute <id> <amount> | withdraw <id> <amount> | remove <id>");
                return 1;
        }
    }

    private int RunDebt(string[] args, GanjoorOptions effective)
    {
        var service = NewService(effective);
        var sub = args.Length > 0 ? args[0] : "list";
        switch (sub)
        {
            case "add" when args.Length >= 3:
            {
                var parsed = ParseFlags(args[3..], "--due", "--notes", "--i-owe", "--owes-me");
                var owedByMe = args.Contains("--i-owe");
                var owesMe = args.Contains("--owes-me");
                if (owedByMe == owesMe)
                    return Fail("Say who owes whom: --i-owe or --owes-me.");

                var debt = service.AddDebt(args[1], args[2], owedByMe, parsed.Get("--due"), parsed.Get("--notes"));
                _output.WriteLine(FormattableString.Invariant(
                    $"Debt #{debt.Id}: {(debt.OwedByMe ? "you owe" : "owed by")} {debt.Person} — {MoneyGuard.Money(debt.Amount)} {effective.DefaultCurrency}{(debt.DueDate is { } by ? $" (by {by:yyyy-MM-dd})" : string.Empty)}."));

                return 0;
            }

            case "settle" when args.Length >= 3:
            {
                var debt = service.SettleDebt(args[1], args[2]);
                if (debt.Settled >= debt.Amount)
                {
                    _output.WriteLine(FormattableString.Invariant($"Debt #{debt.Id} ({debt.Person}) fully settled. ✔"));
                }
                else
                {
                    _output.WriteLine(FormattableString.Invariant(
                        $"Debt #{debt.Id} ({debt.Person}): {MoneyGuard.Money(debt.Amount - debt.Settled)} outstanding."));
                }

                return 0;
            }

            case "remove" when args.Length >= 2:
            {
                var debt = service.RemoveDebt(args[1]);
                _output.WriteLine($"Removed debt #{debt.Id} ({debt.Person}).");
                return 0;
            }

            case "list":
            {
                var debts = service.Debts();
                if (debts.Count == 0)
                {
                    _output.WriteLine("No debts tracked. ganjoor debt add <person> <amount> --i-owe|--owes-me starts one.");
                    return 0;
                }

                foreach (var debt in debts)
                {
                    var outstanding = debt.Amount - debt.Settled;
                    _output.WriteLine(FormattableString.Invariant(
                        $"#{debt.Id} {(debt.OwedByMe ? "→ you owe" : "← owed by")} {debt.Person}: {MoneyGuard.Money(outstanding)} outstanding of {MoneyGuard.Money(debt.Amount)}{(debt.Settled > 0 ? $" (settled {MoneyGuard.Money(debt.Settled)})" : string.Empty)}{(debt.DueDate is { } by ? $" · by {by:yyyy-MM-dd}" : string.Empty)}"));
                }

                return 0;
            }

            default:
                _error.WriteLine("Usage: JameJam ganjoor debt add <person> <amount> --i-owe|--owes-me [--due yyyy-MM-dd] [--notes t] | "
                    + "list | settle <id> <amount> | remove <id>");
                return 1;
        }
    }

    private int RunReport(string[] args, GanjoorOptions effective)
    {
        var parsed = ParseFlags(args, "--month");
        var month = parsed.Get("--month") is { } text ? (DateOnly?)MoneyGuard.Month(text) : null;
        var service = NewService(effective);
        var cash = service.CashFlow(month);
        var currency = effective.DefaultCurrency;
        _output.WriteLine(FormattableString.Invariant(
            $"{cash.Month:MMMM yyyy} — income {MoneyGuard.Money(cash.Income, currency)} · expenses {MoneyGuard.Money(cash.Expenses, currency)} · net {MoneyGuard.Money(cash.Net, currency)}"));
        if (cash.ByCategory.Count > 0)
        {
            _output.WriteLine("Spending by category:");
            foreach (var row in cash.ByCategory.Take(GanjoorDefaults.TopCategories))
            {
                var bar = new string('▇', Math.Max(1, (int)Math.Round(row.Amount * 20 / cash.ByCategory[0].Amount, MidpointRounding.AwayFromZero)));
                _output.WriteLine(FormattableString.Invariant(
                    $"  {row.Category,-20} {MoneyGuard.Money(row.Amount),14}  {bar}"));
            }
        }

        var budgets = service.BudgetStatuses(cash.Month);
        var problems = budgets.Where(b => b.Over).ToList();
        if (problems.Count > 0)
        {
            _output.WriteLine("Over budget:");
            foreach (var status in problems)
            {
                _output.WriteLine(FormattableString.Invariant(
                    $"  ⚠ {status.Category}: {MoneyGuard.Money(status.Spent)} of {MoneyGuard.Money(status.Limit)}"));
            }
        }

        var due = service.DueBills();
        if (due.Count > 0)
        {
            _output.WriteLine($"Bills awaiting apply: {due.Count} (ganjoor bill apply)");
        }

        return 0;
    }

    private void WarnOnBudget(GanjoorService service, GanjoorTransaction tx)
    {
        if (tx.Kind != GanjoorTxKind.Expense)
            return;

        var status = service.BudgetStatuses(new DateOnly(tx.Date.Year, tx.Date.Month, 1))
            .FirstOrDefault(b => string.Equals(b.Category, tx.Category, StringComparison.OrdinalIgnoreCase));
        if (status is null)
            return;

        if (status.Over)
        {
            _output.WriteLine(FormattableString.Invariant(
                $"⚠ Over budget: {status.Category} — {MoneyGuard.Money(status.Spent)} of {MoneyGuard.Money(status.Limit)}"));
        }
        else if (status.PercentUsed >= GanjoorDefaults.BudgetWarnPercent)
        {
            _output.WriteLine(FormattableString.Invariant(
                $"⚠ Budget check: {status.Category} at {status.PercentUsed}% — {MoneyGuard.Money(status.Remaining)} left this month."));
        }
    }

    private int RunNetWorth(GanjoorOptions effective)
    {
        var worth = NewService(effective).NetWorth();
        _output.WriteLine(FormattableString.Invariant(
            $"Net worth: {MoneyGuard.Money(worth.Total, worth.BaseCurrency)}  (accounts {MoneyGuard.Money(worth.Accounts)}, owed to you {MoneyGuard.Money(worth.Receivable)}, you owe {MoneyGuard.Money(worth.Payable)})"));
        return 0;
    }

    private int RunUndo(GanjoorOptions effective)
    {
        if (NewService(effective).Undo())
        {
            _output.WriteLine("Undone — the last change is reverted.");
            return 0;
        }

        _output.WriteLine("Nothing to undo.");
        return 0;
    }

    private int RunExport(string[] args, GanjoorOptions effective)
    {
        if (args.Length < 1)
            return Fail("Usage: JameJam ganjoor export <path.json>");

        File.WriteAllText(args[0], NewService(effective).ExportJson());
        _output.WriteLine($"Wallet exported to {args[0]}.");
        return 0;
    }

    private int RunImport(string[] args, GanjoorOptions effective)
    {
        if (args.Length < 1)
            return Fail("Usage: JameJam ganjoor import <path.json>  (replaces the whole wallet — undo can revert it)");

        var service = NewService(effective);
        service.PushUndoSnapshot();
        service.ImportJson(File.ReadAllText(args[0]));
        _output.WriteLine($"Wallet imported from {args[0]}. ganjoor undo reverts it.");
        return 0;
    }

    private int RunImportCsv(string[] args, GanjoorOptions effective)
    {
        if (args.Length < 2)
            return Fail("Usage: JameJam ganjoor import-csv <statement.csv> --account <id|name> [--category c] [--header] "
                + "(rows: date,description,amount — negative = debit)");

        var parsed = ParseFlags(args[1..], "--account", "--category", "--header");
        var accountText = parsed.Get("--account")
            ?? throw new GanjoorException("CSV imports need a target account: --account <id|name>");
        var service = NewService(effective);
        var result = service.ImportCsv(
            args[0],
            accountText,
            parsed.Get("--category") ?? "imported",
            args.Contains("--header"),
            effective.MaxImportRows);
        _output.WriteLine($"Imported {result.Imported} row(s), skipped {result.Skipped}.");
        return 0;
    }

    private async Task<int> RunAiAsync(string[] args, GanjoorOptions effective, CancellationToken cancellationToken)
    {
        if (_aiCompletion is null)
            return Fail("AI is not available in this context.");

        var service = NewService(effective);
        var assistant = new FinanceAssistant(effective);
        var currency = effective.DefaultCurrency;
        switch (args.Length > 0 ? args[0] : "help")
        {
            case "insights":
            {
                var parsed = ParseFlags(args[1..], "--month");
                var month = parsed.Get("--month") is { } text ? (DateOnly?)MoneyGuard.Month(text) : null;
                var prompt = assistant.BuildInsightsPrompt(
                    service.CashFlow(month), service.BudgetStatuses(month),
                    service.Transactions(new GanjoorFilter { Month = month }), currency);
                var result = await _aiCompletion(new AiRequest(prompt), cancellationToken).ConfigureAwait(false);
                _output.WriteLine(result.Content);
                return 0;
            }

            case "categorize" when args.Length >= 2:
            {
                var apply = args.Contains("--apply");
                var tx = service.Transactions().FirstOrDefault(t => t.Id == MoneyGuard.Id(args[1], "Transaction id"))
                    ?? throw new GanjoorException($"No transaction #{MoneyGuard.Id(args[1], "Transaction id")}.");
                var known = service.KnownCategories();
                if (known.Count == 0)
                    return Fail("No categories known yet — spend something or set a budget first.");

                var response = await _aiCompletion(
                    new AiRequest(assistant.BuildCategorizePrompt(tx, known, currency)),
                    cancellationToken).ConfigureAwait(false);
                var category = FinanceAssistant.ParseCategory(response.Content, known);
                if (category is null)
                {
                    return Fail("The AI suggested no known category — try renaming categories or add a budget row.");
                }

                if (!apply)
                {
                    _output.WriteLine($"Suggestion: {category}  (re-run with --apply to set it)");
                    return 0;
                }

                var updated = service.Recategorize(args[1], category);
                _output.WriteLine($"#{updated.Id} categorized as {category}. ganjoor undo reverts it.");
                return 0;
            }

            case "ask":
            {
                var question = ParseQuestion(args[1..]);
                if (question.Length == 0)
                    return Fail("Ask a question: JameJam ganjoor ai ask \"where does my money go?\"");

                var response = await _aiCompletion(
                    new AiRequest(assistant.BuildAskPrompt(
                        question,
                        service.NetWorth(),
                        service.CashFlow(),
                        service.BudgetStatuses(),
                        service.Transactions(),
                        service.Goals(),
                        service.Debts())),
                    cancellationToken).ConfigureAwait(false);
                _output.WriteLine(response.Content);
                return 0;
            }

            default:
                _error.WriteLine("Usage: JameJam ganjoor ai insights [--month yyyy-MM] | ai categorize <id> [--apply] | "
                    + "ai ask <question...>");
                return 1;
        }
    }

    // ── Helpers ──

    private IGanjoorStore RequireStore() =>
        store ?? throw new GanjoorException(NoStorageMessage);

    private GanjoorService NewService(GanjoorOptions effective) =>
        new(RequireStore(), _clock, effective);

    private GanjoorOptions EffectiveOptions()
    {
        var currency = _currencyProvider?.Invoke();
        if (string.IsNullOrWhiteSpace(currency))
            return _options;

        return _options with { DefaultCurrency = MoneyGuard.Currency(currency) };
    }

    private static GanjoorAccount RequireAccount(long id, GanjoorService service) =>
        service.Accounts().FirstOrDefault(a => a.Id == id)
        ?? throw new GanjoorException($"No account #{id}.");

    private static long ResolveAccountId(GanjoorService service, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.All(char.IsAsciiDigit))
            return MoneyGuard.Id(text, "Account");

        var account = service.Accounts().FirstOrDefault(a => string.Equals(a.Name, text, StringComparison.OrdinalIgnoreCase))
            ?? throw new GanjoorException($"No account named '{text}'.");
        return account.Id;
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

    private static int Percent(decimal part, decimal whole) =>
        whole <= 0 ? 0 : (int)Math.Clamp(Math.Round(part * 100 / whole, MidpointRounding.AwayFromZero), 0, 100);

    private sealed class FlagParser(string[] args, string[] known)
    {
        private readonly Dictionary<string, string?> _values = [];

        public string? Get(string flag) => _values.GetValueOrDefault(flag);

        public void Collect()
        {
            for (var i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith('-'))
                    continue;

                if (!known.Contains(args[i]))
                    throw new GanjoorException($"Unknown option '{args[i]}'.");

                var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith('-');
                _values[args[i]] = hasValue ? args[++i] : null;
            }
        }
    }

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

    private void Help()
    {
        _output.WriteLine($"{GanjoorDefaults.ServiceName} — your personal treasury, private by design");
        _output.WriteLine();
        _output.WriteLine("Usage (everything works with account ids or names):");
        _output.WriteLine("  ganjoor account add <name> [--currency XYZ] [--start <amount>]   Open an account");
        _output.WriteLine("  ganjoor account list | rename <id> <name> | archive <id> | remove <id> [--force]");
        _output.WriteLine("  ganjoor spend <account> <amount> <category> [--tags a,b] [--notes t] [--date d]");
        _output.WriteLine("  ganjoor earn  <account> <amount> <category> [same flags]");
        _output.WriteLine("  ganjoor transfer <from> <to> <amount> [--notes t] [--date d]");
        _output.WriteLine("  ganjoor list [--account a] [--category c] [--tag t] [--month yyyy-MM] [--kind k] [--q text]");
        _output.WriteLine("  ganjoor show <id> | delete <id> | undo");
        _output.WriteLine("  ganjoor budget set <category> <limit> | list [--month] | remove <category>");
        _output.WriteLine("  ganjoor bill add <name> <amount> <in|out> --category <c> --every daily|weekly|monthly|yearly");
        _output.WriteLine("             [--interval N] [--next d] [--account a] | list | due | apply | remove <id>");
        _output.WriteLine("  ganjoor goal add <name> <target> [--by d] | list | contribute <id> <amount> | withdraw <id> <amount> | remove <id>");
        _output.WriteLine("  ganjoor debt add <person> <amount> --i-owe|--owes-me [--due d] [--notes t]");
        _output.WriteLine("             | list | settle <id> <amount> | remove <id>");
        _output.WriteLine("  ganjoor report [--month yyyy-MM] | networth");
        _output.WriteLine("  ganjoor export <path.json> | import <path.json> | import-csv <file.csv> --account <a> [--category c] [--header]");
        _output.WriteLine("  ganjoor ai insights [--month] | ai categorize <id> [--apply] | ai ask <question...>");
        _output.WriteLine();
        _output.WriteLine("Money is exact decimal math; amounts use dots (1234.50). Dates are yyyy-MM-dd.");
        _output.WriteLine("AI runs through the Soroush safety layer (JAMEJAM_AI_API_KEY, loopback endpoints may omit it).");
    }
}
