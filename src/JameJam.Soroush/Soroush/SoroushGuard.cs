using System.Buffers;
using System.Text;

namespace JameJam.Soroush;

/// <summary>
/// The Soroush safety layer: validates and sanitizes everything going to (or coming from)
/// an AI provider, and keeps secrets out of logs and error messages.
/// All bounds come from named rails in <see cref="SoroushLimits"/> — no magic numbers.
/// </summary>
public static class SoroushGuard
{
    /// <summary>Control characters that are stripped from prompts (everything except \n and \t).</summary>
    private static readonly SearchValues<char> ControlChars = CreateControlChars();

    /// <summary>Strips control characters, trims, and enforces the maximum prompt length.</summary>
    /// <param name="prompt">Raw, untrusted prompt.</param>
    /// <param name="maxLength">Maximum allowed length after sanitization.</param>
    /// <returns>A sanitized, non-empty prompt.</returns>
    /// <exception cref="ArgumentException">Empty, whitespace-only, or no readable characters.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Sanitized prompt exceeds <paramref name="maxLength"/>.</exception>
    public static string SanitizePrompt(string? prompt, int maxLength = SoroushLimits.DefaultMaxPromptLength)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("Prompt must not be empty.", nameof(prompt));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 1);

        var trimmed = prompt.Trim();

        // Fast path (the common case): a single vectorized scan proves there is nothing to
        // filter, so the string is returned as-is with zero extra allocations.
        if (!trimmed.AsSpan().ContainsAny(ControlChars))
            return trimmed.Length <= maxLength
                ? trimmed
                : throw LengthError(maxLength);

        // Slow path: one pass keeps formatting whitespace and drops the rest.
        StringBuilder sanitized = new(capacity: Math.Min(trimmed.Length, maxLength));
        foreach (var c in trimmed)
        {
            if (!char.IsControl(c) || c is '\n' or '\t')
                sanitized.Append(c);
        }

        var result = sanitized.ToString().Trim();
        if (result.Length == 0)
            throw new ArgumentException("Prompt contains no readable characters.", nameof(prompt));

        return result.Length <= maxLength
            ? result
            : throw LengthError(maxLength);
    }

    /// <summary>
    /// Validates an AI endpoint. HTTPS is always allowed; plain HTTP only on loopback
    /// (so local models via Ollama/LM Studio keep working without weakening cloud calls).
    /// </summary>
    /// <param name="endpoint">Absolute endpoint URL.</param>
    /// <returns>The parsed endpoint URI.</returns>
    /// <exception cref="SoroushException">Insecure or malformed endpoint.</exception>
    public static Uri ValidateEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            throw new SoroushException($"Invalid AI endpoint '{endpoint}'.");

        var secureEnough = uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
        return secureEnough
            ? uri
            : throw new SoroushException(
                $"Insecure AI endpoint '{endpoint}'. Use HTTPS (plain HTTP is only allowed on loopback).");
    }

    /// <summary>
    /// Fail-fast validation of a full options object (called before every client run):
    /// bounds-checks every knob against its named rail in <see cref="SoroushLimits"/>,
    /// and rejects insecure or malformed endpoints.
    /// </summary>
    /// <param name="options">Options to validate.</param>
    /// <exception cref="SoroushException">Any value outside its safe range.</exception>
    public static void ValidateOptions(SoroushOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.MaxTokens is < 1 or > SoroushLimits.MaxTokensBound)
            throw new SoroushException($"MaxTokens must be between 1 and {SoroushLimits.MaxTokensBound}.");

        if (options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout > SoroushLimits.RequestTimeoutBound)
            throw new SoroushException(
                $"RequestTimeout must be positive and at most {SoroushLimits.RequestTimeoutBound.TotalMinutes:0} minutes.");

        if (options.MaxRetries is < 0 or > SoroushLimits.MaxRetriesBound)
            throw new SoroushException($"MaxRetries must be between 0 and {SoroushLimits.MaxRetriesBound}.");

        if (options.RetryBaseDelay < TimeSpan.Zero)
            throw new SoroushException("RetryBaseDelay must not be negative.");

        if (options.MaxPromptLength < 1)
            throw new SoroushException("MaxPromptLength must be at least 1.");

        if (options.MaxErrorBodyLength is < SoroushLimits.MinErrorBodyLength
            or > SoroushLimits.MaxErrorBodyLengthBound)
        {
            throw new SoroushException(
                $"MaxErrorBodyLength must be between {SoroushLimits.MinErrorBodyLength} "
                + $"and {SoroushLimits.MaxErrorBodyLengthBound}.");
        }

        if (options.JitterScale is < 0 or > 1)
            throw new SoroushException("JitterScale must be between 0 (deterministic) and 1 (fully jittered).");

        if (options.RetryableStatusCodes is null)
            throw new SoroushException("RetryableStatusCodes must not be null.");

        _ = ValidateEndpoint(options.Endpoint);
    }

    /// <summary>Validates a non-empty completion coming back from a provider.</summary>
    /// <param name="content">Raw content from the provider response.</param>
    /// <returns>Trimmed, non-empty content.</returns>
    /// <exception cref="SoroushException">Empty or whitespace-only response.</exception>
    public static string ValidateResponse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new SoroushException("AI provider returned an empty completion.");

        return content.Trim();
    }

    /// <summary>Masks a secret so it can safely appear in logs or error messages.</summary>
    /// <param name="secret">The secret value.</param>
    /// <param name="suffixLength">How many trailing characters may stay visible (0 hides everything).</param>
    /// <returns>A redacted form that leaks at most <paramref name="suffixLength"/> characters.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Suffix length outside its rails.</exception>
    public static string Redact(string? secret, int suffixLength = SoroushLimits.RedactDefaultSuffixLength)
    {
        ValidateSuffixLength(suffixLength);

        if (string.IsNullOrWhiteSpace(secret))
            return "(none)";

        var visible = secret.Length <= suffixLength ? string.Empty : secret[^suffixLength..];
        return $"****{visible}";
    }

    /// <summary>Redacts <paramref name="secret"/> wherever it appears inside <paramref name="text"/>.</summary>
    /// <param name="text">Untrusted text (e.g. a provider error body).</param>
    /// <param name="secret">The secret that must never survive into the output.</param>
    /// <param name="suffixLength">How many trailing characters may stay visible.</param>
    /// <returns>Text safe to show or log.</returns>
    public static string RedactIn(
        string text,
        string? secret,
        int suffixLength = SoroushLimits.RedactDefaultSuffixLength)
    {
        if (string.IsNullOrEmpty(secret) || text.Length == 0)
            return text;

        return text.Contains(secret, StringComparison.Ordinal)
            ? text.Replace(secret, Redact(secret, suffixLength), StringComparison.Ordinal)
            : text;
    }

    private static void ValidateSuffixLength(int suffixLength)
    {
        if (suffixLength is < 0 or > SoroushLimits.RedactSuffixLengthBound)
        {
            throw new ArgumentOutOfRangeException(
                nameof(suffixLength),
                suffixLength,
                $"Suffix length must be between 0 and {SoroushLimits.RedactSuffixLengthBound}.");
        }
    }

    private static ArgumentOutOfRangeException LengthError(int maxLength) => new(
        nameof(maxLength), maxLength, $"Prompt exceeds the maximum length of {maxLength} characters.");

    private static SearchValues<char> CreateControlChars()
    {
        // All C0 control characters except \n and \t, plus DEL — matches char.IsControl semantics.
        Span<char> controls = stackalloc char[31];
        var index = 0;
        for (var c = '\0'; c <= '\u001f'; c++)
        {
            if (c is not ('\n' or '\t'))
                controls[index++] = c;
        }

        controls[index] = '\u007f';
        return SearchValues.Create(controls);
    }
}
