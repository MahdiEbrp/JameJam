using JameJam.Soroush;

namespace JameJam.Tests.Soroush;

/// <summary>Tests for the Soroush safety layer.</summary>
public sealed class SoroushGuardTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SanitizePrompt_WithEmptyInput_Throws(string? prompt) =>
        Assert.Throws<ArgumentException>(() => SoroushGuard.SanitizePrompt(prompt));

    [Fact]
    public void SanitizePrompt_WithOnlyControlCharacters_Throws() =>
        Assert.Throws<ArgumentException>(() => SoroushGuard.SanitizePrompt("\0\u0001\u0002"));

    [Fact]
    public void SanitizePrompt_TooLong_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SoroushGuard.SanitizePrompt("abcdef", maxLength: 5));

    [Theory]
    [InlineData("  Hello  ", "Hello")]
    [InlineData("Salam\tWorld", "Salam\tWorld")]
    public void SanitizePrompt_TrimsAndKeepsFormatting(string prompt, string expected)
    {
        var sanitized = SoroushGuard.SanitizePrompt(prompt);

        Assert.Equal(expected, sanitized);
    }

    [Fact]
    public void SanitizePrompt_StripsControlCharactersButKeepsNewlines()
    {
        var sanitized = SoroushGuard.SanitizePrompt("line1\nline2\u0007tab\there");

        Assert.Equal("line1\nline2tab\there", sanitized);
    }

    [Fact]
    public void SanitizePrompt_StripsDelCharacter() =>
        Assert.Equal("ab", SoroushGuard.SanitizePrompt("a\u007fb"));

    [Fact]
    public void SanitizePrompt_LongCleanString_IsO1AllocationAndUnchanged()
    {
        var prompt = new string('x', 8_000);

        Assert.Equal(prompt, SoroushGuard.SanitizePrompt(prompt)); // fast path: no controls anywhere
    }

    [Theory]
    [InlineData("https://api.example.com/v1")]
    [InlineData("http://localhost:11434/v1/chat/completions")]
    [InlineData("http://127.0.0.1:8080/api")]
    public void ValidateEndpoint_AcceptsHttpsAndLoopbackHttp(string endpoint)
    {
        var uri = SoroushGuard.ValidateEndpoint(endpoint);

        Assert.Equal(new Uri(endpoint), uri);
    }

    [Theory]
    [InlineData("http://api.example.com/v1")]
    [InlineData("ftp://api.example.com")]
    [InlineData("not-a-url")]
    [InlineData("")]
    public void ValidateEndpoint_RejectsInsecureOrInvalid(string endpoint) =>
        Assert.Throws<SoroushException>(() => SoroushGuard.ValidateEndpoint(endpoint));

    [Fact]
    public void ValidateOptions_AcceptsTheDefaults()
    {
        SoroushGuard.ValidateOptions(new SoroushOptions()); // must not throw
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100_001)]
    [InlineData(-5)]
    public void ValidateOptions_RejectsOutOfRangeMaxTokens(int maxTokens)
    {
        var options = new SoroushOptions { MaxTokens = maxTokens };

        var exception = Assert.Throws<SoroushException>(() => SoroushGuard.ValidateOptions(options));
        Assert.Contains("MaxTokens", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateOptions_RejectsZeroTimeout() =>
        Assert.Throws<SoroushException>(
            () => SoroushGuard.ValidateOptions(new SoroushOptions { RequestTimeout = TimeSpan.Zero }));

    [Fact]
    public void ValidateOptions_RejectsTooManyRetries() =>
        Assert.Throws<SoroushException>(
            () => SoroushGuard.ValidateOptions(new SoroushOptions { MaxRetries = 11 }));

    [Fact]
    public void ValidateOptions_RejectsInsecureEndpoint() =>
        Assert.Throws<SoroushException>(
            () => SoroushGuard.ValidateOptions(new SoroushOptions { Endpoint = "http://api.example.com" }));

    [Theory]
    [InlineData(null, "(none)")]
    [InlineData("", "(none)")]
    [InlineData("  ", "(none)")]
    [InlineData("abc", "****")]
    [InlineData("super-secret-key", "****-key")]
    public void Redact_MasksSecrets(string? secret, string expected) =>
        Assert.Equal(expected, SoroushGuard.Redact(secret));

    [Fact]
    public void RedactIn_ScrubsSecretOccurrencesFromText()
    {
        var scrubbed = SoroushGuard.RedactIn("oops: sk-livetest-key is invalid", "sk-livetest-key");

        Assert.Equal("oops: ****-key is invalid", scrubbed);
    }

    [Fact]
    public void RedactIn_KeepsTextUntouched_WithoutSecretMatch() =>
        Assert.Equal("nothing to see", SoroushGuard.RedactIn("nothing to see", "sk-abc"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateResponse_WithEmptyContent_Throws(string? content) =>
        Assert.Throws<SoroushException>(() => SoroushGuard.ValidateResponse(content));

    [Fact]
    public void ValidateResponse_Trims() =>
        Assert.Equal("trimmed", SoroushGuard.ValidateResponse("  trimmed  "));

    [Theory]
    [InlineData("super-secret-key", 0, "****")]
    [InlineData("super-secret-key", 10, "****secret-key")]
    [InlineData("super-secret-key", 64, "****")] // suffix >= length hides everything
    public void Redact_SuffixLength_IsConfigurable(string secret, int suffixLength, string expected) =>
        Assert.Equal(expected, SoroushGuard.Redact(secret, suffixLength));

    [Fact]
    public void Redact_LongSecret_KeepsAtMostTheRailSuffix()
    {
        var secret = new string('x', 70);

        Assert.Equal("****" + new string('x', 64), SoroushGuard.Redact(secret, 64));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65)]
    public void Redact_SuffixLength_OutsideRails_Throws(int suffixLength) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SoroushGuard.Redact("secret", suffixLength));

    [Fact]
    public void RedactIn_SuffixLength_FlowsThrough() =>
        Assert.Equal("oops: ****key!", SoroushGuard.RedactIn("oops: sk-livetest-key!", "sk-livetest-key", 3));

    [Fact]
    public void ValidateOptions_RejectsTooSmallErrorBodyLength() =>
        Assert.Throws<SoroushException>(
            () => SoroushGuard.ValidateOptions(new SoroushOptions { MaxErrorBodyLength = 10 }));

    [Fact]
    public void ValidateOptions_RejectsHugeErrorBodyLength() =>
        Assert.Throws<SoroushException>(
            () => SoroushGuard.ValidateOptions(new SoroushOptions { MaxErrorBodyLength = 200_000 }));

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void ValidateOptions_RejectsJitterOutsideUnitRange(double jitterScale) =>
        Assert.Throws<SoroushException>(
            () => SoroushGuard.ValidateOptions(new SoroushOptions { JitterScale = jitterScale }));

    [Fact]
    public void ValidateOptions_RejectsNullRetryableStatusCodes()
    {
        var options = new SoroushOptions { RetryableStatusCodes = null! };

        var exception = Assert.Throws<SoroushException>(() => SoroushGuard.ValidateOptions(options));
        Assert.Contains("RetryableStatusCodes", exception.Message, StringComparison.Ordinal);
    }
}
