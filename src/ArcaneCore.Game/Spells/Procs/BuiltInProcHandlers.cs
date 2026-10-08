using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells.Procs;

/// <summary>
/// The aura types with a proc handler of their own (vmangos <c>AuraProcHandler[]</c>, UnitAuraProcHandler.cpp:37-231). Every other aura type
/// procs as <c>HandleNULLProc</c> (OK: the proc counts and spends a charge). Class-specific cases of the vmangos handlers (seals, Lightning
/// Shield, the dummy-aura talents) belong to per-spell <see cref="IProcScript"/>s; the PROC_TRIGGER_SPELL talent cases that only rewrite the
/// trigger spell, its chance or its base points (Pyroclasm, Shadowguard, Blessed Recovery, Illumination, the combo-point deferral of
/// Ruthlessness and Seal Fate) stay in <see cref="ProcTriggerSpell"/>, as in vmangos.
/// </summary>
internal static class BuiltInProcHandlers
{
    private const uint FamilyWarlock = 5;
    private const uint FamilyPriest = 6;
    private const uint FamilyPaladin = 10;

    private const uint PyroclasmIcon = 1137;
    private const uint PyroclasmStunSpell = 18093;
    private const uint ShadowguardIcon = 19;
    private const uint BlessedRecoveryIcon = 1875;
    private const uint IlluminationIcon = 241;
    private const uint IlluminationManaSpell = 20272;
    private const uint RuthlessnessPointSpell = 14157;
    private const uint SealFatePointSpell = 14189;

    /// <summary>CF_PALADIN_HOLY_SHOCK (SpellClassMask.h:296), CF_WARLOCK_RAIN_OF_FIRE and CF_WARLOCK_HELLFIRE (:100-101).</summary>
    private const ulong PaladinHolyShockFlag = 1UL << 21;
    private const ulong WarlockRainOfFireFlag = 1UL << 5;
    private const ulong WarlockHellfireFlag = 1UL << 6;

    /// <summary>Shadowguard rank → its damage spell (UnitAuraProcHandler.cpp:1286-1306).</summary>
    private static readonly Dictionary<uint, uint> ShadowguardSpells = new()
    {
        [18137] = 28377,
        [19308] = 28378,
        [19309] = 28379,
        [19310] = 28380,
        [19311] = 28381,
        [19312] = 28382,
    };

    /// <summary>Blessed Recovery rank → its heal (UnitAuraProcHandler.cpp:1313-1324).</summary>
    private static readonly Dictionary<uint, uint> BlessedRecoverySpells = new()
    {
        [27811] = 27813,
        [27815] = 27817,
        [27816] = 27818,
    };

    /// <summary>Holy Shock's triggered heal → the Holy Shock rank that was cast (UnitAuraProcHandler.cpp:1484-1490).</summary>
    private static readonly Dictionary<uint, uint> HolyShockCastSpells = new()
    {
        [25914] = 20473,
        [25913] = 20929,
        [25903] = 20930,
    };

    /// <summary>
    /// The OVERRIDE_CLASS_SCRIPTS misc values <see cref="OverrideClassScripts"/> acts on (Nightfall, Improved Blizzard, Improved Mend Pet,
    /// Corrupted Healing). The talent coverage report reads it: an aura 112 talent whose script number nothing reads is not handled.
    /// </summary>
    internal static IReadOnlySet<int> HandledClassScripts { get; } = new HashSet<int> { 4309, 836, 988, 989, 4086, 4087, 3656 };

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

