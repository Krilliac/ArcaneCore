using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures.AI;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// What a creature's <see cref="CreatureSpellListScheduler{TTarget}"/> asks of the world: combat state, spell readiness, target rules and the
/// cast itself. Re-expresses cmangos UnitAI::CanCastSpell and ChooseTarget (AI/BaseAI/UnitAI.cpp) and Unit::SelectAttackingTarget with
/// MeetsSelectAttackingRequirement (Entities/Unit.cpp). Map thread only, like the creature it belongs to.
/// </summary>
internal sealed class CreatureSpellListHost(CreatureMapSystem system, Creature me) : ICreatureSpellListHost<Unit>
{
    // cmangos SelectFlags (Entities/Unit.h). The row's Param3 carries them.
    private const uint SelectInLos = 0x1;
    private const uint SelectPlayer = 0x2;
    private const uint SelectPowerMana = 0x4;
    private const uint SelectPowerRage = 0x8;
    private const uint SelectPowerEnergy = 0x10;
    private const uint SelectInMeleeRange = 0x40;
    private const uint SelectNotInMeleeRange = 0x80;
    private const uint SelectHasAura = 0x100;
    private const uint SelectNotAura = 0x200;
    private const uint SelectRangeRange = 0x400;
    private const uint SelectRangeAoeRange = 0x800;
    private const uint SelectPowerNotMana = 0x1000;
    private const uint SelectUseEffectRadius = 0x2000;
    private const uint SelectSkipTank = 0x4000;
    private const uint SelectCasting = 0x8000;
    private const uint SelectNotImmune = 0x20000;
    private const uint SelectUseEffectRadiusOfTriggered = 0x40000;

    // cmangos AttackingTarget, the row's Param1.
    private const uint AttackRandom = 0;
    private const uint AttackTopAggro = 1;
    private const uint AttackBottomAggro = 2;
    private const uint AttackNearest = 3;
    private const uint AttackFarthest = 4;

    private readonly CreatureConditionEvaluator _conditions = new(system, me);

    private ICreatureSpellCaster? Spells => system.AiServices.Spells;

    public bool IsInCombat => me.IsAlive && me.Combat.IsInCombat;

    /// <summary>cmangos UnitAI::CanCastSpell for a list: alive, not evading, not retreating, no non-melee cast running, no combat script holding the creature.</summary>
    public bool CanCastSpell
        => me.IsAlive && !me.IsEvading && me.Motion.CurrentType != MovementGeneratorType.Fleeing
            && me.AI is not { CombatScriptRunning: true } && Spells is { } spells && !spells.IsCasting(me);

    public bool InRangedMode => me.AI is CreatureEventAI { CurrentRangedMode: true };

    public bool IsSpellReady(uint spellId) => Spells?.IsSpellReady(me, spellId) ?? false;

    public bool CanAutocast(uint spellId) => Spells?.FindSpell(spellId) is { } spell && !spell.HasAttribute(SpellAttributesEx.NoAutocastAi);

    public bool IsCombatConditionSatisfied(int combatCondition, uint spellId)
        => _conditions.MeetsCombat(combatCondition, Spells?.FindSpell(spellId)?.Range.Max ?? 0f);

    public bool TryChooseTarget(uint targetId, uint spellId, out Unit? target)
    {
        target = null;
        if (Spells?.FindSpell(spellId) is not { } spell)
        {
            return false;
        }

        CreatureSpellListTargeting? row = me.Content.Ai.SpellLists.FindTargeting(targetId);
        switch (row?.Type ?? CreatureSpellListTargetingType.Hardcoded)
        {
            case CreatureSpellListTargetingType.Attack:
                target = SelectAttackingTarget(row!, spell);
                bool found = target is not null;
                if ((row!.Param3 & (SelectUseEffectRadius | SelectUseEffectRadiusOfTriggered)) != 0)
                {
                    target = null; // these flags ask that a target exists; the cast itself goes out without one
                }

                return found;
            case CreatureSpellListTargetingType.Support:
                target = SelectSupportTarget(row!, spell);
                return target is not null;
            default:
                return TryChooseHardcoded(targetId, spell, out target);
        }
    }

