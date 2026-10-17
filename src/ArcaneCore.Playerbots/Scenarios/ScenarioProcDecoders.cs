using ArcaneCore.Protocol;

namespace ArcaneCore.World.Playerbots.Scenarios;

/// <summary>SMSG_SPELLDAMAGESHIELD as a scenario sees it: the shield bearer, the attacker it hurt, the damage and the shield's school.</summary>
public sealed record DamageShieldView(ulong Victim, ulong Attacker, uint Damage, uint School);

/// <summary>SMSG_PROCRESIST as a scenario sees it: the aura owner, the unit that resisted its proc damage, the aura spell.</summary>
public sealed record ProcResistView(ulong Caster, ulong Target, uint SpellId);

/// <summary>
/// Typed decoders for the proc engine's packets (docs/areas/procs.md), mirroring <c>ProcPackets</c>: kept apart from
/// <see cref="ScenarioDecoders"/> so the proc lane's harness additions live in their own file.
/// </summary>
public static class ScenarioProcDecoders
{
    /// <summary>SMSG_SPELLDAMAGESHIELD: u64 victim, u64 attacker, u32 damage, u32 school (vmangos Server/Packets/Combat.cpp:147-153).</summary>
    public static DamageShieldView DamageShield(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length != 24)
        {
            throw new FormatException($"DamageShield: malformed {payload.Length}-byte body");
        }

        var r = new PacketReader(payload);
        return new DamageShieldView(r.ReadUInt64(), r.ReadUInt64(), r.ReadUInt32(), r.ReadUInt32());
    }

    /// <summary>SMSG_PROCRESIST: u64 caster, u64 target, u32 spell, u8 log format (vmangos Server/Packets/Spell.cpp:131-137).</summary>
    public static ProcResistView ProcResist(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length != 21)
        {
            throw new FormatException($"ProcResist: malformed {payload.Length}-byte body");
        }

        var r = new PacketReader(payload);
        return new ProcResistView(r.ReadUInt64(), r.ReadUInt64(), r.ReadUInt32());
    }
}
