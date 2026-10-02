using System.Numerics;

namespace ArcaneCore.Game;

/// <summary>
/// The bitmask of fields in an object-update values block. Serialized as a 1-byte block
/// count (number of uint32 words) followed by that many little-endian words (vmangos
/// UpdateMask / Object::BuildValuesUpdate).
/// </summary>
public sealed class UpdateMask
{
    private readonly uint[] _blocks;

    public UpdateMask(int fieldCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fieldCount);
        FieldCount = fieldCount;
        _blocks = new uint[(fieldCount + 31) / 32];
    }

    public int FieldCount { get; }

    public int BlockCount => _blocks.Length;

    public bool IsEmpty
    {
        get
        {
            foreach (uint block in _blocks)
            {
                if (block != 0)
                {
                    return false;
                }
            }

            return true;
        }
    }

    public void SetBit(int index) => _blocks[index >> 5] |= 1u << (index & 31);

    public void UnsetBit(int index) => _blocks[index >> 5] &= ~(1u << (index & 31));

    public bool GetBit(int index) => (_blocks[index >> 5] & (1u << (index & 31))) != 0;

    public void Clear() => Array.Clear(_blocks);

    /// <summary>Index of the next set bit at or after <paramref name="start"/>, or -1.</summary>
    public int NextSetBit(int start)
    {
        if (start >= FieldCount)
        {
            return -1;
        }

        int blockIndex = start >> 5;
        uint block = _blocks[blockIndex] & (uint.MaxValue << (start & 31));
        while (true)
        {
            if (block != 0)
            {
                int index = (blockIndex << 5) + BitOperations.TrailingZeroCount(block);
                return index < FieldCount ? index : -1;
            }

            if (++blockIndex >= _blocks.Length)
            {
                return -1;
            }

            block = _blocks[blockIndex];
        }
    }

    /// <summary>Append the block count (1 byte) and the mask words (little-endian) to the writer.</summary>
    public void WriteTo(Protocol.PacketWriter writer)
    {
        writer.WriteByte((byte)BlockCount);
        foreach (uint block in _blocks)
        {
            writer.WriteUInt32(block);
        }
    }
}
