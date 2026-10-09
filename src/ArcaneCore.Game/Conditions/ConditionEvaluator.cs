using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;

namespace ArcaneCore.Game.Conditions;

/// <summary>How many table rows can be decided with the collaborators present (see <see cref="ConditionEvaluator.Summarize"/>).</summary>
/// <param name="Total">Rows in the table.</param>
/// <param name="Evaluable">Rows (leaf or composite) whose every leaf type has an implementation and its collaborator.</param>
/// <param name="UnavailableByType">Leaf rows that cannot be decided, by condition type id.</param>
public sealed record ConditionSummary(int Total, int Evaluable, IReadOnlyDictionary<int, int> UnavailableByType);

/// <summary>
/// <see cref="IConditionEvaluator"/> over a <see cref="ConditionTable"/>: cmangos
/// <c>IsConditionSatisfied</c> / <c>ConditionEntry::Meets</c> / <c>Evaluate</c>
/// (D:\refs\mangos-classic\src\game\Globals\Conditions.cpp:109-514, 1023-1028).
/// <para>
/// The caller shape is the one cmangos gossip, vendor, trainer and quest checks use: the player is
/// the target, the NPC (when there is one) the source. <see cref="ConditionFlags.SwapTargets"/>
/// exchanges them first, a type whose parameter requirement then is not met is false and is not
/// reversed (Conditions.cpp:115-122), and <see cref="ConditionFlags.ReverseResult"/> negates last.
/// </para>
/// <para>
/// Fail closed: a type this server cannot decide (encounter, world-state and other
/// conditions that are not about a player at an NPC; a type whose collaborator is missing; a creature
/// target whose race, class, level or gender the NPC contract does not carry) is unknown, not false.
/// Unknown propagates with three-valued logic (NOT unknown is unknown; AND with a false is false; OR
/// with a true is true) and the final answer is "not satisfied", counted in
/// <see cref="Unavailable"/> and reported by <see cref="Summarize"/>. It is never reversed into a pass.
/// </para>
/// World thread only.
/// </summary>
public sealed class ConditionEvaluator(ConditionTable table, ConditionContext context) : IConditionEvaluator
{
    /// <summary>Alliance team id (SharedDefines.h:338).</summary>
    private const uint Alliance = 469;

    /// <summary>Horde team id (SharedDefines.h:337).</summary>
    private const uint Horde = 67;

    private readonly Dictionary<int, long> _unavailable = [];

    public ConditionTable Table { get; } = table ?? throw new ArgumentNullException(nameof(table));

    public ConditionContext Context { get; } = context ?? throw new ArgumentNullException(nameof(context));

    /// <summary>Evaluations that hit an undecidable leaf, by condition type id.</summary>
    public IReadOnlyDictionary<int, long> Unavailable => _unavailable;

    /// <summary>cmangos IsConditionSatisfied(conditionId, target = player, source = npc): a missing condition is false.</summary>
    public bool IsSatisfied(uint conditionId, Player player, NpcInfo? source)
    {
        ArgumentNullException.ThrowIfNull(player);
        return Table.Find(conditionId) is { } condition
            && Meets(condition, new Subject(player, null), source is null ? default : new Subject(null, source)) == true;
    }

    /// <summary>
    /// Count the rows that can and cannot be decided with the collaborators this evaluator has, so a
    /// startup log can say how much of the data is dead.
    /// </summary>
    public ConditionSummary Summarize()
    {
        var unavailable = new Dictionary<int, int>();
        var cache = new Dictionary<uint, bool>();
        int evaluable = 0;
        foreach (ConditionEntry entry in Table.Entries.OrderBy(e => e.Entry))
        {
            if (Decidable(entry, cache))
            {
                evaluable++;
            }
            else if (entry.Type is not (ConditionType.And or ConditionType.Or or ConditionType.Not))
            {
                unavailable[(int)entry.Type] = unavailable.GetValueOrDefault((int)entry.Type) + 1;
            }
        }

        return new ConditionSummary(Table.Count, evaluable, unavailable);
    }

