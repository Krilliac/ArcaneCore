using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells.Procs;

/// <summary>
/// The aura types with a proc handler of their own (vmangos <c>AuraProcHandler[]</c>, UnitAuraProcHandler.cpp:37-231). Every other aura type
/// procs as <c>HandleNULLProc</c> (OK: the proc counts and spends a charge). Class-specific cases of the vmangos handlers (seals, Lightning
/// Shield, Pyroclasm, the dummy auras) belong to per-spell <see cref="IProcScript"/>s, not to these handlers.
/// </summary>
internal static class BuiltInProcHandlers
{
    public static Dictionary<AuraType, AuraProcHandler> Create() => new()
    {
        [AuraType.Dummy] = static (in AuraProcContext _) => AuraProcResult.Ok, // HandleDummyAuraProc: "processed charge only counting case" without a script
        [AuraType.ModFear] = RemoveFearByDamageChance,
        [AuraType.ModDamageDone] = ModDamageDone,
        [AuraType.ModInvisibility] = Invisibility,
        [AuraType.ModResistance] = ModResistance,
        [AuraType.ModRoot] = RemoveByDamageChance,
        [AuraType.ProcTriggerSpell] = ProcTriggerSpell,
        [AuraType.ProcTriggerDamage] = ProcTriggerDamage,
        [AuraType.ModPacifySilence] = RemoveByDamageChance,
        [AuraType.ModCastingSpeedNotStack] = ModCastingSpeedNotStack,
        [AuraType.ModPowerCostSchoolPct] = ModPowerCostSchool,
        [AuraType.ModPowerCostSchool] = ModPowerCostSchool,
        [AuraType.ReflectSpellsSchool] = ReflectSpellsSchool,
        [AuraType.MechanicImmunity] = MechanicImmuneResistance,
        [AuraType.ModPowerRegen] = static (in AuraProcContext _) => AuraProcResult.CantTrigger,
        [AuraType.AddTargetTrigger] = AddTargetTrigger,
        [AuraType.OverrideClassScripts] = OverrideClassScripts,
        [AuraType.ModMechanicResistance] = MechanicImmuneResistance,
        [AuraType.ModMeleeHaste] = Haste,
        [AuraType.ResistPushback] = static (in AuraProcContext _) => AuraProcResult.CantTrigger,
    };

    /// <summary>
    /// vmangos <c>Unit::HandleProcTriggerSpellAuraProc</c> (UnitAuraProcHandler.cpp:1147-1624), generic part: the effect's EffectTriggerSpell (with
    /// the item-proc remaps of the generic family), never an extra-attack spell while extra attacks are pending (except 20178), the custom target
    /// cases, then "positive spell on the owner, anything else on the victim" and <see cref="SpellSystem.TriggerProccedSpell"/>.
    /// </summary>
    private static AuraProcResult ProcTriggerSpell(in AuraProcContext c)
    {
        SpellSystem system = c.System;
        SpellInfo aura = c.Holder.Spell;
        uint triggerSpellId = c.Aura.EffectIndex < aura.Effects.Count ? aura.Effects[c.Aura.EffectIndex].TriggerSpell : 0;
        Unit? target = null;
        int? basePoints0 = null;
        switch (aura.Id)
        {
            case 23780: // Aegis of Preservation
                triggerSpellId = 23781;
                break;
            case 27522: // Mana Drain Trigger: gain 29471 and drain 27526 from the target.
                if (c.Owner.IsAlive && system.Store.Get(29471) is { } gain)
                {
                    system.CastProcSpell(c.Owner, gain, SpellCastTargets.ForUnit(c.Owner.Guid), aura);
                }

                if (c.Target is { IsAlive: true } drained && system.Store.Get(27526) is { } drain)
                {
                    system.CastProcSpell(c.Owner, drain, SpellCastTargets.ForUnit(drained.Guid), aura);
                }

                return AuraProcResult.Ok;
            case 28200: // Talisman of Ascendance: not from area or script-effect spells.
                if (c.ProcSpell is { } ascendance && (ascendance.IsAreaEffect() || (ascendance.Effects.Count > 0 && ascendance.Effects[0].Effect == SpellEffectName.ScriptEffect)))
                {
                    return AuraProcResult.Failed;
                }

                break;
            case 26467: // Persistent Shield (Scarab Brooch): 15% of the heal as a shield on the healed unit.
                basePoints0 = (int)(c.Amount * 15 / 100);
                target = c.Target;
                triggerSpellId = 26470;
                break;
        }

        if (system.Store.Get(triggerSpellId) is not { } trigger)
        {
            return AuraProcResult.Failed;
        }

        // "not allow proc extra attack spell at extra attack" (Windfury and the extra-attack weapons, except 20178 Reckoning).
        if (c.Owner.Combat.HasPendingExtraAttacks && trigger.HasEffect(SpellEffectName.AddExtraAttacks) && trigger.Id != 20178)
        {
            return AuraProcResult.Failed;
        }

        switch (trigger.Id)
        {
            case 7099:  // Curse of Mending: a positive spell on the enemy
            case 20233: // Improved Lay on Hands (cast on target)
                target = c.Target;
                break;
            case 15250: // Rogue Setup: only the main target
                if (c.Target is null || !ReferenceEquals(c.Target, c.Owner.Combat.Victim))
                {
                    return AuraProcResult.Failed;
                }

                break;
        }

        // "try detect target manually if not set"
        target ??= (c.ProcFlag & ProcFlags.DealHelpfulSpell) == 0 && trigger.IsPositive ? c.Owner : c.Target;
        return system.TriggerProccedSpell(c.Owner, target, trigger, c.Holder, c.CooldownMs, basePoints0, procSpell: c.ProcSpell);
    }

