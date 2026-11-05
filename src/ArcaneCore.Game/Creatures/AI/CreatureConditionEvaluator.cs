using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// The <c>combat_condition</c> and <c>unit_condition</c> rows a spell list names (cmangos CombatConditionMgr::Meets and UnitConditionMgr::Meets,
/// Globals/CombatCondition.cpp, Globals/UnitCondition.cpp).
/// <para>
/// Fails closed, once reported per map, on what this server does not have: world state expressions (the table is not imported) and the unit
/// variables listed in <c>docs/design/creature-spell-lists.md</c>. Variables the reference itself leaves unimplemented read 0, as there.
/// </para>
/// </summary>
internal sealed class CreatureConditionEvaluator(CreatureMapSystem system, Creature me)
{
    private const uint Race = 1;
    private const uint Class = 2;
    private const uint Level = 3;
    private const uint IsSelf = 4;
    private const uint IsMyPet = 5;
    private const uint IsMaster = 6;
    private const uint IsTarget = 7;
    private const uint CanAssist = 8;
    private const uint CanAttack = 9;
    private const uint HealthPercent = 12;
    private const uint ManaPercent = 13;
    private const uint RagePercent = 14;
    private const uint EnergyPercent = 15;
    private const uint HasHelpfulAuraSpell = 17;
    private const uint HasHelpfulAuraDispelType = 18;
    private const uint HasHelpfulAuraMechanic = 19;
    private const uint HasHarmfulAuraSpell = 20;
    private const uint HasHarmfulAuraMechanic = 22;
    private const uint DamagePhysical = 24;
    private const uint DamageArcane = 30;
    private const uint InCombat = 31;
    private const uint IsCasting = 33;
    private const uint IsChannelingSpell = 36;
    private const uint NumberOfMeleeAttackers = 37;
    private const uint IsAttackingMe = 38;
    private const uint Range = 39;
    private const uint InMeleeRange = 40;
    private const uint PursuitTime = 41;
    private const uint HasHarmfulAuraWithPeriodicDamage = 43;
    private const uint NumberOfEnemies = 44;
    private const uint NumberOfFriends = 45;
    private const uint ThreatPhysical = 46;
    private const uint ThreatArcane = 52;
    private const uint NumberOfAttackers = 54;
    private const uint NumberOfRangedAttackers = 55;
    private const uint CreatureType = 56;
    private const uint IsMeleeAttacking = 57;
    private const uint IsRangedAttacking = 58;
    private const uint Health = 59;
    private const uint HasHarmfulAuraEffect = 61;
    private const uint IsPlayer = 63;
    private const uint DamageMagic = 64;
    private const uint ThreatTotal = 67;
    private const uint CreatureId = 74;
    private const uint HasAura = 76;
    private const uint IsEnemy = 77;
    private const uint IsDying = 83;

    private CreatureConditionCatalog Conditions => me.Content.Ai.SpellLists.Conditions;

    private ICreatureSpellCaster? Spells => system.AiServices.Spells;

    /// <summary>cmangos CombatConditionMgr::Meets for the creature, with <paramref name="range"/> the range of its friend search. An unknown id does not hold.</summary>
    public bool MeetsCombat(int id, float range)
    {
        if (Conditions.FindCombat(id) is not { } row)
        {
            return false;
        }

        if (row.WorldStateExpressionId != 0)
        {
            system.ReportSpellListOnce($"condition:worldstate:{row.Id}", "world-state-expression", $"combat_condition {row.Id} names world state expression {row.WorldStateExpressionId}, which this server does not load; the entry stays off");
            return false;
        }

        if (Named(row.SelfConditionId) && !MeetsUnit(me, me, row.SelfConditionId))
        {
            return false;
        }

        if (Named(row.TargetConditionId) && (me.Combat.Victim is not { } victim || !MeetsUnit(victim, me, row.TargetConditionId)))
        {
            return false;
        }

        if (Named(row.Friend0.ConditionId) && !Counts(row.FriendLogic, row.Friend0, row.Friend1, Friends(range)))
        {
            return false;
        }

        return !Named(row.Enemy0.ConditionId) || Counts(row.EnemyLogic, row.Enemy0, row.Enemy1, Enemies());
    }

