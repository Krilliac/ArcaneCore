using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells.Casters.Drain;

/// <summary>
/// Packet of the drain auras that the shared <see cref="SpellPackets"/> builders cannot express: the PERIODIC_MANA_LEECH entry of
/// SMSG_PERIODICAURALOG. Layout: wow_messages wow_message_parser/wowm/world/spell/smsg_periodicauralog.wowm
/// (AuraLog, aura_type == PERIODIC_MANA_LEECH: u32 misc_value, u32 damage, f32 gain_multiplier).
/// </summary>
public static class CasterPeriodicPackets
{
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
