using ArcaneCore.Game.Conditions;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Creatures;

public sealed partial class CreatureMapSystem
{
    /// <summary>CREATURE_EXTRA_FLAG_COUNT_SPAWNS (cmangos Entities/Creature.h:69): the creature counts for CONDITION_SPAWN_COUNT.</summary>
    private const uint ExtraFlagCountSpawns = 0x00200000;

    /// <summary>
    /// cmangos IsConditionSatisfied(conditionId, target, map, source) for a DB script (CONDITION_FROM_DBSCRIPTS): a row's condition_id
    /// (ScriptMgr.cpp:1769, the step's target and source) and TERMINATE_COND (:2720-2758, the player and the other object). An unknown
    /// answer is "not satisfied".
    /// </summary>
    private bool RelayConditionHolds(uint conditionId, WorldObject? target, WorldObject? source)
        => RelayCondition(conditionId, target, source) == true;

    /// <summary>
    /// The three-valued answer (null: cannot be decided here). The condition types about the map rather than a player - 36 DEAD_OR_AWAY,
    /// 37 CREATURE_IN_RANGE and 39 SPAWN_COUNT, and the AND/OR/NOT rows over them - are decided here with the script's objects
    /// (Conditions.cpp:299-308, 424-466, 484-489); a subtree without them goes to the player condition evaluator as before. The table is
    /// read through <see cref="IConditionTableEvaluator"/>: the world's evaluator is the forwarding ConditionFeature, not the table
    /// evaluator itself.
    /// </summary>
    private bool? RelayCondition(uint conditionId, WorldObject? target, WorldObject? source)
    {
        if ((_ai.Conditions as IConditionTableEvaluator)?.Current is not { } evaluator)
        {
            return PlayerCondition(conditionId, target, source, null);
        }

        // Conditions.cpp:1023-1028: a condition id that is not loaded is not satisfied.
        return evaluator.Table.Find(conditionId) is { } condition ? RelayCondition(evaluator, condition, target, source) : false;
    }

    private bool? RelayCondition(ConditionEvaluator evaluator, ConditionEntry c, WorldObject? target, WorldObject? source)
    {
        if (!ReadsMapState(evaluator, c))
        {
            return PlayerCondition(c.Entry, target, source, evaluator);
        }

        // ConditionEntry::Meets (Conditions.cpp:109-130): swap first; a type whose parameter requirement fails is false, not reversed.
        if ((c.Flags & ConditionFlags.SwapTargets) != 0)
        {
            (target, source) = (source, target);
        }

        if (c.Type == ConditionType.CreatureInRange && target is null)
        {
            return false; // CONDITION_REQ_TARGET_WORLDOBJECT
        }

        bool? result;
        switch (c.Type)
        {
            case ConditionType.Not:
                result = !Operand(c.Value1);
                break;
            case ConditionType.Or or ConditionType.And:
            {
                // Conditions.cpp:157-176: value3 and value4 (when set) first, then value1 and value2; three-valued as ConditionEvaluator.
                bool isOr = c.Type == ConditionType.Or;
                result = !isOr;
                foreach (uint id in OperandOrder(c))
                {
                    bool? operand = Operand(id);
                    result = isOr
                        ? result == true || operand == true ? true : result is null || operand is null ? null : false
                        : result == false || operand == false ? false : result is null || operand is null ? null : true;
                    if (result == isOr)
                    {
                        break;
                    }
                }

                break;
            }

            case ConditionType.DeadOrAway:
                result = DeadOrAway(c, target, source);
                break;
            case ConditionType.CreatureInRange:
                // NearestCreatureEntryWithLiveStateInObjectRangeCheck(target, entry, onlyAlive, excludeSelf): a living one within value2.
                result = _creatures.Values.Any(creature => creature.Template.Entry == c.Value1 && creature.IsAlive
                    && !ReferenceEquals(creature, target) && DistanceSquared(creature, target!) < c.Value2 * (float)c.Value2);
                break;
            case ConditionType.SpawnCount:
                // Map::SpawnedCountForEntry (Maps/Map.cpp:2988-2991): creatures of the entry in the world that count their spawns
                // (Creature.cpp:208-209, 594), dead ones included until they leave the map.
                result = _creatures.Values.Count(creature => creature.Template.Entry == c.Value1
                    && (creature.Template.ExtraFlags & ExtraFlagCountSpawns) != 0) >= c.Value2;
                break;
            default:
                result = null;
                break;
        }

        return (c.Flags & ConditionFlags.ReverseResult) != 0 ? !result : result;

        bool? Operand(uint id) => evaluator.Table.Find(id) is { } operand ? RelayCondition(evaluator, operand, target, source) : null;
    }

