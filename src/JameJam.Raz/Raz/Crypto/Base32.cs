namespace JameJam.Raz.Crypto;

/// <summary>
/// RFC 4648 Base32 (A–Z, 2–7). TOTP seeds are conventionally shared in this form.
/// Padding is produced nowhere and tolerated on input.
/// </summary>
public static class Base32
{
    /// <summary>The RFC 4648 alphabet.</summary>
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>Bits delivered per output character.</summary>
    private const int BitsPerChar = 5;

    /// <summary>Encodes bytes as unpadded upper-case Base32.</summary>
    public static string Encode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        var bits = bytes.Length * 8;
        var length = (bits + BitsPerChar - 1) / BitsPerChar;
        return string.Create(length, bytes, static (span, data) =>
        {
            var bitBuffer = 0U;
            var bitsInBuffer = 0;
            var position = 0;
            foreach (var octet in data)
            {
                bitBuffer = (bitBuffer << 8) | octet;
                bitsInBuffer += 8;
                while (bitsInBuffer >= BitsPerChar)
                {
                    span[position++] = Alphabet[(int)((bitBuffer >> (bitsInBuffer - BitsPerChar)) & 0x1F)];
                    bitsInBuffer -= BitsPerChar;
                }
            }

            if (bitsInBuffer > 0)
            {
                span[position++] = Alphabet[(int)((bitBuffer << (BitsPerChar - bitsInBuffer)) & 0x1F)];
            }
        });
    }

    /// <summary>
    /// Decodes Base32. Whitespace and '=' padding are ignored; case-insensitive.
    /// </summary>
    /// <exception cref="FormatException">The text holds characters outside the alphabet.</exception>
    public static byte[] Decode(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        List<byte> output = [];
        var bitBuffer = 0U;
        var bitsInBuffer = 0;
        foreach (var raw in text)
        {
            if (raw == '=' || char.IsWhiteSpace(raw))
            {
                continue;
            }

            var value = Alphabet.IndexOf(char.ToUpperInvariant(raw));
            if (value < 0)
            {
                throw new FormatException($"'{raw}' is not a Base32 character.");
            }

            bitBuffer = (bitBuffer << BitsPerChar) | (uint)value;
            bitsInBuffer += BitsPerChar;
            if (bitsInBuffer >= 8)
            {
                output.Add((byte)((bitBuffer >> (bitsInBuffer - 8)) & 0xFF));
                bitsInBuffer -= 8;
            }
        }

        return [.. output];
    }
}
