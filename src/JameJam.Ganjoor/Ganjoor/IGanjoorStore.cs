namespace JameJam.Ganjoor;

/// <summary>
/// Persistent storage for the Ganjoor wallet. Implementations assign ids, persist
/// every entity, and keep an undo-snapshot stack (payloads produced by the service).
/// </summary>
public interface IGanjoorStore
{
    /// <summary>Inserts an account and returns it with its assigned id.</summary>
    GanjoorAccount AddAccount(GanjoorAccount account);

    /// <summary>Overwrites an existing account (same id).</summary>
    void UpdateAccount(GanjoorAccount account);

    /// <summary>Removes an account. Returns true when it existed.</summary>
    bool RemoveAccount(long id);

    /// <summary>Gets an account by id, or null.</summary>
    GanjoorAccount? FindAccount(long id);

    /// <summary>Gets an account by name (case-insensitive), or null.</summary>
    GanjoorAccount? FindAccountByName(string name);

    /// <summary>Lists all accounts ordered by id.</summary>
    IReadOnlyList<GanjoorAccount> ListAccounts();

    /// <summary>Inserts a transaction and returns it with its assigned id.</summary>
    GanjoorTransaction AddTransaction(GanjoorTransaction transaction);

    /// <summary>Removes a transaction. Returns true when it existed.</summary>
    bool RemoveTransaction(long id);

    /// <summary>Overwrites an existing transaction (same id).</summary>
    void UpdateTransaction(GanjoorTransaction transaction);

    /// <summary>Gets a transaction by id, or null.</summary>
    GanjoorTransaction? FindTransaction(long id);

    /// <summary>Lists all transactions, newest record first.</summary>
    IReadOnlyList<GanjoorTransaction> ListTransactions();

    /// <summary>Creates or overwrites the budget for a category.</summary>
    void SetBudget(GanjoorBudget budget);

    /// <summary>Removes the budget for a category. Returns true when it existed.</summary>
    bool RemoveBudget(string category);

    /// <summary>Lists all budgets ordered by category.</summary>
    IReadOnlyList<GanjoorBudget> ListBudgets();

    /// <summary>Inserts a bill and returns it with its assigned id.</summary>
    GanjoorBill AddBill(GanjoorBill bill);

    /// <summary>Overwrites an existing bill (same id).</summary>
    void UpdateBill(GanjoorBill bill);

    /// <summary>Removes a bill. Returns true when it existed.</summary>
    bool RemoveBill(long id);

    /// <summary>Lists all bills ordered by next due date.</summary>
    IReadOnlyList<GanjoorBill> ListBills();

    /// <summary>Inserts a goal and returns it with its assigned id.</summary>
    GanjoorGoal AddGoal(GanjoorGoal goal);

    /// <summary>Overwrites an existing goal (same id).</summary>
    void UpdateGoal(GanjoorGoal goal);

    /// <summary>Removes a goal. Returns true when it existed.</summary>
    bool RemoveGoal(long id);

    /// <summary>Lists all goals ordered by id.</summary>
    IReadOnlyList<GanjoorGoal> ListGoals();

    /// <summary>Inserts a debt and returns it with its assigned id.</summary>
    GanjoorDebt AddDebt(GanjoorDebt debt);

    /// <summary>Overwrites an existing debt (same id).</summary>
    void UpdateDebt(GanjoorDebt debt);

    /// <summary>Removes a debt. Returns true when it existed.</summary>
    bool RemoveDebt(long id);

    /// <summary>Lists all debts ordered by id.</summary>
    IReadOnlyList<GanjoorDebt> ListDebts();

    /// <summary>Pushes an undo snapshot (the service serializes the state before mutating).</summary>
    void PushUndo(string payload);

    /// <summary>Pops the most recent undo snapshot, or null when the stack is empty.</summary>
    string? PopUndo();

    /// <summary>How many undo snapshots are currently stacked.</summary>
    int UndoCount { get; }

    /// <summary>
    /// Maximum number of undo snapshots kept. Pushing beyond the depth drops the oldest
    /// snapshot; defaults to <see cref="GanjoorDefaults.MaxUndoSnapshots"/>. Values below
    /// zero are rejected.
    /// </summary>
    int UndoDepth { get; set; }
}