    /// <summary>Whether the type has an implementation here and, when it needs one, its collaborator.</summary>
    public bool IsTypeAvailable(ConditionType type) => type switch
    {
        ConditionType.Not or ConditionType.Or or ConditionType.And or ConditionType.None or ConditionType.ItemEquipped
            or ConditionType.Team or ConditionType.RaceClass or ConditionType.Level or ConditionType.Gender
            or ConditionType.DeadOrAway => true,
        ConditionType.Aura => Context.HasAura is not null,
        ConditionType.Item or ConditionType.ItemWithBank => Context.ItemCount is not null,
        ConditionType.AreaId => Context.ZoneAndArea is not null,
        ConditionType.AreaFlag => Context.ZoneAndArea is not null && Context.AreaFlags is not null,
        ConditionType.ReputationRankMin or ConditionType.ReputationRankMax => Context.ReputationRank is not null,
        ConditionType.Skill or ConditionType.SkillBelow => Context.SkillValueBase is not null,
        ConditionType.QuestRewarded or ConditionType.QuestTaken or ConditionType.QuestAvailable or ConditionType.QuestNone
            => Context.Quests is not null,
        ConditionType.AdCommissionAura => Context.HasAdCommissionAura is not null,
        ConditionType.PvpRank => Context.HonorRank is not null,
        ConditionType.ActiveGameEvent => Context.IsGameEventActive is not null,
        ConditionType.ActiveHoliday => Context.IsHolidayActive is not null,
        ConditionType.Spell => Context.HasSpell is not null,
        ConditionType.InstanceScript => Context.InstanceScript is not null,
        ConditionType.CompletedEncounter => Context.CompletedEncounter is not null,
        ConditionType.LastWaypoint => Context.LastWaypoint is not null,
        ConditionType.CreatureInRange => Context.CreatureInRange is not null,
        ConditionType.SpawnCount => Context.SpawnCount is not null,
        ConditionType.WorldScript => Context.WorldScript is not null,
        ConditionType.WorldState => Context.WorldState is not null,
        _ => false,
    };

    private bool Decidable(ConditionEntry entry, Dictionary<uint, bool> cache)
    {
        if (cache.TryGetValue(entry.Entry, out bool known))
        {
            return known;
        }

        bool result;
        switch (entry.Type)
        {
            case ConditionType.Not:
                result = DecidableById(entry.Value1, cache);
                break;
            case ConditionType.And or ConditionType.Or:
                result = DecidableById(entry.Value1, cache) && DecidableById(entry.Value2, cache)
                    && (entry.Value3 == 0 || DecidableById(entry.Value3, cache))
                    && (entry.Value4 == 0 || DecidableById(entry.Value4, cache));
                break;
            default:
                result = IsTypeAvailable(entry.Type);
                break;
        }

        cache[entry.Entry] = result;
        return result;
    }

    private bool DecidableById(uint id, Dictionary<uint, bool> cache) => Table.Find(id) is { } entry && Decidable(entry, cache);

    // ---- Meets / Evaluate ------------------------------------------------------------------

    /// <summary>cmangos ConditionEntry::Meets (Conditions.cpp:109-130); null is "cannot be decided".</summary>
    private bool? Meets(ConditionEntry condition, Subject target, Subject source)
    {
        if ((condition.Flags & ConditionFlags.SwapTargets) != 0)
        {
            (source, target) = (target, source);
        }

        if (!ParametersMet(condition.Type, target, source))
        {
            return false;
        }

        bool? result = Evaluate(condition, target, source);
        return (condition.Flags & ConditionFlags.ReverseResult) != 0 ? !result : result;
    }

