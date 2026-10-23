using System.Buffers.Binary;
using System.Text;

namespace ArcaneCore.Data.ClientData;

/// <summary>
/// A client DBC table (WDBC: "WDBC", u32 records, u32 fields, u32 record size, u32 string block
/// size, then the fixed-size records and a NUL-separated UTF-8 string block). Layout per
/// cmangos-classic DBCFileLoader::Load; every 1.12.1 field is four bytes.
/// </summary>
public sealed class DbcFile
{
    private const uint Magic = 0x43424457; // "WDBC" little-endian
    private const long MaxFileBytes = 64L * 1024 * 1024;
    private const uint MaxRecords = 1_000_000;

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
        // 64-bit: `fields * 4` wrapped in uint for fields >= 2^30, letting a header that declares
        // ~1G fields in 4-byte records pass and every field offset overflow later.
        if (fields == 0 || recordSize != (ulong)fields * 4 || fields > int.MaxValue / 4)
        {
            throw new InvalidDataException($"unsupported DBC layout: {fields} fields in {recordSize}-byte records");
        }

        if (records > MaxRecords || strings > MaxFileBytes
            || (ulong)20 + ((ulong)records * recordSize) + strings != (ulong)data.Length)
        {
            throw new InvalidDataException("DBC size does not match its header");
        }

        return new DbcFile(data, (int)records, (int)fields, (int)recordSize, (int)strings);
    }

    public static DbcFile Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, options: FileOptions.SequentialScan);
        if (stream.Length > MaxFileBytes) throw new InvalidDataException($"DBC file exceeds {MaxFileBytes} bytes");
        byte[] data = new byte[checked((int)stream.Length)];
        stream.ReadExactly(data);
        return Parse(data);
    }

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
        // A zero-size block is well-formed (Parse accepts it); offset 0 is the documented "" there.
        // Deliberate small divergence from cmangos' stringOffset < stringSize assert. Only the empty
        // block is special-cased so a non-empty block with no leading NUL keeps its byte-0 string.
        if (offset == 0 && _stringBlockSize == 0)
        {
            return string.Empty;
        }

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

    /// <summary>Read a terminated UTF-8 policy string without replacement decoding.</summary>
    public string GetStringStrict(int record, int field)
    {
        uint offset = GetUInt32(record, field);
        if (offset >= _stringBlockSize)
            throw new InvalidDataException("policy string offset is outside the DBC string block");
        ReadOnlySpan<byte> block = _data.AsSpan(_stringsOffset + (int)offset, _stringBlockSize - (int)offset);
        int end = block.IndexOf((byte)0);
        if (end < 0) throw new InvalidDataException("policy string is not NUL terminated");
        try { return new UTF8Encoding(false, true).GetString(block[..end]); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("policy string is not valid UTF-8", error); }
    }

    // Parse already guarantees RecordSize == FieldCount * 4, so the Require* checks only compare the field count.
    // The caller words the failure (each table keeps its own message); it is built only when the check fails.

    /// <summary>Throws <see cref="InvalidDataException"/> with <paramref name="message"/> unless the table has exactly <paramref name="expected"/> fields.</summary>
    public void RequireFields(int expected, Func<DbcFile, string> message)
    {
        if (FieldCount != expected) throw new InvalidDataException(message(this));
    }

    /// <summary>Throws <see cref="InvalidDataException"/> with <paramref name="message"/> unless the table has <paramref name="first"/> or <paramref name="second"/> fields.</summary>
    public void RequireOneOf(int first, int second, Func<DbcFile, string> message)
    {
        if (FieldCount != first && FieldCount != second) throw new InvalidDataException(message(this));
    }

    /// <summary>Throws <see cref="InvalidDataException"/> with <paramref name="message"/> if the table has fewer than <paramref name="minimum"/> fields.</summary>
    public void RequireAtLeast(int minimum, Func<DbcFile, string> message)
    {
        if (FieldCount < minimum) throw new InvalidDataException(message(this));
    }
}
