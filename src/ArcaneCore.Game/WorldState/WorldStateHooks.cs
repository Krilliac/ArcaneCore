using System.Runtime.CompilerServices;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.WorldState.Time;
using ArcaneCore.Game.WorldState.Zones;

namespace ArcaneCore.Game.WorldState;

/// <summary>
/// The per-world seams of the world-state area (weather, exploration, zone tracking, game
/// time), reached the way <c>CombatHooks.For</c> is: world features fill it in when they attach,
/// the map systems in this assembly read it. One instance per <see cref="WorldRuntime"/>.
/// </summary>
public sealed class WorldStateHooks
{
    private static readonly ConditionalWeakTable<WorldRuntime, WorldStateHooks> s_hooks = new();

    private readonly List<IPlayerLocationListener> _listeners = [];
    private readonly WorldRuntime _world;
    private IZoneLocator? _locator;

    private WorldStateHooks(WorldRuntime world) => _world = world;

    /// <summary>The hooks of <paramref name="world"/> (created with retail defaults on first use).</summary>
    public static WorldStateHooks For(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return s_hooks.GetValue(world, w => new WorldStateHooks(w));
    }

    public ZoneOptions Zones { get; } = new();

    /// <summary>The clock weather seasons and game events read (the real server-local clock by default).</summary>
    public IGameTime Time { get; set; } = SystemGameTime.Instance;

    /// <summary>The zone/area source; the terrain and area table by default.</summary>
    public IZoneLocator Locator
    {
        get => _locator ??= new TerrainZoneLocator(_world);
        set => _locator = value;
    }

    /// <summary>The registered location listeners, in call order.</summary>
    public IReadOnlyList<IPlayerLocationListener> LocationListeners => _listeners;

    /// <summary>Register a listener. Order: <see cref="IPlayerLocationListener.Order"/>, then registration order.</summary>
    public void AddLocationListener(IPlayerLocationListener listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        if (_listeners.Contains(listener))
        {
            return;
        }

        _listeners.Add(listener);
        // List.Sort is not stable, so order by (Order, registration index).
        IPlayerLocationListener[] ordered = _listeners.Select((l, i) => (l, i)).OrderBy(t => t.l.Order).ThenBy(t => t.i).Select(t => t.l).ToArray();
        _listeners.Clear();
        _listeners.AddRange(ordered);
    }

    /// <summary>
    /// Whether the zone comes from the client / stored value instead of terrain right now
    /// (<see cref="ClientZoneTrust"/>).
    /// </summary>
    public bool UsesClientZone => Zones.ClientZoneTrust switch
    {
        ClientZoneTrust.Always => true,
        ClientZoneTrust.Never => false,
        _ => !Locator.CanDeriveZones,
    };
}
