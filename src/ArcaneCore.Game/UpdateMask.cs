namespace ArcaneCore.Game;

/// <summary>
/// The bitmask of set fields in an object-update values block. Serialized as a leading
/// block count (number of uint32 words) followed by that many little-endian words
/// (vmangos UpdateMask).
/// </summary>
public sealed class UpdateMask
{
    private readonly uint[] _blocks;

    public UpdateMask(int fieldCount)
    {
        FieldCount = fieldCount;
        _blocks = new uint[(fieldCount + 31) / 32];
    }

    public int FieldCount { get; }

    public int BlockCount => _blocks.Length;

    public void SetBit(int index) => _blocks[index >> 5] |= 1u << (index & 31);

    public bool GetBit(int index) => (_blocks[index >> 5] & (1u << (index & 31))) != 0;

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
