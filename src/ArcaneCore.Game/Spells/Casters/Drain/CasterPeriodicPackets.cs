using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells.Casters.Drain;

/// <summary>
/// Packets of the drain auras that the shared <see cref="SpellPackets"/> builders cannot express: the periodic flag of
/// SMSG_SPELLNONMELEEDAMAGELOG (leech ticks) and the PERIODIC_MANA_LEECH entry of SMSG_PERIODICAURALOG.
/// Layouts: D:\refs\wow_messages\wow_message_parser\wowm\world\spell\smsg_spellnonmeleedamagelog.wowm (1.12 version) and
/// smsg_periodicauralog.wowm (AuraLog, aura_type == PERIODIC_MANA_LEECH: u32 misc_value, u32 damage, f32 gain_multiplier).
/// </summary>
public static class CasterPeriodicPackets
{
    /// <summary>
    /// SMSG_SPELLNONMELEEDAMAGELOG for a periodic hit (vmangos SendSpellNonMeleeDamageLog with isPeriodic = true): the
    /// client names the spell ("X suffers N damage from Y's spell").
    /// </summary>
    public static byte[] BuildPeriodicSpellDamageLog(ObjectGuid target, ObjectGuid caster, uint spellId, uint damage, SpellSchool school, uint absorbed, uint resisted)
    {
        var writer = new PacketWriter(42);
        writer.WritePackedGuid(target.Value);
        writer.WritePackedGuid(caster.Value);
        writer.WriteUInt32(spellId);
        writer.WriteUInt32(damage);
        writer.WriteByte((byte)school);
        writer.WriteUInt32(absorbed);
        writer.WriteUInt32(resisted);
        writer.WriteByte(1); // periodic_log
        writer.WriteByte(0); // unused
        writer.WriteUInt32(0); // blocked
        writer.WriteUInt32(0); // hit info
        writer.WriteByte(0); // extend flag
        return writer.ToArray();
    }

    /// <summary>SMSG_PERIODICAURALOG with one PERIODIC_MANA_LEECH entry: aura 64, power type, drained amount, gain multiplier.</summary>
    public static byte[] BuildManaLeechLog(ObjectGuid target, ObjectGuid caster, uint spellId, uint power, uint drained, float gainMultiplier)
    {
        var writer = new PacketWriter(40);
        writer.WritePackedGuid(target.Value);
        writer.WritePackedGuid(caster.Value);
        writer.WriteUInt32(spellId);
        writer.WriteUInt32(1);
        writer.WriteUInt32((uint)AuraType.PeriodicManaLeech);
        writer.WriteUInt32(power);
        writer.WriteUInt32(drained);
        writer.WriteSingle(gainMultiplier);
        return writer.ToArray();
    }
}