    /// <summary>cmangos CheckParamRequirements over the ConditionTargets table (Conditions.cpp:54-106, 516-598).</summary>
    private static bool ParametersMet(ConditionType type, Subject target, Subject source) => type switch
    {
        ConditionType.Aura or ConditionType.Level or ConditionType.Gender or ConditionType.IsInCombat => target.IsUnit,
        ConditionType.RaceClass => target.IsUnit,
        ConditionType.Item or ConditionType.ItemEquipped or ConditionType.Team or ConditionType.Skill or ConditionType.QuestRewarded
            or ConditionType.QuestTaken or ConditionType.AdCommissionAura or ConditionType.Spell or ConditionType.QuestAvailable
            or ConditionType.QuestNone or ConditionType.ItemWithBank or ConditionType.LearnableAbility or ConditionType.SkillBelow
            or ConditionType.ReputationRankMin or ConditionType.ReputationRankMax or ConditionType.PvpRank => target.IsPlayer,
        ConditionType.AreaId or ConditionType.AreaFlag => source.Present || target.Present,
        ConditionType.LastWaypoint => source.IsCreature,
        ConditionType.CreatureInRange => target.Present,
        _ => true,
    };

    private bool? Evaluate(ConditionEntry c, Subject target, Subject source)
    {
        switch (c.Type)
        {
            case ConditionType.Not:
                return !MeetsById(c.Value1, target, source);
            case ConditionType.Or:
            {
                // Conditions.cpp:157-165: the optional third and fourth first, then the first two.
                bool? result = false;
                foreach (uint id in OperandIds(c))
                {
                    result = Or(result, MeetsById(id, target, source));
                    if (result == true)
                    {
                        return true;
                    }
                }

                return result;
            }

            case ConditionType.And:
            {
                bool? result = true;
                foreach (uint id in OperandIds(c))
                {
                    result = And(result, MeetsById(id, target, source));
                    if (result == false)
                    {
                        return false;
                    }
                }

                return result;
            }

            case ConditionType.None:
                return true;
            case ConditionType.Aura:
                return Unknown(c, target.Player is { } auraHolder && Context.HasAura is { } hasAura
                    ? hasAura(auraHolder, c.Value1, (byte)c.Value2) : null);
            case ConditionType.Item:
                return Unknown(c, target.Player is { } p1 && Context.ItemCount is { } count1 ? count1(p1, c.Value1, false) >= c.Value2 : null);
            case ConditionType.ItemWithBank:
                return Unknown(c, target.Player is { } p2 && Context.ItemCount is { } count2 ? count2(p2, c.Value1, true) >= c.Value2 : null);
            case ConditionType.ItemEquipped:
                return target.Player!.Inventory.Equipped.Sum(e => e.Item.Entry == c.Value1 ? (long)e.Item.Count : 0) >= 1;
            case ConditionType.AreaId:
            {
                // Conditions.cpp:194-199: searcher = source ? source : target.
                if (Context.ZoneAndArea is not { } where || (source.Present ? source : target).Position is not { } at)
                {
                    return Unknown(c, null);
                }

                (uint zone, uint area) = where(at.MapId, at.X, at.Y, at.Z);
                return (zone == c.Value1 || area == c.Value1) == (c.Value2 == 0);
            }

            case ConditionType.AreaFlag:
            {
                // Conditions.cpp:249-257.
                if (Context.ZoneAndArea is not { } where || Context.AreaFlags is not { } flagsOf
                    || (source.Present ? source : target).Position is not { } at)
                {
                    return Unknown(c, null);
                }

                uint? flags = flagsOf(where(at.MapId, at.X, at.Y, at.Z).AreaId);
                return flags is { } f && (c.Value1 == 0 || (f & c.Value1) != 0) && (c.Value2 == 0 || (f & c.Value2) == 0);
            }

            case ConditionType.ReputationRankMin:
                return Rank(c, target, rank => rank >= c.Value2);
            case ConditionType.ReputationRankMax:
                return Rank(c, target, rank => rank <= c.Value2);
            case ConditionType.Team:
                return (target.Player!.Team == Team.Alliance ? Alliance : Horde) == c.Value1;
            case ConditionType.Skill:
                return Unknown(c, target.Player is { } p3 && Context.SkillValueBase is { } skill1
                    ? skill1(p3, c.Value1) is var have && have > 0 && have >= c.Value2 : null);
            case ConditionType.SkillBelow:
                return Unknown(c, target.Player is { } p4 && Context.SkillValueBase is { } skill2
                    ? c.Value2 == 1 ? skill2(p4, c.Value1) == 0 : skill2(p4, c.Value1) is var base2 && base2 > 0 && base2 < c.Value2 : null);
            case ConditionType.QuestRewarded:
                return Unknown(c, Context.Quests?.Invoke()?.IsRewarded(target.Player!, c.Value1));
            case ConditionType.QuestTaken:
                return Unknown(c, Context.Quests?.Invoke()?.IsCurrent(target.Player!, c.Value1, (byte)Math.Min(c.Value2, 255)));
            case ConditionType.QuestAvailable:
                return Unknown(c, Context.Quests?.Invoke()?.CanTakeQuest(target.Player!, c.Value1));
            case ConditionType.QuestNone:
            {
                // Conditions.cpp:309-313: neither current nor rewarded.
                IConditionQuests? quests = Context.Quests?.Invoke();
                bool? current = quests?.IsCurrent(target.Player!, c.Value1, 0);
                bool? rewarded = quests?.IsRewarded(target.Player!, c.Value1);
                return Unknown(c, current is null || rewarded is null ? null : !current.Value && !rewarded.Value);
            }

            case ConditionType.AdCommissionAura:
                return Unknown(c, Context.HasAdCommissionAura?.Invoke(target.Player!));
            case ConditionType.PvpRank:
                return Unknown(c, Context.HonorRank?.Invoke(target.Player!) is { } rank ? rank >= c.Value1 && rank <= c.Value2 : null);
            case ConditionType.ActiveGameEvent:
                return Unknown(c, Context.IsGameEventActive?.Invoke(c.Value1));
            case ConditionType.ActiveHoliday:
                return Unknown(c, Context.IsHolidayActive?.Invoke(c.Value1));
            case ConditionType.RaceClass:
            {
                // Conditions.cpp:259-263; a creature target's race and class are not part of NpcInfo.
                if (target.Player is not { } unit)
                {
                    return Unknown(c, null);
                }

                return (c.Value1 == 0 || (RaceMask(unit) & c.Value1) != 0) && (c.Value2 == 0 || (ClassMask(unit) & c.Value2) != 0);
            }

            case ConditionType.Level:
            {
                if (target.Player is not { } unit)
                {
                    return Unknown(c, null);
                }

                return c.Value2 switch
                {
                    0 => unit.Level == c.Value1,
                    1 => unit.Level >= c.Value1,
                    2 => unit.Level <= c.Value1,
                    _ => false,
                };
            }

            case ConditionType.Spell:
                return Unknown(c, target.Player is { } p5 && Context.HasSpell is { } hasSpell
                    ? c.Value2 switch { 0 => hasSpell(p5, c.Value1), 1 => !hasSpell(p5, c.Value1), _ => false } : null);
            case ConditionType.InstanceScript:
                return Unknown(c, target.Player is { } p6 ? Context.InstanceScript?.Invoke(p6, c.Value1) : null);
            case ConditionType.CompletedEncounter:
                return Unknown(c, target.Player is { } encounterPlayer
                    ? Context.CompletedEncounter?.Invoke(encounterPlayer, c.Value1, c.Value2) : null);
            case ConditionType.LastWaypoint:
            {
                uint? reached = target.Player is { } waypointPlayer && source.Npc is { } sourceCreature
                    ? Context.LastWaypoint?.Invoke(waypointPlayer, sourceCreature) : null;
                return Unknown(c, reached is { } waypoint ? c.Value2 switch
                {
                    0 => waypoint == c.Value1,
                    1 => waypoint >= c.Value1,
                    2 => waypoint < c.Value1,
                    _ => false,
                } : null);
            }
            case ConditionType.DeadOrAway:
            {
                Player? player = target.Player;
                NpcInfo? npc = source.Npc;
                return c.Value1 switch
                {
                    0 => player is null || !player.IsAlive || (c.Value2 > 0 && npc is not null && Away(player, npc, c.Value2)),
                    1 or 2 => Unknown(c, player is not null ? Context.DeadOrAwayGroup?.Invoke(player, npc, c.Value1, c.Value2) : null),
                    3 => npc is null || !npc.IsAlive,
                    _ => false,
                };
            }
            case ConditionType.CreatureInRange:
                return Unknown(c, target.Player is { } rangePlayer
                    ? Context.CreatureInRange?.Invoke(rangePlayer, c.Value1, c.Value2) : null);
            case ConditionType.SpawnCount:
                return Unknown(c, target.Player is { } spawnPlayer && Context.SpawnCount?.Invoke(spawnPlayer, c.Value1) is { } spawned
                    ? spawned >= c.Value2 : null);
            case ConditionType.WorldScript:
                return Unknown(c, Context.WorldScript?.Invoke(c.Value1, c.Value2));
            case ConditionType.WorldState:
            {
                int? value = target.Player is { } statePlayer ? Context.WorldState?.Invoke(statePlayer, c.Value1) : null;
                return Unknown(c, value is { } state ? CompareWorldState(c.Value2, state, unchecked((int)c.Value3)) : null);
            }
            case ConditionType.Gender:
                return target.Player is { } gendered ? (uint)gendered.Gender == c.Value1 : Unknown(c, null);
            default:
                return Unknown(c, null);
        }
    }