    /// <summary>
    /// vmangos <c>Unit::HandleProcTriggerDamageAuraProc</c> (UnitAuraProcHandler.cpp:1626-1676): a living victim; the aura spell's hit roll (a miss is
    /// reported with SMSG_PROCRESIST and still counts); otherwise the effect's value, through the spell damage bonuses, as non-critical spell damage.
    /// </summary>
    private static AuraProcResult ProcTriggerDamage(in AuraProcContext c)
    {
        if (c.Target is not { IsAlive: true } victim)
        {
            return AuraProcResult.Failed;
        }

        SpellSystem system = c.System;
        SpellInfo spell = c.Holder.Spell;
        if (system.RollHitWithoutReflect(c.Owner, victim, spell) != SpellMissInfo.None)
        {
            SpellSystem.SendToSet(c.Owner, WorldOpcode.SmsgProcresist, ProcPackets.BuildProcResist(c.Owner.Guid, victim.Guid, spell.Id), includeSelf: true);
            return AuraProcResult.Ok;
        }

        float damage = spell.CalculateEffectValue(c.Aura.EffectIndex, c.Owner.Level, system.Random);
        if (system.AmountModifier is { } bonus)
        {
            damage = bonus.Modify(SpellAmountStage.DirectDamage, c.Owner, victim, spell, c.Aura.EffectIndex, damage, 1);
        }

        uint amount = (uint)Math.Floor(Math.Max(damage, 0f) + system.Random.NextSingle()); // rand_ditheru
        if (amount > 0)
        {
            system.DealDirectDamage(c.Owner, victim, spell, amount, allowCrit: false);
        }

        return AuraProcResult.Ok;
    }

    /// <summary>
    /// vmangos <c>Unit::HandleAddTargetTriggerAuraProc</c> (UnitAuraProcHandler.cpp:1799-1843): the chance is effect 0's base points (Blizzard
    /// divides it by its 8 ticks); the trigger spell is cast by the victim on itself, or by the owner on itself for Wolfshead Helm, Frosty Zap and
    /// Relentless Strikes; never on the owner itself (Cone of Cold).
    /// </summary>
    private static AuraProcResult AddTargetTrigger(in AuraProcContext c)
    {
        SpellSystem system = c.System;
        SpellInfo aura = c.Holder.Spell;
        if (aura.Effects.Count == 0)
        {
            return AuraProcResult.Failed;
        }

        float chance = aura.Effects[0].BasePoints != -1 ? aura.Effects[0].BasePoints : 0f;
        if (aura.IsFitToFamily(3, 7)) // SPELLFAMILY_MAGE, CF_MAGE_BLIZZARD
        {
            chance /= 8;
        }

        if (system.Random.NextDouble() * 100d >= chance)
        {
            return AuraProcResult.Failed;
        }

        uint triggerSpellId = aura.Effects[0].TriggerSpell;
        if (triggerSpellId == 0 || system.Store.Get(triggerSpellId) is not { } trigger)
        {
            return AuraProcResult.Ok;
        }

        if (aura.Id is 17768 or 24392 or 14179)
        {
            system.CastProcSpell(c.Owner, trigger, SpellCastTargets.ForUnit(c.Owner.Guid), aura);
            return AuraProcResult.Ok;
        }

        if (c.Target is not { } victim || victim.Guid == c.Owner.Guid)
        {
            return AuraProcResult.Failed;
        }

        system.CastProcSpell(victim, trigger, SpellCastTargets.ForUnit(victim.Guid), aura);
        return AuraProcResult.Ok;
    }

