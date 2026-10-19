using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Instances.Scripts.World;

/// <summary>
/// cmangos <c>ScriptedMap</c> for an open-world map (AI/ScriptDevAI/scripts/world/world_map_scripts.h, kalimdor/world_kalimdor.cpp,
/// eastern_kingdoms/world_eastern_kingdoms.cpp: <see cref="KalimdorWorldMapData"/>, <see cref="EasternKingdomsWorldMapData"/>): the same encounter array and creature store an instance script keeps, on a continent. It is
/// an <see cref="InstanceData"/>, so the creature hooks every map already calls (create, death, evade, respawn) and the EventAI
/// SET_INST_DATA writes reach it, and anything on the map finds it with <c>map.FindUpdater&lt;InstanceData&gt;()</c>
/// (<c>Player::GetInstanceData</c>). Nothing is saved: cmangos keeps world map state in memory only.
/// </summary>
public abstract class WorldMapData : ScriptedInstance
{
    protected WorldMapData(Map map, int maxEncounter)
        : base(map, maxEncounter)
    {
    }

    private readonly System.Collections.Concurrent.ConcurrentQueue<(ushort EventId, bool Active, bool Resume)> _gameEvents = new();

    /// <summary>
    /// A game event started or stopped (mangos-classic GameEventMgr.cpp:1153-1160 calls the world map scripts' <c>OnEventHappened</c>).
    /// Any thread: the change is queued and <see cref="OnEventHappened"/> runs at this map's next update, on the map's own thread.
    /// </summary>
    public void QueueGameEvent(ushort eventId, bool active, bool resume) => _gameEvents.Enqueue((eventId, active, resume));

    /// <summary>mangos-classic <c>InstanceData::OnEventHappened(event_id, activate, resume)</c>, on the map thread.</summary>
    protected virtual void OnEventHappened(ushort eventId, bool active, bool resume)
    {
    }

    /// <summary>Runs the queued game event changes, then the script's update.</summary>
    public override void Update(uint diffMs)
    {
        while (_gameEvents.TryDequeue(out (ushort EventId, bool Active, bool Resume) change))
        {
            OnEventHappened(change.EventId, change.Active, change.Resume);
        }

        base.Update(diffMs);
    }

    /// <summary>World map data is never saved (cmangos world map scripts have no Save()).</summary>
    public override string? GetSaveData() => null;

    /// <summary>SD2 GetSingleCreatureFromStorage(entry, skipDebugLog) for an area trigger script.</summary>
    public Creature? FindStoredCreature(uint entry) => GetSingleCreatureFromStorage(entry);

    /// <summary>Random time source, replaceable by tests.</summary>
    internal Random Random { get; set; } = Random.Shared;

    /// <summary>
    /// cmangos WorldObject::GetRandomPoint(x, y, z, distance): a point within <paramref name="radius"/> yards around the given one, put on
    /// the floor near its height, terrain or a static model (UpdateAllowedPositionZ, Object.cpp:1624, asks Map::GetHeight with vmaps,
    /// Map.cpp:2758-2765); the given height where no floor is known.
    /// </summary>
    protected (float X, float Y, float Z) RandomPointAround(float x, float y, float z, float radius)
    {
        double angle = Random.NextDouble() * Math.PI * 2, distance = Random.NextDouble() * radius;
        float px = x + (float)(distance * Math.Cos(angle)), py = y + (float)(distance * Math.Sin(angle));
        float? ground = MapCreatureHeightProvider.Floor(Instance, px, py, z + 2f);
        return (px, py, ground is { } floor && Math.Abs(floor - z) <= 10f ? floor : z);
    }

    /// <summary>The world map script of a continent (cmangos ScriptMgr's per-map <c>GetInstanceData</c>), or null for a map without one.</summary>
    public static WorldMapData? Create(Map map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (map.InstanceId != 0)
        {
            return null;
        }

        return map.MapId switch
        {
            KalimdorWorldMapData.MapKalimdor => new KalimdorWorldMapData(map),
            EasternKingdomsWorldMapData.MapEasternKingdoms => new EasternKingdomsWorldMapData(map),
            _ => null,
        };
    }
}
