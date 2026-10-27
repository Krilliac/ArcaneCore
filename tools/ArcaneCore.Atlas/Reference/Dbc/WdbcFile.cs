using System.Buffers.Binary;
using System.Text;

namespace ArcaneCore.Atlas.Reference.Dbc;

/// <summary>
/// A client DBC table in the 1.12.1 "WDBC" layout: a 20-byte header (magic, record count, field count, record size, string
/// block size), the fixed-size records (every field is one 4-byte cell), then a string block. Immutable once parsed and
/// thread-safe. This is the minimal reader the map directory needs; it deliberately does not depend on the server's reader.
/// </summary>
/// <remarks>
/// Layout: WoW-RE-Research formats/dbc.md:42-98 (header, records, string block) and :100-121 (a localized string is 8 string
/// offsets plus 1 flags cell); the same header checks as src/ArcaneCore.Data/ClientData/DbcFile.cs:36-66
/// (read here for the format only, not referenced).
/// </remarks>
public sealed class WdbcFile
{
    /// <summary>The 'WDBC' magic as a little-endian 32-bit value.</summary>
    // ref: src/ArcaneCore.Data/ClientData/DbcFile.cs:13 ; WoW-RE-Research formats/dbc.md:51
    public const uint Magic = 0x43424457;

    /// <summary>Size of the header in bytes.</summary>
    public const int HeaderSize = 20;

    /// <summary>Number of locale slots in a localized string (enUS is slot 0, followed by one flags cell).</summary>
    // ref: WoW-RE-Research formats/dbc.md:100-121
    public const int LocaleSlots = 8;

    private const int MaxRecords = 1 << 24;
    private readonly byte[] _data;
    private readonly int _stringsOffset;

    private WdbcFile(byte[] data, int recordCount, int fieldCount, int stringBlockSize)
    {
        _data = data;
        RecordCount = recordCount;
        FieldCount = fieldCount;
        RecordSize = fieldCount * 4;
        StringBlockSize = stringBlockSize;
        _stringsOffset = HeaderSize + (recordCount * RecordSize);
    }

    /// <summary>Number of rows.</summary>
    public int RecordCount { get; }

    /// <summary>Number of 4-byte cells per row.</summary>
    public int FieldCount { get; }

    /// <summary>Bytes per row (always <c>FieldCount * 4</c>).</summary>
    public int RecordSize { get; }

    /// <summary>Size of the string block in bytes.</summary>
    public int StringBlockSize { get; }

    /// <summary>
    /// Validates and wraps a DBC image. The array is used as is (not copied) and must not be modified afterwards.
    /// </summary>
    /// <exception cref="InvalidDataException">Not a WDBC, a layout other than 4-byte cells, or sizes that do not add up to the file length.</exception>
    public static WdbcFile Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic)
        {
            throw new InvalidDataException("Not a WDBC file.");
        }

        uint records = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        uint fields = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
        uint recordSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
        uint strings = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16));

        // 64-bit arithmetic so a hostile header cannot wrap the size check.
        if (fields == 0 || fields > 4096 || recordSize != (ulong)fields * 4)
        {
            throw new InvalidDataException($"Unsupported DBC layout: {fields} fields in {recordSize}-byte records.");
        }

        if (records > MaxRecords || (ulong)HeaderSize + ((ulong)records * recordSize) + strings != (ulong)data.Length)
        {
            throw new InvalidDataException(
                $"DBC size does not match its header ({records} records of {recordSize} bytes and {strings} string bytes in {data.Length} bytes).");
        }

        return new WdbcFile(data, (int)records, (int)fields, (int)strings);
    }

    /// <summary>The unsigned value of one cell.</summary>
    public uint GetUInt32(int record, int field)
        => BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(CellOffset(record, field)));

    /// <summary>
    /// The string a cell points at (a byte offset into the string block, 0 meaning the empty string). Bytes are read as UTF-8.
    /// </summary>
    /// <exception cref="InvalidDataException">The offset lies outside the string block or the string is not terminated.</exception>
    public string GetString(int record, int field) => StringAt(GetUInt32(record, field));

    /// <summary>
    /// A localized string starting at <paramref name="firstField"/>: the enUS slot, or the first non-empty slot when the
    /// enUS one is empty (non-English clients fill their own slot).
    /// </summary>
    public string GetLocalizedString(int record, int firstField)
    {
        if (firstField < 0 || firstField + LocaleSlots >= FieldCount)
        {
            throw new ArgumentOutOfRangeException(nameof(firstField), firstField, "A localized string needs 9 cells.");
        }

        for (int slot = 0; slot < LocaleSlots; slot++)
        {
            string text = GetString(record, firstField + slot);
            if (text.Length > 0)
            {
                return text;
            }
        }

        return string.Empty;
    }

    private string StringAt(uint offset)
    {
        if (offset == 0)
        {
            return string.Empty;
        }

        if (offset >= (uint)StringBlockSize)
        {
            throw new InvalidDataException($"DBC string offset {offset} is outside the {StringBlockSize}-byte string block.");
        }

        ReadOnlySpan<byte> block = _data.AsSpan(_stringsOffset + (int)offset, StringBlockSize - (int)offset);
        int end = block.IndexOf((byte)0);
        if (end < 0)
        {
            throw new InvalidDataException($"DBC string at offset {offset} is not terminated.");
        }

        return Encoding.UTF8.GetString(block[..end]);
    }

    private int CellOffset(int record, int field)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(record);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(record, RecordCount);
        ArgumentOutOfRangeException.ThrowIfNegative(field);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(field, FieldCount);
        return HeaderSize + (record * RecordSize) + (field * 4);
    }
}