    /// <summary>vmangos <c>HandleReflectSpellsSchoolAuraProc</c> (:1778-1782): a spell of a school the aura reflects.</summary>
    private static AuraProcResult ReflectSpellsSchool(in AuraProcContext c)
        => c.ProcSpell is { } spell && ((uint)c.Aura.MiscValue & spell.SchoolMask()) != 0 ? AuraProcResult.Ok : AuraProcResult.Failed;

    /// <summary>vmangos <c>HandleModPowerCostSchoolAuraProc</c> (:1784-1790): a spell with a cost, of a school the aura names.</summary>
    private static AuraProcResult ModPowerCostSchool(in AuraProcContext c)
        => c.ProcSpell is { } spell && (spell.ManaCost != 0 || spell.ManaCostPercentage != 0) && ((uint)c.Aura.MiscValue & spell.SchoolMask()) != 0
            ? AuraProcResult.Ok
            : AuraProcResult.Failed;

    /// <summary>vmangos <c>HandleMechanicImmuneResistanceAuraProc</c> (:1792-1797): a spell of the aura's mechanic.</summary>
    private static AuraProcResult MechanicImmuneResistance(in AuraProcContext c)
        => c.ProcSpell is { } spell && spell.Mechanic == (uint)c.Aura.MiscValue ? AuraProcResult.Ok : AuraProcResult.Failed;

    /// <summary>vmangos <c>HandleModCastingSpeedNotStackAuraProc</c> (:1772-1776): skip melee hits and instant casts.</summary>
    private static AuraProcResult ModCastingSpeedNotStack(in AuraProcContext c)
        => c.ProcSpell is { } spell && spell.CastTime.Base > 0 ? AuraProcResult.Ok : AuraProcResult.Failed;

    /// <summary>vmangos <c>HandleModDamageAuraProc</c> (:1860-1924): the aura's school mask must match the spell's (a swing is physical).</summary>
    private static AuraProcResult ModDamageDone(in AuraProcContext c)
    {
        uint school = c.ProcSpell?.SchoolMask() ?? ProcFlagRules.MeleeSchoolMask;
        return (school & (uint)c.Aura.MiscValue) != 0 ? AuraProcResult.Ok : AuraProcResult.Failed;
    }

    /// <summary>vmangos <c>HandleModResistanceAuraProc</c> (:1845-1858): Inner Fire (CF_PRIEST_INNER_FIRE) loses charges only on real damage.</summary>
    private static AuraProcResult ModResistance(in AuraProcContext c)
        => c.Holder.Spell.IsFitToFamily(6, 1) && c.Amount == 0 ? AuraProcResult.Failed : AuraProcResult.Ok;

    /// <summary>vmangos <c>HandleHasteAuraProc</c> (:538-548): Flurry's last charge is kept by a crit, which re-applies the buff.</summary>
    private static AuraProcResult Haste(in AuraProcContext c)
        => c.Holder.Spell.SpellIconId == 108 && c.Holder.Spell.SpellVisual == 2759 && c.Holder.Charges <= 1 && (c.ProcExtra & ProcFlagsEx.CriticalHit) != 0
            ? AuraProcResult.Failed
            : AuraProcResult.Ok;

    /// <summary>vmangos <c>HandleInvisibilityAuraProc</c> (:2010-2017): a positive, non-passive invisibility ends.</summary>
    private static AuraProcResult Invisibility(in AuraProcContext c)
    {
        if (c.Holder.Spell.IsPassive || !c.Holder.Spell.IsPositive)
        {
            return AuraProcResult.Failed;
        }

        c.System.RemoveAuras(c.Owner, c.Holder.Spell.Id);
        return AuraProcResult.Ok;
    }