    private static bool Away(Player player, NpcInfo source, uint range)
    {
        if (player.MapId != source.MapId) return true;
        double dx = player.X - source.X, dy = player.Y - source.Y, dz = player.Z - source.Z;
        return dx * dx + dy * dy + dz * dz > (double)range * range;
    }

    // mangos-classic Conditions.cpp ConditionEntry::CheckOp (132-145), signed int32.
    private static bool CompareWorldState(uint operation, int value, int operand) => operation switch
    {
        1 => value == operand,
        2 => value != operand,
        3 => value < operand,
        4 => value <= operand,
        5 => value > operand,
        6 => value >= operand,
        _ => true,
    };

    /// <summary>The operands in evaluation order: value3 and value4 when set, then value1 and value2 (Conditions.cpp:157-176).</summary>
    private static IEnumerable<uint> OperandIds(ConditionEntry c)
    {
        if (c.Value3 != 0)
        {
            yield return c.Value3;
        }

        if (c.Value4 != 0)
        {
            yield return c.Value4;
        }

        yield return c.Value1;
        yield return c.Value2;
    }

    private bool? MeetsById(uint id, Subject target, Subject source)
        => Table.Find(id) is { } condition ? Meets(condition, target, source) : null;

    private static bool? Or(bool? a, bool? b) => a == true || b == true ? true : a is null || b is null ? null : false;

