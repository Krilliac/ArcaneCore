using System.Collections.Concurrent;

namespace ArcaneCore.Game;

/// <summary>
/// Shared, process-wide world state: the live maps and their players. Registered as a
/// singleton so every world session shares it. This is also the seam where, later, a
/// clustered world would distribute maps across processes.
/// </summary>
public sealed class WorldState
{
    private readonly ConcurrentDictionary<uint, Map> _maps = new();

    public Map GetMap(uint mapId) => _maps.GetOrAdd(mapId, id => new Map(id));
}
