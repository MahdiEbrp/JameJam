using System.Buffers;

namespace JameJam.Settings;

/// <summary>
/// Validates setting keys and values before they reach any store.
/// All limits arrive as parameters (defaulting to named rails) — no magic numbers.
/// </summary>
public static class SettingGuard
{
    /// <summary>Rail: default maximum allowed key length.</summary>
    public const int DefaultMaxKeyLength = 128;

    /// <summary>Rail: default maximum allowed value length.</summary>
    public const int DefaultMaxValueLength = 8_192;

    /// <summary>Rail: upper bound for any configured key-length limit.</summary>
    public const int MaxKeyLengthBound = 4_096;

    /// <summary>Rail: upper bound for any configured value-length limit.</summary>
    public const int MaxValueLengthBound = 1_000_000;

    /// <summary>Default substrings that mark a key as a secret (matched case-insensitively).</summary>
    public static readonly IReadOnlySet<string> DefaultSecretKeyNeedles = new HashSet<string>(
        ["apikey", "api-key", "api_key", "secret", "token", "password", "passwd", "pwd", "credential"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly SearchValues<string> DefaultSecretNeedlesSearch = SearchValues.Create(
        [.. DefaultSecretKeyNeedles], StringComparison.OrdinalIgnoreCase);

    /// <summary>Validates a <see cref="SettingsOptions"/> object (bounds every knob).</summary>
    /// <exception cref="ArgumentOutOfRangeException">Any limit outside its rails.</exception>
    /// <exception cref="ArgumentException">Null needle set.</exception>
    public static void ValidateOptions(SettingsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.MaxKeyLength is < 1 or > MaxKeyLengthBound)
            throw new ArgumentOutOfRangeException(nameof(options), $"MaxKeyLength must be between 1 and {MaxKeyLengthBound}.");

        if (options.MaxValueLength is < 1 or > MaxValueLengthBound)
            throw new ArgumentOutOfRangeException(nameof(options), $"MaxValueLength must be between 1 and {MaxValueLengthBound}.");

        if (options.SecretKeyNeedles is null)
            throw new ArgumentException("SecretKeyNeedles must not be null.", nameof(options));
    }

    /// <summary>Validates a setting key against <paramref name="maxLength"/>.</summary>
    /// <exception cref="ArgumentException">Null, empty, or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Longer than <paramref name="maxLength"/>.</exception>
    public static string ValidateKey(string? key, int maxLength = DefaultMaxKeyLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 1);

        return key.Length <= maxLength
            ? key
            : throw new ArgumentOutOfRangeException(
                nameof(key), $"Setting key exceeds {maxLength} characters.");
    }

    /// <summary>Validates a setting value (null becomes empty).</summary>
    /// <exception cref="ArgumentOutOfRangeException">Longer than <paramref name="maxLength"/>.</exception>
    public static string ValidateValue(string? value, int maxLength = DefaultMaxValueLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 1);

        var safeValue = value ?? string.Empty;
        return safeValue.Length <= maxLength
            ? safeValue
            : throw new ArgumentOutOfRangeException(
                nameof(value), $"Setting value exceeds {maxLength} characters.");
    }

    /// <summary>
    /// Refuses keys that look like secrets: the settings database is plaintext, so
    /// secrets belong in environment variables or a system keyring instead (by design).
    /// </summary>
    /// <param name="key">The key about to be written.</param>
    /// <param name="needles">Custom secret markers; defaults to <see cref="DefaultSecretKeyNeedles"/>.</param>
    /// <exception cref="InvalidOperationException">The key looks like a secret.</exception>
    public static void EnsureNotSecretKey(string key, IReadOnlySet<string>? needles = null)
    {
        if (needles is null)
        {
            if (key.AsSpan().IndexOfAny(DefaultSecretNeedlesSearch) != -1)
                throw Refuse(key);
            return;
        }

        if (needles.Count > 0 && key.AsSpan().IndexOfAny(SearchValues.Create([.. needles], StringComparison.OrdinalIgnoreCase)) != -1)
            throw Refuse(key);
    }

    private static InvalidOperationException Refuse(string key) => new(
        $"Refusing to store '{key}': it looks like a secret. "
        + "The settings database is plaintext — keep secrets in environment variables or a system keyring.");
}