    private static bool? And(bool? a, bool? b) => a == false || b == false ? false : a is null || b is null ? null : true;

    /// <summary>Conditions.cpp:201-205 and 386-391: an unknown faction is false, not unknown.</summary>
    private bool? Rank(ConditionEntry c, Subject target, Func<byte, bool> test)
    {
        if (target.Player is not { } player || Context.ReputationRank is not { } rankOf)
        {
            return Unknown(c, null);
        }

        return Context.FactionExists is { } exists && !exists(c.Value1) ? false : test(rankOf(player, c.Value1));
    }

    /// <summary>Record an undecidable leaf (a null value) and pass the value through.</summary>
    private bool? Unknown(ConditionEntry c, bool? value)
    {
        if (value is null)
        {
            _unavailable[(int)c.Type] = _unavailable.GetValueOrDefault((int)c.Type) + 1;
        }

        return value;
    }

    private static uint RaceMask(Player p) => (byte)p.Race is > 0 and <= 32 ? 1u << ((byte)p.Race - 1) : 0;

    private static uint ClassMask(Player p) => (byte)p.Class is > 0 and <= 32 ? 1u << ((byte)p.Class - 1) : 0;

    /// <summary>A WorldObject taking part in a condition: the asking player or the NPC.</summary>
    private readonly record struct Subject(Player? Player, NpcInfo? Npc)
    {
        public bool Present => Player is not null || Npc is not null;

        public bool IsPlayer => Player is not null;

        public bool IsCreature => Npc is not null;

        public bool IsUnit => Present;

        public (uint MapId, float X, float Y, float Z)? Position
            => Player is { } p ? (p.MapId, p.X, p.Y, p.Z) : Npc is { } n ? (n.MapId, n.X, n.Y, n.Z) : null;
    }
}
