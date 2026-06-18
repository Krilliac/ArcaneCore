using System.Globalization;
using System.Numerics;

namespace ArcaneCore.Cryptography.Tests;

/// <summary>Hex helpers for the big-endian KAT files.</summary>
internal static class Hex
{
    public static byte[] ToBytesBigEndian(string hex)
    {
        byte[] bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return bytes;
    }

    /// <summary>Little-endian byte array of a big-endian hex string.</summary>
    public static byte[] ToBytesLittleEndian(string hex)
    {
        byte[] be = ToBytesBigEndian(hex);
        Array.Reverse(be);
        return be;
    }

    public static BigInteger ToBigInteger(string hex)
        => new(ToBytesBigEndian(hex), isUnsigned: true, isBigEndian: true);

    public static IEnumerable<string[]> ReadColumns(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Vectors", fileName);
        foreach (string line in File.ReadLines(path))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                yield return line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            }
        }
    }
}
