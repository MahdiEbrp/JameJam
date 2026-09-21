using System.Text.Json;

namespace JameJam.Raz;

/// <summary>Encrypted-at-rest form of one entry (Base64 AES-GCM payloads for sensitive fields).</summary>
/// <param name="Id">Entry id in the source vault.</param>
/// <param name="Title">Ciphertext.</param>
/// <param name="Secret">Ciphertext.</param>
/// <param name="Username">Ciphertext.</param>
/// <param name="Url">Ciphertext.</param>
/// <param name="Notes">Ciphertext.</param>
/// <param name="Tags">Ciphertext.</param>
/// <param name="TotpSeed">Ciphertext.</param>
/// <param name="TotpAlgorithm">Algorithm code (0 none, 1 SHA-1, 2 SHA-256, 3 SHA-512).</param>
/// <param name="TotpDigits">Code length.</param>
/// <param name="TotpPeriodSeconds">Time step.</param>
/// <param name="ExpiresOn">ISO date or null.</param>
/// <param name="Favorite">Pinned flag.</param>
/// <param name="CreatedAt">ISO-8601 timestamp.</param>
/// <param name="UpdatedAt">ISO-8601 timestamp.</param>
public sealed record RazEntryDto(
    long Id,
    string Title,
    string Secret,
    string Username,
    string Url,
    string Notes,
    string Tags,
    string TotpSeed,
    int TotpAlgorithm,
    int TotpDigits,
    int TotpPeriodSeconds,
    string? ExpiresOn,
    bool Favorite,
    string CreatedAt,
    string UpdatedAt);

/// <summary>
/// Versioned backup envelope. <see cref="Payload"/> holds the encrypted entry list; the
/// meta fields let any vault with the <em>same passphrase</em> re-derive the file's key
/// (key = PBKDF2(passphrase, Salt, Iterations), verified by <see cref="KeyCheck"/>).
/// </summary>
/// <param name="Version">Backup format version.</param>
/// <param name="Salt">Base64 KDF salt of the source vault.</param>
/// <param name="Iterations">KDF iteration count of the source vault.</param>
/// <param name="KeyCheck">Base64 key-check payload of the source vault.</param>
/// <param name="Payload">Base64 AES-GCM payload: a JSON list of <see cref="RazEntryDto"/>.</param>
public sealed record VaultBackupFile(
    int Version,
    string Salt,
    int Iterations,
    string KeyCheck,
    string Payload);
