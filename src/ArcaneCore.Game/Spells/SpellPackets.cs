using ArcaneCore.Game.Ranged;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells;

/// <summary>A cooldown entry of SMSG_INITIAL_SPELLS.</summary>
public readonly record struct InitialSpellCooldown(uint SpellId, uint ItemId, uint Category, uint CooldownMs, uint CategoryCooldownMs);

/// <summary>One SMSG_PERIODICAURALOG entry (aura type + its type-specific payload).</summary>
public readonly record struct PeriodicLogEntry(AuraType AuraType, uint Amount, uint SchoolOrPower, uint Absorbed = 0, uint Resisted = 0);

/// <summary>
/// Spell packet bodies for build 5875. Layouts are cited per method; gtker/wow_messages
/// (wowm/world/spell/*.wowm) is the second source for each, and disagreements are recorded in
/// docs/areas/spells.md (servers win).
/// </summary>
public static class SpellPackets
{
    /// <summary>
    /// SMSG_INITIAL_SPELLS (vmangos Player::SendInitialSpells / WorldPackets::Spell::InitialSpells):
    /// u8 talent spec (0), u16 count, (u16 spell, u16 0)*, u16 cooldown count, (u16 spell, u16 item,
    /// u16 category, u32 cooldown ms, u32 category cooldown ms)*. gtker smsg_initial_spells agrees.
    /// </summary>
    public static byte[] BuildInitialSpells(IReadOnlyCollection<uint> spells, IReadOnlyCollection<InitialSpellCooldown> cooldowns)
    {
        ArgumentNullException.ThrowIfNull(spells);
        ArgumentNullException.ThrowIfNull(cooldowns);
        var writer = new PacketWriter(5 + (spells.Count * 4) + (cooldowns.Count * 14));
        writer.WriteByte(0);
        writer.WriteUInt16((ushort)spells.Count);
        foreach (uint spell in spells)
        {
            writer.WriteUInt16((ushort)spell);
            writer.WriteUInt16(0);
        }

        writer.WriteUInt16((ushort)cooldowns.Count);
        foreach (InitialSpellCooldown cd in cooldowns)
        {
            writer.WriteUInt16((ushort)cd.SpellId);
            writer.WriteUInt16((ushort)cd.ItemId);
            writer.WriteUInt16((ushort)cd.Category);
            writer.WriteUInt32(cd.CooldownMs);
            writer.WriteUInt32(cd.CategoryCooldownMs);
        }

        return writer.ToArray();
    }

    /// <summary>
    /// SMSG_LEARNED_SPELL. vmangos WorldPackets::Spell::LearnedSpell writes u16 spell + i16 slot
    /// (0); gtker smsg_learned_spell lists one u32. The bytes are identical for 1.12 spell ids
    /// (&lt; 65536), so one u32 is written.
    /// </summary>
    public static byte[] BuildLearnedSpell(uint spellId)
    {
        var writer = new PacketWriter(4);
        writer.WriteUInt32(spellId);
        return writer.ToArray();
    }

    /// <summary>SMSG_REMOVED_SPELL: u16 spell (vmangos Player::RemoveSpell; gtker smsg_removed_spell).</summary>
    public static byte[] BuildRemovedSpell(uint spellId)
    {
        var writer = new PacketWriter(2);
        writer.WriteUInt16((ushort)spellId);
        return writer.ToArray();
    }

    /// <summary>
    /// SMSG_SPELL_START (vmangos Spell::SendSpellStart): packed cast-item-or-caster GUID, packed
    /// caster GUID, u32 spell, u16 cast flags, u32 cast time, targets [, u32 ammo display, u32
    /// ammo inventory type when CAST_FLAG_AMMO].
    /// </summary>
    public static byte[] BuildSpellStart(ObjectGuid castItemOrCaster, ObjectGuid caster, uint spellId, SpellCastFlags flags, uint castTimeMs, SpellCastTargets targets, AmmoVisual ammo = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var writer = new PacketWriter(40);
        writer.WritePackedGuid(castItemOrCaster.Value);
        writer.WritePackedGuid(caster.Value);
        writer.WriteUInt32(spellId);
        writer.WriteUInt16((ushort)flags);
        writer.WriteUInt32(castTimeMs);
        targets.Write(writer);
        if ((flags & SpellCastFlags.Ammo) != 0)
        {
            writer.WriteUInt32(ammo.DisplayId);
            writer.WriteUInt32(ammo.InventoryType);
        }

        return writer.ToArray();
    }