    private static IEnumerable<uint> OperandOrder(ConditionEntry c)
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

    /// <summary>CONDITION_DEAD_OR_AWAY (Conditions.cpp:424-466): value1 picks who, value2 is the distance from the source (0: any).</summary>
    private bool DeadOrAway(ConditionEntry c, WorldObject? target, WorldObject? source)
    {
        Player? player = target as Player;
        switch (c.Value1)
        {
            case 0: // the player dead or out of range
                return player is null || !player.IsAlive || (c.Value2 != 0 && source is not null && !WithinDistance(source, player, c.Value2));
            case 1: // every member of the player's group dead or out of range
            {
                if (player is null)
                {
                    return true;
                }

                IReadOnlyList<Player> group = _ai.ScriptQuests?.GroupMembersOf(player) ?? [];
                if (group.Count == 0)
                {
                    return !player.IsAlive || (c.Value2 != 0 && source is not null && !WithinDistance(source, player, c.Value2));
                }

                return !group.Any(member => member.IsAlive && !member.IsGameMaster
                    && (c.Value2 == 0 || source is null || WithinDistance(source, member, c.Value2)));
            }

            case 2: // every player of the instance dead or out of range; false on a map that is not an instance
                return Map.InstanceId != 0 && !Map.Players.Any(each => each.IsAlive && !each.IsGameMaster
                    && (c.Value2 == 0 || source is null || WithinDistance(source, each, c.Value2)));
            case 3: // the creature source dead
                return source is not Creature { IsAlive: true };
            default:
                return false;
        }
    }

    /// <summary>WorldObject::IsWithinDistInMap (Object.cpp:1361-1368): same map, 3D, both combat reaches added to the distance.</summary>
    private static bool WithinDistance(WorldObject a, WorldObject b, float distance)
    {
        if (!ReferenceEquals(a.Map, b.Map))
        {
            return false;
        }

        float reach = distance + CombatReach(a) + CombatReach(b);
        return DistanceSquared(a, b) < reach * reach;
    }

    private static float CombatReach(WorldObject o) => o is Unit unit ? unit.GetFloat(UpdateFields.UnitFieldCombatreach) : 0f;

    /// <summary>Whether <paramref name="c"/> is, or combines, a condition type decided from the map here.</summary>
    private static bool ReadsMapState(ConditionEvaluator evaluator, ConditionEntry c) => c.Type switch
    {
        ConditionType.DeadOrAway or ConditionType.CreatureInRange or ConditionType.SpawnCount => true,
        ConditionType.Not or ConditionType.And or ConditionType.Or => ((uint[])[c.Value1, c.Value2, c.Value3, c.Value4])
            .Any(id => id != 0 && evaluator.Table.Find(id) is { } operand && ReadsMapState(evaluator, operand)),
        _ => false,
    };

    /// <summary>
    /// A condition about a player at an NPC, through <see cref="CreatureAiServices.Conditions"/>: the player is the target or else the
    /// source, the NPC the source or else the target. Null when there is no player or (with the table evaluator) a leaf type it cannot
    /// decide, so a NOT above it does not turn "cannot tell" into a pass.
    /// </summary>
    private bool? PlayerCondition(uint conditionId, WorldObject? target, WorldObject? source, ConditionEvaluator? evaluator)
    {
        Player? player = target as Player ?? source as Player;
        if (player is null || _ai.Conditions is not { } conditions)
        {
            return null;
        }

        if (evaluator is not null && evaluator.Table.Find(conditionId) is { } entry && !Decidable(evaluator, entry))
        {
            return null;
        }

        Creature? npc = source as Creature ?? target as Creature;
        NpcInfo? info = npc is null ? null : new NpcInfo(npc.Guid, npc.Entry, npc.Spawn?.Guid ?? npc.Guid.Low, (NpcFlags)npc.NpcFlags, npc.MapId,
            npc.X, npc.Y, npc.Z, npc.BoundingRadius, npc.IsAlive, IsHostile: false, npc.Combat.IsInCombat,
            (npc.UnitFlags & UnitFlags.NotSelectable) != 0, npc.Template.GossipMenuId);
        return conditions.IsSatisfied(conditionId, player, info);
    }

    private static bool Decidable(ConditionEvaluator evaluator, ConditionEntry c) => c.Type switch
    {
        ConditionType.Not or ConditionType.And or ConditionType.Or => ((uint[])[c.Value1, c.Value2, c.Value3, c.Value4])
            .All(id => id == 0 || (evaluator.Table.Find(id) is { } operand && Decidable(evaluator, operand))),
        _ => evaluator.IsTypeAvailable(c.Type),
    };
}
