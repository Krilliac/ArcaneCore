using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Procs;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Spells.Rules.Immunity;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// The combat side of the proc engine: white swings (vmangos Unit::AttackerStateUpdate, Unit.cpp:2230-2290, and Unit::DealMeleeDamage,
/// Unit.cpp:1708-1783), damage shields (Unit::TriggerDamageShields, Unit.cpp:1785-1849), periodic ticks (Aura::PeriodicTick) and kills
/// (Unit::Kill, Unit.cpp:1102-1104). Map combat calls <see cref="OnMeleeSwingResolved"/> after the hit table; the world's item proc feature
/// subscribes <see cref="OnMeleeWeaponHit"/> to the dealt hit.
/// </summary>
public sealed partial class SpellSystem
{
    /// <summary>
    /// A white swing's hit-table result is known (vmangos Unit.cpp:2263: <c>ProcDamageAndSpell</c> right after CalculateMeleeDamage, before the
    /// packet and the damage, so a killing blow still procs the victim's auras).
    /// </summary>
    public void OnMeleeSwingResolved(MeleeDamageInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        ProcDamageAndSpell(info.Attacker, ProcFlagRules.ForMeleeSwing(info));
    }

    /// <summary>
    /// The end of vmangos Unit::DealMeleeDamage (Unit.cpp:1774-1782): a swing that affected its victim and was not parried or dodged ("Thorns does
    /// not trigger on Immune, Absorb and Dodge") casts the attacker's weapon chance-on-hit spells (<see cref="HandleItemCombatProc"/>) and then
    /// triggers the victim's damage shields.
    /// </summary>
    public void OnMeleeWeaponHit(MeleeDamageInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if ((info.HitInfo & HitInfo.AffectsVictim) == 0 || info.TargetState is VictimState.Parry or VictimState.Dodge)
        {
            return;
        }

        HandleItemCombatProc(info);
        TriggerDamageShields(info.Attacker, info.Target);
    }

    /// <summary>
    /// vmangos Unit::TriggerDamageShields (Unit.cpp:1785-1849): each DAMAGE_SHIELD aura of <paramref name="victim"/> rolls its spell's hit against the
    /// attacker (a miss is logged with SMSG_SPELLLOGMISS) and its school immunity (SMSG_SPELLORDAMAGE_IMMUNE), then deals its amount, through the
    /// victim's spell damage bonus and, for a creature victim, the attacker's damage taken bonus, as spell damage with SMSG_SPELLDAMAGESHIELD. The
    /// damage does not start combat (vmangos ShouldEnterCombat: a DAMAGE_SHIELD spell never does).
    /// </summary>
    public void TriggerDamageShields(Unit attacker, Unit victim)
    {
        ArgumentNullException.ThrowIfNull(attacker);
        ArgumentNullException.ThrowIfNull(victim);
        if (ReferenceEquals(attacker, victim) || IsQuestSettlementPending(attacker) || IsQuestSettlementPending(victim))
        {
            return;
        }

        var done = new HashSet<SpellAura>(ReferenceEqualityComparer.Instance);
        while (attacker.IsAlive && victim.IsInWorld
            && GetAuras(victim).Where(h => !h.IsRemoved).SelectMany(h => h.Auras.OfType<SpellAura>().Where(a => a.Type == AuraType.DamageShield && !done.Contains(a)).Select(a => (h, a)))
                .FirstOrDefault() is ({ } holder, { } aura))
        {
            done.Add(aura);
            SpellInfo spell = holder.Spell;
            SpellMissInfo miss = RollHitWithoutReflect(victim, attacker, spell);
            if (miss != SpellMissInfo.None)
            {
                SendToSet(victim, WorldOpcode.SmsgSpelllogmiss, SpellPackets.BuildSpellLogMiss(spell.Id, victim.Guid, attacker.Guid, miss), includeSelf: true);
                continue;
            }

            if (ImmunityRules.IsImmuneToDamage(this, attacker, spell.SchoolMask(), spell))
            {
                SendToSet(victim, WorldOpcode.SmsgSpellordamageImmune, SpellRulePackets.BuildSpellOrDamageImmune(victim.Guid, attacker.Guid, spell.Id), includeSelf: true);
                continue;
            }

            float amount = aura.Amount;
            if (AmountModifier is { } bonus)
            {
                amount = bonus.Modify(SpellAmountStage.DirectDamage, victim, attacker, spell, aura.EffectIndex, amount, holder.StackAmount);
            }

            uint damage = (uint)Math.Floor(Math.Max(amount, 0f) + Random.NextSingle()); // rand_ditheru
            SendToSet(victim, WorldOpcode.SmsgSpelldamageshield, ProcPackets.BuildSpellDamageShield(victim.Guid, attacker.Guid, damage, (uint)spell.School), includeSelf: true);
            if (damage > 0)
            {
                uint dealt = Damage.DealSpellDamage(victim, attacker, spell, damage, periodic: false, startsCombat: false);
                OnDamageTaken(attacker, victim, dealt, periodic: false, sourceSpellId: spell.Id);
            }
        }
    }