    /// <summary>
    /// SMSG_SPELL_GO (vmangos Spell::SendSpellGo + WriteSpellGoTargets): packed cast-item-or-caster
    /// GUID, packed caster GUID, u32 spell, u16 cast flags, u8 hit count + full u64 GUIDs, u8 miss
    /// count + (u64 GUID, u8 miss reason [, u8 reflect result when REFLECT]), targets [, ammo].
    /// </summary>
    public static byte[] BuildSpellGo(
        ObjectGuid castItemOrCaster, ObjectGuid caster, uint spellId, SpellCastFlags flags,
        IReadOnlyList<ObjectGuid> hits, IReadOnlyList<(ObjectGuid Guid, SpellMissInfo Reason)> misses, SpellCastTargets targets, AmmoVisual ammo = default)
    {
        ArgumentNullException.ThrowIfNull(hits);
        ArgumentNullException.ThrowIfNull(misses);
        ArgumentNullException.ThrowIfNull(targets);
        var writer = new PacketWriter(48 + (hits.Count * 8) + (misses.Count * 10));
        writer.WritePackedGuid(castItemOrCaster.Value);
        writer.WritePackedGuid(caster.Value);
        writer.WriteUInt32(spellId);
        writer.WriteUInt16((ushort)flags);
        writer.WriteByte((byte)hits.Count);
        foreach (ObjectGuid hit in hits)
        {
            writer.WriteUInt64(hit.Value);
        }

        writer.WriteByte((byte)misses.Count);
        foreach ((ObjectGuid guid, SpellMissInfo reason) in misses)
        {
            writer.WriteUInt64(guid.Value);
            writer.WriteByte((byte)reason);
            if (reason == SpellMissInfo.Reflect)
            {
                writer.WriteByte(0); // reflect result: the reflected spell hit (vmangos reflectResult)
            }
        }

        targets.Write(writer);
        if ((flags & SpellCastFlags.Ammo) != 0)
        {
            writer.WriteUInt32(ammo.DisplayId);
            writer.WriteUInt32(ammo.InventoryType);
        }

        return writer.ToArray();
    }

    /// <summary>
    /// SMSG_CAST_RESULT (vmangos Spell::SendCastResult / WorldPackets::Spell::CastResult): u32
    /// spell, u8 status (0 success, 2 failure); on failure u8 reason followed by the reason's
    /// argument (REQUIRES_SPELL_FOCUS / REQUIRES_AREA: u32; EQUIPPED_ITEM_CLASS: u32 class + u32
    /// subclass mask). gtker smsg_cast_result writes the reason when "result != FAILURE",
    /// which reads inverted against every server; vmangos wins (docs/areas/spells.md).
    /// </summary>
    public static byte[] BuildCastResult(uint spellId, SpellCastResult result, uint arg1 = 0, uint arg2 = 0)
    {
        var writer = new PacketWriter(14);
        writer.WriteUInt32(spellId);
        if (result == SpellCastResult.CastOk)
        {
            writer.WriteByte((byte)SpellCastResultStatus.Success);
            return writer.ToArray();
        }

        writer.WriteByte((byte)SpellCastResultStatus.Failure);
        writer.WriteByte((byte)result);
        switch (result)
        {
            case SpellCastResult.RequiresSpellFocus:
            case SpellCastResult.RequiresArea:
                writer.WriteUInt32(arg1);
                break;
            case SpellCastResult.EquippedItemClass:
                writer.WriteUInt32(arg1);
                writer.WriteUInt32(arg2);
                break;
        }

        return writer.ToArray();
    }

    /// <summary>SMSG_SPELL_FAILURE: u64 caster, u32 spell, u8 reason (vmangos WorldPackets::Spell::SpellFailure; gtker agrees).</summary>
    public static byte[] BuildSpellFailure(ObjectGuid caster, uint spellId, SpellCastResult reason)
    {
        var writer = new PacketWriter(13);
        writer.WriteUInt64(caster.Value);
        writer.WriteUInt32(spellId);
        writer.WriteByte((byte)reason);
        return writer.ToArray();
    }

    /// <summary>SMSG_SPELL_FAILED_OTHER: u64 caster, u32 spell (vmangos Spell::SendInterrupted; gtker agrees).</summary>
    public static byte[] BuildSpellFailedOther(ObjectGuid caster, uint spellId)
    {
        var writer = new PacketWriter(12);
        writer.WriteUInt64(caster.Value);
        writer.WriteUInt32(spellId);
        return writer.ToArray();
    }

