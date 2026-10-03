using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells.Casters.Dispel;

/// <summary>
/// The dispel result packets of the 1.12 client. Layouts:
/// D:\refs\wow_messages\wow_message_parser\wowm\world\spell\smsg_spelldispellog.wowm (version 1.12) and smsg_dispel_failed.wowm;
/// vmangos Spell::EffectDispel (SpellEffects.cpp:2540-2590).
/// </summary>
public static class DispelPackets
{
    /// <summary>SMSG_SPELLDISPELLOG: packed victim, packed caster, u32 count, then the dispelled spell ids.</summary>
    public static byte[] BuildDispelLog(ObjectGuid victim, ObjectGuid caster, IReadOnlyList<uint> spellIds)
    {
        ArgumentNullException.ThrowIfNull(spellIds);
        var writer = new PacketWriter(24 + (spellIds.Count * 4));
        writer.WritePackedGuid(victim.Value);
        writer.WritePackedGuid(caster.Value);
        writer.WriteUInt32((uint)spellIds.Count);
        foreach (uint id in spellIds)
        {
            writer.WriteUInt32(id);
        }

        return writer.ToArray();
    }

    /// <summary>SMSG_DISPEL_FAILED: full caster guid, full victim guid, then the spell ids that resisted.</summary>
    public static byte[] BuildDispelFailed(ObjectGuid caster, ObjectGuid victim, IReadOnlyList<uint> spellIds)
    {
        ArgumentNullException.ThrowIfNull(spellIds);
        var writer = new PacketWriter(16 + (spellIds.Count * 4));
        writer.WriteUInt64(caster.Value);
        writer.WriteUInt64(victim.Value);
        foreach (uint id in spellIds)
        {
            writer.WriteUInt32(id);
        }

        return writer.ToArray();
    }
}
