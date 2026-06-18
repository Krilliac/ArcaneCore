namespace ArcaneCore.Game;

/// <summary>
/// A 64-bit object GUID. Players in vanilla use high-guid 0, so the value is just the low
/// guid (the character's database id). Provides the WoW "packed GUID" encoding used in
/// object-update blocks (vmangos ByteBuffer::appendPackGUID).
/// </summary>
public readonly struct ObjectGuid(ulong value)
{
    public ulong Value { get; } = value;

    public uint Low => (uint)(Value & 0xFFFFFFFF);

    /// <summary>Player GUID = low guid with high part 0 (HIGHGUID_PLAYER is 0 in 1.12).</summary>
    public static ObjectGuid Player(uint low) => new(low);

    /// <summary>
    /// Packed-GUID encoding: a mask byte whose bit i is set when byte i of the GUID is
    /// non-zero, followed by those non-zero bytes (low to high).
    /// </summary>
    public byte[] ToPacked()
    {
        Span<byte> bytes = stackalloc byte[9];
        byte mask = 0;
        int length = 1;
        ulong v = Value;
        for (int i = 0; i < 8; i++)
        {
            byte b = (byte)(v & 0xFF);
            if (b != 0)
            {
                mask |= (byte)(1 << i);
                bytes[length++] = b;
            }

            v >>= 8;
        }

        bytes[0] = mask;
        return bytes[..length].ToArray();
    }
}
