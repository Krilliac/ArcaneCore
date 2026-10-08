using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ArcaneCore.Cryptography;

/// <summary>RFC 6238 HMAC-SHA1, 30-second TOTP; vmangos AuthSocket.cpp:1274-1301
/// accepts offsets -2 through +1. The secret is RFC 4648 Base32 without padding.</summary>
public static class Totp
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static bool TryDecodeSecret(string secret, out byte[] key)
    {
        key = [];
        if (string.IsNullOrEmpty(secret) || secret.Length > 103) return false; // vmangos caps decoded key at 64 bytes
        var result = new List<byte>(secret.Length * 5 / 8);
        int bits = 0, value = 0;
        foreach (char c in secret)
        {
            int digit = Alphabet.IndexOf(char.ToUpperInvariant(c));
            if (digit < 0) return false;
            value = (value << 5) | digit;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                result.Add((byte)(value >> bits));
                value &= (1 << bits) - 1;
            }
        }
        if (result.Count is 0 or > 64 || value != 0) return false;
        key = [.. result];
        return true;
    }

    public static uint Generate(ReadOnlySpan<byte> key, long unixSeconds, int offset = 0)
    {
        long counter = unixSeconds / 30 + offset;
        if (counter < 0) throw new ArgumentOutOfRangeException(nameof(unixSeconds));
        Span<byte> message = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(message, (ulong)counter);
        byte[] digest = HMACSHA1.HashData(key, message);
        int position = digest[^1] & 15;
        uint binary = BinaryPrimitives.ReadUInt32BigEndian(digest.AsSpan(position, 4)) & 0x7fffffff;
        return binary % 1_000_000;
    }

    public static string NewSecret()
    {
        // 160 bits of entropy, rendered as 32 Base32 characters with no padding.
        byte[] random = RandomNumberGenerator.GetBytes(20);
        Span<char> text = stackalloc char[32];
        int value = 0, bits = 0, index = 0;
        foreach (byte b in random)
        {
            value = (value << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                text[index++] = Alphabet[(value >> bits) & 31];
                value &= (1 << bits) - 1;
            }
        }
        return new string(text);
    }
}