        // The talent cases of the family switch (UnitAuraProcHandler.cpp:1226-1330, 1468-1500).
        switch (aura.SpellFamilyName)
        {
            case FamilyWarlock when aura.SpellIconId == PyroclasmIcon:
                if (Pyroclasm(c) is not { } pyroclasm)
                {
                    return AuraProcResult.Failed;
                }

                triggerSpellId = pyroclasm;
                break;
            case FamilyPriest when aura.SpellIconId == ShadowguardIcon:
                if (!ShadowguardSpells.TryGetValue(aura.Id, out triggerSpellId))
                {
                    return AuraProcResult.Failed; // "Spell %u not handled in SG"
                }

                break;
            case FamilyPriest when aura.SpellIconId == BlessedRecoveryIcon:
                if (!BlessedRecoverySpells.TryGetValue(aura.Id, out triggerSpellId))
                {
                    return AuraProcResult.Failed; // "Spell %u not handled in BR"
                }

                basePoints0 = NonZero(Dither(c.Amount * (float)c.Aura.Amount / 100f / 3f, system.Random));
                target = c.Owner;
                break;
            case FamilyPaladin when aura.SpellIconId == IlluminationIcon:
                if (c.ProcSpell is not { } healed || c.Owner is not Player)
                {
                    return AuraProcResult.Failed;
                }

                // "procspell is triggered spell but we need mana cost of original casted spell": Holy Shock's heal is triggered by the cast spell.
                SpellInfo? original = healed;
                if ((healed.SpellFamilyFlags & PaladinHolyShockFlag) != 0)
                {
                    original = HolyShockCastSpells.TryGetValue(healed.Id, out uint castId) ? system.Store.Get(castId) : null;
                    if (original is null)
                    {
                        return AuraProcResult.Failed; // "Spell %u not handled in HShock"
                    }
                }

                basePoints0 = NonZero((int)original.ManaCost);
                triggerSpellId = IlluminationManaSpell;
                target = c.Owner;
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
            case SealFatePointSpell:     // Seal Fate (and the Netherblade set)
            case RuthlessnessPointSpell: // Ruthlessness
                // "Need add combopoint AFTER finishing move (or they get dropped in finish phase)": the point is cast on the running spell's unit
                // target once that spell has finished (vmangos: a lambda event after the batching interval, without CONFIG_UINT32_SPELL_PROC_DELAY).
                if (system.PostFinishProcsEnabled)
                {
                    if (system.CurrentGenericCast(c.Owner) is not { } running)
                    {
                        return AuraProcResult.Failed;
                    }

                    ObjectGuid pointTarget = running.Targets.Unit;
                    if (!pointTarget.IsEmpty)
                    {
                        Unit owner = c.Owner;
                        uint pointSpell = trigger.Id;
                        system.DeferUntilFinished(running, () =>
                        {
                            if (owner.IsInWorld && owner.IsAlive)
                            {
                                system.CastSpell(owner, pointSpell, SpellCastTargets.ForUnit(pointTarget), triggered: true);
                            }
                        });
                    }

                    return AuraProcResult.Ok;
                }

                break;
        }

        // "try detect target manually if not set"
        target ??= (c.ProcFlag & ProcFlags.DealHelpfulSpell) == 0 && trigger.IsPositive ? c.Owner : c.Target;
        return system.TriggerProccedSpell(c.Owner, target, trigger, c.Holder, c.CooldownMs, basePoints0, procSpell: c.ProcSpell);
    }

    /// <summary>
    /// Pyroclasm (UnitAuraProcHandler.cpp:1226-1262): a living other victim and a spell of known tick count (Soul Fire's icon 184 / visual 2253:
    /// 1, Hellfire: 15, Rain of Fire: 4), then the rank's chance (18096: 13, 18073: 26) over the ticks; the stun 18093, or null when it fails.
    /// </summary>
    private static uint? Pyroclasm(in AuraProcContext c)
    {
        if (c.Target is not { IsAlive: true } victim || ReferenceEquals(victim, c.Owner) || c.ProcSpell is not { } spell)
        {
            return null;
        }

        int ticks;
        if (spell.SpellIconId == 184 && spell.SpellVisual == 2253)
        {
            ticks = 1;
        }
        else if ((spell.SpellFamilyFlags & WarlockHellfireFlag) != 0)
        {
            ticks = 15;
        }
        else if ((spell.SpellFamilyFlags & WarlockRainOfFireFlag) != 0)
        {
            ticks = 4;
        }
        else
        {
            return null;
        }

        float chance = c.Holder.Spell.Id switch
        {
            18096 => 13.0f / ticks,
            18073 => 26.0f / ticks,
            _ => 0f,
        };
        return c.System.Random.NextDouble() * 100d < chance ? PyroclasmStunSpell : null; // roll_chance_f
    }

    /// <summary>vmangos <c>rand_dither</c> (Utilities/Random.cpp:80-83).</summary>
    private static int Dither(float value, Random random) => (int)MathF.CopySign(MathF.Floor(MathF.Abs(value) + random.NextSingle()), value);

    /// <summary>A base point of 0 is none: vmangos TriggerProccedSpell casts the plain spell when every base point is 0.</summary>
    private static int? NonZero(int value) => value != 0 ? value : null;

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
