namespace JameJam.Settings;

/// <summary>
/// Tunables for a settings store. Everything that used to be a hardcoded constant is now
/// a settable option here; <see cref="Validate"/> (the safe guard) bounds every value.
/// </summary>
public sealed record SettingsOptions
{
    /// <summary>Maximum allowed key length.</summary>
    public int MaxKeyLength { get; init; } = SettingGuard.DefaultMaxKeyLength;

    /// <summary>Maximum allowed value length.</summary>
    public int MaxValueLength { get; init; } = SettingGuard.DefaultMaxValueLength;

    /// <summary>
    /// Substrings that mark a key as a secret (matched case-insensitively).
    /// Extend or replace to fit your organization's key-naming conventions.
    /// </summary>
    public IReadOnlySet<string> SecretKeyNeedles { get; init; } = SettingGuard.DefaultSecretKeyNeedles;

    /// <summary>Validates every value against its named rail. Throws when out of range.</summary>
    public void Validate() => SettingGuard.ValidateOptions(this);
}
