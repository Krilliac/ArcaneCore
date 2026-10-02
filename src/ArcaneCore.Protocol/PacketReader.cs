using System.Buffers.Binary;
using System.Text;

namespace ArcaneCore.Protocol;

/// <summary>
/// Sequential little-endian reader over a packet payload. Reads past the end throw
/// <see cref="ArgumentOutOfRangeException"/>; handlers treat that as a malformed packet.
/// </summary>
public ref struct PacketReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _position;

    public readonly int Remaining => _data.Length - _position;

    public readonly int Position => _position;

    public byte ReadByte() => _data[_position++];

    public ushort ReadUInt16()
    {
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(_position, 2));
        _position += 2;
        return value;
    }

    public uint ReadUInt32()
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_position, 4));
        _position += 4;
        return value;
    }

    public int ReadInt32() => unchecked((int)ReadUInt32());

    public ulong ReadUInt64()
    {
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(_data.Slice(_position, 8));
        _position += 8;
        return value;
    }

    public float ReadSingle()
    {
        float value = BinaryPrimitives.ReadSingleLittleEndian(_data.Slice(_position, 4));
        _position += 4;
        return value;
    }

    /// <summary>
    /// Read a packed GUID: a mask byte whose bit i says byte i of the GUID follows
    /// (vmangos ByteBuffer::readPackGUID).
    /// </summary>
    public ulong ReadPackedGuid()
    {
        byte mask = ReadByte();
        ulong value = 0;
        for (int i = 0; i < 8; i++)
        {
            if ((mask & (1 << i)) != 0)
            {
                value |= (ulong)ReadByte() << (i * 8);
            }
        }

        return value;
    }

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        ReadOnlySpan<byte> slice = _data.Slice(_position, count);
        _position += count;
        return slice;
    }

    public void Skip(int count)
    {
        if (count < 0 || count > Remaining)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        _position += count;
    }

    /// <summary>Read a null-terminated string (the client sends UTF-8).</summary>
    public string ReadCString()
    {
        int start = _position;
        while (_position < _data.Length && _data[_position] != 0)
        {
            _position++;
        }

        string value = Encoding.UTF8.GetString(_data.Slice(start, _position - start));
        if (_position < _data.Length)
        {
            _position++; // consume null terminator
        }

        return value;
    }

    public ReadOnlySpan<byte> ReadToEnd()
    {
        ReadOnlySpan<byte> slice = _data.Slice(_position);
        _position = _data.Length;
        return slice;
    }
}
