using System.Buffers;
using System.Text;

namespace JameJam.Text;

/// <summary>
/// Shared text safety: strips control characters (keeping <c>\n</c> and <c>\t</c>),
/// trims, and enforces length caps. Used by every layer that accepts untrusted text.
/// </summary>
public static class TextGuard
{
    /// <summary>Control characters that are stripped (everything except \n and \t, plus DEL).</summary>
    private static readonly SearchValues<char> ControlChars = CreateControlChars();

    /// <summary>Sanitizes a required, non-empty single field.</summary>
    /// <param name="value">Raw, untrusted value.</param>
    /// <param name="maxLength">Maximum allowed length after sanitization.</param>
    /// <param name="paramName">Name reported in exceptions.</param>
    /// <returns>Sanitized, trimmed, non-empty value.</returns>
    /// <exception cref="ArgumentException">Empty, whitespace-only, or no readable characters.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Sanitized value exceeds <paramref name="maxLength"/>.</exception>
    public static string SanitizeRequired(string? value, int maxLength, string paramName)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 1);

        var sanitized = SanitizeInternal(value, maxLength, out var overTheLimit);
        if (sanitized.Length == 0)
            throw new ArgumentException("Value must not be empty.", paramName);
        if (overTheLimit)
            throw new ArgumentOutOfRangeException(
                paramName, maxLength, $"Value exceeds the maximum length of {maxLength} characters.");

        return sanitized;
    }

    /// <summary>Sanitizes an optional field: null/whitespace becomes <see cref="string.Empty"/>.</summary>
    /// <param name="value">Raw, untrusted value.</param>
    /// <param name="maxLength">Maximum allowed length after sanitization.</param>
    /// <param name="paramName">Name reported in exceptions.</param>
    /// <returns>Sanitized value (possibly empty, never null).</returns>
    /// <exception cref="ArgumentOutOfRangeException">Sanitized value exceeds <paramref name="maxLength"/>.</exception>
    public static string SanitizeOptional(string? value, int maxLength, string paramName)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 1);

        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var sanitized = SanitizeInternal(value, maxLength, out var overTheLimit);
        if (overTheLimit)
            throw new ArgumentOutOfRangeException(
                paramName, maxLength, $"Value exceeds the maximum length of {maxLength} characters.");

        return sanitized;
    }

    private static string SanitizeInternal(string? value, int maxLength, out bool overTheLimit)
    {
        overTheLimit = false;
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var trimmed = value.Trim();

        // Fast path: one vectorized scan proves there is nothing to filter.
        if (!trimmed.AsSpan().ContainsAny(ControlChars))
        {
            overTheLimit = trimmed.Length > maxLength;
            return overTheLimit ? trimmed[..maxLength] : trimmed;
        }

        // Slow path: one pass keeps formatting whitespace and drops the rest.
        StringBuilder sanitized = new(capacity: Math.Min(trimmed.Length, maxLength));
        foreach (var c in trimmed)
        {
            if (!char.IsControl(c) || c is '\n' or '\t')
                sanitized.Append(c);
        }

        var result = sanitized.ToString().Trim();
        if (result.Length > maxLength)
        {
            overTheLimit = true;
            result = result[..maxLength];
        }

        return result;
    }

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
