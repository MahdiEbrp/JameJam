using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace JameJam.Raz.Crypto;

/// <summary>
/// RFC 6238 time-based one-time passwords, with per-entry parameters
/// (SHA-1/256/512, 6–8 digits, configurable period).
/// </summary>
public static class Totp
{
    /// <summary>Length of the counter fed to the HMAC (64-bit big-endian).</summary>
    private const int CounterBytes = 8;

    /// <summary>Computes the code for a Base32 seed at <paramref name="time"/>.</summary>
    public static string ComputeCode(
        string base32Seed,
        DateTimeOffset time,
        int periodSeconds = RazDefaults.DefaultTotpPeriodSeconds,
        int digits = RazDefaults.DefaultTotpDigits,
        TotpAlgorithm algorithm = TotpAlgorithm.Sha1)
    {
        var key = Base32.Decode(base32Seed);
        var counter = (ulong)(time.ToUnixTimeSeconds() / periodSeconds);
        return ComputeCode(key, counter, digits, algorithm);
    }

    /// <summary>Seconds left before the current code window rolls over.</summary>
    public static int SecondsRemaining(DateTimeOffset time, int periodSeconds = RazDefaults.DefaultTotpPeriodSeconds)
    {
        var intoWindow = time.ToUnixTimeSeconds() % periodSeconds;
        return (int)(periodSeconds - intoWindow);
    }

    /// <summary>Resolves the code for an entry, or null when the entry has no TOTP seed.</summary>
    public static string? CodeFor(RazEntry entry, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.TotpAlgorithm == TotpAlgorithm.None || entry.TotpSeed.Length == 0)
        {
            return null;
        }

        return ComputeCode(entry.TotpSeed, now, entry.TotpPeriodSeconds, entry.TotpDigits, entry.TotpAlgorithm);
    }

    [SuppressMessage(
        "Security",
        "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "RFC 6238 TOTP is specified over HMAC-SHA-1/256/512; SHA-1 is the authenticator-app default and HMAC-SHA-1 remains unbroken for this use.")]
    private static string ComputeCode(byte[] key, ulong counter, int digits, TotpAlgorithm algorithm)
    {
        var hashAlgorithm = algorithm switch
        {
            TotpAlgorithm.Sha256 => HashAlgorithmName.SHA256,
            TotpAlgorithm.Sha512 => HashAlgorithmName.SHA512,
            _ => HashAlgorithmName.SHA1,
        };

        Span<byte> counterBytes = stackalloc byte[CounterBytes];
        for (var i = 0; i < CounterBytes; i++)
        {
            counterBytes[CounterBytes - 1 - i] = (byte)(counter >> (8 * i));
        }

        var digest = algorithm switch
        {
            TotpAlgorithm.Sha256 => HMACSHA256.HashData(key, counterBytes.ToArray()),
            TotpAlgorithm.Sha512 => HMACSHA512.HashData(key, counterBytes.ToArray()),
            _ => HMACSHA1.HashData(key, counterBytes.ToArray()),
        };

        // RFC 4226 dynamic truncation.
        var offset = digest[^1] & 0x0F;
        var binary = ((digest[offset] & 0x7F) << 24)
            | ((digest[offset + 1] & 0xFF) << 16)
            | ((digest[offset + 2] & 0xFF) << 8)
            | (digest[offset + 3] & 0xFF);
        var modulus = 1;
        for (var i = 0; i < digits; i++)
        {
            modulus *= 10;
        }

        return (binary % modulus).ToString(new string('0', digits), System.Globalization.CultureInfo.InvariantCulture);
    }
}
