using System.Security.Cryptography;
using System.Text;

namespace JameJam.Raz.Crypto;

/// <summary>
/// The vault's cryptographic core: PBKDF2-HMAC-SHA512 key derivation and AES-256-GCM
/// authenticated encryption. Wire format for one encrypted field:
/// <c>nonce (12) || ciphertext || tag (16)</c>, Base64 when stored as text.
/// </summary>
public static class VaultCrypto
{
    /// <summary>Derives a 256-bit key from the passphrase and salt.</summary>
    public static byte[] DeriveKey(string passphrase, byte[] salt, int iterations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passphrase);
        ArgumentNullException.ThrowIfNull(salt);
        ArgumentOutOfRangeException.ThrowIfNotEqual(salt.Length, RazDefaults.SaltSizeBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, RazDefaults.MinIterations);

        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase),
            salt,
            iterations,
            HashAlgorithmName.SHA512,
            RazDefaults.KeySizeBytes);
    }

    /// <summary>Encrypts text under <paramref name="key"/>; output is nonce||ciphertext||tag.</summary>
    public static byte[] Encrypt(byte[] key, string plaintext)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(plaintext); // empty is fine: optional fields encrypt too

        using var aes = new AesGcm(key, RazDefaults.TagSizeBytes);
        var nonce = RandomNumberGenerator.GetBytes(RazDefaults.NonceSizeBytes);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[RazDefaults.TagSizeBytes];
        aes.Encrypt(nonce, plain, cipher, tag);

        var payload = new byte[RazDefaults.NonceSizeBytes + plain.Length + RazDefaults.TagSizeBytes];
        nonce.CopyTo(payload, 0);
        cipher.CopyTo(payload, RazDefaults.NonceSizeBytes);
        tag.CopyTo(payload, RazDefaults.NonceSizeBytes + cipher.Length);
        return payload;
    }

    /// <summary>
    /// Decrypts a payload produced by <see cref="Encrypt"/>.
    /// </summary>
    /// <exception cref="CryptographicException">Wrong key or tampered payload.</exception>
    public static string Decrypt(byte[] key, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length < RazDefaults.NonceSizeBytes + RazDefaults.TagSizeBytes)
        {
            throw new CryptographicException("Encrypted payload is truncated.");
        }

        using var aes = new AesGcm(key, RazDefaults.TagSizeBytes);
        var nonce = payload[..RazDefaults.NonceSizeBytes];
        var cipher = payload[RazDefaults.NonceSizeBytes..^RazDefaults.TagSizeBytes];
        var tag = payload[^RazDefaults.TagSizeBytes..];
        var plain = new byte[cipher.Length];
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>Base64 helper for storing encrypted payloads in text formats.</summary>
    public static string ToText(byte[] payload) => Convert.ToBase64String(payload);

    /// <summary>Base64 helper for storing encrypted payloads in text formats.</summary>
    public static byte[] FromText(string text) => Convert.FromBase64String(text);

    /// <summary>
    /// Zeroes key material as soon as a caller is done with it (best effort —
    /// the runtime may have copied the buffer).
    /// </summary>
    public static void Wipe(byte[]? material) => Array.Clear(material ?? []);
}
