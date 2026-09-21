namespace JameJam.Settings;

/// <summary>A single saved setting.</summary>
/// <param name="Key">Unique setting key.</param>
/// <param name="Value">Stored value (never null — null is stored as empty).</param>
/// <param name="UpdatedAt">When the value was last written (UTC).</param>
public sealed record SettingsEntry(string Key, string Value, DateTimeOffset UpdatedAt);

/// <summary>Persistent key/value store for toolbox settings (non-secret configuration only).</summary>
public interface ISettingsStore
{
    /// <summary>Gets the value stored under <paramref name="key"/>, or null when absent.</summary>
    string? GetValue(string key);

    /// <summary>Gets all entries ordered by key.</summary>
    IReadOnlyList<SettingsEntry> GetAll();

    /// <summary>
    /// Creates or overwrites the value under <paramref name="key"/>. Null is stored as empty.
    /// Secret-looking keys are refused by design — the database is plaintext.
    /// </summary>
    void SetValue(string key, string? value);

    /// <summary>Removes <paramref name="key"/>. Returns true when it existed.</summary>
    bool Remove(string key);

    /// <summary>Removes every setting. Returns the number of removed entries.</summary>
    int Clear();
}