    public bool TryCast(uint spellId, Unit? target)
    {
        CreatureCastResult result = system.CastSpell(me, spellId, target, triggered: false);
        (me.AI as CreatureEventAI)?.HandleMainSpellCastResult(spellId, result);
        return result == CreatureCastResult.Ok;
    }

    public void OnCast(CreatureSpellListEntry entry, Unit? target)
    {
        if (entry.ScriptId != 0)
        {
            system.StartRelayScript(entry.ScriptId, me, target);
        }
    }

    private bool TryChooseHardcoded(uint targetId, SpellInfo spell, out Unit? target)
    {
        target = null;
        switch (targetId)
        {
            case CreatureSpellListTargets.None:
                return true;
            case CreatureSpellListTargets.Current:
                target = me.Combat.Victim;
                return target is not null; // cmangos would cast at nothing here; with a null target the cast would land on the creature itself
            case CreatureSpellListTargets.SelfTarget:
                target = me;
                return true;
            case CreatureSpellListTargets.DispellableFriendly:
            case CreatureSpellListTargets.DispellableFriendlyNoSelf:
                target = PickRandom(FindFriendlies(spell, targetId == CreatureSpellListTargets.DispellableFriendly, f => NeedsDispel(f, spell)));
                return target is not null;
            case CreatureSpellListTargets.FriendlyMissingBuff:
            case CreatureSpellListTargets.FriendlyMissingBuffNoSelf:
                target = PickRandom(FindFriendlies(spell, targetId == CreatureSpellListTargets.FriendlyMissingBuff, f => !Spells!.HasAura(f, spell.Id)));
                return target is not null;
            case CreatureSpellListTargets.CurrentNotAlone:
                target = me.Combat.Victim;
                return target is not null && Threat.Count > 1;
            default:
                return false;
        }
    }

    private IReadOnlyList<ThreatEntry> Threat => me.Combat.HasThreatList ? me.Combat.Threat.Entries : [];

    /// <summary>The living, in-combat creatures in the spell's range this one can assist (cmangos FriendlyEligibleDispelInRangeCheck, FriendlyMissingBuffInRangeInCombatCheck).</summary>
    private List<Creature> FindFriendlies(SpellInfo spell, bool self, Func<Creature, bool> eligible)
    {
        List<Creature> found = [];
        foreach (Creature other in system.Creatures)
        {
            if (other.IsAlive && other.Combat.IsInCombat && (self || !ReferenceEquals(other, me)) && CanAssist(other)
                && WorldGeometry.DistanceMinusRadii(me, other) <= spell.Range.Max && eligible(other))
            {
                found.Add(other);
            }
        }

        return found;
    }

    private bool CanAssist(Creature other)
    {
        ICreatureHostility hostility = system.AiServices.Hostility;
        return ReferenceEquals(other, me) || hostility.CanAssist(other, me) || hostility.IsFriendly(me, other);
    }

    private Creature? PickRandom(List<Creature> list) => list.Count == 0 ? null : list[system.RandomInt(0, list.Count - 1)];

