using System.Buffers.Binary;
using System.Text;

namespace ArcaneCore.Protocol;

/// <summary>Growable little-endian payload builder for world packets.</summary>
public sealed class PacketWriter
{
    private byte[] _buffer;
    private int _length;

    public PacketWriter(int capacity = 64)
    {
        _buffer = new byte[Math.Max(capacity, 1)];
    }

    public int Length => _length;

    public void WriteByte(byte value)
    {
        EnsureCapacity(1);
        _buffer[_length++] = value;
    }

    public void WriteUInt16(ushort value)
    {
        EnsureCapacity(2);
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(_length), value);
        _length += 2;
    }

    public void WriteUInt32(uint value)
    {
        EnsureCapacity(4);
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_length), value);
        _length += 4;
    }

    public void WriteInt32(int value) => WriteUInt32(unchecked((uint)value));

    public void WriteUInt64(ulong value)
    {
        EnsureCapacity(8);
        BinaryPrimitives.WriteUInt64LittleEndian(_buffer.AsSpan(_length), value);
        _length += 8;
    }

    public void WriteSingle(float value)
    {
        EnsureCapacity(4);
        BinaryPrimitives.WriteSingleLittleEndian(_buffer.AsSpan(_length), value);
        _length += 4;
    }

    /// <summary>
    /// Write a packed GUID: a mask byte with bit i set when byte i of the GUID is non-zero,
    /// followed by those bytes low to high (vmangos ByteBuffer::appendPackGUID).
    /// </summary>
    public void WritePackedGuid(ulong guid)
    {
        EnsureCapacity(9);
        int maskPosition = _length++;
        byte mask = 0;
        for (int i = 0; i < 8; i++)
        {
            byte b = (byte)(guid >> (i * 8));
            if (b != 0)
            {
                mask |= (byte)(1 << i);
                _buffer[_length++] = b;
            }
        }

        _buffer[maskPosition] = mask;
    }

    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        EnsureCapacity(value.Length);
        value.CopyTo(_buffer.AsSpan(_length));
        _length += value.Length;
    }

    /// <summary>Write a UTF-8 string followed by a null terminator.</summary>
    public void WriteCString(string value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        EnsureCapacity(byteCount + 1);
        Encoding.UTF8.GetBytes(value, _buffer.AsSpan(_length));
        _length += byteCount;
        _buffer[_length++] = 0;
    }

    /// <summary>Overwrite a previously written little-endian uint32 (e.g. a count known only later).</summary>
    public void PatchUInt32(int position, uint value)
    {
        if (position < 0 || position + 4 > _length)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(position), value);
    }

    /// <summary>Discard the contents, keeping the buffer for reuse.</summary>
    public void Reset() => _length = 0;

    public ReadOnlyMemory<byte> AsMemory() => _buffer.AsMemory(0, _length);

    public ReadOnlySpan<byte> AsSpan() => _buffer.AsSpan(0, _length);

    public byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();

    private void EnsureCapacity(int additional)
    {
        if (_length + additional <= _buffer.Length)
        {
            return;
        }

        Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + additional));
    }
}
