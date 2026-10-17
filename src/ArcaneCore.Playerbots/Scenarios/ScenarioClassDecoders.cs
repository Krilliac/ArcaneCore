using ArcaneCore.Protocol;

namespace ArcaneCore.World.Playerbots.Scenarios;

/// <summary>SMSG_SPELLNONMELEEDAMAGELOG as a scenario sees it: the target, the caster, the spell, the damage, the school and the hit info.</summary>
public sealed record SpellDamageView(ulong Target, ulong Caster, uint SpellId, uint Damage, byte School, uint Absorbed, uint Resisted, uint HitInfo = 0)
{
    /// <summary>SPELL_HIT_TYPE_CRIT (0x2) in the hit info: the damage is a critical hit's.</summary>
    public bool Critical => (HitInfo & 0x2) != 0;
}

/// <summary>SMSG_PERIODICAURALOG (one entry) as a scenario sees it: the target, the caster, the spell, the aura type and the amount.</summary>
public sealed record PeriodicAuraView(ulong Target, ulong Caster, uint SpellId, uint AuraType, uint Amount);

/// <summary>
/// Typed decoders for the class-script scenarios (docs/areas/class-scripts.md), mirroring <c>SpellPackets</c>: kept apart from
/// <see cref="ScenarioDecoders"/> so the class-scripts lane's harness additions live in their own file.
/// </summary>
public static class ScenarioClassDecoders
{
    /// <summary>
    /// SMSG_SPELLNONMELEEDAMAGELOG: packed target, packed caster, u32 spell, u32 damage, u8 school, u32 absorbed, u32 resisted, u8 periodic, u8 unused,
    /// u32 blocked, u32 hit info, u8 extend (vmangos Unit::SendSpellNonMeleeDamageLog).
    /// </summary>
    public static SpellDamageView SpellDamage(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        try
        {
            var r = new PacketReader(payload);
            ulong target = r.ReadPackedGuid();
            ulong caster = r.ReadPackedGuid();
            uint spell = r.ReadUInt32();
            uint damage = r.ReadUInt32();
            byte school = r.ReadByte();
            uint absorbed = r.ReadUInt32();
            uint resisted = r.ReadUInt32();
            _ = r.ReadByte(); // periodic
            _ = r.ReadByte(); // unused
            _ = r.ReadUInt32(); // blocked
            uint hitInfo = r.ReadUInt32();
            return new SpellDamageView(target, caster, spell, damage, school, absorbed, resisted, hitInfo);
        }
        catch (Exception ex) when (ex is not FormatException)
        {
            throw new FormatException($"SpellDamage: malformed {payload.Length}-byte body", ex);
        }
    }

    /// <summary>
    /// SMSG_PERIODICAURALOG with its first entry: packed target, packed caster, u32 spell, u32 count, then u32 aura type and its amount (damage
    /// and heal types: the amount first; energize types: the power first, then the amount; vmangos Unit::SendPeriodicAuraLog).
    /// </summary>
    public static PeriodicAuraView PeriodicAura(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        try
        {
            var r = new PacketReader(payload);
            ulong target = r.ReadPackedGuid();
            ulong caster = r.ReadPackedGuid();
            uint spell = r.ReadUInt32();
            uint count = r.ReadUInt32();
            if (count == 0)
            {
                return new PeriodicAuraView(target, caster, spell, 0, 0);
            }

            uint auraType = r.ReadUInt32();
            uint amount = auraType is 24 or 21 ? SkipThenRead(r) : r.ReadUInt32(); // PERIODIC_ENERGIZE (24), OBS_MOD_MANA (21)
            return new PeriodicAuraView(target, caster, spell, auraType, amount);
        }
        catch (Exception ex) when (ex is not FormatException)
        {
            throw new FormatException($"PeriodicAura: malformed {payload.Length}-byte body", ex);
        }
    }

    private static uint SkipThenRead(PacketReader reader)
    {
        _ = reader.ReadUInt32();
        return reader.ReadUInt32();
    }
}