    /// <summary>
    /// cmangos FriendlyEligibleDispelInRangeCheck after its range checks: the friend is held, slowed or crowd controlled, and when the spell's
    /// dispel or dispel-mechanic effects name a type, an aura of that type is on it. A friend's dispel removes harmful auras, so only those count.
    /// </summary>
    private bool NeedsDispel(Creature friend, SpellInfo spell)
    {
        uint dispelMask = 0;
        uint mechanicMask = 0;
        foreach (SpellEffectInfo effect in spell.Effects)
        {
            if (effect.Effect == SpellEffectName.Dispel)
            {
                dispelMask |= DispelTypes.GetDispelMask((uint)effect.MiscValue);
            }
            else if (effect.Effect == SpellEffectName.DispelMechanic)
            {
                mechanicMask |= SpellMechanics.Mask((uint)effect.MiscValue);
            }
        }

        IReadOnlyList<SpellAuraHolder> auras = Spells!.GetAuras(friend);
        bool held = false;
        foreach (SpellAuraHolder holder in auras)
        {
            if (!holder.IsRemoved && (holder.HasAura(AuraType.ModRoot) || holder.HasAura(AuraType.ModStun) || holder.HasAura(AuraType.ModConfuse)
                || holder.HasAura(AuraType.ModFear) || (holder.HasAura(AuraType.ModDecreaseSpeed) && !holder.IsPositive)))
            {
                held = true;
                break;
            }
        }

        if (!held)
        {
            return false;
        }

        if (dispelMask == 0 && mechanicMask == 0)
        {
            return true;
        }

        foreach (SpellAuraHolder holder in auras)
        {
            if (holder.IsRemoved)
            {
                continue;
            }

            if ((dispelMask != 0 && !holder.IsPositive && (DispelTypes.GetDispelMask(holder.Spell.Dispel) & dispelMask) != 0)
                || (mechanicMask & holder.Spell.AllMechanicMask()) != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>cmangos Unit::SelectAttackingTarget with the row's type, position, flags and unit condition. Bottom aggro walks the list from its end, as its name says.</summary>
    private Unit? SelectAttackingTarget(CreatureSpellListTargeting row, SpellInfo spell)
    {
        IReadOnlyList<ThreatEntry> threat = Threat;
        int position = (int)Math.Min(row.Param2, int.MaxValue);
        if (threat.Count == 0 || position >= threat.Count)
        {
            return null;
        }

        switch (row.Param1)
        {
            case AttackRandom:
            {
                List<Unit> suitable = [];
                for (int i = position; i < threat.Count; i++)
                {
                    if (MeetsAttackRequirement(threat[i].Target, spell, row))
                    {
                        suitable.Add(threat[i].Target);
                    }
                }

                return suitable.Count == 0 ? null : suitable[system.RandomInt(0, suitable.Count - 1)];
            }
            case AttackTopAggro:
                for (int i = position; i < threat.Count; i++)
                {
                    if (MeetsAttackRequirement(threat[i].Target, spell, row))
                    {
                        return threat[i].Target;
                    }
                }

                return null;
            case AttackBottomAggro:
                for (int i = threat.Count - 1 - position; i >= 0; i--)
                {
                    if (MeetsAttackRequirement(threat[i].Target, spell, row))
                    {
                        return threat[i].Target;
                    }
                }

                return null;
            case AttackNearest:
            case AttackFarthest:
            {
                List<Unit> suitable = [];
                foreach (ThreatEntry entry in threat)
                {
                    if (MeetsAttackRequirement(entry.Target, spell, row))
                    {
                        suitable.Add(entry.Target);
                    }
                }

                suitable.Sort((a, b) => WorldGeometry.DistanceSq3D(me, a).CompareTo(WorldGeometry.DistanceSq3D(me, b)));
                if (row.Param1 == AttackFarthest)
                {
                    suitable.Reverse();
                }

                return position < suitable.Count ? suitable[position] : null;
            }
            default:
                return null;
        }
    }

    /// <summary>cmangos Unit::MeetsSelectAttackingRequirement with a spell, in the same order. A row has no range parameters, so the two range flags can never pass.</summary>
    private bool MeetsAttackRequirement(Unit target, SpellInfo spell, CreatureSpellListTargeting row)
    {
        uint flags = row.Param3;
        if (flags != 0)
        {
            if (((flags & SelectPlayer) != 0 && target is not Player)
                || ((flags & SelectPowerMana) != 0 && target.PowerType != PowerType.Mana)
                || ((flags & SelectPowerRage) != 0 && target.PowerType != PowerType.Rage)
                || ((flags & SelectPowerEnergy) != 0 && target.PowerType != PowerType.Energy)
                || ((flags & SelectInMeleeRange) != 0 && !MapCombat.CanReachWithMeleeAutoAttack(me, target))
                || ((flags & SelectNotInMeleeRange) != 0 && MapCombat.CanReachWithMeleeAutoAttack(me, target))
                || ((flags & SelectInLos) != 0 && !system.IsInLineOfSight(me, target))
                || !target.IsAlive
                || (flags & (SelectRangeRange | SelectRangeAoeRange)) != 0
                || ((flags & SelectPowerNotMana) != 0 && target.PowerType == PowerType.Mana)
                || ((flags & SelectSkipTank) != 0 && ReferenceEquals(target, me.Combat.Victim))
                || ((flags & SelectCasting) != 0 && !(system.AiServices.UnitSpells?.IsCasting(target) ?? false)))
            {
                return false;
            }
        }

        if ((target.UnitFlags & UnitFlags.NotSelectable) != 0
            || ((flags & SelectHasAura) != 0 && !Spells!.HasAura(target, spell.Id))
            || ((flags & SelectNotAura) != 0 && Spells!.HasAura(target, spell.Id))
            || ((flags & SelectNotImmune) != 0 && Spells!.IsImmuneToSpell(target, spell))
            || (spell.HasAttribute(SpellAttributesEx3Combat.OnlyOnPlayer) && target is not Player)
            || (!spell.HasAttribute(SpellAttributesEx2.IgnoreLineOfSight) && !system.IsInLineOfSight(me, target)))
        {
            return false;
        }

        // cmangos returns from the range test, so a row's unit condition only counts for spells without one: the rows that exist all say -1.
        if (spell.RangeIndex == SpellConstants.RangeIndexCombat)
        {
            return MapCombat.CanReachWithMeleeAutoAttack(me, target);
        }

        float distance = WorldGeometry.DistanceMinusRadii(me, target);
        if ((flags & (SelectUseEffectRadius | SelectUseEffectRadiusOfTriggered)) != 0)
        {
            SpellInfo? source = (flags & SelectUseEffectRadiusOfTriggered) != 0 ? Spells!.FindSpell(spell.Effects[0].TriggerSpell) : spell;
            return source is not null && distance < source.Effects[0].Radius;
        }

        return distance < spell.Range.Max && distance >= spell.Range.Min;
    }

    /// <summary>
    /// cmangos ChooseTarget for a support row: with a unit condition the friend that meets it (DoSelectConditionalFriendly), otherwise the friend missing the
    /// most health, more than Param1 points or percent (Param2) with Param3 allowing the creature itself (DoSelectLowestHpFriendly).
    /// </summary>
    private Unit? SelectSupportTarget(CreatureSpellListTargeting row, SpellInfo spell)
    {
        float range = spell.Range.Max;
        Creature? best = null;
        float bestMissing = row.Param1;
        foreach (Creature other in system.Creatures)
        {
            if (!other.IsAlive || !CanAssist(other) || WorldGeometry.DistanceMinusRadii(me, other) > range)
            {
                continue;
            }

            if (row.UnitCondition != -1)
            {
                if (_conditions.MeetsUnit(other, me, row.UnitCondition))
                {
                    best = other;
                }

                continue;
            }

            if (!other.Combat.IsInCombat || (row.Param3 == 0 && ReferenceEquals(other, me)))
            {
                continue;
            }

            uint health = Math.Min(other.Health, other.MaxHealth);
            float missing = row.Param2 != 0
                ? (other.MaxHealth == 0 ? 0f : 100f - (health * 100f / other.MaxHealth))
                : other.MaxHealth - health;
            if (missing > bestMissing)
            {
                best = other;
                bestMissing = missing;
            }
        }

        return best;
    }
}
