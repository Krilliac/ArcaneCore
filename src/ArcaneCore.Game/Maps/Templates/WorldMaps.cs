using System.Runtime.CompilerServices;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.WorldData;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Maps.Templates;

/// <summary>
/// The map services of one world: the map registry, terrain, area table and
/// the area-trigger / teleport-location tables. Attached to the <see cref="WorldRuntime"/>
/// without changing it (a weak side table), created with defaults on first use — continents
/// only, terrain from <see cref="WorldRuntimeOptions.Maps"/> — and filled from the world
/// database by <see cref="Load"/> (the world daemon's teleport feature does this at startup).
/// <para>Thread affinity: world thread (and startup, before the world thread runs).</para>
/// </summary>
public sealed class WorldMaps
{
    private static readonly ConditionalWeakTable<WorldRuntime, WorldMaps> Attached = new();

    private Dictionary<uint, AreaTriggerTemplate> _areaTriggers = [];
    private Dictionary<uint, AreaTriggerTeleport> _areaTriggerTeleports = [];
    private IReadOnlyList<GameTele> _gameTeles = [];

    private WorldMaps(WorldRuntime world, ILogger? logger)
    {
        Terrain = new TerrainManager(world.Options.Maps.DataDirectory, logger);
    }

    public MapRegistry Registry { get; private set; } = MapRegistry.Default;

    public TerrainManager Terrain { get; }

    public AreaTable Areas => Terrain.Areas;

    public IReadOnlyCollection<AreaTriggerTemplate> AreaTriggers => _areaTriggers.Values;

    public IReadOnlyList<GameTele> GameTeles => _gameTeles;

    /// <summary>The map services of <paramref name="world"/> (created with defaults on first use).</summary>
    public static WorldMaps Of(WorldRuntime world) => Attached.GetValue(world, w => new WorldMaps(w, null));

    /// <summary>
    /// Create the map services of <paramref name="world"/> with a logger (before anything else
    /// touched them, i.e. at startup). Returns the existing instance if there already is one.
    /// </summary>
    public static WorldMaps Attach(WorldRuntime world, ILogger logger) => Attached.GetValue(world, w => new WorldMaps(w, logger));

    /// <summary>
    /// Install the world database's map content, applying vmangos' loader rules
    /// (ObjectMgr::LoadAreaTriggerTeleports): a teleport row needs an area trigger row, a known
    /// target map and a non-zero target position. Returns the rows skipped, for logging.
    /// </summary>
    public IReadOnlyList<string> Load(MapContent content)
    {
        Registry = new MapRegistry(content.Maps);
        Terrain.Areas = new AreaTable(content.Areas, Registry);

        AreaTriggerTables tables = BuildAreaTriggerTables(content);
        _areaTriggers = tables.Triggers;
        _areaTriggerTeleports = tables.Teleports;
        _gameTeles = content.GameTeles.OrderBy(t => t.Id).ToArray();
        return tables.Skipped;
    }

    /// <summary>
    /// Build the area-trigger and teleport tables of <paramref name="content"/> without installing them
    /// (vmangos ObjectMgr::LoadAreaTriggerTeleports rules, checked against the current map registry), so
    /// a reload can prepare them off the world thread and swap them with <see cref="ReplaceAreaTriggerTables"/>.
    /// </summary>
    public AreaTriggerTables BuildAreaTriggerTables(MapContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        MapRegistry registry = Registry;
        Dictionary<uint, AreaTriggerTemplate> triggers = content.AreaTriggers.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());

        var skipped = new List<string>();
        var teleports = new Dictionary<uint, AreaTriggerTeleport>();
        foreach (AreaTriggerTeleport teleport in content.AreaTriggerTeleports)
        {
            if (!triggers.ContainsKey(teleport.Id))
            {
                skipped.Add($"areatrigger_teleport {teleport.Id}: no areatrigger_template row");
            }
            else if (!registry.Contains(teleport.TargetMap))
            {
                skipped.Add($"areatrigger_teleport {teleport.Id}: unknown target map {teleport.TargetMap}");
            }
            else if (teleport.TargetX == 0 && teleport.TargetY == 0 && teleport.TargetZ == 0)
            {
                skipped.Add($"areatrigger_teleport {teleport.Id}: target position is zero");
            }
            else
            {
                teleports[teleport.Id] = teleport;
            }
        }

        return new AreaTriggerTables(triggers, teleports, skipped);
    }

    /// <summary>Install tables from <see cref="BuildAreaTriggerTables"/> and return the ones replaced (world thread).</summary>
    public AreaTriggerTables ReplaceAreaTriggerTables(AreaTriggerTables tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        var previous = new AreaTriggerTables(_areaTriggers, _areaTriggerTeleports, []);
        _areaTriggers = tables.Triggers;
        _areaTriggerTeleports = tables.Teleports;
        return previous;
    }

    /// <summary>The number of area trigger teleports in force.</summary>
    public int AreaTriggerTeleportCount => _areaTriggerTeleports.Count;

    /// <summary>Replace the teleport locations (<c>game_tele</c>, ordered by id) and return the ones replaced (world thread).</summary>
    public IReadOnlyList<GameTele> ReplaceGameTeles(IEnumerable<GameTele> teles)
    {
        ArgumentNullException.ThrowIfNull(teles);
        IReadOnlyList<GameTele> previous = _gameTeles;
        _gameTeles = teles.OrderBy(t => t.Id).ToArray();
        return previous;
    }

    public AreaTriggerTemplate? FindAreaTrigger(uint id) => _areaTriggers.GetValueOrDefault(id);

    public AreaTriggerTeleport? FindAreaTriggerTeleport(uint id) => _areaTriggerTeleports.GetValueOrDefault(id);

    /// <summary>
    /// A teleport location by name (vmangos/cmangos <c>ObjectMgr::GetGameTele</c>): an exact
    /// case-insensitive match, else the first location (by id) whose name contains the text.
    /// </summary>
    public GameTele? FindGameTele(string name)
    {
        GameTele? partial = null;
        foreach (GameTele tele in _gameTeles)
        {
            if (string.Equals(tele.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return tele;
            }

            if (partial is null && tele.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                partial = tele;
            }
        }

        return partial;
    }
}

/// <summary>
/// Area triggers and their teleports, built from a <see cref="MapContent"/> (immutable once built;
/// <see cref="WorldMaps.BuildAreaTriggerTables"/>).
/// </summary>
public sealed class AreaTriggerTables
{
    internal AreaTriggerTables(
        Dictionary<uint, AreaTriggerTemplate> triggers,
        Dictionary<uint, AreaTriggerTeleport> teleports,
        IReadOnlyList<string> skipped)
    {
        Triggers = triggers;
        Teleports = teleports;
        Skipped = skipped;
    }

    internal Dictionary<uint, AreaTriggerTemplate> Triggers { get; }

    internal Dictionary<uint, AreaTriggerTeleport> Teleports { get; }

    /// <summary>The teleport rows the loader rejected, for logging.</summary>
    public IReadOnlyList<string> Skipped { get; }

    public int TriggerCount => Triggers.Count;

    public int TeleportCount => Teleports.Count;
}
