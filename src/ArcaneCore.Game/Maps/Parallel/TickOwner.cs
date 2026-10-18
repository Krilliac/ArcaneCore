namespace ArcaneCore.Game.Maps;

/// <summary>
/// Which map the current thread is updating right now, if any (docs/research/multithreading-plan-20261017.md, W2.1). Set by
/// <see cref="WorldRuntime"/> around every <see cref="Map.Update(uint)"/> of the maps phase, on the world thread and on the map
/// workers alike; null outside a map's update. A map is updated by exactly one thread per tick, so "the map I am updating" is
/// the ownership question every guard asks.
/// </summary>
public static class TickOwner
{
    [ThreadStatic] private static Map? t_map;
    [ThreadStatic] private static WorldRuntime? t_world;

    /// <summary>The map this thread is updating, or null.</summary>
    public static Map? CurrentMap => t_map;

    /// <summary>The world whose map this thread is updating, or null.</summary>
    public static WorldRuntime? CurrentWorld => t_world;

    /// <summary>Make this thread the owner of <paramref name="map"/> until the scope is disposed (nests: the previous owner comes back).</summary>
    internal static Scope Enter(WorldRuntime world, Map map)
    {
        var scope = new Scope(t_world, t_map);
        t_world = world;
        t_map = map;
        return scope;
    }

    internal readonly struct Scope(WorldRuntime? previousWorld, Map? previousMap) : IDisposable
    {
        public void Dispose()
        {
            t_world = previousWorld;
            t_map = previousMap;
        }
    }
}
