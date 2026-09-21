using System.Security.Cryptography;
using JameJam.Raz;
using JameJam.Raz.Crypto;

namespace JameJam.Tests.Raz;

/// <summary>Option rails for the vault.</summary>
public sealed class RazOptionsTests
{
    [Fact]
    public void Defaults_AreValid() => Assert.Null(Record.Exception(() => new RazOptions().Validate()));

    [Theory]
    [InlineData(99_999)]
    [InlineData(10_000_001)]
    public void Iterations_HaveRails(int iterations) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RazOptions(Iterations: iterations).Validate());

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void UndoDepth_HasRails(int depth) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RazOptions(UndoDepth: depth).Validate());

    [Theory]
    [InlineData(7)]
    [InlineData(257)]
    public void PasswordLength_HasRails(int length) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RazOptions(PasswordLength: length).Validate());

    [Theory]
    [InlineData(0)]
    [InlineData(3_651)]
    public void ExpiringSoonDays_HasRails(int days) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RazOptions(ExpiringSoonDays: days).Validate());

    [Theory]
    [InlineData(0)]
    [InlineData(3_651)]
    public void OldAfterDays_HasRails(int days) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RazOptions(OldAfterDays: days).Validate());

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void WeakScoreThreshold_HasRails(int threshold) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RazOptions(WeakScoreThreshold: threshold).Validate());
}

/// <summary>RFC 4648 Base32 codec.</summary>
public sealed class Base32Tests
{
    [Fact]
    public void Encode_MatchesTheRfcVector()
    {
        // "12345678901234567890" in ASCII is the canonical RFC 6238 seed.
        var seed = "12345678901234567890"u8.ToArray();
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", Base32.Encode(seed));
        Assert.Equal(string.Empty, Base32.Encode([]));
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("s3cret-data-42")]
    [InlineData("\u0001\u0002\u0003")]
    public void Decode_ReversesEncode(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        Assert.Equal(bytes, Base32.Decode(Base32.Encode(bytes)));
    }

    [Fact]
    public void Decode_IgnoresCasePaddingAndWhitespace()
    {
        var expected = Base32.Decode("GEZDGNBV");
        Assert.Equal(expected, Base32.Decode("gezdgnbv"));
        Assert.Equal(expected, Base32.Decode("GEZDGNBV======"));
        Assert.Equal(expected, Base32.Decode(" GEZD GNBV "));
    }

    [Fact]
    public void Decode_RejectsInvalidCharacters() => Assert.Throws<FormatException>(() => Base32.Decode("abc123!"));

    [Fact]
    public void Decode_RejectsEmpty() => Assert.Throws<ArgumentException>(() => Base32.Decode(""));
}

/// <summary>RFC 6238 TOTP against the published test vectors.</summary>
public sealed class TotpTests
{
    private static readonly DateTimeOffset T59 = DateTimeOffset.FromUnixTimeSeconds(59);

    private const string Sha1Seed = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ"; // ASCII "12345678901234567890"

    [Fact]
    public void Sha1_Vector_T59() =>
        Assert.Equal("94287082", Totp.ComputeCode(Sha1Seed, T59, digits: 8));

    [Fact]
    public void Sha1_SixDigits_IsTheTruncation()
    {
        var code = Totp.ComputeCode(Sha1Seed, T59, digits: 6);
        Assert.Equal(6, code.Length);
        Assert.Equal("287082", code); // last 6 of the 8-digit RFC value
    }

    [Fact]
    public void Sha256_Vector_T59()
    {
        var seed = Base32.Encode("12345678901234567890123456789012"u8.ToArray());
        Assert.Equal("46119246", Totp.ComputeCode(seed, T59, digits: 8, algorithm: TotpAlgorithm.Sha256));
    }

    [Fact]
    public void Sha512_Vector_T59()
    {
        var secret64 = string.Concat(Enumerable.Repeat("12345678901234567890", 3)) + "1234";
        var seed = Base32.Encode(System.Text.Encoding.ASCII.GetBytes(secret64));
        Assert.Equal("90693936", Totp.ComputeCode(seed, T59, digits: 8, algorithm: TotpAlgorithm.Sha512));
    }

    [Fact]
    public void CounterRollsOver_WithThePeriod()
    {
        var early = Totp.ComputeCode(Sha1Seed, DateTimeOffset.FromUnixTimeSeconds(59));
        var late = Totp.ComputeCode(Sha1Seed, DateTimeOffset.FromUnixTimeSeconds(88)); // counter 2
        Assert.NotEqual(early, late);
    }

    [Fact]
    public void SecondsRemaining_CountsDown()
    {
        var time = DateTimeOffset.FromUnixTimeSeconds(59); // 29 into a 30s window
        Assert.Equal(1, Totp.SecondsRemaining(time));
        Assert.Equal(30, Totp.SecondsRemaining(time.AddSeconds(-29)));
    }

    [Fact]
    public void CodeFor_NullWithoutSeed()
    {
        RazEntry entry = new(1, "x", "s", "", "", "", "", "", TotpAlgorithm.None, 0, 0, null, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Assert.Null(Totp.CodeFor(entry, DateTimeOffset.UtcNow));

        var withSeed = entry with { TotpSeed = Sha1Seed, TotpAlgorithm = TotpAlgorithm.Sha1, TotpDigits = 6, TotpPeriodSeconds = 30 };
        Assert.Equal(Totp.ComputeCode(Sha1Seed, DateTimeOffset.UtcNow, 30, 6, TotpAlgorithm.Sha1), Totp.CodeFor(withSeed, DateTimeOffset.UtcNow));
    }
}

/// <summary>Deterministic strength estimation.</summary>
public sealed class StrengthMeterTests
{
    [Fact]
    public void Empty_IsZero() => Assert.Equal(0, StrengthMeter.Score("").Score);

