using System.Security.Cryptography;

namespace JameJam.Raz.Crypto;

/// <summary>
/// Customizable generation policy. Every knob has safe bounds; classes can be turned
/// off but at least one must remain.
/// </summary>
/// <param name="Length">Password length.</param>
/// <param name="Lower">Include a–z.</param>
/// <param name="Upper">Include A–Z.</param>
/// <param name="Digit">Include 0–9.</param>
/// <param name="Symbol">Include printable symbols.</param>
/// <param name="ExcludeAmbiguous">Drop look-alike characters (Il1O0|`'" etc.).</param>
public sealed record PasswordPolicy(
    int Length = RazDefaults.DefaultPasswordLength,
    bool Lower = true,
    bool Upper = true,
    bool Digit = true,
    bool Symbol = true,
    bool ExcludeAmbiguous = false)
{
    /// <summary>Validates the policy.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Length is out of range or no class is enabled.</exception>
    public void Validate()
    {
        if (Length is < RazDefaults.MinPasswordLength or > RazDefaults.MaxPasswordLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Length),
                Length,
                $"Length must be between {RazDefaults.MinPasswordLength} and {RazDefaults.MaxPasswordLength}.");
        }

        if (!Lower && !Upper && !Digit && !Symbol)
        {
            throw new ArgumentOutOfRangeException(nameof(Lower), "At least one character class must be enabled.");
        }
    }
}

/// <summary>
/// Cryptographically secure password generator: uniform draws via
/// <see cref="RandomNumberGenerator"/>, one guaranteed character per enabled class,
/// Fisher–Yates shuffle, optional look-alike exclusion.
/// </summary>
public static class PasswordGenerator
{
    // Look-alike characters removed when ExcludeAmbiguous is set.
    private const string Ambiguous = "Il1|O0oQ";

    // Printable ASCII symbol range without space/delete-prone characters.
    private const char SymbolStart = '!';
    private const char SymbolEnd = '~';

    /// <summary>Generates one password under the policy.</summary>
    public static string Generate(PasswordPolicy policy)
    {
        policy.Validate();

        Span<char> lower = "abcdefghijklmnopqrstuvwxyz".ToCharArray();
        Span<char> upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ".ToCharArray();
        Span<char> digit = "0123456789".ToCharArray();
        var symbol = SymbolRange().ToArray();

        if (policy.ExcludeAmbiguous)
        {
            lower = Strip(lower);
            upper = Strip(upper);
            digit = Strip(digit);
            symbol = [.. symbol.Where(c => !Ambiguous.Contains(c))];
        }

        List<char[]> classes = [];
        if (policy.Lower)
        {
            classes.Add(lower.ToArray());
        }

        if (policy.Upper)
        {
            classes.Add(upper.ToArray());
        }

        if (policy.Digit)
        {
            classes.Add(digit.ToArray());
        }

        if (policy.Symbol)
        {
            classes.Add(symbol);
        }

        var union = classes.SelectMany(c => c).Distinct().ToArray();
        var result = new char[policy.Length];

        // Guarantee one character per class (as many classes as fit).
        var guaranteed = Math.Min(classes.Count, policy.Length);
        for (var i = 0; i < guaranteed; i++)
        {
            result[i] = classes[i][RandomNumberGenerator.GetInt32(classes[i].Length)];
        }

        for (var i = guaranteed; i < policy.Length; i++)
        {
            result[i] = union[RandomNumberGenerator.GetInt32(union.Length)];
        }

        Shuffle(result);
        return new string(result);
    }

    private static char[] SymbolRange()
    {
        List<char> symbols = [];
        for (var c = SymbolStart; c <= SymbolEnd; c++)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                symbols.Add(c);
            }
        }

        return [.. symbols];
    }

    private static char[] Strip(ReadOnlySpan<char> source)
    {
        var output = new char[source.Length];
        var count = 0;
        foreach (var c in source)
        {
            if (!Ambiguous.Contains(c))
            {
                output[count++] = c;
            }
        }

        return output[..count];
    }

    private static void Shuffle(Span<char> values)
    {
        for (var i = values.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }
}
