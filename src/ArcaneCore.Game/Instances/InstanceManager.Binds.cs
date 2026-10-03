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

        // vmangos GetRespawnTimeEx: the absolute time the corpse's spawn comes back (0 delay
        // for a creature that does not respawn on a timer).
        long delayMs = creature.RespawnAtMs - (creature.System?.ClockMs ?? 0);
        long resetTime = Now + Math.Max(0, delayMs) / 1000 + (2 * 60 * 60);
        if (save.ResetTime < resetTime)
        {
            save.ResetTime = resetTime;
            _persistence.InstanceSaved(save);
        }
    }
}
