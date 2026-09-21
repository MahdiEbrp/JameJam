using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using JameJam.Raz.Crypto;

namespace JameJam.Raz;

/// <summary>Filters accepted by <see cref="VaultService.ListEntries"/>.</summary>
/// <param name="Tag">Only entries whose tags contain this (case-insensitive).</param>
/// <param name="Query">Substring match over decrypted fields.</param>
/// <param name="WeakOnly">Only entries whose strength is at or below the weak threshold.</param>
/// <param name="ExpiredOnly">Only entries past their expiry date.</param>
/// <param name="FavoritesOnly">Only favorites.</param>
public sealed record VaultFilter(
    string? Tag = null,
    string? Query = null,
    bool WeakOnly = false,
    bool ExpiredOnly = false,
    bool FavoritesOnly = false);

/// <summary>Aggregate audit statistics. Deliberately contains no titles or identifying data.</summary>
/// <param name="TotalEntries">Entries in the vault.</param>
/// <param name="WeakCount">Entries at or below the weak threshold.</param>
/// <param name="ReusedCount">Entries sharing a secret with at least one other entry.</param>
/// <param name="ExpiredCount">Entries past their expiry date.</param>
/// <param name="ExpiringSoonCount">Entries expiring within the configured window.</param>
/// <param name="OldCount">Entries not changed for longer than the rotation window.</param>
/// <param name="AverageSecretLength">Mean secret length (a number, never the secrets).</param>
/// <param name="UniqueSecrets">Distinct secret values.</param>
public sealed record VaultAuditStats(
    int TotalEntries,
    int WeakCount,
    int ReusedCount,
    int ExpiredCount,
    int ExpiringSoonCount,
    int OldCount,
    int AverageSecretLength,
    int UniqueSecrets);

/// <summary>
/// Business logic for the Raz vault: unlock, entry CRUD, generation, TOTP, audit,
/// expiry reminders, encrypted undo, and export/import. The derived key lives only in
/// this instance; every snapshot pushed to the store is ciphertext.
/// </summary>
public sealed class VaultService
{
    private readonly IVaultStore _store;
    private readonly TimeProvider _clock;
    private readonly RazOptions _options;
    private byte[]? _key;
    private string? _passphrase; // kept only to re-derive keys for portable backups; never persisted

    /// <summary>Initializes the service over a store with validated options.</summary>
    public VaultService(IVaultStore store, TimeProvider clock, RazOptions? options = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? new RazOptions();
        _options.Validate();
        store.UndoDepth = _options.UndoDepth;
    }

    /// <summary>The validated options in effect.</summary>
    public RazOptions Options => _options;

