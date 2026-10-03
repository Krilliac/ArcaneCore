using ArcaneCore.Game.WorldState.Weather;
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

    public TimeOptions TimeSettings { get; } = new();

    public WeatherOptions WeatherSettings { get; } = new();

    public GameEventOptions GameEventSettings { get; } = new();

    public WorldStatesOptions WorldStateSettings { get; } = new();

    /// <summary>The default pairs and providers of SMSG_INIT_WORLD_STATES.</summary>
    public States.WorldStateRegistry WorldStates { get; } = new();

    /// <summary>Where explored-zones changes go for persistence (null: not persisted).</summary>
    public Exploration.IExploredZonesSink? ExploredZonesSink { get; set; }

    public ExplorationOptions ExplorationSettings { get; } = new();

    /// <summary>The <c>exploration_basexp</c> table (empty until a feature loads it: exploration then gives 0 XP).</summary>
    public Exploration.ExplorationBaseXpTable ExplorationBaseXp
    {
        get => _baseXp;
        set => _baseXp = value ?? throw new ArgumentNullException(nameof(value));
    }

    private volatile Exploration.ExplorationBaseXpTable _baseXp = Exploration.ExplorationBaseXpTable.Empty;

    /// <summary>The explore check the zone tracker calls when a player moved (null: no exploration).</summary>
    public Exploration.IExplorationChecker? Explorer { get; set; }

    /// <summary>The loaded <c>game_weather</c> chances (empty until a feature loads them).</summary>
    public WeatherChanceTable WeatherChances { get; } = new();

    /// <summary>The weather random source (the shared RNG by default; tests script it).</summary>
    public IWeatherRandom WeatherRandom { get; set; } = SharedWeatherRandom.Instance;

    /// <summary>
    /// "Now" in the game's local zone (vmangos <c>localtime</c>): the server's zone by default, the
    /// configured <see cref="TimeOptions.TimeZoneId"/> if set, UTC when
    /// <see cref="TimeOptions.UseServerLocalTime"/> is false.
    /// </summary>
    public DateTimeOffset LocalNow()
    {
        TimeZoneInfo zone = !TimeSettings.UseServerLocalTime
            ? TimeZoneInfo.Utc
            : TimeSettings.TimeZoneId.Length == 0 ? Time.Zone : ResolveZone(TimeSettings.TimeZoneId);
        return TimeZoneInfo.ConvertTime(Time.UtcNow, zone);
    }

    private TimeZoneInfo? _namedZone;

    private TimeZoneInfo ResolveZone(string id)
    {
        if (_namedZone is null || !string.Equals(_namedZone.Id, id, StringComparison.Ordinal))
        {
            _namedZone = TimeZoneInfo.FindSystemTimeZoneById(id);
        }

        return _namedZone;
    }

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
