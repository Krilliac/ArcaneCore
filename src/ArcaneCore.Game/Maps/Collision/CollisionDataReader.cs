using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace ArcaneCore.Game.Maps.Collision;

/// <summary>
/// Bounds-checked little-endian reader over a collision data file (vmap/mmap). Every read past
/// the end, and every count that cannot fit in the remaining bytes, throws
/// <see cref="InvalidDataException"/>, so a truncated or corrupt file is rejected instead of
/// over-allocating or reading garbage.
/// </summary>
internal ref struct CollisionDataReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;

    public int Position { get; private set; }

    public readonly int Remaining => _data.Length - Position;

    public readonly bool AtEnd => Position >= _data.Length;

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        if (count < 0 || count > Remaining)
        {
            throw new InvalidDataException($"truncated: {count} bytes wanted at {Position}, {Remaining} left");
        }

        ReadOnlySpan<byte> slice = _data.Slice(Position, count);
        Position += count;
        return slice;
    }

    public void Skip(int count) => ReadBytes(count);

    /// <summary>Move to an absolute offset (0 ≤ offset ≤ length).</summary>
    public void Seek(int offset)
    {
        if (offset < 0 || offset > _data.Length)
        {
            throw new InvalidDataException($"offset {offset} is outside the data ({_data.Length} bytes)");
        }

        Position = offset;
    }

    /// <summary>Round the position up to a multiple of four (Detour <c>dtAlign4</c>).</summary>
    public void Align4() => Seek(Math.Min(_data.Length, (Position + 3) & ~3));

    public byte ReadByte() => ReadBytes(1)[0];

    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(ReadBytes(2));

    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(4));

    public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(ReadBytes(4));

    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(ReadBytes(8));

    public float ReadSingle() => BinaryPrimitives.ReadSingleLittleEndian(ReadBytes(4));

    public Vector3 ReadVector3() => new(ReadSingle(), ReadSingle(), ReadSingle());

    /// <summary>A count of elements of <paramref name="elementSize"/> bytes that must still fit in the data.</summary>
    public int ReadCount(int elementSize)
    {
        uint count = ReadUInt32();
        if (count > int.MaxValue || (long)count * elementSize > Remaining)
        {
            throw new InvalidDataException($"count {count} × {elementSize} bytes does not fit in the {Remaining} bytes left");
        }

        return (int)count;
    }

    /// <summary>Read and check a fixed ASCII tag (chunk name or magic).</summary>
    public void Expect(string tag)
    {
        ReadOnlySpan<byte> bytes = ReadBytes(tag.Length);
        for (int i = 0; i < tag.Length; i++)
        {
            if (bytes[i] != (byte)tag[i])
            {
                throw new InvalidDataException($"expected '{tag}' at {Position - tag.Length}, found '{Encoding.ASCII.GetString(bytes)}'");
            }
        }
    }

    /// <summary>Whether the next bytes are <paramref name="tag"/> (consumed only when they are).</summary>
    public bool TryExpect(string tag)
    {
        if (Remaining < tag.Length)
        {
            return false;
        }

        ReadOnlySpan<byte> bytes = _data.Slice(Position, tag.Length);
        for (int i = 0; i < tag.Length; i++)
        {
            if (bytes[i] != (byte)tag[i])
            {
                return false;
            }
        }

        Position += tag.Length;
        return true;
    }
}
