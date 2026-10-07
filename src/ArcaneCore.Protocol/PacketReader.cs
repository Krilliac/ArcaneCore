using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace ArcaneCore.Protocol;

/// <summary>
/// Sequential little-endian reader over a packet payload. Two surfaces over one cursor:
/// <list type="bullet">
/// <item>The throwing reads (<see cref="ReadByte"/>, <see cref="ReadUInt32"/>, ...) throw exactly
/// <see cref="ArgumentOutOfRangeException"/> through <see cref="MalformedPacket.Throw"/> when the
/// payload is too short, a count is negative or a string is too long; handlers treat that as a
/// malformed packet and the session disconnects. No other exception type can leave a read: every
/// access is bounds-checked against <see cref="Remaining"/> before it touches the span.</item>
/// <item>The <c>TryRead*</c> reads return false instead and leave the cursor where it was, so a
/// hot handler can refuse a packet without an exception. They allocate nothing (a string read has
/// a <c>Bytes</c> twin for that).</item>
/// </list>
/// The cursor never passes the end of the payload on either surface (the fuzz tests pin this).
/// </summary>
public ref struct PacketReader(ReadOnlySpan<byte> data)
{
    /// <summary>
    /// Longest null-terminated string a read accepts, in bytes: the largest client frame
    /// (vmangos WorldSocket::handle_input_header, 0x2800) so no legitimate packet is affected, while
    /// a decompressed or server-side buffer can never turn into a multi-megabyte string.
    /// </summary>
    public const int MaxCStringBytes = 0x2800;

    private readonly ReadOnlySpan<byte> _data = data;
    private int _position;

    public readonly int Remaining => _data.Length - _position;

    public readonly int Position => _position;

    // --- throwing surface ------------------------------------------------------------

    public byte ReadByte()
    {
        if (!TryReadByte(out byte value))
        {
            MalformedPacket.Throw("byte past the end of the payload");
        }

        return value;
    }

    public ushort ReadUInt16()
    {
        if (!TryReadUInt16(out ushort value))
        {
            MalformedPacket.Throw("uint16 past the end of the payload");
        }

        return value;
    }

    public uint ReadUInt32()
    {
        if (!TryReadUInt32(out uint value))
        {
            MalformedPacket.Throw("uint32 past the end of the payload");
        }

        return value;
    }

    public int ReadInt32() => unchecked((int)ReadUInt32());

    public ulong ReadUInt64()
    {
        if (!TryReadUInt64(out ulong value))
        {
            MalformedPacket.Throw("uint64 past the end of the payload");
        }

        return value;
    }

    public float ReadSingle()
    {
        if (!TryReadSingle(out float value))
        {
            MalformedPacket.Throw("float past the end of the payload");
        }

        return value;
    }

    /// <summary>
    /// Read a packed GUID: a mask byte whose bit i says byte i of the GUID follows
    /// (vmangos ByteBuffer::readPackGUID).
    /// </summary>
    public ulong ReadPackedGuid()
    {
        if (!TryReadPackedGuid(out ulong value))
        {
            MalformedPacket.Throw("packed GUID past the end of the payload");
        }

        return value;
    }

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        if (!TryReadBytes(count, out ReadOnlySpan<byte> slice))
        {
            MalformedPacket.Throw("byte run past the end of the payload or negative");
        }

        return slice;
    }

    public void Skip(int count)
    {
        if (!TrySkip(count))
        {
            MalformedPacket.Throw("skip past the end of the payload or negative");
        }
    }

    /// <summary>
    /// A count or length field bounded by <paramref name="max"/> (an array count, a list length): the
    /// packet is malformed when it exceeds the bound, before anything is allocated for it.
    /// </summary>
    public int ReadCount(int max)
    {
        if (!TryReadCount(max, out int count))
        {
            MalformedPacket.Throw("count past the end of the payload or above its bound");
        }

        return count;
    }

    /// <summary>Read a null-terminated string of at most <see cref="MaxCStringBytes"/> bytes (the client sends UTF-8).</summary>
    public string ReadCString() => ReadCString(MaxCStringBytes);

    /// <summary>Read a null-terminated string of at most <paramref name="maxBytes"/> bytes (terminator excluded).</summary>
    public string ReadCString(int maxBytes)
    {
        if (!TryReadCString(maxBytes, out string value))
        {
            MalformedPacket.Throw("string longer than its bound");
        }

        return value;
    }

    /// <summary>
    /// The raw bytes of a null-terminated string (terminator consumed; a missing terminator reads to
    /// the end). For text that must be validated as UTF-8 itself, such as a character name.
    /// </summary>
    public ReadOnlySpan<byte> ReadCStringBytes() => ReadCStringBytes(MaxCStringBytes);

    /// <summary>The raw bytes of a null-terminated string of at most <paramref name="maxBytes"/> bytes.</summary>
    public ReadOnlySpan<byte> ReadCStringBytes(int maxBytes)
    {
        if (!TryReadCStringBytes(maxBytes, out ReadOnlySpan<byte> value))
        {
            MalformedPacket.Throw("string longer than its bound");
        }

        return value;
    }

    public ReadOnlySpan<byte> ReadToEnd()
    {
        ReadOnlySpan<byte> slice = _data.Slice(_position);
        _position = _data.Length;
        return slice;
    }

    // --- non-throwing surface --------------------------------------------------------

    public bool TryReadByte(out byte value)
    {
        if (Remaining < 1)
        {
            value = 0;
            return false;
        }

        value = _data[_position];
        _position++;
        return true;
    }

    public bool TryReadUInt16(out ushort value)
    {
        if (Remaining < 2)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(_position, 2));
        _position += 2;
        return true;
    }

    public bool TryReadUInt32(out uint value)
    {
        if (Remaining < 4)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_position, 4));
        _position += 4;
        return true;
    }

    public bool TryReadInt32(out int value)
    {
        bool ok = TryReadUInt32(out uint raw);
        value = unchecked((int)raw);
        return ok;
    }

    public bool TryReadUInt64(out ulong value)
    {
        if (Remaining < 8)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(_data.Slice(_position, 8));
        _position += 8;
        return true;
    }

    public bool TryReadSingle(out float value)
    {
        if (Remaining < 4)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadSingleLittleEndian(_data.Slice(_position, 4));
        _position += 4;
        return true;
    }

    /// <summary>Packed GUID; on failure the cursor is left where it was (the mask byte is not consumed).</summary>
    public bool TryReadPackedGuid(out ulong value)
    {
        value = 0;
        if (Remaining < 1)
        {
            return false;
        }

        byte mask = _data[_position];
        int needed = System.Numerics.BitOperations.PopCount(mask);
        if (Remaining < 1 + needed)
        {
            return false;
        }

        int at = _position + 1;
        for (int i = 0; i < 8; i++)
        {
            if ((mask & (1 << i)) != 0)
            {
                value |= (ulong)_data[at++] << (i * 8);
            }
        }

        Debug.Assert(at == _position + 1 + needed, "packed GUID consumed exactly its mask's bytes");
        _position = at;
        return true;
    }

    public bool TryReadBytes(int count, out ReadOnlySpan<byte> value)
    {
        if (count < 0 || count > Remaining)
        {
            value = default;
            return false;
        }

        value = _data.Slice(_position, count);
        _position += count;
        return true;
    }

    public bool TrySkip(int count)
    {
        if (count < 0 || count > Remaining)
        {
            return false;
        }

        _position += count;
        return true;
    }

    /// <summary>A u32 count that must be at most <paramref name="max"/> (and not negative as an int); the cursor stays put when it is not.</summary>
    public bool TryReadCount(int max, out int count)
    {
        if (Remaining < 4)
        {
            count = 0;
            return false;
        }

        uint raw = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_position, 4));
        if (raw > (uint)Math.Max(0, max))
        {
            count = 0;
            return false;
        }

        count = (int)raw;
        _position += 4;
        return true;
    }

    /// <summary>A u8 count that must be at most <paramref name="max"/>; the cursor stays put when it is not.</summary>
    public bool TryReadByteCount(int max, out int count)
    {
        if (Remaining < 1 || _data[_position] > Math.Max(0, max))
        {
            count = 0;
            return false;
        }

        count = _data[_position];
        _position++;
        return true;
    }

    /// <summary>
    /// A null-terminated UTF-8 string of at most <paramref name="maxBytes"/> bytes. Allocates the
    /// string (use <see cref="TryReadCStringBytes"/> on a hot path). Invalid UTF-8 becomes U+FFFD, never
    /// an exception. The cursor stays put on failure.
    /// </summary>
    public bool TryReadCString(int maxBytes, out string value)
    {
        if (!TryReadCStringBytes(maxBytes, out ReadOnlySpan<byte> bytes))
        {
            value = string.Empty;
            return false;
        }

        value = bytes.Length == 0 ? string.Empty : Encoding.UTF8.GetString(bytes);
        return true;
    }

    /// <summary>
    /// The raw bytes of a null-terminated string of at most <paramref name="maxBytes"/> bytes
    /// (terminator excluded and consumed; a missing terminator reads to the end). Allocation-free.
    /// The cursor stays put on failure.
    /// </summary>
    public bool TryReadCStringBytes(int maxBytes, out ReadOnlySpan<byte> value)
    {
        ReadOnlySpan<byte> rest = _data.Slice(_position);
        int end = rest.IndexOf((byte)0);
        int length = end < 0 ? rest.Length : end;
        if (maxBytes < 0 || length > maxBytes)
        {
            value = default;
            return false;
        }

        value = rest.Slice(0, length);
        _position += end < 0 ? length : length + 1;
        return true;
    }
}
