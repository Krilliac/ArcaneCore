namespace ArcaneCore.Game;

/// <summary>
/// High 16 bits of an object GUID for build 5875 (vmangos ObjectGuid.h HighGuid; the inline
/// "blizz" comments there record the retail values).
/// </summary>
public enum HighGuid : ushort
{
    Player = 0x0000,
    Item = 0x4000,
    Container = 0x4000,
    DynamicObject = 0xF100,
    Corpse = 0xF101,
    GameObject = 0xF110,
    Transport = 0xF120,
    Unit = 0xF130,
    Pet = 0xF140,
    MoTransport = 0x1FC0,
}

/// <summary>
/// A 64-bit object GUID. Layout (vmangos ObjectGuid constructors): players and items carry a
/// 32-bit counter under the high part; creatures, pets and game objects carry
/// <c>high &lt;&lt; 48 | entry &lt;&lt; 24 | 24-bit counter</c>.
/// </summary>
public readonly record struct ObjectGuid(ulong Value)
{
    public static readonly ObjectGuid Empty = new(0);

    public HighGuid High => (HighGuid)(Value >> 48);

    public bool IsEmpty => Value == 0;

    public bool IsPlayer => Value != 0 && High == HighGuid.Player;

    /// <summary>Whether this GUID kind embeds an entry id (vmangos ObjectGuid::HasEntry).</summary>
    public bool HasEntry => High is HighGuid.Unit or HighGuid.Pet or HighGuid.GameObject or HighGuid.Transport;

    /// <summary>Low 32 bits — the database id for players.</summary>
    public uint Low => (uint)(Value & 0xFFFFFFFF);

    /// <summary>The per-type counter (24 bits for entry-carrying GUIDs, else 32).</summary>
    public uint Counter => HasEntry ? (uint)(Value & 0x00FFFFFF) : Low;

    /// <summary>The template entry for entry-carrying GUIDs, else 0.</summary>
    public uint Entry => HasEntry ? (uint)((Value >> 24) & 0x00FFFFFF) : 0;

    /// <summary>Player GUID: HIGHGUID_PLAYER is 0, so the value is the character id.</summary>
    public static ObjectGuid Player(uint counter) => new(counter);

    /// <summary>Item/container GUID: HIGHGUID_ITEM | counter.</summary>
    public static ObjectGuid Item(uint counter) => new(((ulong)HighGuid.Item << 48) | counter);

    /// <summary>Entry-carrying GUID (creature, pet, game object, transport).</summary>
    public static ObjectGuid WithEntry(HighGuid high, uint entry, uint counter)
        => new(((ulong)high << 48) | ((ulong)(entry & 0x00FFFFFF) << 24) | (counter & 0x00FFFFFF));

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

    public override string ToString() => $"{High}:{Value:X16}";
}