    /// <summary>
    /// vmangos <c>HandleRemoveByDamageChanceProc</c> (:1926-1940), roots and pacify-silence: the chance to break is the damage taken over
    /// 25 x level - 150 (50 below level 9) of the owner; a break removes the caster's auras of that spell.
    /// </summary>
    private static AuraProcResult RemoveByDamageChance(in AuraProcContext c)
    {
        uint level = c.Owner.Level;
        uint maxDamage = level > 8 ? (25 * level) - 150 : 50;
        float chance = c.Amount / (float)maxDamage * 100f;
        if (c.System.Random.NextDouble() * 100d >= chance)
        {
            return AuraProcResult.Failed;
        }

        c.System.RemoveAurasByCaster(c.Owner, c.Holder.Spell.Id, c.Holder.CasterGuid);
        return AuraProcResult.Ok;
    }

    /// <summary>
    /// vmangos <c>HandleRemoveFearByDamageChanceProc</c> (:1942-2008, build 5875 branches): fear and turn effects; players are three times as easy to
    /// break, damage over time three times as hard; the chance is the final damage (1.11 rule) over the same level scale.
    /// </summary>
    private static AuraProcResult RemoveFearByDamageChance(in AuraProcContext c)
    {
        if (c.OriginalAmount == 0 || c.Holder.Spell.Mechanic is not ((uint)SpellMechanic.Fear or (uint)SpellMechanic.Turn))
        {
            return AuraProcResult.Failed;
        }

        uint level = c.Owner.Level;
        float maxDamage = level > 8 ? (25 * level) - 150 : 50;
        if (c.Owner is Player)
        {
            maxDamage *= 0.333f;
        }

        if ((c.ProcFlag & ProcFlags.TakeHarmfulPeriodic) != 0)
        {
            maxDamage *= 3;
        }

        float chance = c.OriginalAmount / maxDamage * 100f;
        if (c.System.Random.NextDouble() * 100d >= chance)
        {
            return AuraProcResult.Failed;
        }

        c.System.RemoveAurasByCaster(c.Owner, c.Holder.Spell.Id, c.Holder.CasterGuid);
        return AuraProcResult.Ok;
    }

    /// <summary>
    /// vmangos <c>HandleOverrideClassScriptAuraProc</c> (:1678-1770), the class-independent script ids: Nightfall (4309 → Shadow Trance 17941),
    /// Improved Blizzard (836/988/989 from Blizzard, SpellVisual 259), Improved Mend Pet (4086/4087, chance = amount) and Corrupted Healing (3656, direct
    /// heals). Any other script id counts the charge only (the class-scripts lane adds the set-bonus ids through <see cref="IProcScript"/>s).
    /// </summary>
    private static AuraProcResult OverrideClassScripts(in AuraProcContext c)
    {
        if (c.Target is not { IsAlive: true } victim)
        {
            return AuraProcResult.Failed;
        }

        uint triggerSpellId = 0;
        switch (c.Aura.MiscValue)
        {
            case 4309:
                triggerSpellId = 17941;
                break;
            case 836:
            case 988:
            case 989:
                if (c.ProcSpell is not { SpellVisual: 259 })
                {
                    return AuraProcResult.Failed;
                }

                triggerSpellId = c.Aura.MiscValue switch { 836 => 12484u, 988 => 12485u, _ => 12486u };
                break;
            case 4086:
            case 4087:
                if (c.System.Random.NextDouble() * 100d >= c.Aura.Amount)
                {
                    return AuraProcResult.Failed;
                }

                triggerSpellId = 24406;
                break;
            case 3656:
                if (c.ProcSpell?.HasEffect(SpellEffectName.Heal) == true)
                {
                    triggerSpellId = 23402;
                }

                break;
        }

        if (triggerSpellId == 0)
        {
            return AuraProcResult.Ok;
        }

        return c.System.Store.Get(triggerSpellId) is { } trigger
            ? c.System.TriggerProccedSpell(c.Owner, victim, trigger, c.Holder, c.CooldownMs)
            : AuraProcResult.Failed;
    }
}

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
