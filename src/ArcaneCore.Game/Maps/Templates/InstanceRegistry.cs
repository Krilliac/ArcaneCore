using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData;

namespace ArcaneCore.Game.Maps.Templates;

/// <summary>
/// Instance ids and per-player instance bindings — the first step of vmangos'
/// <c>MapPersistentStateManager</c> / <c>MapManager::CreateMap</c>. Continents and other
/// common maps always use instance 0 (vmangos <c>GetContinentInstanceId</c> without continent
/// instancing); an instanceable map gets a fresh id the first time a player enters it, and
/// the player stays bound to that id until <see cref="Unbind"/>. Ids start above vmangos'
/// <c>RESERVED_INSTANCES_LAST</c> (100).
/// <para>
/// Scope (docs/areas/grid-terrain.md): bindings live in memory only, and every instance of a
/// map currently shares the map's single <see cref="Map"/> — separate per-instance maps,
/// resets and saved bindings arrive with the dungeon milestone.
/// </para>
/// <para>Thread affinity: world thread.</para>
/// </summary>
public sealed class InstanceRegistry
{
    /// <summary>vmangos <c>RESERVED_INSTANCES_LAST</c> (Map.h): ids up to this are reserved.</summary>
    public const uint ReservedInstancesLast = 100;

    private readonly Dictionary<(ulong Player, uint Map), uint> _bindings = [];
    private readonly Dictionary<uint, uint> _instanceMaps = [];
    private uint _nextInstanceId = ReservedInstancesLast + 1;

    /// <summary>Number of instances created so far.</summary>
    public int InstanceCount => _instanceMaps.Count;

    /// <summary>
    /// The instance a player enters on <paramref name="map"/>: 0 for a non-instanceable map,
    /// otherwise its existing binding or a newly created instance it is then bound to.
    /// </summary>
    public uint GetOrCreateInstance(Player player, MapTemplate map)
    {
        if (!map.Instanceable)
        {
            return 0;
        }

        if (_bindings.TryGetValue((player.Guid.Value, map.Entry), out uint bound))
        {
            return bound;
        }

        uint id = CreateInstance(map);
        _bindings[(player.Guid.Value, map.Entry)] = id;
        return id;
    }

    /// <summary>Create a new instance of an instanceable map (vmangos <c>MapManager::CreateInstance</c>).</summary>
    public uint CreateInstance(MapTemplate map)
    {
        if (!map.Instanceable)
        {
            throw new InvalidOperationException($"map {map.Entry} ({map.Name}) is not instanceable");
        }

        uint id = _nextInstanceId++;
        _instanceMaps[id] = map.Entry;
        return id;
    }

    /// <summary>The map an instance belongs to, or null for an unknown id.</summary>
    public uint? MapOf(uint instanceId) => _instanceMaps.TryGetValue(instanceId, out uint map) ? map : null;

    /// <summary>The player's binding on a map, or null.</summary>
    public uint? BindingOf(Player player, uint mapId)
        => _bindings.TryGetValue((player.Guid.Value, mapId), out uint id) ? id : null;

    /// <summary>Drop a player's binding on a map (vmangos <c>UnbindInstance</c>).</summary>
    public bool Unbind(Player player, uint mapId) => _bindings.Remove((player.Guid.Value, mapId));
}
