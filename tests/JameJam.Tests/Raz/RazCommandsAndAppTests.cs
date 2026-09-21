using JameJam.Raz;
using JameJam.Settings;
using JameJam.Soroush;

namespace JameJam.Tests.Raz;

/// <summary>The <c>raz</c> CLI: verbs, strict parsing, friendly failures, and the AI gate.</summary>
public sealed class RazCommandsTests
{
    private static readonly FixedTimeProvider Clock =
        new(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));

    private const string Passphrase = "correct-horse-battery";

    private static RazCommands Build(
        out StringWriter output,
        out StringWriter error,
        string? stdin = null,
        MemoryVaultStore? store = null,
        Func<AiRequest, CancellationToken, Task<SoroushResult>>? ai = null,
        Func<int?>? defaultLength = null) =>
        new(
            store ?? new MemoryVaultStore(),
            Clock,
            output = new StringWriter(),
            error = new StringWriter(),
            stdin: stdin is null ? null : new StringReader(stdin),
            aiCompletion: ai,
            options: new RazOptions(Iterations: RazDefaults.MinIterations),
            passphraseProvider: () => Passphrase,
            defaultLengthProvider: defaultLength);

    [Fact]
    public async Task Help_ListsTheArsenal()
    {
        var commands = Build(out var output, out _);

        Assert.Equal(0, await commands.RunAsync(["help"]));

        var text = output.ToString();
        Assert.Contains("raz init", text, StringComparison.Ordinal);
        Assert.Contains("--secret-stdin", text, StringComparison.Ordinal);
        Assert.Contains("totp", text, StringComparison.Ordinal);
        Assert.Contains("ai audit", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Init_Add_Undo_Lifecycle()
    {
        var commands = Build(out var output, out _, stdin: "hunter2!\n");

        Assert.Equal(0, await commands.RunAsync(["init"]));
        Assert.Contains("Vault initialized", output.ToString(), StringComparison.Ordinal);

        Assert.Equal(0, await commands.RunAsync(["add", "GitHub", "--username", "octocat", "--secret-stdin"]));
        Assert.Contains("Added #1 GitHub", output.ToString(), StringComparison.Ordinal);

        Assert.Equal(0, await commands.RunAsync(["delete", "1"]));
        Assert.Contains("raz undo brings it back", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, await commands.RunAsync(["undo"]));
        Assert.Equal(0, await commands.RunAsync(["list"]));
        Assert.Contains("#1  GitHub — octocat", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_NeedsASecret()
    {
        var commands = Build(out _, out var error, stdin: "");

        Assert.Equal(1, await commands.RunAsync(["add", "GitHub"]));
        Assert.Contains("Provide the secret", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyStdinSecret_IsRejected()
    {
        var commands = Build(out _, out var error, stdin: "\n");

        Assert.Equal(1, await commands.RunAsync(["add", "GitHub", "--secret-stdin"]));
        Assert.Contains("No Secret arrived on stdin", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_FiltersAndMarks()
    {
        var commands = Build(out var output, out _, stdin: "long-solid-secret-1!\nshort\n");
        await commands.RunAsync(["init"]);
        await commands.RunAsync(["add", "Solid", "--secret-stdin", "--favorite"]);
        await commands.RunAsync(["add", "Shaky", "--secret-stdin", "--expires", "2026-01-01"]);

        output.GetStringBuilder().Clear();
        Assert.Equal(0, await commands.RunAsync(["list"]));
        var text = output.ToString();
        Assert.Contains("★ Solid", text, StringComparison.Ordinal);
        Assert.Contains("⚠ weak", text, StringComparison.Ordinal);
        Assert.Contains("⏰ expired", text, StringComparison.Ordinal);

        output.GetStringBuilder().Clear();
        Assert.Equal(0, await commands.RunAsync(["list", "--weak"]));
        Assert.Contains("Shaky", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Solid", output.ToString(), StringComparison.Ordinal);

        Assert.Equal(0, await commands.RunAsync(["list", "--q", "ghost"]));
        Assert.Contains("No entries match.", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Show_RevealsAndCounts()
    {
        var commands = Build(out var output, out _, stdin: "hunter2!\n");
        await commands.RunAsync(["init"]);
        await commands.RunAsync(["add", "GitHub", "--username", "octocat", "--secret-stdin", "--expires", "2026-12-31"]);

        output.GetStringBuilder().Clear();
        Assert.Equal(0, await commands.RunAsync(["show", "1"]));
        var text = output.ToString();
        Assert.Contains("#1 — GitHub", text, StringComparison.Ordinal);
        Assert.Contains("Username: octocat", text, StringComparison.Ordinal);
        Assert.Contains("Secret: ", text, StringComparison.Ordinal);
        Assert.Contains("Expires: 2026-12-31", text, StringComparison.Ordinal);
        Assert.Contains("Updated: 2026-09-20 12:00 UTC", text, StringComparison.Ordinal);

        Assert.Equal(1, await commands.RunAsync(["show", "99"]));
    }

    [Fact]
    public async Task Edit_ChangesFields()
    {
        var commands = Build(out var output, out _, stdin: "hunter2!\nrotated-42!\n");
        await commands.RunAsync(["init"]);
        await commands.RunAsync(["add", "GitHub", "--secret-stdin"]);

        Assert.Equal(0, await commands.RunAsync(["edit", "1", "--username", "new-me", "--secret-stdin"]));
        Assert.Contains("Updated #1 GitHub", output.ToString(), StringComparison.Ordinal);

        await commands.RunAsync(["show", "1"]);
        Assert.Contains("Username: new-me", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Totp_FlowsThrough()
    {
        var commands = Build(out var output, out var error, stdin: "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ\n");
        await commands.RunAsync(["init"]);
        await commands.RunAsync(["add", "Authy", "--generate", "--totp", "sha1", "--totp-secret-stdin"]);

        Assert.Equal(0, await commands.RunAsync(["totp", "1"]));
        Assert.Matches(@"(?m)^\d{6} \(\d+s left\)", output.ToString());

        Assert.Equal(0, await commands.RunAsync(["add", "Plain", "--generate"]));
        Assert.Equal(1, await commands.RunAsync(["totp", "2"]));
        Assert.Contains("no TOTP seed", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Expire_ListsDueEntries()
    {
        var commands = Build(out var output, out _, stdin: "card-secret-1!\n");
        await commands.RunAsync(["init"]);

        Assert.Equal(0, await commands.RunAsync(["expire"]));
        Assert.Contains("Nothing expires within 30 day(s).", output.ToString(), StringComparison.Ordinal);

        await commands.RunAsync(["add", "Card", "--secret-stdin", "--expires", "2026-10-01"]);
        Assert.Equal(0, await commands.RunAsync(["expire"]));
        Assert.Contains("#1 Card — expires 2026-10-01", output.ToString(), StringComparison.Ordinal);

        Assert.Equal(0, await commands.RunAsync(["expire", "--days", "1"]));
        Assert.Contains("Nothing expires within 1 day(s).", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Audit_SumsAndCelebrates()
    {
        var commands = Build(out var output, out _, stdin: "shaky\n");
        await commands.RunAsync(["init"]);
        await commands.RunAsync(["add", "Shaky", "--secret-stdin"]);

        Assert.Equal(0, await commands.RunAsync(["audit"]));
        Assert.Contains("Vault audit — 1 entries", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("weak secrets: 1", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("✔", output.ToString(), StringComparison.Ordinal);

        await commands.RunAsync(["delete", "1"]);
        Assert.Equal(0, await commands.RunAsync(["audit"]));
        Assert.Contains("The vault is empty", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generate_And_Strength()
    {
        var commands = Build(out var output, out _, stdin: "Tr0ub4dour&3");

        Assert.Equal(0, await commands.RunAsync(["generate", "--length", "16", "--count", "2"]));
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.All(lines, l => Assert.Equal(16, l.Trim().Length));

        output.GetStringBuilder().Clear();
        Assert.Equal(0, await commands.RunAsync(["strength"]));
        Assert.Contains("Score:", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("/4", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_Import_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"raz-{Guid.NewGuid():N}.bak");
        try
        {
            var commands = Build(out var output, out _, stdin: "hunter2!\n");
            await commands.RunAsync(["init"]);
            await commands.RunAsync(["add", "GitHub", "--secret-stdin"]);

            Assert.Equal(0, await commands.RunAsync(["export", path]));
            Assert.Contains("(encrypted)", output.ToString(), StringComparison.Ordinal);

            Assert.Equal(0, await commands.RunAsync(["import", path]));
            Assert.Contains("Imported 1 entry(ies). raz undo reverts it.", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task BareVerb_ShowsHelp_AndUsageGaps()
    {
        var commands = Build(out var output, out var error);

        Assert.Equal(0, await commands.RunAsync([]));
        Assert.Contains("Raz —", output.ToString(), StringComparison.Ordinal);

        Assert.Equal(1, await commands.RunAsync(["add"]));
        Assert.Contains("Usage: JameJam raz add", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await commands.RunAsync(["show"]));
        Assert.Contains("Usage: JameJam raz show", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await commands.RunAsync(["edit"]));
        Assert.Contains("Usage: JameJam raz edit", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await commands.RunAsync(["delete"]));
        Assert.Contains("Usage: JameJam raz delete", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await commands.RunAsync(["totp"]));
        Assert.Contains("Usage: JameJam raz totp", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Show_RendersEveryOptionalSection()
    {
        var commands = Build(out var output, out _, stdin: "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ\n");
        await commands.RunAsync(["init"]);
        await commands.RunAsync(["add", "Full", "--username", "u", "--url", "https://x", "--notes", "n", "--tags", "t1,t2", "--generate", "--totp", "sha1", "--totp-secret-stdin"]);

        Assert.Equal(0, await commands.RunAsync(["show", "1"]));
        var text = output.ToString();
        Assert.Contains("URL: https://x", text, StringComparison.Ordinal);
        Assert.Contains("Tags: t1,t2", text, StringComparison.Ordinal);
        Assert.Contains("Notes: n", text, StringComparison.Ordinal);
        Assert.Contains("TOTP: ", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edit_Regenerate_AndKeepSecret()
    {
        var commands = Build(out var output, out _, stdin: "first-secret-1!\n");
        await commands.RunAsync(["init"]);
        await commands.RunAsync(["add", "GitHub", "--secret-stdin"]);

        Assert.Equal(0, await commands.RunAsync(["edit", "1", "--regenerate", "--length", "24"]));
        await commands.RunAsync(["show", "1"]);
        Assert.Contains("Secret: ", output.ToString(), StringComparison.Ordinal);

        Assert.Equal(0, await commands.RunAsync(["edit", "1", "--notes", "kept"]));
        await commands.RunAsync(["show", "1"]);
        Assert.Contains("Notes: kept", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateCount_AndStrengthWithoutSecret_AreGuarded()
    {
        var commands = Build(out _, out var error, stdin: "");

        Assert.Equal(1, await commands.RunAsync(["generate", "--count", "51"]));
        Assert.Contains("Count must be between 1 and 50", error.ToString(), StringComparison.Ordinal);

        Assert.Equal(1, await commands.RunAsync(["strength"]));
        Assert.Contains("No secret arrived", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ai_WithoutAiCompletion_IsUnavailable_AndAskNeedsAQuestion()
    {
        var commands = Build(out _, out var error, stdin: "hunter2!\n");
        await commands.RunAsync(["init"]);
        await commands.RunAsync(["add", "GitHub", "--secret-stdin"]);

        Assert.Equal(1, await commands.RunAsync(["ai", "audit"]));
        Assert.Contains("AI is not available in this context.", error.ToString(), StringComparison.Ordinal);

        error.GetStringBuilder().Clear();
        Assert.Equal(1, await commands.RunAsync(["ai", "ask"]));
        Assert.Contains("AI is not available", error.ToString(), StringComparison.Ordinal); // the gate fires before parsing
    }

    [Fact]
    public async Task TotpAlgorithms_Parse_OrFail()
    {
        var commands = Build(out var output, out var error, stdin: "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ\nGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ\n");
        await commands.RunAsync(["init"]);
        Assert.Equal(0, await commands.RunAsync(["add", "A256", "--generate", "--totp", "sha256", "--totp-secret-stdin"]));
        Assert.Equal(0, await commands.RunAsync(["add", "A512", "--generate", "--totp", "sha512", "--totp-secret-stdin"]));

        Assert.Equal(1, await commands.RunAsync(["add", "Bad", "--generate", "--totp", "md5"]));
        Assert.Contains("sha1, sha256, or sha512", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edit_Flags_DriveEveryField()
    {
        var commands = Build(out var output, out _, stdin: "first-secret-1!\nGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ\n");
        await commands.RunAsync(["init"]);
        await commands.RunAsync(["add", "GitHub", "--username", "old", "--url", "https://old", "--tags", "old", "--notes", "old", "--secret-stdin", "--expires", "2026-12-31"]);

        Assert.Equal(0, await commands.RunAsync(["edit", "1", "--title", "Repo", "--username", "new", "--url", "https://new",
            "--tags", "new", "--notes", "new", "--favorite", "--totp", "sha1", "--totp-secret-stdin"]));
        Assert.Contains("Updated #1 Repo", output.ToString(), StringComparison.Ordinal);

        Assert.Equal(0, await commands.RunAsync(["edit", "1", "--clear-expires", "--clear-totp"]));
        Assert.Equal(0, await commands.RunAsync(["show", "1"]));
        var text = output.ToString();
        Assert.Contains("Username: new", text, StringComparison.Ordinal);
        Assert.Contains("URL: https://new", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Expires:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("TOTP:", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Audit_CelebratesACleanVault()
    {
        var commands = Build(out var output, out _, stdin: "long-and-healthy-1!\n");
        await commands.RunAsync(["init"]);
        await commands.RunAsync(["add", "Fine", "--secret-stdin"]);

        Assert.Equal(0, await commands.RunAsync(["audit"]));
        Assert.Contains("No weak, reused, or expired secrets. ✔", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SettingsDefaultLength_DrivesGeneration()
    {
        var commands = Build(out var output, out _, defaultLength: () => 32);

        Assert.Equal(0, await commands.RunAsync(["generate", "--count", "1"]));
        Assert.Equal(32, output.ToString().Trim().Length);
    }

    [Fact]
    public async Task BadNumbers_FailFriendly()
    {
        var commands = Build(out _, out var error, stdin: "hunter2!\n");
        await commands.RunAsync(["init"]);

        Assert.Equal(1, await commands.RunAsync(["init", "--iterations", "abc"]));
        Assert.Contains("Iterations must be a positive number", error.ToString(), StringComparison.Ordinal);

        Assert.Equal(1, await commands.RunAsync(["generate", "--count", "lots"]));
        Assert.Contains("Count must be a positive number", error.ToString(), StringComparison.Ordinal);

        Assert.Equal(1, await commands.RunAsync(["totp", "zero"]));
        Assert.Contains("Entry id must be a positive number", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownCommand_AndUnknownOption_FailStrictly()
    {
        var commands = Build(out _, out var error);

        Assert.Equal(1, await commands.RunAsync(["gamble"]));
        Assert.Contains("Unknown raz command 'gamble'", error.ToString(), StringComparison.Ordinal);

        Assert.Equal(1, await commands.RunAsync(["add", "X", "--secret", "on-the-command-line"]));
        Assert.Contains("Unknown option '--secret'", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutStorage_OnlyHelpWorks()
    {
        using StringWriter output = new();
        using StringWriter error = new();
        var commands = new RazCommands(
            null, Clock, output, error, passphraseProvider: () => Passphrase);

        Assert.Equal(1, await commands.RunAsync(["audit"]));
        Assert.Contains("Vault storage is not available", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, await commands.RunAsync(["help"]));
    }

    [Fact]
    public async Task WithoutPassphrase_FailsSafe()
    {
        using StringWriter error = new();
        var commands = new RazCommands(
            new MemoryVaultStore(),
            Clock,
            new StringWriter(),
            error,
            stdin: new StringReader(""),
            passphraseProvider: () => null);

        Assert.Equal(1, await commands.RunAsync(["audit"]));

        Assert.Contains("Set JAMEJAM_RAZ_PASSPHRASE", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AiAudit_StreamsThroughTheGate_WithoutSecrets()
    {
        List<string> prompts = [];
        var commands = Build(
            out var output,
            out _,
            ai: (request, _) =>
            {
                prompts.Add(request.Prompt);
                return Task.FromResult(new SoroushResult("Rotate the weak secrets first.", "stub", "stub-model", 1, TimeSpan.Zero));
            });
        await commands.RunAsync(["init"]);
        await commands.RunAsync(["add", "GitHub", "--secret-stdin"]);

        Assert.Equal(0, await commands.RunAsync(["ai", "audit"]));
        Assert.Contains("Rotate the weak secrets first.", output.ToString(), StringComparison.Ordinal);
        var prompt = Assert.Single(prompts);
        Assert.Contains("---AUDIT BEGIN---", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("GitHub", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2!", prompt, StringComparison.Ordinal);

        output.GetStringBuilder().Clear();
        Assert.Equal(0, await commands.RunAsync(["ai", "ask", "how bad is it?"]));
        Assert.Contains("Rotate the weak secrets first.", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, prompts.Count);
        Assert.Contains("Question: how bad is it?", prompts[1], StringComparison.Ordinal);

        Assert.Equal(1, await commands.RunAsync(["ai", "gamble"]));
    }
}

/// <summary>App routing and aliases for the vault.</summary>
[Collection("EnvSequential")]
public sealed class AppRazTests
{
    private sealed class StubAi : ISoroushClient
    {
        public List<string> Prompts { get; } = [];

        public Task<SoroushResult> CompleteAsync(string prompt, CancellationToken cancellationToken = default)
        {
            Prompts.Add(prompt);
            return Task.FromResult(new SoroushResult("Rotate 2 weak secrets.", "stub", "stub-model", 1, TimeSpan.Zero));
        }
    }

    [Fact]
    public async Task Raz_And_Vault_Alias_Route()
    {
        using StringWriter output = new();
        var app = new App(
            output, new StringWriter(), new MemorySettingsStore(),
            soroushFactory: _ => new StubAi(),
            razStore: new MemoryVaultStore());
        Environment.SetEnvironmentVariable(RazCommands.PassphraseEnvironmentVariable, "correct-horse-battery");
        try
        {
            Assert.Equal(0, await app.RunAsync(["raz", "init"]));
            Assert.Contains("Vault initialized", output.ToString(), StringComparison.Ordinal);
            Assert.Equal(0, await app.RunAsync(["vault", "audit"]));
            Assert.Contains("Vault audit — 0 entries", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(RazCommands.PassphraseEnvironmentVariable, null);
        }
    }

    [Fact]
    public async Task AiAudit_RequiresTheKey_LikeEveryService()
    {
        Environment.SetEnvironmentVariable(RazCommands.PassphraseEnvironmentVariable, "correct-horse-battery");
        Environment.SetEnvironmentVariable(App.ApiKeyEnvironmentVariable, null);
        using StringWriter error = new();
        var app = new App(
            new StringWriter(), error, new MemorySettingsStore(),
            soroushFactory: _ => new StubAi(),
            razStore: new MemoryVaultStore());
        try
        {
            Assert.Equal(0, await app.RunAsync(["raz", "init"]));
            Assert.Equal(1, await app.RunAsync(["raz", "ai", "audit"]));
            Assert.Contains("Missing API key", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(RazCommands.PassphraseEnvironmentVariable, null);
        }
    }

    [Fact]
    public async Task App_Help_MentionsRaz()
    {
        using StringWriter output = new();
        var app = new App(output, new StringWriter(), new MemorySettingsStore());

        Assert.Equal(0, await app.RunAsync(["--help"]));
        Assert.Contains("raz", output.ToString(), StringComparison.Ordinal);
    }
}
