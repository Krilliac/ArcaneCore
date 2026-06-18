using System.Buffers.Binary;
using System.Text;

namespace ArcaneCore.Realm.Protocol;

/// <summary>
/// Minimal growable little-endian packet builder for the logon stream. All multi-byte
/// integers on this stream are little-endian (vmangos ByteBuffer semantics); strings are
/// null-terminated C-strings.
/// </summary>
public sealed class PacketWriter
{
    private byte[] _buffer;
    private int _length;

    public PacketWriter(int capacity = 128)
    {
        _buffer = new byte[capacity];
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

    public void WriteSingle(float value)
    {
        EnsureCapacity(4);
        BinaryPrimitives.WriteSingleLittleEndian(_buffer.AsSpan(_length), value);
        _length += 4;
    }

    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        EnsureCapacity(value.Length);
        value.CopyTo(_buffer.AsSpan(_length));
        _length += value.Length;
    }

    /// <summary>Write an ASCII string followed by a null terminator.</summary>
    public void WriteCString(string value)
    {
        int byteCount = Encoding.ASCII.GetByteCount(value);
        EnsureCapacity(byteCount + 1);
        Encoding.ASCII.GetBytes(value, _buffer.AsSpan(_length));
        _length += byteCount;
        _buffer[_length++] = 0;
    }

    public ReadOnlyMemory<byte> AsMemory() => _buffer.AsMemory(0, _length);

    private void EnsureCapacity(int additional)
    {
        if (_length + additional <= _buffer.Length)
        {
            return;
        }

        int newCapacity = Math.Max(_buffer.Length * 2, _length + additional);
        Array.Resize(ref _buffer, newCapacity);
    }
}
