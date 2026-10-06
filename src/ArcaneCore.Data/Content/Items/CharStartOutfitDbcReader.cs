using System.Buffers.Binary;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.Data.Content.Items;

/// <summary>
/// Reads the build-5875 CharStartOutfit.dbc record layout. This is intentionally independent of
/// <see cref="Spells.DbcFile"/>: CharStartOutfit declares 41 fields but its compact record is 152
/// bytes because race, class, gender and outfit selector are packed byte fields. The record
/// begins with its row ID; packed identity is the second word, followed by twelve item IDs.
/// </summary>
public static class CharStartOutfitDbcReader
{
    private const uint Magic = 0x43424457; // WDBC, little endian
    private const int HeaderBytes = 20;
    private const int FieldCount = 41;
    private const int RecordBytes = 152;
    private const int StringBytes = 1;
    private const int MaxFileBytes = 64 * 1024 * 1024;
    private const int MaxRecords = 100_000;

    public static IReadOnlyList<CharStartOutfit> Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (new FileInfo(path).Length > MaxFileBytes)
        {
            throw new InvalidDataException($"CharStartOutfit.dbc exceeds {MaxFileBytes} bytes");
        }

        byte[] bytes = File.ReadAllBytes(path);
        return Read(bytes);
    }

    public static IReadOnlyList<CharStartOutfit> Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderBytes || bytes.Length > MaxFileBytes
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic)
        {
            throw new InvalidDataException("not a bounded CharStartOutfit WDBC file");
        }

        uint records = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        uint fields = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        uint recordBytes = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        uint strings = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]);
        if (records > MaxRecords || fields != FieldCount || recordBytes != RecordBytes || strings != StringBytes
            || (ulong)HeaderBytes + ((ulong)records * RecordBytes) + strings != (ulong)bytes.Length)
        {
            throw new InvalidDataException("CharStartOutfit.dbc header or length is not the build-5875 layout");
        }

        var result = new List<CharStartOutfit>((int)records);
        var seen = new HashSet<uint>();
        int recordsOffset = HeaderBytes;
        for (int row = 0; row < records; row++)
        {
            ReadOnlySpan<byte> record = bytes.Slice(recordsOffset + (row * RecordBytes), RecordBytes);
            uint packed = BinaryPrimitives.ReadUInt32LittleEndian(record[4..]);
            byte race = (byte)(packed & 0xFF);
            byte cls = (byte)((packed >> 8) & 0xFF);
            byte gender = (byte)((packed >> 16) & 0xFF);
            if (!IsPlayable(race, cls) || gender > 1 || !seen.Add(packed & 0x00FF_FFFF))
            {
                throw new InvalidDataException($"CharStartOutfit.dbc has an unmapped or duplicate race/class/gender row at {row}");
            }

            var itemIds = new int[CharStartOutfit.ItemSlotCount];
            for (int slot = 0; slot < itemIds.Length; slot++)
            {
                itemIds[slot] = BinaryPrimitives.ReadInt32LittleEndian(record.Slice(8 + (slot * 4), 4));
            }

            result.Add(new CharStartOutfit(packed & 0x00FF_FFFF, itemIds));
        }

        return result;
    }

    private static bool IsPlayable(byte race, byte cls)
        => race is >= 1 and <= 8 && cls is 1 or 2 or 3 or 4 or 5 or 7 or 8 or 9 or 11;
}
