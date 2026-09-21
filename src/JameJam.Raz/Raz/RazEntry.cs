namespace JameJam.Raz;

/// <summary>Hash algorithm for TOTP (RFC 6238). <see cref="None"/> marks an entry without TOTP.</summary>
public enum TotpAlgorithm
{
    /// <summary>The entry carries no TOTP seed.</summary>
    None = 0,

    /// <summary>HMAC-SHA1 — the authenticator-app default.</summary>
    Sha1 = 1,

    /// <summary>HMAC-SHA256.</summary>
    Sha256 = 2,

    /// <summary>HMAC-SHA512.</summary>
    Sha512 = 3,
}

/// <summary>
/// One vault entry. Every sensitive field is encrypted at rest; only bookkeeping
/// (ids, timestamps, expiry date, favorite flag, TOTP parameters) is stored in the clear —
/// a documented trade-off so reminders can be queried without unlocking the vault.
/// </summary>
/// <param name="Id">Assigned by the store.</param>
/// <param name="Title">Human label (encrypted at rest).</param>
/// <param name="Secret">The password or key (encrypted at rest).</param>
/// <param name="Username">Login name (encrypted at rest).</param>
/// <param name="Url">Where this is used (encrypted at rest).</param>
/// <param name="Notes">Free notes (encrypted at rest).</param>
/// <param name="Tags">Comma-joined tags (encrypted at rest).</param>
/// <param name="TotpSeed">Base32 TOTP seed (encrypted at rest; empty = none).</param>
/// <param name="TotpAlgorithm">TOTP hash algorithm.</param>
/// <param name="TotpDigits">TOTP code length.</param>
/// <param name="TotpPeriodSeconds">TOTP time step.</param>
/// <param name="ExpiresOn">Rotation deadline (plaintext for queryable reminders).</param>
/// <param name="Favorite">Pinned in listings (plaintext).</param>
/// <param name="CreatedAt">When the entry was created.</param>
/// <param name="UpdatedAt">When the entry was last changed.</param>
public sealed record RazEntry(
    long Id,
    string Title,
    string Secret,
    string Username,
    string Url,
    string Notes,
    string Tags,
    string TotpSeed,
    TotpAlgorithm TotpAlgorithm,
    int TotpDigits,
    int TotpPeriodSeconds,
    DateOnly? ExpiresOn,
    bool Favorite,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