    /// <summary>
    /// A periodic damage tick's procs (vmangos Aura::PeriodicTick, SpellAuras.cpp:5902-5917): DEAL/TAKE_HARMFUL_PERIODIC with TAKEN_ANY_DAMAGE when
    /// damage got through, a normal hit, before the damage is dealt.
    /// </summary>
    internal void FirePeriodicDamageProcs(Unit caster, Unit target, SpellInfo spell, uint amount, uint originalAmount)
    {
        ProcFlags victim = ProcFlags.TakeHarmfulPeriodic;
        if (amount > 0)
        {
            victim |= ProcFlags.TakenAnyDamage;
        }

        ProcDamageAndSpell(caster, new ProcEvent
        {
            Victim = target,
            AttackerFlags = ProcFlags.DealHarmfulPeriodic,
            VictimFlags = victim,
            Extra = ProcFlagsEx.NormalHit,
            Amount = amount,
            OriginalAmount = originalAmount,
            ProcSpell = spell,
        });
    }

    /// <summary>
    /// A periodic heal tick's procs (vmangos Aura::PeriodicTick, SpellAuras.cpp:6060-6085): the same flags with PROC_EX_PERIODIC_POSITIVE, the
    /// amount being the health gained (1 when the target was already full, so the tick still counts).
    /// </summary>
    internal void FirePeriodicHealProcs(Unit caster, Unit target, SpellInfo spell, uint gain, uint amount)
    {
        ProcDamageAndSpell(caster, new ProcEvent
        {
            Victim = target,
            AttackerFlags = ProcFlags.DealHarmfulPeriodic,
            VictimFlags = ProcFlags.TakeHarmfulPeriodic,
            Extra = ProcFlagsEx.NormalHit | ProcFlagsEx.PeriodicPositive,
            Amount = gain,
            OriginalAmount = amount,
            ProcSpell = spell,
        });
    }

    /// <summary>
    /// The killer's KILL procs (vmangos Unit::Kill, Unit.cpp:1102-1104: "The one who has the fatal blow"; the victim side is HEARTBEAT and the victim
    /// is dead, so it never procs). Map combat raises the kill; the world's proc feature forwards it here.
    /// </summary>
    public void OnUnitKilled(Unit? killer, Unit victim)
    {
        ArgumentNullException.ThrowIfNull(victim);
        if (killer is null || ReferenceEquals(killer, victim) || !killer.IsAlive)
        {
            return;
        }

        ProcDamageAndSpell(killer, new ProcEvent
        {
            Victim = victim,
            AttackerFlags = ProcFlags.Kill,
            VictimFlags = ProcFlags.Heartbeat,
        });
    }

    /// <summary>vmangos SpellEntry::CanTriggerWeaponProcs (SpellEntry.cpp:1123-1132): a weapon ability of melee range.</summary>
    internal static bool CanTriggerWeaponProcs(SpellInfo spell) => spell.EquippedItemClass == 2 && spell.RangeIndex == SpellConstants.RangeIndexCombat;
}
