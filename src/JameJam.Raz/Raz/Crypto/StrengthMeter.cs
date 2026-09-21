namespace JameJam.Raz.Crypto;

/// <summary>The verdict of <see cref="StrengthMeter"/>: a 0–4 score plus actionable issues.</summary>
/// <param name="Score">0 very weak … 4 excellent.</param>
/// <param name="EntropyBits">Estimated entropy after penalties.</param>
/// <param name="Issues">Human-readable weaknesses found (may be empty).</param>
public sealed record PasswordStrength(int Score, int EntropyBits, IReadOnlyList<string> Issues)
{
    /// <summary>Label for the score, for CLI and AI contexts.</summary>
    public string Label => Score switch
    {
        0 => "very weak",
        1 => "weak",
        2 => "fair",
        3 => "strong",
        _ => "excellent",
    };
}

/// <summary>
/// Deterministic secret-strength estimate: charset entropy, minus penalties for
/// sequences ("abc", "4321") and repeats ("aaa"), banded to the familiar 0–4 score.
/// </summary>
public static class StrengthMeter
{
    private const int LowerPoolSize = 26;
    private const int UpperPoolSize = 26;
    private const int DigitPoolSize = 10;
    private const int SymbolPoolSize = 33; // printable ASCII symbols

    private const int VeryWeakBits = 28;
    private const int WeakBits = 36;
    private const int FairBits = 60;
    private const int StrongBits = 100;

    private const int SequencePenaltyBits = 12;
    private const int RepeatPenaltyBits = 12;
    private const int MinPatternRun = 3;

    /// <summary>Scores a secret. Empty or whitespace scores 0 with an issue.</summary>
    public static PasswordStrength Score(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);

        if (secret.Length == 0)
        {
            return new PasswordStrength(0, 0, ["empty secret"]);
        }

        var issues = new List<string>();
        var poolSize = 0;
        if (secret.Any(char.IsAsciiLetterLower))
        {
            poolSize += LowerPoolSize;
        }

        if (secret.Any(char.IsAsciiLetterUpper))
        {
            poolSize += UpperPoolSize;
        }

        if (secret.Any(char.IsAsciiDigit))
        {
            poolSize += DigitPoolSize;
        }

        if (secret.Any(c => !char.IsAsciiLetterOrDigit(c)))
        {
            poolSize += SymbolPoolSize;
        }

        if (poolSize == 0)
        {
            return new PasswordStrength(0, 0, ["no scoreable characters"]);
        }

        var entropy = (int)(secret.Length * Math.Log2(poolSize));

        if (HasSequence(secret))
        {
            issues.Add("sequential characters");
            entropy -= SequencePenaltyBits;
        }

        if (HasRepeat(secret))
        {
            issues.Add("repeated characters");
            entropy -= RepeatPenaltyBits;
        }

        entropy = Math.Max(0, entropy);
        var score = entropy switch
        {
            < VeryWeakBits => 0,
            < WeakBits => 1,
            < FairBits => 2,
            < StrongBits => 3,
            _ => 4,
        };
        return new PasswordStrength(score, entropy, issues);
    }

    private static bool HasSequence(string secret)
    {
        var run = 1;
        for (var i = 1; i < secret.Length; i++)
        {
            run = secret[i] == secret[i - 1] + 1 || secret[i] == secret[i - 1] - 1 ? run + 1 : 1;
            if (run >= MinPatternRun)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasRepeat(string secret)
    {
        var run = 1;
        for (var i = 1; i < secret.Length; i++)
        {
            run = secret[i] == secret[i - 1] ? run + 1 : 1;
            if (run >= MinPatternRun)
            {
                return true;
            }
        }

        return false;
    }
}
