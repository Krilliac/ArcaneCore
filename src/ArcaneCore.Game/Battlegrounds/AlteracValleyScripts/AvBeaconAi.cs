using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using static ArcaneCore.Game.Battlegrounds.AlteracValley;

namespace ArcaneCore.Game.Battlegrounds.AlteracValleyScripts;

/// <summary>
/// vmangos AV_BeaconInvocationObjectAI (scripts/battlegrounds/battleground_alterac.cpp:3802-3858), the beacons a player plants for an air
/// assault (script go_av_beacon): the planted beacon belongs to nobody, takes level 0 (the summon effect gave it the caster's level, which
/// makes it unusable) and its team's faction (83 Alliance, 84 Horde); a minute later it calls a war rider (Horde) or an aerie gryphon
/// (Alliance) 30 yd above itself and goes. A player who uses it (the enemy channels it away) removes it before that.
/// </summary>
public sealed class AvBeaconAi : IGameObjectAi
{
    /// <summary>m_invocationTimer: a beacon calls its rider after a minute.</summary>
    public const uint InvocationDelayMs = 60_000;

    private readonly Dictionary<ObjectGuid, uint> _timers = [];

    /// <summary>Whether the beacon is an Alliance one (Ichman's, Vipore's, Slidore's).</summary>
    public static bool IsAllianceBeacon(uint entry) => entry is GameObjectBeaconIchman or GameObjectBeaconVipore or GameObjectBeaconSlidore;

    public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => false;

    public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs)
    {
        if (!go.IsSpawned)
        {
            _timers.Remove(go.Guid);
            return;
        }

        if (!_timers.TryGetValue(go.Guid, out uint timer))
        {
            // The script's construction: no owner, level 0, the team's faction, and the minute starts.
            go.SetUInt32(UpdateFields.GameobjectLevel, 0);
            go.SetOwner(default);
            go.SetUInt32(UpdateFields.GameobjectFaction, IsAllianceBeacon(go.Entry) ? 83u : 84u);
            _timers[go.Guid] = InvocationDelayMs;
            return;
        }

        if (timer < diffMs)
        {
            if (go.Map?.FindUpdater<CreatureMapSystem>() is { } creatures
                && creatures.Content.FindTemplate(IsAllianceBeacon(go.Entry) ? NpcAerieGryphon : NpcWarRider) is { } template)
            {
                Creature rider = creatures.SpawnTemporary(template, go.X, go.Y, go.Z + 30, go.Orientation);
                creatures.MarkCorpseDespawn(rider);
            }

            _timers.Remove(go.Guid);
            objects.Remove(go); // AddObjectToRemoveList
            return;
        }

        _timers[go.Guid] = timer - diffMs;
    }

    /// <summary>OnUse (:3851-3856): the channel finished, the beacon is gone.</summary>
    public bool OnUse(GameObjectMapSystem objects, GameObject go, Unit user)
    {
        _timers.Remove(go.Guid);
        objects.Remove(go);
        return true;
    }
}
