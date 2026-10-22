using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells.Procs;

/// <summary>The packets of the proc engine.</summary>
public static class ProcPackets
{
    /// <summary>SMSG_PROCRESIST (vmangos WorldPackets::Spell::ProcResist, Server/Packets/Spell.cpp:123-137): caster, target, spell, log format 0.</summary>
    public static byte[] BuildProcResist(ObjectGuid caster, ObjectGuid target, uint spellId)
    {
        var writer = new PacketWriter(21);
        writer.WriteUInt64(caster.Value);
        writer.WriteUInt64(target.Value);
        writer.WriteUInt32(spellId);
        writer.WriteByte(0);
        return writer.ToArray();
    }

    /// <summary>SMSG_SPELLDAMAGESHIELD (vmangos WorldPackets::Combat::SpellDamageShield, Server/Packets/Combat.cpp:139-153): shield bearer, attacker, damage, school.</summary>
    public static byte[] BuildSpellDamageShield(ObjectGuid victim, ObjectGuid attacker, uint damage, uint school)
    {
        var writer = new PacketWriter(24);
        writer.WriteUInt64(victim.Value);
        writer.WriteUInt64(attacker.Value);
        writer.WriteUInt32(damage);
        writer.WriteUInt32(school);
        return writer.ToArray();
    }
}
