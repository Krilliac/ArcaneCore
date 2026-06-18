using System.Buffers.Binary;
using System.Text;

namespace ArcaneCore.Protocol;

/// <summary>Sequential little-endian reader over a packet payload.</summary>
public ref struct PacketReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _position;

    public readonly int Remaining => _data.Length - _position;

    public byte ReadByte() => _data[_position++];

    public uint ReadUInt32()
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_position, 4));
        _position += 4;
        return value;
    }

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        ReadOnlySpan<byte> slice = _data.Slice(_position, count);
        _position += count;
        return slice;
    }

    /// <summary>Read a null-terminated ASCII string.</summary>
    public string ReadCString()
    {
        int start = _position;
        while (_position < _data.Length && _data[_position] != 0)
        {
            _position++;
        }

        string value = Encoding.ASCII.GetString(_data.Slice(start, _position - start));
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