    /// <summary>cmangos UnitConditionMgr::Meets. An unknown id does not hold.</summary>
    public bool MeetsUnit(Unit source, Unit target, int id)
    {
        if (Conditions.FindUnit(id) is not { } row)
        {
            return false;
        }

        foreach (UnitConditionTerm term in row.Terms)
        {
            bool result = TryGetValue(source, target, term, out int value) && Check(term.Operation, value, term.Value);
            if (result == row.AnyOf)
            {
                return result;
            }
        }

        return !row.AnyOf;
    }

    /// <summary>The cmangos loader turns an id without a row into 0, which names nothing.</summary>
    private bool Named(int id) => id != 0 && Conditions.FindUnit(id) is not null;

    private bool Counts(CreatureConditionLogic logic, CombatConditionClause first, CombatConditionClause second, List<Unit> units)
    {
        uint matches0 = 0;
        uint matches1 = 0;
        foreach (Unit unit in units)
        {
            if (MeetsUnit(unit, me, first.ConditionId)) matches0++;
            if (Named(second.ConditionId) && MeetsUnit(unit, me, second.ConditionId)) matches1++;
        }

        bool result0 = Check(first.Operation, (int)matches0, (int)first.Count);
        bool result1 = Check(second.Operation, (int)matches1, (int)second.Count);
        return logic switch
        {
            CreatureConditionLogic.And => result0 && result1,
            CreatureConditionLogic.Or => result0 || result1,
            CreatureConditionLogic.Xor => result0 != result1,
            _ => result0,
        };
    }

    private List<Unit> Friends(float range)
    {
        ICreatureHostility hostility = system.AiServices.Hostility;
        List<Unit> found = [];
        foreach (Creature other in system.Creatures)
        {
            if (other.IsAlive && WorldGeometry.DistanceMinusRadii(me, other) <= range
                && (ReferenceEquals(other, me) || hostility.CanAssist(other, me) || hostility.IsFriendly(me, other)))
            {
                found.Add(other);
            }
        }

        foreach (Player player in system.Map.Players)
        {
            if (player.IsAlive && WorldGeometry.DistanceMinusRadii(me, player) <= range && hostility.IsFriendly(me, player))
            {
                found.Add(player);
            }
        }

        return found;
    }

    private List<Unit> Enemies()
    {
        if (!me.Combat.HasThreatList)
        {
            return [.. me.Combat.Attackers];
        }

        List<Unit> found = [];
        foreach (ThreatEntry entry in me.Combat.Threat.Entries)
        {
            found.Add(entry.Target);
        }

        return found;
    }

    /// <summary>cmangos ConditionEntry::CheckOp: no operation holds.</summary>
    private static bool Check(CreatureConditionOperation operation, int value, int operand) => operation switch
    {
        CreatureConditionOperation.EqualTo => value == operand,
        CreatureConditionOperation.NotEqualTo => value != operand,
        CreatureConditionOperation.LessThan => value < operand,
        CreatureConditionOperation.LessThanOrEqualTo => value <= operand,
        CreatureConditionOperation.GreaterThan => value > operand,
        CreatureConditionOperation.GreaterThanOrEqualTo => value >= operand,
        _ => true,
    };

