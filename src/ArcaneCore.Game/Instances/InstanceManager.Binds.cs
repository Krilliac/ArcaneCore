using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Instances;

public sealed partial class InstanceManager
{
    /// <summary>Resolves the credited player of a creature kill (default: the killer if it is a player).</summary>
    public IKillCreditResolver KillCreditResolver { get; set; } = PlayerKillCreditResolver.Instance;

    /// <summary>
    /// A creature died on an instance map (vmangos Unit.cpp:1253-1263 -> <c>Map::BindToInstanceOrRaid</c>,
    /// Map.cpp:3526-3545). Needs a credited player (owner, charmer or tapper through
    /// <see cref="KillCreditResolver"/>); nobody credited means no bind and no reset-time change.
    /// Raids bind permanently only for a creature flagged <see cref="CreatureFlagExtraInstanceBind"/>
    /// (the IsRaid gate; the flag is classic-db's <c>ExtraFlags</c> bit, vmangos's own
    /// LOCK_TAPPERS_TO_RAID_ON_DEATH static flag is not set in that data). A normal dungeon
    /// never binds permanently: its reset time becomes respawn time + 2 h when that is later
    /// ("the reset time is set but not added to the scheduler until the players leave").
    /// </summary>
    private void OnUnitKilled(Map map, Unit? killer, Unit victim)
    {
        if (victim is not Creature creature || !_mapStates.TryGetValue(map, out InstanceMapState? mapState) || mapState.Save.IsDeleted)
        {
            return;
        }

        Player? credited = KillCreditResolver.Resolve(killer, creature);
        if (credited is null)
        {
            return;
        }

        InstanceSave save = mapState.Save;
        if (save.Template.IsRaid)
        {
            if ((creature.Template.ExtraFlags & CreatureFlagExtraInstanceBind) != 0)
            {
                PermBindAllPlayers(map, credited);
            }

            return;
        }

        if (!_options.ResetExtendsOnKills)
        {
            return;
        }

        long resetTime = Now + SecondsUntilBack(map, creature) + (2 * 60 * 60);
        if (save.ResetTime < resetTime)
        {
            save.ResetTime = resetTime;
            _persistence.InstanceSaved(save);
        }
    }

    /// <summary>
    /// vmangos <c>Creature::GetRespawnTimeEx</c> (Creature.cpp:3305-3314) relative to now: the time left to the respawn
    /// when it is ahead; else, while the corpse lies, the respawn delay plus the corpse time left; else 0. The respawn time
    /// is a map-clock value, so it is measured against the clock of the creature's system (the map's when the creature is
    /// in none); without any clock it is not read at all.
    /// </summary>
    private static long SecondsUntilBack(Map map, Creature creature)
    {
        CreatureMapSystem? clock = creature.System ?? map.FindUpdater<CreatureMapSystem>();
        if (clock is not null && creature.RespawnAtMs > clock.ClockMs)
        {
            return (creature.RespawnAtMs - clock.ClockMs) / 1000;
        }

        return creature.CorpseDecayMs > 0 ? creature.RespawnDelaySeconds + (creature.CorpseDecayMs / 1000) : 0;
    }
}