    /// <summary>
    /// SMSG_SPELL_COOLDOWN for 1.12: u64 caster, then (u32 spell, u32 ms)* to the end of the packet
    /// (vmangos WorldPackets::Spell::SpellCooldown; cmangos-classic Player::AddGCD comments out the
    /// later expansions' flags byte; gtker smsg_spell_cooldown agrees).
    /// </summary>
    public static byte[] BuildSpellCooldown(ObjectGuid caster, IReadOnlyList<(uint SpellId, uint Ms)> cooldowns)
    {
        ArgumentNullException.ThrowIfNull(cooldowns);
        var writer = new PacketWriter(8 + (cooldowns.Count * 8));
        writer.WriteUInt64(caster.Value);
        foreach ((uint spell, uint ms) in cooldowns)
        {
            writer.WriteUInt32(spell);
            writer.WriteUInt32(ms);
        }

        return writer.ToArray();
    }

    /// <summary>SMSG_COOLDOWN_EVENT: u32 spell, u64 GUID (vmangos Player::SendCooldownEvent; gtker agrees).</summary>
    public static byte[] BuildCooldownEvent(uint spellId, ObjectGuid guid)
    {
        var writer = new PacketWriter(12);
        writer.WriteUInt32(spellId);
        writer.WriteUInt64(guid.Value);
        return writer.ToArray();
    }

    /// <summary>SMSG_CLEAR_COOLDOWN: u32 spell, u64 GUID (vmangos Player::RemoveSpellCooldown; gtker agrees).</summary>
    public static byte[] BuildClearCooldown(uint spellId, ObjectGuid guid)
    {
        var writer = new PacketWriter(12);
        writer.WriteUInt32(spellId);
        writer.WriteUInt64(guid.Value);
        return writer.ToArray();
    }

    /// <summary>MSG_CHANNEL_START (server form): u32 spell, u32 duration (vmangos WorldPackets::Spell::ChannelStart).</summary>
    public static byte[] BuildChannelStart(uint spellId, uint durationMs)
    {
        var writer = new PacketWriter(8);
        writer.WriteUInt32(spellId);
        writer.WriteUInt32(durationMs);
        return writer.ToArray();
    }

    /// <summary>MSG_CHANNEL_UPDATE (server form): u32 remaining ms (vmangos WorldPackets::Spell::ChannelUpdate).</summary>
    public static byte[] BuildChannelUpdate(uint remainingMs)
    {
        var writer = new PacketWriter(4);
        writer.WriteUInt32(remainingMs);
        return writer.ToArray();
    }

    /// <summary>SMSG_UPDATE_AURA_DURATION: u8 slot, u32 ms (vmangos SpellAuraHolder::SendAuraDurationForCaster / UpdateAuraDuration; gtker agrees).</summary>
    public static byte[] BuildUpdateAuraDuration(byte slot, uint durationMs)
    {
        var writer = new PacketWriter(5);
        writer.WriteByte(slot);
        writer.WriteUInt32(durationMs);
        return writer.ToArray();
    }

    /// <summary>
    /// SMSG_SPELLNONMELEEDAMAGELOG (vmangos Unit::SendSpellNonMeleeDamageLog, 1.12 branch): packed
    /// target, packed caster, u32 spell, u32 damage, u8 school, u32 absorbed, u32 resisted, u8
    /// periodic log (0), u8 unused, u32 blocked, u32 hit info, u8 extend flag (0).
    /// </summary>
    public static byte[] BuildSpellNonMeleeDamageLog(ObjectGuid target, ObjectGuid caster, uint spellId, uint damage, SpellSchool school, uint absorbed = 0, uint resisted = 0, uint blocked = 0, uint hitInfo = 0)
    {
        var writer = new PacketWriter(42);
        writer.WritePackedGuid(target.Value);
        writer.WritePackedGuid(caster.Value);
        writer.WriteUInt32(spellId);
        writer.WriteUInt32(damage);
        writer.WriteByte((byte)school);
        writer.WriteUInt32(absorbed);
        writer.WriteUInt32(resisted);
        writer.WriteByte(0);
        writer.WriteByte(0);
        writer.WriteUInt32(blocked);
        writer.WriteUInt32(hitInfo);
        writer.WriteByte(0);
        return writer.ToArray();
    }