    [Fact]
    public void LongMixedSecret_ScoresExcellent()
    {
        var strength = StrengthMeter.Score("Correct-Horse-Battery-42!");
        Assert.Equal(4, strength.Score);
        Assert.Equal("excellent", strength.Label);
        Assert.Empty(strength.Issues);
    }

    [Fact]
    public void Repeats_AndSequences_ArePenalized()
    {
        var repeat = StrengthMeter.Score("aaaaaaaa");
        Assert.Contains("repeated characters", repeat.Issues);

        var sequence = StrengthMeter.Score("Xabc1234");
        Assert.Contains("sequential characters", sequence.Issues);
    }

    [Fact]
    public void ShortNumeric_IsVeryWeak()
    {
        var strength = StrengthMeter.Score("1234");
        Assert.Equal(0, strength.Score);
        Assert.Equal("very weak", strength.Label);
    }
}

/// <summary>Policy-compliant generation from the CSPRNG.</summary>
public sealed class PasswordGeneratorTests
{
    [Fact]
    public void Generate_HonorsTheLength_AndIncludesEveryClass()
    {
        var password = PasswordGenerator.Generate(new PasswordPolicy(Length: 24));
        Assert.Equal(24, password.Length);
        Assert.Contains(password, char.IsAsciiLetterLower);
        Assert.Contains(password, char.IsAsciiLetterUpper);
        Assert.Contains(password, char.IsAsciiDigit);
        Assert.Contains(password, c => !char.IsAsciiLetterOrDigit(c));
    }

    [Fact]
    public void NoSymbols_Policy_IsHonored()
    {
        var password = PasswordGenerator.Generate(new PasswordPolicy(Length: 32, Symbol: false));
        Assert.All(password, c => Assert.True(char.IsAsciiLetterOrDigit(c), $"{c} is not alphanumeric"));
    }

    [Fact]
    public void ExcludeAmbiguous_DropsLookAlikes()
    {
        for (var i = 0; i < 10; i++)
        {
            var password = PasswordGenerator.Generate(new PasswordPolicy(Length: 64, ExcludeAmbiguous: true));
            Assert.All(password, c => Assert.False("Il1|O0oQ".Contains(c), $"{c} is ambiguous"));
        }
    }

    [Fact]
    public void DigitsOnly_StillGenerates()
    {
        var password = PasswordGenerator.Generate(new PasswordPolicy(Length: 12, Lower: false, Upper: false, Symbol: false));
        Assert.All(password, c => Assert.True(char.IsAsciiDigit(c)));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(257)]
    public void Length_IsRailed(int length) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PasswordGenerator.Generate(new PasswordPolicy(Length: length)));

    [Fact]
    public void NoClasses_IsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PasswordGenerator.Generate(new PasswordPolicy(Lower: false, Upper: false, Digit: false, Symbol: false)));
}

/// <summary>AES-256-GCM and PBKDF2 behavior.</summary>
public sealed class VaultCryptoTests
{
    private static readonly byte[] Key = new byte[RazDefaults.KeySizeBytes];

    static VaultCryptoTests() => RandomNumberGenerator.Fill(Key);

    [Theory]
    [InlineData("hunter2!")]
    [InlineData("")]
    [InlineData("unicode-\u0633\u0644\u0627\u0645")]
    public void RoundTrips(string plaintext) => Assert.Equal(plaintext, VaultCrypto.Decrypt(Key, VaultCrypto.Encrypt(Key, plaintext)));

    [Fact]
    public void TamperedPayload_FailsAuthentication()
    {
        var payload = VaultCrypto.Encrypt(Key, "secret");
        payload[^1] ^= 0xFF;
        Assert.ThrowsAny<CryptographicException>(() => VaultCrypto.Decrypt(Key, payload));
    }

    [Fact]
    public void WrongKey_FailsAuthentication()
    {
        var payload = VaultCrypto.Encrypt(Key, "secret");
        Assert.ThrowsAny<CryptographicException>(() => VaultCrypto.Decrypt(new byte[RazDefaults.KeySizeBytes], payload));
    }

    [Fact]
    public void TruncatedPayload_IsRejected() =>
        Assert.ThrowsAny<CryptographicException>(() => VaultCrypto.Decrypt(Key, [1, 2, 3]));

    [Fact]
    public void Derivation_IsDeterministic_PerSalt_AndStronglyDifferentAcrossSalts()
    {
        var salt = RandomNumberGenerator.GetBytes(RazDefaults.SaltSizeBytes);
        var again = VaultCrypto.DeriveKey("passphrase", salt, RazDefaults.MinIterations);
        Assert.Equal(VaultCrypto.DeriveKey("passphrase", salt, RazDefaults.MinIterations), again);

        var otherSalt = RandomNumberGenerator.GetBytes(RazDefaults.SaltSizeBytes);
        Assert.NotEqual(again, VaultCrypto.DeriveKey("passphrase", otherSalt, RazDefaults.MinIterations));
        Assert.NotEqual(again, VaultCrypto.DeriveKey("Passphrase", salt, RazDefaults.MinIterations));
    }

    [Fact]
    public void Wipe_Zeroes() 
    {
        var material = new byte[8];
        Array.Fill(material, (byte)7);
        VaultCrypto.Wipe(material);
        Assert.All(material, b => Assert.Equal(0, b));
        VaultCrypto.Wipe(null); // no-op, no throw
    }
}
