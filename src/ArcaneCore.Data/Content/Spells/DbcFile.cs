using System.Buffers.Binary;
using System.Text;

namespace ArcaneCore.Data.Content.Spells;

/// <summary>
/// A client DBC table (WDBC: "WDBC", u32 records, u32 fields, u32 record size, u32 string block
/// size, then the fixed-size records and a NUL-separated UTF-8 string block). Layout per
/// cmangos-classic DBCFileLoader::Load; every 1.12.1 field is four bytes.
/// </summary>
public sealed class DbcFile
{
    private const uint Magic = 0x43424457; // "WDBC" little-endian

    private readonly byte[] _data;
    private readonly int _recordsOffset;
    private readonly int _stringsOffset;
    private readonly int _stringBlockSize;

    private DbcFile(byte[] data, int recordCount, int fieldCount, int recordSize, int stringBlockSize)
    {
        _data = data;
        RecordCount = recordCount;
        FieldCount = fieldCount;
        RecordSize = recordSize;
        _stringBlockSize = stringBlockSize;
        _recordsOffset = 20;
        _stringsOffset = 20 + (recordCount * recordSize);
    }

    public int RecordCount { get; }

    public int FieldCount { get; }

    public int RecordSize { get; }

    /// <summary>Parse a DBC image; throws <see cref="InvalidDataException"/> on a malformed file.</summary>
    public static DbcFile Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic)
        {
            throw new InvalidDataException("not a WDBC file");
        }

        uint records = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        uint fields = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
        uint recordSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
        uint strings = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16));
        if (fields == 0 || recordSize != fields * 4)
        {
            throw new InvalidDataException($"unsupported DBC layout: {fields} fields in {recordSize}-byte records");
        }

        if ((ulong)20 + ((ulong)records * recordSize) + strings != (ulong)data.Length)
        {
            throw new InvalidDataException("DBC size does not match its header");
        }

        return new DbcFile(data, (int)records, (int)fields, (int)recordSize, (int)strings);
    }

    public static DbcFile Load(string path) => Parse(File.ReadAllBytes(path));

    public uint GetUInt32(int record, int field)
        => BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(FieldOffset(record, field)));

    public int GetInt32(int record, int field)
        => BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(FieldOffset(record, field)));

    public float GetFloat(int record, int field)
        => BinaryPrimitives.ReadSingleLittleEndian(_data.AsSpan(FieldOffset(record, field)));

    /// <summary>The string a field's string-block offset points to ("" for offset 0).</summary>
    public string GetString(int record, int field)
    {
        uint offset = GetUInt32(record, field);
        if (offset >= _stringBlockSize)
        {
            throw new InvalidDataException($"string offset {offset} outside the {_stringBlockSize}-byte string block");
        }

        ReadOnlySpan<byte> block = _data.AsSpan(_stringsOffset + (int)offset, _stringBlockSize - (int)offset);
        int end = block.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? block : block[..end]);
    }

    private int FieldOffset(int record, int field)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(record);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(record, RecordCount);
        ArgumentOutOfRangeException.ThrowIfNegative(field);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(field, FieldCount);
        return _recordsOffset + (record * RecordSize) + (field * 4);
    }
}