    /// <summary>
    /// SMSG_SPELLLOGMISS (vmangos SpellCaster::SendSpellMiss, 1.12): u32 spell, u64 caster, u8 0,
    /// u32 target count (1), then (u64 target, u8 miss reason) per target. gtker smsg_spelllogmiss agrees.
    /// </summary>
    public static byte[] BuildSpellLogMiss(uint spellId, ObjectGuid caster, ObjectGuid target, SpellMissInfo reason)
    {
        var writer = new PacketWriter(22);
        writer.WriteUInt32(spellId);
        writer.WriteUInt64(caster.Value);
        writer.WriteByte(0);
        writer.WriteUInt32(1);
        writer.WriteUInt64(target.Value);
        writer.WriteByte((byte)reason);
        return writer.ToArray();
    }

    /// <summary>
    /// SMSG_SPELL_DELAYED (cmangos-classic / vmangos Spell::Delayed): packed caster GUID, u32 delay
    /// ms. gtker lists a full GUID for 1.12; the servers win (recorded in docs/integration/spells-persistence.md).
    /// </summary>
    public static byte[] BuildSpellDelayed(ObjectGuid caster, uint delayMs)
    {
        var writer = new PacketWriter(13);
        writer.WritePackedGuid(caster.Value);
        writer.WriteUInt32(delayMs);
        return writer.ToArray();
    }

    /// <summary>SMSG_SPELLHEALLOG: packed target, packed caster, u32 spell, u32 amount, u8 critical (vmangos Unit::SendHealSpellLog).</summary>
    public static byte[] BuildSpellHealLog(ObjectGuid target, ObjectGuid caster, uint spellId, uint amount, bool critical = false)
    {
        var writer = new PacketWriter(27);
        writer.WritePackedGuid(target.Value);
        writer.WritePackedGuid(caster.Value);
        writer.WriteUInt32(spellId);
        writer.WriteUInt32(amount);
        writer.WriteByte(critical ? (byte)1 : (byte)0);
        return writer.ToArray();
    }

    /// <summary>SMSG_SPELLENERGIZELOG: packed target, packed caster, u32 spell, u32 power type, u32 amount (vmangos Unit::SendEnergizeSpellLog).</summary>
    public static byte[] BuildSpellEnergizeLog(ObjectGuid target, ObjectGuid caster, uint spellId, uint powerType, uint amount)
    {
        var writer = new PacketWriter(30);
        writer.WritePackedGuid(target.Value);
        writer.WritePackedGuid(caster.Value);
        writer.WriteUInt32(spellId);
        writer.WriteUInt32(powerType);
        writer.WriteUInt32(amount);
        return writer.ToArray();
    }

    /// <summary>
    /// SMSG_PERIODICAURALOG (vmangos Unit::SendPeriodicAuraLog, 1.12): packed target, packed
    /// caster, u32 spell, u32 count (1), then u32 aura type and its payload: damage auras u32
    /// damage, u32 school, u32 absorbed, u32 resisted; heal auras u32 amount; energize / mana
    /// auras u32 power type, u32 amount.
    /// </summary>
    public static byte[] BuildPeriodicAuraLog(ObjectGuid target, ObjectGuid caster, uint spellId, PeriodicLogEntry entry)
    {
        var writer = new PacketWriter(46);
        writer.WritePackedGuid(target.Value);
        writer.WritePackedGuid(caster.Value);
        writer.WriteUInt32(spellId);
        writer.WriteUInt32(1);
        writer.WriteUInt32((uint)entry.AuraType);
        switch (entry.AuraType)
        {
            case AuraType.PeriodicDamage:
            case AuraType.PeriodicDamagePercent:
                writer.WriteUInt32(entry.Amount);
                writer.WriteUInt32(entry.SchoolOrPower);
                writer.WriteUInt32(entry.Absorbed);
                writer.WriteUInt32(entry.Resisted);
                break;
            case AuraType.PeriodicHeal:
            case AuraType.ObsModHealth:
                writer.WriteUInt32(entry.Amount);
                break;
            case AuraType.ObsModMana:
            case AuraType.PeriodicEnergize:
                writer.WriteUInt32(entry.SchoolOrPower);
                writer.WriteUInt32(entry.Amount);
                break;
        }

        return writer.ToArray();
    }

    /// <summary>
    /// MSG_MOVE_TELEPORT_ACK to the controller (vmangos MovementPacketSender::SendTeleportToController,
    /// build &gt; 1.9.4): packed GUID, u32 movement counter, movement info at the destination.
    /// </summary>
    public static byte[] BuildTeleportAck(ObjectGuid guid, uint counter, in MovementInfo movement)
    {
        var writer = new PacketWriter(48);
        writer.WritePackedGuid(guid.Value);
        writer.WriteUInt32(counter);
        movement.Write(writer);
        return writer.ToArray();
    }
}
