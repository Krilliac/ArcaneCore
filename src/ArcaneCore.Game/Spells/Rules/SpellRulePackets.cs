using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells.Rules;

/// <summary>
/// Packet builders for the spell-rule results the base <see cref="SpellPackets"/> do not cover. Layouts
/// are the 1.12 forms in D:\refs\wow_messages\wowm_language\src\docs (cited per member); bodies only,
/// the session adds the opcode header.
/// </summary>
public static class SpellRulePackets
{
    /// <summary>SMSG_SPELLORDAMAGE_IMMUNE (smsg_spellordamage_immune.md): u64 caster, u64 target, u32 spell, u8 debug-log format.</summary>
    public static byte[] BuildSpellOrDamageImmune(ObjectGuid caster, ObjectGuid target, uint spellId, bool debugLogFormat = false)
    {
        var writer = new PacketWriter(21);
        writer.WriteUInt64(caster.Value);
        writer.WriteUInt64(target.Value);
        writer.WriteUInt32(spellId);
        writer.WriteByte(debugLogFormat ? (byte)1 : (byte)0);
        return writer.ToArray();
    }

    /// <summary>SMSG_SPELLDISPELLOG, client 1.12 block of smsg_spelldispellog.md: packed victim, packed caster, u32 count, u32 spells.</summary>
    public static byte[] BuildSpellDispelLog(ObjectGuid victim, ObjectGuid caster, IReadOnlyList<uint> dispelledSpells)
    {
        ArgumentNullException.ThrowIfNull(dispelledSpells);
        var writer = new PacketWriter(20 + (dispelledSpells.Count * 4));
        writer.WritePackedGuid(victim.Value);
        writer.WritePackedGuid(caster.Value);
        writer.WriteUInt32((uint)dispelledSpells.Count);
        foreach (uint spell in dispelledSpells)
        {
            writer.WriteUInt32(spell);
        }

        return writer.ToArray();
    }

    /// <summary>SMSG_DISPEL_FAILED (smsg_dispel_failed.md): u64 caster, u64 target, then u32 spells to the end of the packet.</summary>
    public static byte[] BuildDispelFailed(ObjectGuid caster, ObjectGuid target, IReadOnlyList<uint> failedSpells)
    {
        ArgumentNullException.ThrowIfNull(failedSpells);
        var writer = new PacketWriter(16 + (failedSpells.Count * 4));
        writer.WriteUInt64(caster.Value);
        writer.WriteUInt64(target.Value);
        foreach (uint spell in failedSpells)
        {
            writer.WriteUInt32(spell);
        }

        return writer.ToArray();
    }
}
