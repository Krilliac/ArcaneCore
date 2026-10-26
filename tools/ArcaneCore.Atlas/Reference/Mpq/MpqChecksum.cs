namespace ArcaneCore.Atlas.Reference.Mpq;

/// <summary>
/// The Adler-32 checksum MPQ archives use for per-sector checksums. It is the zlib algorithm; what differs from a zlib
/// stream trailer is the seed: StormLib starts a sector checksum at 0, a zlib stream starts at 1. Stateless and thread-safe.
/// </summary>
public static class MpqChecksum
{
    private const uint Base = 65521;

    // The largest n for which the 32-bit sums cannot overflow before the next modulo, given a and b below BASE on entry
    // ref: StormLib src/zlib/adler32.c:14-15
    private const int MaxRun = 5552;

    /// <summary>Seed StormLib uses for sector checksums.</summary>
    // ref: StormLib src/SFileReadFile.cpp:153 ; SFileAddFile.cpp:238 (adler32(0, sector, length))
    public const uint SectorSeed = 0;

    /// <summary>Seed of a standard Adler-32 (RFC 1950, and the trailer of every zlib stream).</summary>
    // ref: StormLib src/zlib/adler32.c:84-85 (adler32(adler, Z_NULL, 0) returns 1)
    public const uint ZlibSeed = 1;

    /// <summary>
    /// Updates <paramref name="adler"/> (a previous result, <see cref="SectorSeed"/> or <see cref="ZlibSeed"/>) with
    /// <paramref name="data"/>.
    /// </summary>
    public static uint Adler32(uint adler, ReadOnlySpan<byte> data)
    {
        // ref: StormLib src/zlib/adler32.c:60-128 ; RFC 1950 section 8.2
        // With a and b below BASE on entry, one modulo per run of MaxRun bytes keeps both sums inside 32 bits.
        uint a = adler & 0xFFFF;
        uint b = adler >> 16;
        while (!data.IsEmpty)
        {
            int run = Math.Min(data.Length, MaxRun);
            foreach (byte value in data[..run])
            {
                a += value;
                b += a;
            }

            a %= Base;
            b %= Base;
            data = data[run..];
        }

        return (b << 16) | a;
    }
}

/// <summary>Rules for the sector offset table that carry the checksum table's location.</summary>
internal static class MpqSectorTable
{
    /// <summary>
    /// Finds the sector checksum table of a compressed, multi-sector file that has the checksum flag. The sector offset
    /// table then has <c>sectorCount + 2</c> entries (the first one equals its own byte length); the last two bound the
    /// checksum table, which is stored after the final sector. Anything else means "no usable checksums", which StormLib
    /// ignores silently.
    /// </summary>
    /// <param name="offsets">The decoded sector offset table, <c>sectorCount + 2</c> entries long.</param>
    /// <param name="sectorCount">Number of data sectors.</param>
    /// <param name="sectorSize">Archive sector size.</param>
    /// <param name="offset">Offset (relative to the file's block) of the checksum table.</param>
    /// <param name="storedSize">Stored length of the checksum table.</param>
    public static bool TryLocateChecksums(ReadOnlySpan<uint> offsets, int sectorCount, int sectorSize, out uint offset, out uint storedSize)
    {
        // ref: StormLib src/SBaseCommon.cpp:1405-1437 (AllocateSectorChecksums)
        offset = 0;
        storedSize = 0;
        if (offsets.Length < sectorCount + 2 || offsets[0] == 0 || offsets[0] != (uint)(sectorCount + 2) * 4)
        {
            return false;
        }

        uint start = offsets[sectorCount];
        uint end = offsets[sectorCount + 1];
        if (end < start || end - start < 4 || end - start > (uint)sectorSize)
        {
            return false;
        }

        offset = start;
        storedSize = end - start;
        return true;
    }
}