    private bool TryGetValue(Unit source, Unit target, UnitConditionTerm term, out int value)
    {
        int operand = term.Value;
        uint variable = term.Variable;
        bool known = true;
        switch (variable)
        {
            case Race: value = (int)source.Race; break;
            case Class: value = (int)source.Class; break;
            case Level: value = source.Level; break;
            case IsSelf: value = Flag(ReferenceEquals(source, target)); break;
            case IsMyPet: value = Flag(!ReferenceEquals(source, target) && ReferenceEquals(target.GetCharmerOrOwnerOrSelf(), source)); break;
            case IsMaster: value = Flag(!ReferenceEquals(source, target) && ReferenceEquals(source.GetCharmerOrOwnerOrSelf(), target)); break;
            case IsTarget: value = Flag(ReferenceEquals(target.Combat.Victim, source)); break;
            case CanAssist: value = Flag(source is Creature helper && (ReferenceEquals(source, target) || system.AiServices.Hostility.IsFriendly(helper, target))); break;
            case CanAttack: case IsEnemy: value = Flag(source is Creature attacker && system.AiServices.Hostility.IsHostile(attacker, target)); break;
            case HealthPercent: value = source.MaxHealth == 0 ? 0 : (int)(source.Health * 100L / source.MaxHealth); break;
            case ManaPercent: value = PowerPercent(source, PowerType.Mana); break;
            case RagePercent: value = PowerPercent(source, PowerType.Rage); break;
            case EnergyPercent: value = PowerPercent(source, PowerType.Energy); break;
            case HasHelpfulAuraSpell: value = AnyAura(source, h => h.IsPositive && h.Spell.Id == (uint)operand) ? operand : 0; break;
            case HasHelpfulAuraDispelType: value = AnyAura(source, h => h.IsPositive && h.Spell.Dispel == (uint)operand) ? operand : 0; break;
            case HasHelpfulAuraMechanic: value = AnyAura(source, h => h.IsPositive && (h.Spell.AllMechanicMask() & SpellMechanics.Mask((uint)operand)) != 0) ? operand : 0; break;
            case HasHarmfulAuraSpell: value = AnyAura(source, h => !h.IsPositive && h.Spell.Id == (uint)operand) ? operand : 0; break;
            case HasHarmfulAuraMechanic: value = AnyAura(source, h => !h.IsPositive && (h.Spell.AllMechanicMask() & SpellMechanics.Mask((uint)operand)) != 0) ? operand : 0; break;
            case InCombat: value = Flag(source.Combat.IsInCombat); break;
            case >= IsCasting and <= IsChannelingSpell: value = Flag(system.AiServices.UnitSpells?.IsCasting(source) ?? false); break;
            case NumberOfMeleeAttackers: value = source.Combat.Attackers.Count(a => MapCombat.CanReachWithMeleeAutoAttack(source, a)); break;
            case IsAttackingMe: value = Flag(ReferenceEquals(source.Combat.Victim, target)); break;
            case Range: value = (int)WorldGeometry.CenterDistance3D(source, target); break;
            case InMeleeRange: value = Flag(MapCombat.CanReachWithMeleeAutoAttack(source, target)); break;
            case HasHarmfulAuraWithPeriodicDamage: value = Flag(AnyAura(source, h => h.HasAura(AuraType.PeriodicDamage))); break;
            case NumberOfEnemies: value = source.Combat.HasThreatList ? source.Combat.Threat.Entries.Count : 0; break;
            case NumberOfAttackers: value = source.Combat.Attackers.Count; break;
            case NumberOfRangedAttackers: value = source.Combat.Attackers.Count(a => !MapCombat.CanReachWithMeleeAutoAttack(source, a)); break;
            case CreatureType: value = source is Creature creature ? (int)creature.Template.CreatureType : 0; break;
            case IsMeleeAttacking: value = Flag(source.Combat.Victim is { } meleeVictim && MapCombat.CanReachWithMeleeAutoAttack(source, meleeVictim)); break;
            case IsRangedAttacking: value = Flag(source.Combat.Victim is { } rangedVictim && !MapCombat.CanReachWithMeleeAutoAttack(source, rangedVictim)); break;
            case Health: value = (int)Math.Min(source.Health, int.MaxValue); break;
            case HasHarmfulAuraEffect: value = operand >= 0 && AnyAura(source, h => !h.IsPositive && h.HasAura((AuraType)operand)) ? operand : 0; break;
            case IsPlayer: value = Flag(source is Player); break;
            case CreatureId: value = source is Creature entry ? (int)entry.Template.Entry : 0; break;
            case HasAura: value = Spells?.HasAura(source, (uint)operand) == true ? operand : 0; break;
            case IsDying: value = Flag(source.Health == 0); break;
            case >= DamagePhysical and <= DamageArcane:
            case PursuitTime:
            case NumberOfFriends:
            case >= ThreatPhysical and <= ThreatArcane:
            case >= DamageMagic and <= ThreatTotal:
                value = 0; // cmangos getConditionValue breaks out of these and returns false
                break;
            default:
                value = 0;
                known = false;
                system.ReportSpellListOnce($"condition:variable:{variable}", "unit-condition-unmodelled", $"unit_condition variable {variable} is not modelled; a term using it does not hold");
                break;
        }

        return known;
    }

    private static int Flag(bool value) => value ? 1 : 0;

    private static int PowerPercent(Unit unit, PowerType power)
    {
        uint max = MapCombat.GetMaxPower(unit, power);
        return max == 0 ? 0 : (int)(MapCombat.GetPower(unit, power) * 100L / max);
    }

    private bool AnyAura(Unit unit, Func<SpellAuraHolder, bool> match)
    {
        if (Spells is not { } spells)
        {
            return false;
        }

        foreach (SpellAuraHolder holder in spells.GetAuras(unit))
        {
            if (!holder.IsRemoved && match(holder))
            {
                return true;
            }
        }

        return false;
    }
}
