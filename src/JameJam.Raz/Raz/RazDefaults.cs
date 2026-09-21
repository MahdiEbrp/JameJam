namespace JameJam.Raz;

/// <summary>
/// Central, documented constants for the Raz vault. Nothing is hardcoded elsewhere:
/// every bound is referenced from here and made customizable through <see cref="RazOptions"/> rails.
/// </summary>
public static class RazDefaults
{
    // ── Crypto ──

    /// <summary>Salt length for PBKDF2 (256 bits).</summary>
    public const int SaltSizeBytes = 32;

    /// <summary>Derived key length (AES-256).</summary>
    public const int KeySizeBytes = 32;

    /// <summary>GCM nonce length (the standard 96 bits).</summary>
    public const int NonceSizeBytes = 12;

    /// <summary>GCM authentication tag length (128 bits).</summary>
    public const int TagSizeBytes = 16;

    /// <summary>Lowest acceptable PBKDF2 iteration count.</summary>
    public const int MinIterations = 100_000;

    /// <summary>Default PBKDF2-HMAC-SHA512 iteration count (OWASP guidance).</summary>
    public const int DefaultIterations = 210_000;

    /// <summary>Highest acceptable PBKDF2 iteration count.</summary>
    public const int MaxIterations = 10_000_000;

    /// <summary>Plaintext check token encrypted on init and verified on unlock.</summary>
    public const string KeyCheckPlaintext = "raz-key-check-v1";

    /// <summary>Current backup format version.</summary>
    public const int BackupVersion = 1;

    // ── Password generation ──

    /// <summary>Default generated password length.</summary>
    public const int DefaultPasswordLength = 20;

    /// <summary>Shortest allowed generated password.</summary>
    public const int MinPasswordLength = 8;

    /// <summary>Longest allowed generated password.</summary>
    public const int MaxPasswordLength = 256;

    /// <summary>Default number of passwords produced by <c>raz generate</c>.</summary>
    public const int DefaultGenerateCount = 1;

    /// <summary>Highest number of passwords a single <c>raz generate</c> may produce.</summary>
    public const int MaxGenerateCount = 50;

    // ── TOTP (RFC 6238) ──

    /// <summary>Default TOTP code length.</summary>
    public const int DefaultTotpDigits = 6;

    /// <summary>Shortest TOTP code.</summary>
    public const int MinTotpDigits = 6;

    /// <summary>Longest TOTP code.</summary>
    public const int MaxTotpDigits = 8;

    /// <summary>Default TOTP time step (seconds).</summary>
    public const int DefaultTotpPeriodSeconds = 30;

    /// <summary>Shortest allowed time step.</summary>
    public const int MinTotpPeriodSeconds = 15;

    /// <summary>Longest allowed time step.</summary>
    public const int MaxTotpPeriodSeconds = 120;

    // ── Vault policy ──

    /// <summary>Undo snapshots kept (parity with the wallet; snapshots are ciphertext).</summary>
    public const int UndoDepth = 20;

    /// <summary>Upper rail for the undo depth.</summary>
    public const int UndoDepthBound = 100;

    /// <summary>A secret older than this many days shows up in audits as "rotate me".</summary>
    public const int OldAfterDays = 365;

    /// <summary>"Expiring soon" window for reminders and audits.</summary>
    public const int ExpiringSoonDays = 30;

    /// <summary>Upper rail for the expiring-soon window.</summary>
    public const int ExpiringSoonDaysBound = 3_650;

    /// <summary>Maximum number of entries in one vault.</summary>
    public const int MaxEntries = 5_000;

    /// <summary>Strength scores at or below this are flagged as weak (scale 0–4).</summary>
    public const int WeakScoreThreshold = 1;

    // ── Field bounds ──

    /// <summary>Longest entry title.</summary>
    public const int MaxTitleLength = 100;

    /// <summary>Longest username / url / notes / tags field.</summary>
    public const int MaxFieldLength = 400;

    /// <summary>Maximum tags per entry.</summary>
    public const int MaxTagsPerEntry = 10;

    /// <summary>Longest passphrase the CLI accepts.</summary>
    public const int MaxPassphraseLength = 1_024;

    /// <summary>Longest AI question accepted from the CLI.</summary>
    public const int MaxAiQuestionChars = 400;
}
