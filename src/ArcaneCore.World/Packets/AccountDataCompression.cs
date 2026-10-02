using System.IO.Compression;

namespace ArcaneCore.World.Packets;

/// <summary>
/// The zlib streams of CMSG_UPDATE_ACCOUNT_DATA / SMSG_UPDATE_ACCOUNT_DATA (u32 decompressed
/// size, then a zlib stream; vmangos Misc::UpdateAccountData, cmangos-classic
/// HandleRequestAccountData, gtker <c>compressed = "true"</c>).
/// </summary>
public static class AccountDataCompression
{
    /// <summary>Largest blob accepted (vmangos HandleUpdateAccountData rejects sizes above 0xFFFF).</summary>
    public const int MaxDecompressedSize = 0xFFFF;

    /// <summary>
    /// Inflate a client blob to exactly <paramref name="expectedSize"/> bytes. The Adler-32
    /// trailer is not required: vmangos decompresses with IgnoreChecksum because the client
    /// may omit it, so the raw deflate data after the two-byte zlib header is inflated directly.
    /// Returns false for a bad header, corrupt data, or a size that does not match.
    /// </summary>
    public static bool TryDecompress(ReadOnlySpan<byte> zlib, int expectedSize, out byte[] data)
    {
        data = [];
        if (expectedSize is <= 0 or > MaxDecompressedSize || zlib.Length < 2)
        {
            return false;
        }

        // RFC 1950: CM = 8 (deflate), FCHECK makes CMF·256 + FLG a multiple of 31, no FDICT.
        byte cmf = zlib[0];
        byte flg = zlib[1];
        if ((cmf & 0x0F) != 8 || ((cmf << 8) | flg) % 31 != 0 || (flg & 0x20) != 0)
        {
            return false;
        }

        byte[] output = new byte[expectedSize];
        try
        {
            using var input = new MemoryStream(zlib[2..].ToArray(), writable: false);
            using var inflater = new DeflateStream(input, CompressionMode.Decompress);
            int total = 0;
            while (total < expectedSize)
            {
                int read = inflater.Read(output, total, expectedSize - total);
                if (read == 0)
                {
                    return false; // shorter than declared
                }

                total += read;
            }

            Span<byte> probe = stackalloc byte[1];
            if (inflater.Read(probe) != 0)
            {
                return false; // longer than declared
            }
        }
        catch (InvalidDataException)
        {
            return false;
        }

        data = output;
        return true;
    }

    /// <summary>Deflate a blob as a complete zlib stream (header and Adler-32 trailer).</summary>
    public static byte[] Compress(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream();
        using (var deflater = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflater.Write(data);
        }

        return output.ToArray();
    }
}
