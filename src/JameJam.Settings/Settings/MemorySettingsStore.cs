namespace JameJam.Settings;

/// <summary>
/// Non-persistent in-memory settings store (for tests, dry runs, and previews).
/// Enforces the same configurable limits as the SQLite store via <see cref="SettingsOptions"/>.
/// </summary>
public sealed class MemorySettingsStore : ISettingsStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SettingsEntry> _entries = new(StringComparer.Ordinal);
    private readonly SettingsOptions _options;

    /// <summary>Initializes the store with optional custom limits (validated on construction).</summary>
    public MemorySettingsStore(SettingsOptions? options = null)
    {
        _options = options ?? new SettingsOptions();
        _options.Validate();
    }

    /// <inheritdoc />
    public string? GetValue(string key)
    {
        SettingGuard.ValidateKey(key, _options.MaxKeyLength);
        lock (_gate)
        {
            return _entries.TryGetValue(key, out var entry) ? entry.Value : null;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<SettingsEntry> GetAll()
    {
        lock (_gate)
        {
            return [.. _entries.Values.OrderBy(entry => entry.Key, StringComparer.Ordinal)];
        }
    }

    /// <inheritdoc />
    public void SetValue(string key, string? value)
    {
        SettingGuard.ValidateKey(key, _options.MaxKeyLength);
        SettingGuard.EnsureNotSecretKey(key, _options.SecretKeyNeedles);
        var safeValue = SettingGuard.ValidateValue(value, _options.MaxValueLength);
        lock (_gate)
        {
            _entries[key] = new SettingsEntry(key, safeValue, DateTimeOffset.UtcNow);
        }
    }

    /// <inheritdoc />
    public bool Remove(string key)
    {
        SettingGuard.ValidateKey(key, _options.MaxKeyLength);
        lock (_gate)
        {
            return _entries.Remove(key);
        }
    }

    /// <inheritdoc />
    public int Clear()
    {
        lock (_gate)
        {
            var count = _entries.Count;
            _entries.Clear();
            return count;
        }
    }
}
