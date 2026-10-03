using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Kernel.WorldData;

namespace ArcaneCore.Game.Instances;

// Read-only seams other lanes use (corpse/ghost rules, entrance relocation) and lifecycle events.
public sealed partial class InstanceManager
{
    /// <summary>Raised on the world thread at the end of <see cref="Load"/>, once the saves and binds are in memory.</summary>
    public event Action? Loaded;

    /// <summary>Raised on the world thread when a new logical save is created (first entry of a player or group without a bind), after its storage write was queued.</summary>
    public event Action<InstanceSave>? SaveCreated;

    /// <summary>
    /// The area trigger that teleports players out of a dungeon map to its ghost entrance map
    /// (vmangos <c>ObjectMgr::GetGoBackTrigger</c>, ObjectMgr.cpp:7789-7806): a trigger placed on
    /// <paramref name="mapId"/> whose destination is the map's <c>ghostEntranceMap</c>. vmangos
    /// walks an unordered map, so with several candidates its pick is arbitrary; here the lowest
    /// trigger id wins (the order the entrance relocation always used).
    /// </summary>
    public AreaTriggerTeleport? GetGoBackTrigger(uint mapId)
    {
        MapTemplate? template = Registry.Find(mapId);
        if (template is null || !template.IsDungeon || template.GhostEntranceMap < 0)
        {
            return null;
        }

        WorldMaps maps = WorldMaps.Of(_world);
        return maps.AreaTriggers
            .Where(t => t.MapId == mapId)
            .OrderBy(t => t.Id)
            .Select(t => maps.FindAreaTriggerTeleport(t.Id))
            .FirstOrDefault(t => t is not null && t.TargetMap == (uint)template.GhostEntranceMap);
    }

    /// <summary>
    /// The area trigger that teleports players into <paramref name="mapId"/> (vmangos
    /// <c>ObjectMgr::GetMapEntranceTrigger</c>, ObjectMgr.cpp:7808-7822); the trigger must have a
    /// volume. Maps with several entrances (Molten Core, Naxxramas) have an arbitrary pick in
    /// vmangos; here the lowest trigger id wins.
    /// </summary>
    public AreaTriggerTeleport? GetMapEntranceTrigger(uint mapId)
    {
        WorldMaps maps = WorldMaps.Of(_world);
        return maps.AreaTriggers
            .OrderBy(t => t.Id)
            .Select(t => maps.FindAreaTriggerTeleport(t.Id))
            .FirstOrDefault(t => t is not null && t.TargetMap == mapId);
    }

    /// <summary>Whether the logical save still exists (not reset, not deleted), whether or not its map is loaded.</summary>
    public bool IsSaveLive(uint mapId, uint instanceId) =>
        _saves.TryGetValue(instanceId, out InstanceSave? save) && save.MapId == mapId && !save.IsDeleted;

    /// <summary>The parent maps of a map (<c>map_template.Parent</c>), nearest first; stops on a cycle.</summary>
    public IReadOnlyList<uint> GetParentMapChain(uint mapId)
    {
        var chain = new List<uint>();
        MapTemplate? template = Registry.Find(mapId);
        while (template is { Parent: not 0 } && !chain.Contains(template.Parent) && template.Parent != mapId)
        {
            chain.Add(template.Parent);
            template = Registry.Find(template.Parent);
        }

        return chain;
    }
}
