using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Creatures;

/// <summary>Creature query packets for build 5875.</summary>
public static class CreaturePackets
{
    /// <summary>vmangos/gtker: the "not found" reply is the queried entry with the high bit set.</summary>
    public const uint NotFoundFlag = 0x80000000;

    /// <summary>
    /// SMSG_CREATURE_QUERY_RESPONSE (vmangos WorldPackets::Query::CreatureQueryResponse::AppendBodyTo,
    /// build &gt; 1.10.2; gtker/wow_messages SMSG_CREATURE_QUERY_RESPONSE 1.12 agrees):
    /// u32 entry, cstring name, three empty cstrings (name2–4), cstring subname, u32 type flags,
    /// u32 creature type, u32 family, u32 rank, u32 0 (reserved), u32 pet spell list id,
    /// u32 display id (the template's first), u8 civilian, u8 racial leader.
    /// <para>cmangos-classic writes civilian as a u16 and no racial-leader byte (same length);
    /// servers disagree, vmangos and gtker match, so the two-u8 form is used (docs/areas/creatures.md).</para>
    /// <para>Unknown entry: u32 (entry | 0x80000000) and nothing else.</para>
    /// </summary>
    public static byte[] BuildCreatureQueryResponse(uint entry, CreatureTemplate? template)
    {
        if (template is null)
        {
            var notFound = new PacketWriter(4);
            notFound.WriteUInt32(entry | NotFoundFlag);
            return notFound.ToArray();
        }

        var w = new PacketWriter(64 + template.Name.Length + template.SubName.Length);
        w.WriteUInt32(template.Entry);
        w.WriteCString(template.Name);
        w.WriteCString(string.Empty);
        w.WriteCString(string.Empty);
        w.WriteCString(string.Empty);
        w.WriteCString(template.SubName);
        w.WriteUInt32(template.TypeFlags);
        w.WriteUInt32(template.CreatureType);
        w.WriteUInt32(template.Family);
        w.WriteUInt32(template.Rank);
        w.WriteUInt32(0);
        w.WriteUInt32(template.PetSpellDataId);
        w.WriteUInt32(template.DisplayIds.Count > 0 ? template.DisplayIds[0] : 0);
        w.WriteByte(template.Civilian ? (byte)1 : (byte)0);
        w.WriteByte(template.RacialLeader ? (byte)1 : (byte)0);
        return w.ToArray();
    }
}
