using System.Globalization;
using JameJam.Raz.Ai;
using JameJam.Raz.Crypto;
using JameJam.Soroush;

namespace JameJam.Raz;

/// <summary>
/// The <c>raz</c> CLI: an encrypted personal vault. Secrets are never accepted on the
/// command line — they arrive generated, piped via stdin, or typed at a hidden prompt.
/// </summary>
/// <param name="store">Vault storage; null disables Raz (storage unavailable).</param>
/// <param name="clock">Time source for TOTP windows and expiry checks.</param>
/// <param name="output">Standard output.</param>
/// <param name="error">Error output.</param>
/// <param name="stdin">Piped input for <c>--secret-stdin</c>; defaults to the console.</param>
/// <param name="aiCompletion">Routes a prompt through the Soroush safety layer; null when AI is unavailable.</param>
/// <param name="options">Customizable rails; defaults apply when null.</param>
/// <param name="passphraseProvider">Resolves the passphrase (usually the environment variable).</param>
/// <param name="defaultLengthProvider">Resolves the stored default generated-password length.</param>
public sealed class RazCommands(
    IVaultStore? store,
    TimeProvider clock,
    TextWriter output,
    TextWriter error,
    TextReader? stdin = null,
    Func<AiRequest, CancellationToken, Task<SoroushResult>>? aiCompletion = null,
    RazOptions? options = null,
    Func<string?>? passphraseProvider = null,
    Func<int?>? defaultLengthProvider = null)
{
    /// <summary>Environment variable that carries the vault passphrase (never the command line).</summary>
    public const string PassphraseEnvironmentVariable = "JAMEJAM_RAZ_PASSPHRASE";

    private const string NoStorageMessage = "Vault storage is not available in this context.";
    private const string NoPassphraseMessage =
        "No passphrase available. Set JAMEJAM_RAZ_PASSPHRASE or run interactively to type it.";

    private readonly IVaultStore? _store = store;
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly TextWriter _output = output ?? throw new ArgumentNullException(nameof(output));
    private readonly TextWriter _error = error ?? throw new ArgumentNullException(nameof(error));
    private readonly TextReader _stdin = stdin ?? Console.In;
    private readonly Func<AiRequest, CancellationToken, Task<SoroushResult>>? _aiCompletion = aiCompletion;
    private readonly RazOptions _options = options ?? new RazOptions();
    private readonly Func<string?>? _passphraseProvider = passphraseProvider;
    private readonly Func<int?>? _defaultLengthProvider = defaultLengthProvider;
    private readonly SecurityAssistant _assistant = new(options);

    /// <summary>Runs a <c>raz</c> command; returns the process exit code.</summary>
    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
        {
            return Help();
        }

        try
        {
            return args[0] switch
            {
                "help" => Help(),
                "init" => RunInit(args[1..]),
                "add" => RunAdd(args[1..]),
                "list" => RunList(args[1..]),
                "show" => RunShow(args[1..]),
                "edit" => RunEdit(args[1..]),
                "delete" => RunDelete(args[1..]),
                "undo" => RunUndo(),
                "generate" => RunGenerate(args[1..]),
                "strength" => RunStrength(args[1..]),
                "totp" => RunTotp(args[1..]),
                "expire" => RunExpire(args[1..]),
                "audit" => RunAudit(),
                "export" => RunExport(args[1..]),
                "import" => RunImport(args[1..]),
                "ai" when args.Length >= 2 => await RunAiAsync(args[1..], cancellationToken).ConfigureAwait(false),
                "ai" => Fail("Usage: JameJam raz ai audit | raz ai ask <question...>"),
                _ => Fail($"Unknown raz command '{args[0]}'. Run 'JameJam raz help'."),
            };
        }
        catch (RazException ex)
        {
            return Fail(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Fail(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Fail(ex.Message);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return Fail("Wrong passphrase or corrupted vault.");
        }
        catch (SoroushException ex)
        {
            return Fail(ex.Message);
        }
        catch (FormatException)
        {
            return Fail("That value is not in the expected format (date: yyyy-MM-dd, TOTP seed: Base32).");
        }
        catch (IOException ex)
        {
            return Fail($"File error: {ex.Message}");
        }
    }

    // ── Command implementations ──

    private int RunInit(string[] args)
    {
        if (_store is null)
        {
            return Fail(NoStorageMessage);
        }

        var parsed = ParseFlags(args, "--iterations");
        var iterations = parsed.Get("--iterations") is { } text ? MoneyIterations(text) : _options.Iterations;
        var effective = _options with { Iterations = iterations };
        var passphrase = RequirePassphrase(confirm: true);
        var service = new VaultService(_store, _clock, effective);
        service.Init(passphrase);
        _output.WriteLine(FormattableString.Invariant(
            $"Vault initialized ({iterations} KDF iterations). Keep the passphrase safe — there is no recovery."));
        return 0;
    }

    private int RunAdd(string[] args)
    {
        if (args.Length < 1)
        {
            return Fail("Usage: JameJam raz add <title> [--username u] [--url u] [--notes t] [--tags a,b] "
                + "[--expires yyyy-MM-dd] [--favorite] [--generate] [--length N] [--secret-stdin] "
                + "[--totp sha1|sha256|sha512] [--digits N] [--period N] [--totp-secret-stdin]");
        }

        var parsed = ParseFlags(args[1..], "--username", "--url", "--notes", "--tags", "--expires", "--favorite",
            "--generate", "--length", "--no-symbols", "--no-ambiguous", "--secret-stdin",
            "--totp", "--digits", "--period", "--totp-secret-stdin");
        var totpSeed = parsed.Has("--totp-secret-stdin") ? ReadStdinSecret("TOTP seed") : string.Empty;
        var secret = ResolveNewSecret(null, parsed); // validate the secret source before unlocking
        var service = UnlockedService();
        if (parsed.Has("--generate"))
        {
            secret = service.Generate(PasswordPolicyFrom(parsed));
        }

        var entry = service.AddEntry(
            args[0],
            secret,
            parsed.Get("--username") ?? string.Empty,
            parsed.Get("--url") ?? string.Empty,
            parsed.Get("--notes") ?? string.Empty,
            parsed.Get("--tags") ?? string.Empty,
            totpSeed,
            ParseTotpAlgorithm(parsed.Get("--totp"), totpSeed),
            parsed.Get("--digits") is { } digits ? MoneyInt(digits, "TOTP digits") : null,
            parsed.Get("--period") is { } period ? MoneyInt(period, "TOTP period") : null,
            parsed.Get("--expires") is { } expires ? System.DateOnly.ParseExact(expires, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
            parsed.Has("--favorite"));
        var strength = StrengthMeter.Score(secret);
        _output.WriteLine(FormattableString.Invariant(
            $"Added #{entry.Id} {entry.Title} — strength: {strength.Label} ({strength.EntropyBits} bits)."));
        return 0;
    }

    private int RunList(string[] args)
    {
        var parsed = ParseFlags(args, "--tag", "--q", "--weak", "--expired", "--favorites");
        var service = UnlockedService();
        var filter = new VaultFilter(
            Tag: parsed.Get("--tag"),
            Query: parsed.Get("--q"),
            WeakOnly: parsed.Has("--weak"),
            ExpiredOnly: parsed.Has("--expired"),
            FavoritesOnly: parsed.Has("--favorites"));
        var rows = service.ListEntries(filter);
        if (rows.Count == 0)
        {
            _output.WriteLine("No entries match.");
            return 0;
        }

        foreach (var entry in rows)
        {
            _output.WriteLine(ListLine(service, entry));
        }

        return 0;
    }

    private int RunShow(string[] args)
    {
        if (args.Length < 1)
        {
            return Fail("Usage: JameJam raz show <id>");
        }

        var service = UnlockedService();
        var entry = service.FindEntry(MoneyInt(args[0], "Entry id"))
            ?? throw new RazException($"No entry #{MoneyInt(args[0], "Entry id")}.");
        var strength = StrengthMeter.Score(entry.Secret);
        _output.WriteLine(FormattableString.Invariant(
            $"#{entry.Id} — {entry.Title} ({strength.Label}, {strength.EntropyBits} bits)"));
        if (entry.Username.Length > 0)
        {
            _output.WriteLine($"  Username: {entry.Username}");
        }

        if (entry.Url.Length > 0)
        {
            _output.WriteLine($"  URL: {entry.Url}");
        }

        if (entry.Tags.Length > 0)
        {
            _output.WriteLine($"  Tags: {entry.Tags}");
        }

        if (entry.Notes.Length > 0)
        {
            _output.WriteLine($"  Notes: {entry.Notes}");
        }

        _output.WriteLine($"  Secret: {entry.Secret}");
        var totp = service.TotpNow(entry.Id);
        if (totp is { } code)
        {
            _output.WriteLine(FormattableString.Invariant($"  TOTP: {code.Code} ({code.SecondsRemaining}s left)"));
        }

        if (entry.ExpiresOn is { } day)
        {
            _output.WriteLine(FormattableString.Invariant($"  Expires: {day:yyyy-MM-dd} ({(day.DayNumber - service.Today.DayNumber)} days)"));
        }

        _output.WriteLine(FormattableString.Invariant($"  Updated: {entry.UpdatedAt:yyyy-MM-dd HH:mm} UTC"));
        return 0;
    }

    private int RunEdit(string[] args)
    {
        if (args.Length < 1)
        {
            return Fail("Usage: JameJam raz edit <id> [--title t] [--username u] [--url u] [--notes t] [--tags a,b] "
                + "[--expires yyyy-MM-dd] [--favorite] [--regenerate] [--length N] [--secret-stdin] [--clear-expires] [--clear-totp]");
        }

        var id = MoneyInt(args[0], "Entry id");
        var parsed = ParseFlags(args[1..], "--title", "--username", "--url", "--notes", "--tags", "--expires",
            "--favorite", "--regenerate", "--length", "--no-symbols", "--no-ambiguous", "--secret-stdin",
            "--clear-expires", "--clear-totp", "--totp", "--digits", "--period", "--totp-secret-stdin");
        var service = UnlockedService();
        var existing = service.FindEntry(id) ?? throw new RazException($"No entry #{id}.");

        string secret;
        if (parsed.Has("--regenerate"))
        {
            secret = service.Generate(PasswordPolicyFrom(parsed));
        }
        else if (parsed.Has("--secret-stdin"))
        {
            secret = ReadStdinSecret("Secret");
        }
        else
        {
            secret = existing.Secret; // untouched
        }

        var totpSeed = parsed.Has("--clear-totp")
            ? string.Empty
            : parsed.Has("--totp-secret-stdin")
                ? ReadStdinSecret("TOTP seed")
                : existing.TotpSeed;
        var algorithm = parsed.Has("--clear-totp")
            ? TotpAlgorithm.None
            : ParseTotpAlgorithm(parsed.Get("--totp"), totpSeed);

        var updated = existing with
        {
            Title = parsed.Get("--title") ?? existing.Title,
            Secret = secret,
            Username = parsed.Get("--username") ?? existing.Username,
            Url = parsed.Get("--url") ?? existing.Url,
            Notes = parsed.Get("--notes") ?? existing.Notes,
            Tags = parsed.Get("--tags") ?? existing.Tags,
            TotpSeed = totpSeed,
            TotpAlgorithm = algorithm,
            TotpDigits = parsed.Get("--digits") is { } digits
                ? MoneyInt(digits, "TOTP digits")
                : existing.TotpDigits >= RazDefaults.MinTotpDigits ? existing.TotpDigits : RazDefaults.DefaultTotpDigits,
            TotpPeriodSeconds = parsed.Get("--period") is { } period
                ? MoneyInt(period, "TOTP period")
                : existing.TotpPeriodSeconds >= RazDefaults.MinTotpPeriodSeconds ? existing.TotpPeriodSeconds : RazDefaults.DefaultTotpPeriodSeconds,
            ExpiresOn = parsed.Has("--clear-expires")
                ? null
                : parsed.Get("--expires") is { } expires
                    ? System.DateOnly.ParseExact(expires, "yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : existing.ExpiresOn,
            Favorite = parsed.Has("--favorite") ? true : existing.Favorite,
        };
        var saved = service.UpdateEntry(id, updated);
        var strength = StrengthMeter.Score(saved.Secret);
        _output.WriteLine(FormattableString.Invariant(
            $"Updated #{saved.Id} {saved.Title} — strength: {strength.Label} ({strength.EntropyBits} bits)."));
        return 0;
    }

    private int RunDelete(string[] args)
    {
        if (args.Length < 1)
        {
            return Fail("Usage: JameJam raz delete <id>");
        }

        var service = UnlockedService();
        var deleted = service.DeleteEntry(MoneyInt(args[0], "Entry id"));
        _output.WriteLine($"Deleted #{deleted.Id} ({deleted.Title}). raz undo brings it back.");
        return 0;
    }

    private int RunUndo()
    {
        var undone = UnlockedService().Undo();
        _output.WriteLine(undone ? "Undone — the last change is reverted." : "Nothing to undo.");
        return 0;
    }

    private int RunGenerate(string[] args)
    {
        var parsed = ParseFlags(args, "--length", "--count", "--no-symbols", "--no-ambiguous");
        var policy = PasswordPolicyFrom(parsed);
        var count = parsed.Get("--count") is { } text ? MoneyInt(text, "Count") : RazDefaults.DefaultGenerateCount;
        if (count is < 1 or > RazDefaults.MaxGenerateCount)
        {
            throw new RazException($"Count must be between 1 and {RazDefaults.MaxGenerateCount}.");
        }

        for (var i = 0; i < count; i++)
        {
            _output.WriteLine(PasswordGenerator.Generate(policy));
        }

        return 0;
    }

    private int RunStrength(string[] args)
    {
        ParseFlags(args);
        var secret = InputIsRedirected()
            ? _stdin.ReadLine()?.Trim()
            : ReadHidden("Secret to score: ");
        if (string.IsNullOrEmpty(secret))
        {
            throw new RazException("No secret arrived — pipe it in or type it at the prompt.");
        }

        var strength = StrengthMeter.Score(secret);
        var issues = strength.Issues.Count == 0 ? "no issues" : $"issues: {string.Join(", ", strength.Issues)}";
        _output.WriteLine(FormattableString.Invariant($"Score: {strength.Score}/4 ({strength.Label}) — {strength.EntropyBits} bits. {issues}."));
        return 0;
    }

    private int RunTotp(string[] args)
    {
        if (args.Length < 1)
        {
            return Fail("Usage: JameJam raz totp <id>");
        }

        var totp = UnlockedService().TotpNow(MoneyInt(args[0], "Entry id"));
        if (totp is null)
        {
            throw new RazException("That entry has no TOTP seed — add one with --totp <algorithm> --totp-secret-stdin.");
        }

        _output.WriteLine(FormattableString.Invariant($"{totp.Value.Code} ({totp.Value.SecondsRemaining}s left)"));
        return 0;
    }

    private int RunExpire(string[] args)
    {
        var parsed = ParseFlags(args, "--days");
        var window = parsed.Get("--days") is { } text ? (int?)MoneyInt(text, "Days") : null;
        var service = UnlockedService();
        var rows = service.ExpiringWithin(window);
        if (rows.Count == 0)
        {
            _output.WriteLine(FormattableString.Invariant(
                $"Nothing expires within {window ?? service.Options.ExpiringSoonDays} day(s)."));
            return 0;
        }

        foreach (var entry in rows)
        {
            _output.WriteLine(FormattableString.Invariant(
                $"#{entry.Id} {entry.Title} — expires {entry.ExpiresOn:yyyy-MM-dd}"));
        }

        return 0;
    }

    private int RunAudit()
    {
        var service = UnlockedService();
        var stats = service.Audit();
        _output.WriteLine(FormattableString.Invariant($"Vault audit — {stats.TotalEntries} entries"));
        _output.WriteLine(FormattableString.Invariant(
            $"  weak secrets: {stats.WeakCount} · reused: {stats.ReusedCount} · expired: {stats.ExpiredCount} · expiring soon: {stats.ExpiringSoonCount}"));
        _output.WriteLine(FormattableString.Invariant(
            $"  unchanged over {service.Options.OldAfterDays} days: {stats.OldCount} · distinct secrets: {stats.UniqueSecrets} · average length: {stats.AverageSecretLength}"));
        if (stats.TotalEntries == 0)
        {
            _output.WriteLine("The vault is empty — add an entry: JameJam raz add <title>");
        }
        else if (stats.WeakCount == 0 && stats.ReusedCount == 0 && stats.ExpiredCount == 0)
        {
            _output.WriteLine("No weak, reused, or expired secrets. ✔");
        }

        return 0;
    }

    private int RunExport(string[] args)
    {
        if (args.Length < 1)
        {
            return Fail("Usage: JameJam raz export <path>  (the file is fully encrypted)");
        }

        var bundle = UnlockedService().ExportJson();
        File.WriteAllText(args[0], bundle, System.Text.Encoding.UTF8);
        _output.WriteLine($"Vault exported to {args[0]} (encrypted). The same passphrase reopens it.");
        return 0;
    }

    private int RunImport(string[] args)
    {
        if (args.Length < 1)
        {
            return Fail("Usage: JameJam raz import <path>  (raz undo reverts it)");
        }

        var imported = UnlockedService().ImportJson(File.ReadAllText(args[0], System.Text.Encoding.UTF8));
        _output.WriteLine(FormattableString.Invariant($"Imported {imported} entry(ies). raz undo reverts it."));
        return 0;
    }

    private async Task<int> RunAiAsync(string[] args, CancellationToken cancellationToken)
    {
        if (_aiCompletion is null)
        {
            return Fail("AI is not available in this context.");
        }

        var service = UnlockedService();
        switch (args[0])
        {
            case "audit":
            {
                var response = await _aiCompletion(
                    new AiRequest(_assistant.BuildAuditPrompt(service.Audit())),
                    cancellationToken).ConfigureAwait(false);
                _output.WriteLine(response.Content);
                return 0;
            }

            case "ask":
            {
                var question = ParseQuestion(args[1..]);
                if (question.Length == 0)
                {
                    return Fail("Ask a question: JameJam raz ai ask \"how strong is my vault?\"");
                }

                var response = await _aiCompletion(
                    new AiRequest(_assistant.BuildAskPrompt(question, service.Audit())),
                    cancellationToken).ConfigureAwait(false);
                _output.WriteLine(response.Content);
                return 0;
            }

            default:
                return Fail("Usage: JameJam raz ai audit | raz ai ask <question...>");
        }
    }

    // ── Helpers ──

    private int Help()
    {
        _output.WriteLine("Raz — your encrypted personal vault, safe by construction");
        _output.WriteLine();
        _output.WriteLine("Secrets are never passed on the command line: generate them, pipe them, or type them at the hidden prompt.");
        _output.WriteLine("  raz init [--iterations N]                              Create the vault");
        _output.WriteLine("  raz add <title> [--username u] [--url u] [--notes t] [--tags a,b]");
        _output.WriteLine("          [--expires yyyy-MM-dd] [--favorite] [--generate] [--secret-stdin]");
        _output.WriteLine("          [--totp sha1|sha256|sha512] [--totp-secret-stdin]");
        _output.WriteLine("  raz list [--tag t] [--q text] [--weak] [--expired] [--favorites]");
        _output.WriteLine("  raz show <id> | edit <id> [add-flags] | delete <id> | undo");
        _output.WriteLine("  raz generate [--length N] [--count N] [--no-symbols] [--no-ambiguous]");
        _output.WriteLine("  raz strength                                            (reads stdin or the hidden prompt)");
        _output.WriteLine("  raz totp <id> | expire [--days N] | audit");
        _output.WriteLine("  raz export <path> | import <path>                       (always encrypted)");
        _output.WriteLine("  raz ai audit | ai ask <question...>                     (aggregate stats only — never secrets)");
        return 0;
    }

    private VaultService UnlockedService()
    {
        if (_store is null)
        {
            throw new RazException(NoStorageMessage);
        }

        var service = new VaultService(_store, _clock, EffectiveOptions());
        service.Unlock(RequirePassphrase());
        return service;
    }

    private RazOptions EffectiveOptions()
    {
        var length = _defaultLengthProvider?.Invoke();
        if (length is { } value)
        {
            return _options with { PasswordLength = value };
        }

        return _options;
    }

    private string RequirePassphrase(bool confirm = false)
    {
        var fromProvider = _passphraseProvider?.Invoke();
        if (!string.IsNullOrWhiteSpace(fromProvider))
        {
            return fromProvider;
        }

        if (InputIsRedirected())
        {
            throw new RazException(NoPassphraseMessage);
        }

        var first = ReadHidden("Raz passphrase: ");
        if (first.Length == 0)
        {
            throw new RazException("The passphrase cannot be empty.");
        }

        if (!confirm)
        {
            return first;
        }

        var second = ReadHidden("Confirm passphrase: ");
        if (first != second)
        {
            throw new RazException("The passphrases do not match.");
        }

        return first;
    }

    /// <summary>
    /// True when stdin is not an interactive terminal. An injected <see cref="TextReader"/>
    /// (tests, hosted use) always counts as redirected — the hidden prompt is never used there.
    /// </summary>
    private bool InputIsRedirected() =>
        !ReferenceEquals(_stdin, Console.In) || Console.IsInputRedirected;

    private static string ReadHidden(string label)
    {
        Console.Write(label);
        List<char> chars = [];
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (chars.Count > 0)
                {
                    chars.RemoveAt(chars.Count - 1);
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                chars.Add(key.KeyChar);
            }
        }

        return new string([.. chars]);
    }

    private string ReadStdinSecret(string label)
    {
        var line = _stdin.ReadLine();
        if (string.IsNullOrEmpty(line))
        {
            throw new RazException($"No {label} arrived on stdin.");
        }

        return line.TrimEnd('\r', '\n');
    }

    private string ResolveNewSecret(VaultService? service, FlagParser parsed)
    {
        if (parsed.Has("--generate"))
        {
            return (service ?? UnlockedService()).Generate(PasswordPolicyFrom(parsed));
        }

        if (parsed.Has("--secret-stdin"))
        {
            return ReadStdinSecret("Secret");
        }

        if (InputIsRedirected())
        {
            throw new RazException(
                "Provide the secret with --generate or --secret-stdin (never on the command line).");
        }

        return ReadHidden("Secret: ");
    }

    private PasswordPolicy PasswordPolicyFrom(FlagParser parsed)
    {
        var length = parsed.Get("--length") is { } text ? MoneyInt(text, "Length") : EffectiveOptions().PasswordLength;
        return new PasswordPolicy(
            Length: length,
            Symbol: !parsed.Has("--no-symbols"),
            ExcludeAmbiguous: parsed.Has("--no-ambiguous"));
    }

    private static TotpAlgorithm ParseTotpAlgorithm(string? text, string? seed)
    {
        if (text is null)
        {
            return seed is { } s && s.Trim().Length > 0 ? TotpAlgorithm.Sha1 : TotpAlgorithm.None;
        }

        return text.ToLowerInvariant() switch
        {
            "sha1" => TotpAlgorithm.Sha1,
            "sha256" => TotpAlgorithm.Sha256,
            "sha512" => TotpAlgorithm.Sha512,
            _ => throw new RazException("TOTP algorithm must be sha1, sha256, or sha512."),
        };
    }

    private static int MoneyIterations(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var iterations)
            ? iterations
            : throw new RazException("Iterations must be a positive number.");

    private static int MoneyInt(string text, string label) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : throw new RazException($"{label} must be a positive number.");

    private static string ListLine(VaultService service, RazEntry entry)
    {
        var strength = StrengthMeter.Score(entry.Secret);
        var markers = string.Empty;
        if (strength.Score <= service.Options.WeakScoreThreshold)
        {
            markers += "  ⚠ weak";
        }

        if (entry.ExpiresOn is { } day)
        {
            markers += day < service.Today ? "  ⏰ expired" : FormattableString.Invariant($"  ⏰ {day:yyyy-MM-dd}");
        }

        var favorite = entry.Favorite ? "★ " : string.Empty;
        var username = entry.Username.Length == 0 ? string.Empty : $" — {entry.Username}";
        return FormattableString.Invariant($"#{entry.Id}  {favorite}{entry.Title}{username} [{strength.Label}]{markers}");
    }

    private static string ParseQuestion(string[] args)
    {
        List<string> parts = [];
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith('-'))
            {
                i++; // skip the flag's value; AI questions carry no flags today
                continue;
            }

            parts.Add(args[i]);
        }

        return string.Join(' ', parts);
    }

    private static FlagParser ParseFlags(string[] args, params string[] known)
    {
        var parser = new FlagParser(args, known);
        parser.Collect();
        return parser;
    }

    private int Fail(string message)
    {
        _error.WriteLine(message);
        return 1;
    }

    /// <summary>Strict flag parser: unknown options fail, flags consume the next non-dash token.</summary>
    private sealed class FlagParser(string[] args, string[] known)
    {
        private readonly Dictionary<string, string?> _values = [];

        public string? Get(string flag) => _values.GetValueOrDefault(flag);

        public bool Has(string flag) => _values.ContainsKey(flag);

        public void Collect()
        {
            for (var i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith('-'))
                {
                    continue;
                }

                if (!known.Contains(args[i]))
                {
                    throw new RazException($"Unknown option '{args[i]}'.");
                }

                if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                {
                    _values[args[i]] = args[i + 1];
                    i++;
                }
                else
                {
                    _values[args[i]] = null;
                }
            }
        }
    }
}
