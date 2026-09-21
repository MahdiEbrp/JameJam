using System.Globalization;

using JameJam.Text;

namespace JameJam.Ganjoor;

/// <summary>
/// Input security gate for the wallet: strict amounts, currency codes, dates, months,
/// enums, and ids; all text passes the shared vectorized sanitizer with rail-bounded
/// lengths. Culture-invariant everywhere — money never depends on the user's locale.
/// </summary>
public static class MoneyGuard
{
    /// <summary>Validates and sanitizes a required name (account, bill, goal, person).</summary>
    public static string Name(string? value, int maxLength) =>
        TextGuard.SanitizeRequired(value, maxLength, "name");

    /// <summary>Validates and sanitizes optional notes.</summary>
    public static string Notes(string? value, int maxLength) =>
        TextGuard.SanitizeOptional(value, maxLength, "notes");

    /// <summary>Parses a positive amount within the rail.</summary>
    public static decimal Amount(string? text, decimal max)
    {
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)
            || amount < GanjoorDefaults.MinAmount)
        {
            throw new ArgumentException(
                $"Amount must be a number between {GanjoorDefaults.MinAmount.ToString("0.##", CultureInfo.InvariantCulture)} "
                + $"and {max.ToString("0.##", CultureInfo.InvariantCulture)}.");
        }

        return WithinAmountRail(amount, max);
    }

    /// <summary>Validates an already-parsed amount against the rail.</summary>
    public static decimal WithinAmountRail(decimal amount, decimal max)
    {
        if (amount < GanjoorDefaults.MinAmount || amount > max)
        {
            throw new ArgumentException(
                $"Amount must be between {GanjoorDefaults.MinAmount.ToString("0.##", CultureInfo.InvariantCulture)} "
                + $"and {max.ToString("0.##", CultureInfo.InvariantCulture)}.");
        }

        return amount;
    }

    /// <summary>Validates a currency code: exactly three ASCII letters, upper-cased.</summary>
    public static string Currency(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)
            || code.Trim().Length != GanjoorDefaults.CurrencyCodeLength
            || !code.Trim().All(char.IsAsciiLetter))
        {
            throw new ArgumentException("Currency must be a 3-letter code (e.g. USD, EUR, IRR).");
        }

        return code.Trim().ToUpperInvariant();
    }

    /// <summary>Validates and sanitizes a category name.</summary>
    public static string Category(string? value) =>
        TextGuard.SanitizeRequired(value, GanjoorDefaults.MaxTagLength, "category");

    /// <summary>Parses a comma-separated tag list within the rails.</summary>
    public static IReadOnlyList<string> Tags(string? csv, int maxCount)
    {
        if (string.IsNullOrWhiteSpace(csv))
            return [];

        var tags = csv
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(tag => TextGuard.SanitizeRequired(tag, GanjoorDefaults.MaxTagLength, nameof(tag)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (tags.Count > maxCount)
            throw new ArgumentException($"At most {maxCount} tags are allowed.");

        return tags;
    }

    /// <summary>Parses a strict <c>yyyy-MM-dd</c> date.</summary>
    public static DateOnly Date(string? text)
    {
        if (text is null
            || !DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            throw new ArgumentException("Date must be yyyy-MM-dd (e.g. 2026-09-19).");
        }

        return date;
    }

    /// <summary>Parses a strict <c>yyyy-MM</c> month into its first day.</summary>
    public static DateOnly Month(string? text)
    {
        if (text is null
            || !DateOnly.TryParseExact(text + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first))
        {
            throw new ArgumentException("Month must be yyyy-MM (e.g. 2026-09).");
        }

        return first;
    }

    /// <summary>Parses a transaction kind.</summary>
    public static GanjoorTxKind Kind(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "income" or "in" => GanjoorTxKind.Income,
        "expense" or "out" or "spend" => GanjoorTxKind.Expense,
        "transfer" => GanjoorTxKind.Transfer,
        _ => throw new ArgumentException("Kind must be income, expense, or transfer."),
    };

    /// <summary>Parses a bill frequency.</summary>
    public static GanjoorFrequency Frequency(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "daily" or "day" => GanjoorFrequency.Daily,
        "weekly" or "week" => GanjoorFrequency.Weekly,
        "monthly" or "month" => GanjoorFrequency.Monthly,
        "yearly" or "year" => GanjoorFrequency.Yearly,
        _ => throw new ArgumentException("Frequency must be daily, weekly, monthly, or yearly."),
    };

    /// <summary>Parses a positive identifier.</summary>
    public static long Id(string? text, string what)
    {
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id < 1)
            throw new ArgumentException($"{what} must be a positive number.");

        return id;
    }

    /// <summary>Formats an amount for display, invariant ("1,234.5 USD").</summary>
    public static string Money(decimal amount, string currency) =>
        string.Create(CultureInfo.InvariantCulture, $"{amount:N2} {currency}");

    /// <summary>Formats an amount without currency ("1,234.5").</summary>
    public static string Money(decimal amount) =>
        string.Create(CultureInfo.InvariantCulture, $"{amount:N2}");
}
