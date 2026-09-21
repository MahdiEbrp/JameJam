using System.Globalization;
using JameJam.Data;
using Microsoft.Data.Sqlite;

namespace JameJam.Ganjoor;

/// <summary>
/// SQLite-backed wallet store (schema version 1). Safety: parameterized SQL only,
/// transactional writes, owner-only file permissions, single-init versioned schema
/// (shared <see cref="SqliteDatabase"/>). Amounts are stored as exact invariant text.
/// </summary>
/// <param name="databasePath">Path of the SQLite database file (created on first use).</param>
public sealed class SqliteGanjoorStore(string databasePath) : IGanjoorStore
{
    private const int CurrentSchemaVersion = 1;

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS accounts (
            id              INTEGER PRIMARY KEY,
            name            TEXT NOT NULL,
            currency        TEXT NOT NULL,
            initial_balance TEXT NOT NULL,
            is_archived     INTEGER NOT NULL DEFAULT 0,
            created_at      TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS transactions (
            id             INTEGER PRIMARY KEY,
            kind           INTEGER NOT NULL,
            account_id     INTEGER NOT NULL,
            amount         TEXT NOT NULL,
            category       TEXT NOT NULL,
            date           TEXT NOT NULL,
            created_at     TEXT NOT NULL,
            transfer_to_id INTEGER,
            tags           TEXT NOT NULL DEFAULT '',
            notes          TEXT NOT NULL DEFAULT '',
            from_bill_id   INTEGER
        );
        CREATE INDEX IF NOT EXISTS ix_tx_account_date ON transactions(account_id, date);
        CREATE INDEX IF NOT EXISTS ix_tx_category ON transactions(category);
        CREATE TABLE IF NOT EXISTS budgets (
            category      TEXT PRIMARY KEY,
            monthly_limit TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS bills (
            id         INTEGER PRIMARY KEY,
            name       TEXT NOT NULL,
            amount     TEXT NOT NULL,
            kind       INTEGER NOT NULL,
            category   TEXT NOT NULL,
            account_id INTEGER NOT NULL DEFAULT 1,
            frequency  INTEGER NOT NULL,
            interval   INTEGER NOT NULL,
            next_due   TEXT NOT NULL,
            created_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS goals (
            id          INTEGER PRIMARY KEY,
            name        TEXT NOT NULL,
            target      TEXT NOT NULL,
            contributed TEXT NOT NULL,
            deadline    TEXT,
            created_at  TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS debts (
            id         INTEGER PRIMARY KEY,
            person     TEXT NOT NULL,
            amount     TEXT NOT NULL,
            settled    TEXT NOT NULL,
            owed_by_me INTEGER NOT NULL,
            due_date   TEXT,
            notes      TEXT NOT NULL DEFAULT '',
            created_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS undo_log (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            created_at TEXT NOT NULL,
            payload    TEXT NOT NULL
        );
        """;

    private readonly SqliteDatabase _database = new(databasePath);

    /// <summary>Path of the SQLite database file.</summary>
    public string DatabasePath => _database.DatabasePath;

    /// <inheritdoc />
    public GanjoorAccount AddAccount(GanjoorAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return WithWrite(connection =>
        {
            var id = NextId(connection, "accounts");
            Exec(connection,
                "INSERT INTO accounts (id, name, currency, initial_balance, is_archived, created_at) "
                + "VALUES ($id, $name, $currency, $initial, $archived, $created)",
                Param("$id", id), Param("$name", account.Name), Param("$currency", account.Currency),
                Param("$initial", Text(account.InitialBalance)), Param("$archived", account.IsArchived ? 1 : 0),
                Param("$created", Text(account.CreatedAt)));
            return account with { Id = id };
        });
    }

    /// <inheritdoc />
    public void UpdateAccount(GanjoorAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        WithWrite(connection => Exec(connection,
            "UPDATE accounts SET name=$name, currency=$currency, initial_balance=$initial, "
            + "is_archived=$archived, created_at=$created WHERE id=$id",
            Param("$name", account.Name), Param("$currency", account.Currency),
            Param("$initial", Text(account.InitialBalance)), Param("$archived", account.IsArchived ? 1 : 0),
            Param("$created", Text(account.CreatedAt)), Param("$id", account.Id)));
    }

    /// <inheritdoc />
    public bool RemoveAccount(long id) =>
        WithWrite(connection => Exec(connection, "DELETE FROM accounts WHERE id=$id", Param("$id", id)) > 0);

    /// <inheritdoc />
    public GanjoorAccount? FindAccount(long id) =>
        WithRead(connection => QueryOne(connection,
            "SELECT id, name, currency, initial_balance, is_archived, created_at FROM accounts WHERE id=$id",
            Param("$id", id), MapAccount));

    /// <inheritdoc />
    public GanjoorAccount? FindAccountByName(string name) =>
        WithRead(connection => QueryOne(connection,
            "SELECT id, name, currency, initial_balance, is_archived, created_at FROM accounts WHERE lower(name)=lower($name)",
            Param("$name", name), MapAccount));

    /// <inheritdoc />
    public IReadOnlyList<GanjoorAccount> ListAccounts() =>
        WithRead(connection => QueryList(connection,
            "SELECT id, name, currency, initial_balance, is_archived, created_at FROM accounts ORDER BY id",
            MapAccount));

    /// <inheritdoc />
    public GanjoorTransaction AddTransaction(GanjoorTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return WithWrite(connection =>
        {
            var id = NextId(connection, "transactions");
            Exec(connection,
                "INSERT INTO transactions (id, kind, account_id, amount, category, date, created_at, "
                + "transfer_to_id, tags, notes, from_bill_id) VALUES "
                + "($id, $kind, $account, $amount, $category, $date, $created, $to, $tags, $notes, $bill)",
                Param("$id", id), Param("$kind", (int)transaction.Kind), Param("$account", transaction.AccountId),
                Param("$amount", Text(transaction.Amount)), Param("$category", transaction.Category),
                Param("$date", Text(transaction.Date)), Param("$created", Text(transaction.CreatedAt)),
                Param("$to", transaction.TransferToAccountId), Param("$tags", string.Join(',', transaction.Tags)),
                Param("$notes", transaction.Notes), Param("$bill", transaction.FromBillId));
            return transaction with { Id = id };
        });
    }

    /// <inheritdoc />
    public bool RemoveTransaction(long id) =>
        WithWrite(connection => Exec(connection, "DELETE FROM transactions WHERE id=$id", Param("$id", id)) > 0);

    /// <inheritdoc />
    public void UpdateTransaction(GanjoorTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        WithWrite(connection => Exec(connection,
            "UPDATE transactions SET kind=$kind, account_id=$account, amount=$amount, category=$category, "
            + "date=$date, created_at=$created, transfer_to_id=$to, tags=$tags, notes=$notes, from_bill_id=$bill "
            + "WHERE id=$id",
            Param("$kind", (int)transaction.Kind), Param("$account", transaction.AccountId), Param("$amount", Text(transaction.Amount)),
            Param("$category", transaction.Category), Param("$date", Text(transaction.Date)),
            Param("$created", Text(transaction.CreatedAt)), Param("$to", transaction.TransferToAccountId),
            Param("$tags", string.Join(',', transaction.Tags)), Param("$notes", transaction.Notes),
            Param("$bill", transaction.FromBillId), Param("$id", transaction.Id)));
    }

    /// <inheritdoc />
    public GanjoorTransaction? FindTransaction(long id) =>
        WithRead(connection => QueryOne(connection, SelectTransactionsSql + " WHERE id=$id", Param("$id", id), MapTransaction));

    /// <inheritdoc />
    public IReadOnlyList<GanjoorTransaction> ListTransactions() =>
        WithRead(connection => QueryList(connection, SelectTransactionsSql + " ORDER BY date DESC, id DESC", MapTransaction));

    /// <inheritdoc />
    public void SetBudget(GanjoorBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        WithWrite(connection => Exec(connection,
            "INSERT INTO budgets (category, monthly_limit) VALUES ($category, $limit) "
            + "ON CONFLICT(category) DO UPDATE SET monthly_limit=$limit",
            Param("$category", budget.Category), Param("$limit", Text(budget.MonthlyLimit))));
    }

    /// <inheritdoc />
    public bool RemoveBudget(string category) =>
        WithWrite(connection => Exec(connection, "DELETE FROM budgets WHERE lower(category)=lower($category)",
            Param("$category", category)) > 0);

    /// <inheritdoc />
    public IReadOnlyList<GanjoorBudget> ListBudgets() =>
        WithRead(connection => QueryList(connection, "SELECT category, monthly_limit FROM budgets ORDER BY category",
            reader => new GanjoorBudget(reader.GetString(0), Money(reader, 1))));

    /// <inheritdoc />
    public GanjoorBill AddBill(GanjoorBill bill)
    {
        ArgumentNullException.ThrowIfNull(bill);
        return WithWrite(connection =>
        {
            var id = NextId(connection, "bills");
            Exec(connection,
                "INSERT INTO bills (id, name, amount, kind, category, account_id, frequency, interval, next_due, created_at) "
                + "VALUES ($id, $name, $amount, $kind, $category, $account, $frequency, $interval, $due, $created)",
                Param("$id", id), Param("$name", bill.Name), Param("$amount", Text(bill.Amount)),
                Param("$kind", (int)bill.Kind), Param("$category", bill.Category),
                Param("$account", bill.AccountId),
                Param("$frequency", (int)bill.Frequency), Param("$interval", bill.Interval),
                Param("$due", Text(bill.NextDue)), Param("$created", Text(bill.CreatedAt)));
            return bill with { Id = id };
        });
    }

    /// <inheritdoc />
    public void UpdateBill(GanjoorBill bill)
    {
        ArgumentNullException.ThrowIfNull(bill);
        WithWrite(connection => Exec(connection,
            "UPDATE bills SET name=$name, amount=$amount, kind=$kind, category=$category, account_id=$account, "
            + "frequency=$frequency, interval=$interval, next_due=$due, created_at=$created WHERE id=$id",
            Param("$name", bill.Name), Param("$amount", Text(bill.Amount)), Param("$kind", (int)bill.Kind),
            Param("$category", bill.Category), Param("$account", bill.AccountId), Param("$frequency", (int)bill.Frequency),
            Param("$interval", bill.Interval), Param("$due", Text(bill.NextDue)),
            Param("$created", Text(bill.CreatedAt)), Param("$id", bill.Id)));
    }

    /// <inheritdoc />
    public bool RemoveBill(long id) =>
        WithWrite(connection => Exec(connection, "DELETE FROM bills WHERE id=$id", Param("$id", id)) > 0);

    /// <inheritdoc />
    public IReadOnlyList<GanjoorBill> ListBills() =>
        WithRead(connection => QueryList(connection,
            "SELECT id, name, amount, kind, category, account_id, frequency, interval, next_due, created_at FROM bills "
            + "ORDER BY next_due, id",
            reader => new GanjoorBill(
                reader.GetInt64(0), reader.GetString(1), Money(reader, 2), (GanjoorTxKind)reader.GetInt32(3),
                reader.GetString(4), (GanjoorFrequency)reader.GetInt32(6), reader.GetInt32(7),
                ParseDate(reader.GetString(8)), ParseStamp(reader.GetString(9)))
            {
                AccountId = reader.GetInt64(5),
            }));

    /// <inheritdoc />
    public GanjoorGoal AddGoal(GanjoorGoal goal)
    {
        ArgumentNullException.ThrowIfNull(goal);
        return WithWrite(connection =>
        {
            var id = NextId(connection, "goals");
            Exec(connection,
                "INSERT INTO goals (id, name, target, contributed, deadline, created_at) "
                + "VALUES ($id, $name, $target, $contributed, $deadline, $created)",
                Param("$id", id), Param("$name", goal.Name), Param("$target", Text(goal.Target)),
                Param("$contributed", Text(goal.Contributed)), Param("$deadline", TextOptional(goal.Deadline)),
                Param("$created", Text(goal.CreatedAt)));
            return goal with { Id = id };
        });
    }

    /// <inheritdoc />
    public void UpdateGoal(GanjoorGoal goal)
    {
        ArgumentNullException.ThrowIfNull(goal);
        WithWrite(connection => Exec(connection,
            "UPDATE goals SET name=$name, target=$target, contributed=$contributed, deadline=$deadline, "
            + "created_at=$created WHERE id=$id",
            Param("$name", goal.Name), Param("$target", Text(goal.Target)),
            Param("$contributed", Text(goal.Contributed)), Param("$deadline", TextOptional(goal.Deadline)),
            Param("$created", Text(goal.CreatedAt)), Param("$id", goal.Id)));
    }

    /// <inheritdoc />
    public bool RemoveGoal(long id) =>
        WithWrite(connection => Exec(connection, "DELETE FROM goals WHERE id=$id", Param("$id", id)) > 0);

    /// <inheritdoc />
    public IReadOnlyList<GanjoorGoal> ListGoals() =>
        WithRead(connection => QueryList(connection,
            "SELECT id, name, target, contributed, deadline, created_at FROM goals ORDER BY id",
            reader => new GanjoorGoal(
                reader.GetInt64(0), reader.GetString(1), Money(reader, 2), Money(reader, 3),
                reader.IsDBNull(4) ? null : ParseDate(reader.GetString(4)), ParseStamp(reader.GetString(5)))));

    /// <inheritdoc />
    public GanjoorDebt AddDebt(GanjoorDebt debt)
    {
        ArgumentNullException.ThrowIfNull(debt);
        return WithWrite(connection =>
        {
            var id = NextId(connection, "debts");
            Exec(connection,
                "INSERT INTO debts (id, person, amount, settled, owed_by_me, due_date, notes, created_at) "
                + "VALUES ($id, $person, $amount, $settled, $owed, $due, $notes, $created)",
                Param("$id", id), Param("$person", debt.Person), Param("$amount", Text(debt.Amount)),
                Param("$settled", Text(debt.Settled)), Param("$owed", debt.OwedByMe ? 1 : 0),
                Param("$due", TextOptional(debt.DueDate)), Param("$notes", debt.Notes),
                Param("$created", Text(debt.CreatedAt)));
            return debt with { Id = id };
        });
    }

    /// <inheritdoc />
    public void UpdateDebt(GanjoorDebt debt)
    {
        ArgumentNullException.ThrowIfNull(debt);
        WithWrite(connection => Exec(connection,
            "UPDATE debts SET person=$person, amount=$amount, settled=$settled, owed_by_me=$owed, "
            + "due_date=$due, notes=$notes, created_at=$created WHERE id=$id",
            Param("$person", debt.Person), Param("$amount", Text(debt.Amount)),
            Param("$settled", Text(debt.Settled)), Param("$owed", debt.OwedByMe ? 1 : 0),
            Param("$due", TextOptional(debt.DueDate)), Param("$notes", debt.Notes),
            Param("$created", Text(debt.CreatedAt)), Param("$id", debt.Id)));
    }

    /// <inheritdoc />
    public bool RemoveDebt(long id) =>
        WithWrite(connection => Exec(connection, "DELETE FROM debts WHERE id=$id", Param("$id", id)) > 0);

    /// <inheritdoc />
    public IReadOnlyList<GanjoorDebt> ListDebts() =>
        WithRead(connection => QueryList(connection,
            "SELECT id, person, amount, settled, owed_by_me, due_date, notes, created_at FROM debts ORDER BY id",
            reader => new GanjoorDebt(
                reader.GetInt64(0), reader.GetString(1), Money(reader, 2), Money(reader, 3),
                reader.GetInt64(4) != 0, reader.IsDBNull(5) ? null : ParseDate(reader.GetString(5)),
                reader.GetString(6), ParseStamp(reader.GetString(7)))));

    /// <inheritdoc />
    public void PushUndo(string payload)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        WithWrite(connection =>
        {
            _ = Exec(connection,
                "INSERT INTO undo_log (created_at, payload) VALUES ($created, $payload)",
                Param("$created", Text(DateTimeOffset.UtcNow)), Param("$payload", payload));
            TrimUndo(connection);
        });
    }

    private void TrimUndo(SqliteConnection connection)
    {
        if (_undoDepth == 0)
        {
            _ = Exec(connection, "DELETE FROM undo_log");
            return;
        }

        _ = Exec(
            connection,
            "DELETE FROM undo_log WHERE id < " +
            "(SELECT MIN(id) FROM (SELECT id FROM undo_log ORDER BY id DESC LIMIT $depth))",
            Param("$depth", _undoDepth));
    }

    /// <inheritdoc />
    public string? PopUndo() =>
        WithWrite(connection =>
        {
            var payload = QueryOne(
                connection,
                "SELECT payload FROM undo_log ORDER BY id DESC LIMIT 1",
                reader => reader.GetString(0));
            if (payload is null)
                return null;

            _ = Exec(connection, "DELETE FROM undo_log WHERE id = (SELECT MAX(id) FROM undo_log)");
            return payload;
        });

    /// <inheritdoc />
    /// <inheritdoc />
    public int UndoDepth
    {
        get => _undoDepth;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _undoDepth = value;
        }
    }

    private int _undoDepth = GanjoorDefaults.UndoDepth;

    /// <inheritdoc />
    public int UndoCount =>
        WithRead(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM undo_log";
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        });

    private const string SelectTransactionsSql = """
        SELECT id, kind, account_id, amount, category, date, created_at, transfer_to_id, tags, notes, from_bill_id
        FROM transactions
        """;

    private static GanjoorTransaction MapTransaction(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        (GanjoorTxKind)reader.GetInt32(1),
        reader.GetInt64(2),
        Money(reader, 3),
        reader.GetString(4),
        ParseDate(reader.GetString(5)),
        ParseStamp(reader.GetString(6)))
    {
        TransferToAccountId = reader.IsDBNull(7) ? null : reader.GetInt64(7),
        Tags = reader.GetString(8).Length == 0
            ? []
            : [.. reader.GetString(8).Split(',')],
        Notes = reader.GetString(9),
        FromBillId = reader.IsDBNull(10) ? null : reader.GetInt64(10),
    };

    private static GanjoorAccount MapAccount(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        Money(reader, 3),
        ParseStamp(reader.GetString(5)))
    {
        IsArchived = reader.GetInt64(4) != 0,
    };

    private static string SchemaFactory(SqliteConnection connection)
    {
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        var current = Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (current >= CurrentSchemaVersion)
            return string.Empty;

        if (current is not 0)
        {
            throw new GanjoorException(
                $"This wallet database was written by a newer Ganjoor (schema {current}).");
        }

        return SchemaSql + $"PRAGMA user_version = {CurrentSchemaVersion};";
    }

    private T WithWrite<T>(Func<SqliteConnection, T> action)
    {
        _database.Initialize(SchemaFactory);
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        var result = action(connection);
        transaction.Commit();
        return result;
    }

    private void WithWrite(Action<SqliteConnection> action) =>
        _ = WithWrite(connection =>
        {
            action(connection);
            return 0;
        });

    private T WithRead<T>(Func<SqliteConnection, T> action)
    {
        _database.Initialize(SchemaFactory);
        using var connection = _database.Open();
        return action(connection);
    }

    private static long NextId(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COALESCE(MAX(id), 0) + 1 FROM {table}";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static int Exec(SqliteConnection connection, string sql, params SqliteParameter[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.Add(parameter);

        return command.ExecuteNonQuery();
    }

    private static List<T> QueryList<T>(SqliteConnection connection, string sql, Func<SqliteDataReader, T> map)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var results = new List<T>();
        while (reader.Read())
            results.Add(map(reader));

        return results;
    }

    private static T? QueryOne<T>(SqliteConnection connection, string sql, Func<SqliteDataReader, T> map)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        return reader.Read() ? map(reader) : default;
    }

    private static T? QueryOne<T>(SqliteConnection connection, string sql, SqliteParameter parameter, Func<SqliteDataReader, T> map)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(parameter);
        using var reader = command.ExecuteReader();
        return reader.Read() ? map(reader) : default;
    }

    private static SqliteParameter Param(string name, object? value) => new(name, value ?? DBNull.Value);

    private static decimal Money(SqliteDataReader reader, int column) =>
        decimal.Parse(reader.GetString(column), CultureInfo.InvariantCulture);

    private static string Text(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Text(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Text(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static string? TextOptional(DateOnly? value) => value.HasValue
        ? value.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        : null;

    private static DateOnly ParseDate(string value) =>
        DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseStamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
