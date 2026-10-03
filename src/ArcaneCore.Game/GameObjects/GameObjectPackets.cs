using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.GameObjects;

/// <summary>Game object packets for 1.12.1 (field order from vmangos/cmangos-classic and gtker/wow_messages).</summary>
public static class GameObjectPackets
{
    /// <summary>
    /// SMSG_GAMEOBJECT_QUERY_RESPONSE: u32 entry, u32 type, u32 display id, cstring name, three
    /// more empty name cstrings, cstring (unused, empty), then the 24 u32 data fields (vmangos
    /// and cmangos-classic HandleGameObjectQueryOpcode). gtker/wow_messages models the data as a
    /// shorter array; the 1.12.1 client reads all 24 — see docs/integration/gameobjects-loot.md.
    /// </summary>
    public static byte[] QueryResponse(GameObjectTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var writer = new PacketWriter(128);
        writer.WriteUInt32(template.Entry);
        writer.WriteUInt32(template.Type);
        writer.WriteUInt32(template.DisplayId);
        writer.WriteCString(template.Name);
        writer.WriteByte(0);
        writer.WriteByte(0);
        writer.WriteByte(0);
        writer.WriteByte(0);
        for (int i = 0; i < GameObjectTemplate.DataCount; i++)
        {
            writer.WriteUInt32(template.GetData(i));
        }

        return writer.ToArray();
    }

    /// <summary>SMSG_GAMEOBJECT_QUERY_RESPONSE for an unknown entry: entry | 0x80000000.</summary>
    public static byte[] QueryUnknown(uint entry)
    {
        var writer = new PacketWriter(4);
        writer.WriteUInt32(entry | 0x80000000u);
        return writer.ToArray();
    }

    /// <summary>SMSG_GAMEOBJECT_CUSTOM_ANIM: u64 guid, u32 animation.</summary>
    public static byte[] CustomAnim(ObjectGuid guid, uint anim)
    {
        var writer = new PacketWriter(12);
        writer.WriteUInt64(guid.Value);
        writer.WriteUInt32(anim);
        return writer.ToArray();
    }

    /// <summary>SMSG_GAMEOBJECT_PAGETEXT: u64 guid (the client then queries the page text).</summary>
    public static byte[] PageText(ObjectGuid guid)
    {
        var writer = new PacketWriter(8);
        writer.WriteUInt64(guid.Value);
        return writer.ToArray();
    }
}