    /// <summary>Today according to the injected clock.</summary>
    public DateOnly Today => DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);

    /// <summary>True once a passphrase has been accepted this lifetime.</summary>
    public bool IsUnlocked => _key is not null;

    /// <summary>
    /// Creates the vault with a fresh salt and key-check row.
    /// </summary>
    /// <exception cref="RazException">The vault already exists.</exception>
    public void Init(string passphrase)
    {
        if (_store.IsInitialized)
        {
            throw new RazException("This vault already exists — unlock it instead of initializing again.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(passphrase);
        var salt = RandomNumberGenerator.GetBytes(RazDefaults.SaltSizeBytes);
        var key = VaultCrypto.DeriveKey(passphrase, salt, _options.Iterations);
        var check = VaultCrypto.Encrypt(key, RazDefaults.KeyCheckPlaintext);
        _store.SetMeta(salt, _options.Iterations, check);
        _key = key;
        _passphrase = passphrase;
    }

    /// <summary>
    /// Verifies the passphrase against the key-check row and unlocks the service.
    /// </summary>
    /// <exception cref="RazException">No vault yet, or wrong passphrase.</exception>
    public void Unlock(string passphrase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passphrase);
        if (!_store.IsInitialized)
        {
            throw new RazException("No vault yet — create one first: JameJam raz init");
        }

        var salt = _store.GetSalt() ?? throw new RazException("Vault meta is missing its salt.");
        var check = _store.GetKeyCheck() ?? throw new RazException("Vault meta is missing its key check.");
        var key = VaultCrypto.DeriveKey(passphrase, salt, _store.GetIterations());
        string verified;
        try
        {
            verified = VaultCrypto.Decrypt(key, check);
        }
        catch (CryptographicException)
        {
            VaultCrypto.Wipe(key);
            throw new RazException("Wrong passphrase or corrupted vault.");
        }

        if (verified != RazDefaults.KeyCheckPlaintext)
        {
            VaultCrypto.Wipe(key);
            throw new RazException("Wrong passphrase or corrupted vault.");
        }

        _key = key;
        _passphrase = passphrase;
    }

    /// <summary>Guards the rest of the service: an unlocked service is required.</summary>
    private byte[] RequireKey() =>
        _key ?? throw new RazException("The vault is locked — set the passphrase first.");

    // ── Entry lifecycle ──

    /// <summary>Adds an entry (encrypting sensitive fields) with an undo snapshot.</summary>
    public RazEntry AddEntry(
        string title,
        string secret,
        string username = "",
        string url = "",
        string notes = "",
        string tags = "",
        string totpSeed = "",
        TotpAlgorithm totpAlgorithm = TotpAlgorithm.None,
        int? totpDigits = null,
        int? totpPeriodSeconds = null,
        DateOnly? expiresOn = null,
        bool favorite = false)
    {
        RequireKey();
        if (_store.Count() >= RazDefaults.MaxEntries)
        {
            throw new RazException($"At most {RazDefaults.MaxEntries} entries are allowed.");
        }

        PushSnapshot();
        var now = _clock.GetUtcNow();
        var algorithm = NormalizeTotp(totpAlgorithm, totpSeed, totpDigits, totpPeriodSeconds);
        var plaintext = new RazEntry(
            0,
            Clean(title, RazDefaults.MaxTitleLength),
            secret,
            Clean(username, RazDefaults.MaxFieldLength),
            Clean(url, RazDefaults.MaxFieldLength),
            Clean(notes, RazDefaults.MaxFieldLength),
            CleanTags(tags),
            Clean(totpSeed, RazDefaults.MaxFieldLength),
            algorithm.Algorithm,
            algorithm.Digits,
            algorithm.Period,
            expiresOn,
            favorite,
            now,
            now);
        var stored = _store.AddEntry(EncryptForStore(plaintext));
        return plaintext with { Id = stored.Id };
    }

    /// <summary>Updates an entry, re-encrypting changed fields, with an undo snapshot.</summary>
    public RazEntry UpdateEntry(long id, RazEntry updated)
    {
        ArgumentNullException.ThrowIfNull(updated);
        _ = FindEntry(id) ?? throw new RazException($"No entry #{id}.");
        PushSnapshot();

        var existing = _store.FindEntry(id)!;
        var now = _clock.GetUtcNow();
        var algorithm = NormalizeTotp(updated.TotpAlgorithm, updated.TotpSeed, updated.TotpDigits, updated.TotpPeriodSeconds);
        var merged = existing with
        {
            Title = Clean(updated.Title, RazDefaults.MaxTitleLength),
            Secret = updated.Secret,
            Username = Clean(updated.Username, RazDefaults.MaxFieldLength),
            Url = Clean(updated.Url, RazDefaults.MaxFieldLength),
            Notes = Clean(updated.Notes, RazDefaults.MaxFieldLength),
            Tags = CleanTags(updated.Tags),
            TotpSeed = Clean(updated.TotpSeed, RazDefaults.MaxFieldLength),
            TotpAlgorithm = algorithm.Algorithm,
            TotpDigits = algorithm.Digits,
            TotpPeriodSeconds = algorithm.Period,
            ExpiresOn = updated.ExpiresOn,
            Favorite = updated.Favorite,
            UpdatedAt = now,
        };
        _store.UpdateEntry(EncryptForStore(merged));
        return merged;
    }

    /// <summary>Deletes an entry (undo brings it back).</summary>
    public RazEntry DeleteEntry(long id)
    {
        _ = FindEntry(id) ?? throw new RazException($"No entry #{id}.");
        PushSnapshot();
        var encrypted = _store.FindEntry(id)!;
        _ = _store.RemoveEntry(id);
        return Decrypt(encrypted);
    }

    /// <summary>Gets and decrypts one entry.</summary>
    public RazEntry? FindEntry(long id) =>
        _store.FindEntry(id) is { } encrypted ? Decrypt(encrypted) : null;

    /// <summary>Lists and decrypts entries under the filter.</summary>
    public IReadOnlyList<RazEntry> ListEntries(VaultFilter? filter = null)
    {
        if (!_store.IsInitialized)
        {
            throw new RazException("No vault yet — create one first: JameJam raz init");
        }

        RequireKey();
        var filterOrDefault = filter ?? new VaultFilter();
        var today = Today;
        return _store.ListEntries()
            .Select(Decrypt)
            .Where(e => filterOrDefault.Tag is null || TagsOf(e).Contains(filterOrDefault.Tag, StringComparer.OrdinalIgnoreCase))
            .Where(e => filterOrDefault.FavoritesOnly == false || e.Favorite)
            .Where(e => filterOrDefault.ExpiredOnly == false || (e.ExpiresOn is { } day && day < today))
            .Where(e => filterOrDefault.WeakOnly == false || StrengthMeter.Score(e.Secret).Score <= _options.WeakScoreThreshold)
            .Where(e => filterOrDefault.Query is null || Matches(e, filterOrDefault.Query))
            .ToList();
    }

    /// <summary>Deletes everything (used by restore), then reinserts the snapshot's entries.</summary>
    public bool Undo()
    {
        var key = RequireKey();
        var payload = _store.PopUndo();
        if (payload is null)
        {
            return false;
        }

        List<RazEntryDto>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<RazEntryDto>>(VaultCrypto.Decrypt(key, payload));
        }
        catch (JsonException)
        {
            throw new RazException("The undo snapshot is unreadable.");
        }
        catch (ArgumentException)
        {
            throw new RazException("The undo snapshot is unreadable.");
        }

        _store.ReplaceEntries((entries ?? []).Select(FromDto).ToList());
        return true;
    }

    // ── Insight ──

    /// <summary>Computes aggregate audit statistics (safe to share with the AI coach).</summary>
    public VaultAuditStats Audit()
    {
        var entries = ListEntries();
        var today = Today;
        var bySecret = entries.GroupBy(e => e.Secret, StringComparer.Ordinal).ToList();
        var reused = bySecret.Where(g => g.Count() > 1).Sum(g => g.Count());
        return new VaultAuditStats(
            entries.Count,
            entries.Count(e => StrengthMeter.Score(e.Secret).Score <= _options.WeakScoreThreshold),
            reused,
            entries.Count(e => e.ExpiresOn is { } day && day < today),
            entries.Count(e => e.ExpiresOn is { } day && day >= today && day <= today.AddDays(_options.ExpiringSoonDays)),
            entries.Count(e => e.UpdatedAt <= _clock.GetUtcNow().AddDays(-_options.OldAfterDays)),
            entries.Count == 0 ? 0 : (int)Math.Round(entries.Average(e => e.Secret.Length)),
            bySecret.Count);
    }

    /// <summary>Entries expiring within <paramref name="days"/> (default: the configured window).</summary>
    public IReadOnlyList<RazEntry> ExpiringWithin(int? days = null)
    {
        var window = days ?? _options.ExpiringSoonDays;
        if (window is < 1 or > RazDefaults.ExpiringSoonDaysBound)
        {
            throw new RazException($"Days must be between 1 and {RazDefaults.ExpiringSoonDaysBound}.");
        }

        var today = Today;
        return ListEntries()
            .Where(e => e.ExpiresOn is { } day && day >= today && day <= today.AddDays(window))
            .OrderBy(e => e.ExpiresOn)
            .ToList();
    }

    /// <summary>Current TOTP code for an entry plus seconds remaining.</summary>
    public (string Code, int SecondsRemaining)? TotpNow(long id)
    {
        var entry = FindEntry(id) ?? throw new RazException($"No entry #{id}.");
        var now = _clock.GetUtcNow();
        var code = Totp.CodeFor(entry, now);
        return code is null ? null : (code, Totp.SecondsRemaining(now, entry.TotpPeriodSeconds));
    }

    /// <summary>Generates a password under the (validated) policy.</summary>
    public string Generate(PasswordPolicy? policy = null)
    {
        var effective = policy ?? new PasswordPolicy(Length: _options.PasswordLength);
        return PasswordGenerator.Generate(effective);
    }

    // ── Export / import (ciphertext-safe) ──

    /// <summary>
    /// Serializes the vault to a backup bundle. The envelope carries the KDF meta so any
    /// vault with the same passphrase reopens it; the entry list stays AES-GCM encrypted.
    /// </summary>
    public string ExportJson()
    {
        var key = RequireKey();
        var inner = JsonSerializer.Serialize(_store.ListEntries().Select(ToDto).ToList());
        var file = new VaultBackupFile(
            RazDefaults.BackupVersion,
            Convert.ToBase64String(_store.GetSalt() ?? []),
            _store.GetIterations(),
            Convert.ToBase64String(_store.GetKeyCheck() ?? []),
            Convert.ToBase64String(VaultCrypto.Encrypt(key, inner)));
        return JsonSerializer.Serialize(file);
    }

    /// <summary>
    /// Imports a backup bundle: verifies the bundle's key check under the current
    /// passphrase, decrypts its payload, and inserts the entries (fresh ids, undo-able).
    /// </summary>
    public int ImportJson(string bundle)
    {
        if (string.IsNullOrWhiteSpace(bundle))
        {
            throw new RazException("This file is not a Raz backup.");
        }
        var key = RequireKey();
        if (_passphrase is null)
        {
            throw new RazException("The vault is locked.");
        }

        VaultBackupFile? file;
        try
        {
            file = JsonSerializer.Deserialize<VaultBackupFile>(bundle);
        }
        catch (JsonException)
        {
            throw new RazException("This file is not a Raz backup.");
        }

        if (file is null || file.Version != RazDefaults.BackupVersion)
        {
            throw new RazException("This backup version is not supported.");
        }

        var fileKey = VaultCrypto.DeriveKey(_passphrase, Convert.FromBase64String(file.Salt), file.Iterations);
        string verified;
        string inner;
        try
        {
            verified = VaultCrypto.Decrypt(fileKey, Convert.FromBase64String(file.KeyCheck));
            if (verified != RazDefaults.KeyCheckPlaintext)
            {
                throw new RazException("This backup was made under a different passphrase — unlock with that passphrase first.");
            }

            inner = VaultCrypto.Decrypt(fileKey, Convert.FromBase64String(file.Payload));
        }
        catch (CryptographicException)
        {
            throw new RazException("This backup was made under a different passphrase — unlock with that passphrase first.");
        }
        catch (FormatException)
        {
            throw new RazException("This file is not a Raz backup.");
        }

        List<RazEntryDto>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<RazEntryDto>>(inner);
        }
        catch (JsonException)
        {
            throw new RazException("This backup's contents are unreadable.");
        }

        PushSnapshot();
        var imported = 0;
        foreach (var dto in entries ?? [])
        {
            var plaintext = new RazEntry(
                0,
                VaultCrypto.Decrypt(fileKey, VaultCrypto.FromText(dto.Title)),
                VaultCrypto.Decrypt(fileKey, VaultCrypto.FromText(dto.Secret)),
                VaultCrypto.Decrypt(fileKey, VaultCrypto.FromText(dto.Username)),
                VaultCrypto.Decrypt(fileKey, VaultCrypto.FromText(dto.Url)),
                VaultCrypto.Decrypt(fileKey, VaultCrypto.FromText(dto.Notes)),
                VaultCrypto.Decrypt(fileKey, VaultCrypto.FromText(dto.Tags)),
                VaultCrypto.Decrypt(fileKey, VaultCrypto.FromText(dto.TotpSeed)),
                (TotpAlgorithm)dto.TotpAlgorithm,
                dto.TotpDigits,
                dto.TotpPeriodSeconds,
                dto.ExpiresOn is { } day ? DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
                dto.Favorite,
                DateTimeOffset.Parse(dto.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                DateTimeOffset.Parse(dto.UpdatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
            _ = _store.AddEntry(EncryptForStore(plaintext));
            imported++;
        }

        return imported;
    }

    // ── Internals ──

    private static string ToTextOrThrow(byte[] key, string secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            throw new RazException("A secret is required — pipe it in, generate one, or type it at the prompt.");
        }

        return VaultCrypto.ToText(VaultCrypto.Encrypt(key, secret));
    }

    private static string Enc(byte[] key, string value) =>
        VaultCrypto.ToText(VaultCrypto.Encrypt(key, value));

    /// <summary>
    /// Maps a plaintext entry to its store form: the eight sensitive fields become
    /// Base64 AES-GCM ciphertext; bookkeeping stays clear.
    /// </summary>
    private RazEntry EncryptForStore(RazEntry plain)
    {
        var key = RequireKey();
        return plain with
        {
            Title = Enc(key, plain.Title),
            Secret = ToTextOrThrow(key, plain.Secret),
            Username = Enc(key, plain.Username),
            Url = Enc(key, plain.Url),
            Notes = Enc(key, plain.Notes),
            Tags = Enc(key, CleanTags(plain.Tags)),
            TotpSeed = Enc(key, plain.TotpSeed),
        };
    }

    private RazEntry Decrypt(RazEntry encrypted)
    {
        var key = RequireKey();
        return encrypted with
        {
            Title = VaultCrypto.Decrypt(key, VaultCrypto.FromText(encrypted.Title)),
            Secret = VaultCrypto.Decrypt(key, VaultCrypto.FromText(encrypted.Secret)),
            Username = VaultCrypto.Decrypt(key, VaultCrypto.FromText(encrypted.Username)),
            Url = VaultCrypto.Decrypt(key, VaultCrypto.FromText(encrypted.Url)),
            Notes = VaultCrypto.Decrypt(key, VaultCrypto.FromText(encrypted.Notes)),
            Tags = VaultCrypto.Decrypt(key, VaultCrypto.FromText(encrypted.Tags)),
            TotpSeed = VaultCrypto.Decrypt(key, VaultCrypto.FromText(encrypted.TotpSeed)),
        };
    }

    private static bool Matches(RazEntry entry, string query)
    {
        if (entry.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || entry.Username.Contains(query, StringComparison.OrdinalIgnoreCase)
            || entry.Url.Contains(query, StringComparison.OrdinalIgnoreCase)
            || entry.Notes.Contains(query, StringComparison.OrdinalIgnoreCase)
            || TagsOf(entry).Contains(query, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static string[] TagsOf(RazEntry entry) =>
        entry.Tags.Length == 0 ? [] : entry.Tags.Split(',');

    private static string CleanTags(string tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var split = tags
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(RazDefaults.MaxTagsPerEntry)
            .ToList();
        var joined = string.Join(',', split);
        return joined.Length <= RazDefaults.MaxFieldLength ? joined : joined[..RazDefaults.MaxFieldLength];
    }

    private static string Clean(string? value, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(value);
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static (TotpAlgorithm Algorithm, int Digits, int Period) NormalizeTotp(
        TotpAlgorithm algorithm, string seed, int? digits, int? period)
    {
        var hasSeed = seed.Trim().Length > 0;
        if (algorithm == TotpAlgorithm.None && !hasSeed)
        {
            return (TotpAlgorithm.None, 0, 0);
        }

        var resolvedAlgorithm = algorithm == TotpAlgorithm.None ? TotpAlgorithm.Sha1 : algorithm;
        if (!hasSeed)
        {
            throw new RazException("A TOTP seed is required — pipe it in with --totp-secret-stdin.");
        }
        var resolvedDigits = digits ?? RazDefaults.DefaultTotpDigits;
        var resolvedPeriod = period ?? RazDefaults.DefaultTotpPeriodSeconds;
        if (resolvedDigits is < RazDefaults.MinTotpDigits or > RazDefaults.MaxTotpDigits)
        {
            throw new RazException($"TOTP digits must be between {RazDefaults.MinTotpDigits} and {RazDefaults.MaxTotpDigits}.");
        }

        if (resolvedPeriod is < RazDefaults.MinTotpPeriodSeconds or > RazDefaults.MaxTotpPeriodSeconds)
        {
            throw new RazException($"TOTP period must be between {RazDefaults.MinTotpPeriodSeconds} and {RazDefaults.MaxTotpPeriodSeconds} seconds.");
        }

        return (resolvedAlgorithm, resolvedDigits, resolvedPeriod);
    }

    private void PushSnapshot()
    {
        var key = RequireKey();
        var plain = JsonSerializer.Serialize(_store.ListEntries().Select(ToDto).ToList());
        _store.PushUndo(VaultCrypto.Encrypt(key, plain));
    }

    private static RazEntryDto ToDto(RazEntry entry) => new(
        entry.Id,
        entry.Title,
        entry.Secret,
        entry.Username,
        entry.Url,
        entry.Notes,
        entry.Tags,
        entry.TotpSeed,
        (int)entry.TotpAlgorithm,
        entry.TotpDigits,
        entry.TotpPeriodSeconds,
        entry.ExpiresOn?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        entry.Favorite,
        entry.CreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        entry.UpdatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

    private static RazEntry FromDto(RazEntryDto dto)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dto.Title);
        ArgumentException.ThrowIfNullOrWhiteSpace(dto.Secret);
        return new RazEntry(
            dto.Id,
            dto.Title,
            dto.Secret,
            dto.Username,
            dto.Url,
            dto.Notes,
            dto.Tags,
            dto.TotpSeed,
            (TotpAlgorithm)dto.TotpAlgorithm,
            dto.TotpDigits,
            dto.TotpPeriodSeconds,
            dto.ExpiresOn is { } day ? DateOnly.ParseExact(day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : null,
            dto.Favorite,
            DateTimeOffset.Parse(dto.CreatedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(dto.UpdatedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind));
    }
}
