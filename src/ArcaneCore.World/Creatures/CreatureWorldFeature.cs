using ArcaneCore.Data.Npc;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Features;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Creatures;

/// <summary>
/// Creatures in the world daemon (an <see cref="IWorldFeature"/>, discovered): loads the creature
/// content from <see cref="ICreatureDataStore"/> when the world starts and attaches a
/// <see cref="CreatureMapSystem"/> to every map that has spawns (vmangos ObjectMgr::LoadCreatures
/// then per-map grid loading). Without a registered store the world simply has no creatures.
/// <para>Options come from the <c>Creatures</c> configuration section (<see cref="CreatureOptions"/>).</para>
/// <para>
/// Creature AI services (docs/integration/creature-ai.md): hostility from a registered
/// <see cref="ICreatureHostility"/>, else FactionTemplate.dbc (a registered
/// <see cref="FactionTemplateCatalog"/> or <c>Creatures:FactionTemplateDbcPath</c>; without one
/// nothing aggroes on sight); an optional <see cref="CreatureAiFactory"/> registration; creature
/// casts through the <see cref="SpellFeature"/>'s spell system when that feature is registered.
/// Paths and line of sight come from each map's collision services (<c>map.Collision</c>,
/// installed by the collision feature, feat/vmap-los).
/// </para>
/// </summary>
public sealed class CreatureWorldFeature(IServiceProvider services, ILogger<CreatureWorldFeature> logger,
    CreatureDisplayModelMetadataFeature? displayModelMetadata = null) : IWorldFeature
{
    private readonly Dictionary<uint, CreatureMapSystem> _systems = [];
    private readonly Dictionary<Map, CreatureMapSystem> _instanceSystems = new(ReferenceEqualityComparer.Instance);
    private CreatureContent _content = CreatureContent.Empty;
    private WorldRuntime? _world;
    private ICreatureHeightProvider? _height;
    private CreatureAiServices _aiServices = CreatureAiServices.Default;

    /// <summary>The loaded content (immutable; safe to read from any thread).</summary>
    public CreatureContent Content => Volatile.Read(ref _content);

    public CreatureOptions Options { get; } = new();

    /// <summary>The AI services every map's creature system uses (built at attach).</summary>
    public CreatureAiServices AiServices => _aiServices;

    /// <summary>
    /// Load the content and schedule the map systems (before the world thread starts). A store
    /// that fails to load stops the daemon (fail closed): a world without its creatures is not a
    /// state to run in silently.
    /// </summary>
    public void Attach(WorldRuntime world)
    {
        _world = world;
        world.PlayerDisplayModelResolver = displayId =>
        {
            CreatureModelInfo? addon = Content.FindModel(displayId);
            CreatureDisplayModelMetadata? dbc = displayModelMetadata?.Content.Find(displayId);
            if (addon is null && dbc is null) return null;
            return new ArcaneCore.Game.Spells.DisplayModelGeometry(
                dbc?.NativeScale ?? 1.0f, addon?.BoundingRadius ?? 0, addon?.CombatReach ?? 0,
                dbc?.CollisionHeight ?? 0, dbc?.ModelScale ?? 1.0f, dbc?.HasModelData ?? false);
        };
        services.GetService<IConfiguration>()?.GetSection(CreatureOptions.SectionName).Bind(Options);
        foreach (string name in Options.Rates.Normalize())
        {
            logger.LogError("{Section}:Rates:{Option} can't be negative. Using 1 instead.", CreatureOptions.SectionName, name);
        }

        _height = services.GetService<ICreatureHeightProvider>();
        _aiServices = BuildAiServices();

        CreatureContent content = CreatureContent.Empty;
        using (IServiceScope scope = services.CreateScope())
        {
            if (scope.ServiceProvider.GetService<ICreatureDataStore>() is { } store)
            {
                content = store.LoadAsync().GetAwaiter().GetResult();
            }
        }

        logger.LogInformation("Loaded {Templates} creature templates and {Spawns} spawns", content.TemplateCount, content.SpawnCount);
        EventAiCoverageReport aiCoverage = EventAiCoverage.Analyze(content.Ai.AllEvents);
        logger.LogInformation("EventAI type coverage: {Rows} rows, {Complete}/{Creatures} creature script keys complete; {Parameters} rows have unsupported parameters",
            aiCoverage.Rows, aiCoverage.CreaturesFullySupported, aiCoverage.CreaturesWithScripts, aiCoverage.RowsWithUnsupportedParameters);
        if (aiCoverage.UsedUnsupportedEventIds.Count != 0 || aiCoverage.UsedUnsupportedActionIds.Count != 0)
        {
            logger.LogWarning("EventAI used but unsupported IDs: events {Events}; actions {Actions}",
                string.Join(", ", aiCoverage.UsedUnsupportedEventIds.Select(pair => $"{pair.Key} ({pair.Value})")),
                string.Join(", ", aiCoverage.UsedUnsupportedActionIds.Select(pair => $"{pair.Key} ({pair.Value})")));
        }

        logger.LogInformation("EventAI unsupported reference IDs unused by loaded scripts: events {Events}; actions {Actions}",
            string.Join(", ", aiCoverage.UnusedUnsupportedEventIds), string.Join(", ", aiCoverage.UnusedUnsupportedActionIds));
        ReportWaypointSpawnsWithoutPath(content);
        world.MapCreated += OnMapCreated;
        world.MapUnloading += OnMapUnloading;
        world.Post(() => Install(content));
    }

    /// <summary>
    /// One warning for every waypoint spawn without a path (instead of one per creature and grid load): the count, the configured
    /// fallback (<c>Creatures:Movement:MissingWaypointPathFallback</c>) and the first spawn guids.
    /// </summary>
    private void ReportWaypointSpawnsWithoutPath(CreatureContent content)
    {
        IReadOnlyList<CreatureSpawn> missing = content.FindWaypointSpawnsWithoutPath();
        if (missing.Count == 0)
        {
            return;
        }

        const int Sample = 20;
        string guids = string.Join(", ", missing.Take(Sample).Select(s => $"{s.Guid} (entry {s.Entry})"));
        logger.LogWarning(
            "{Count} creature spawn(s) have waypoint movement but no creature_movement or creature_movement_template path; they fall back to {Fallback} movement (Creatures:Movement:MissingWaypointPathFallback). First {Shown}: {Guids}",
            missing.Count, Options.Movement.MissingWaypointPathFallback, Math.Min(Sample, missing.Count), guids);
    }

    /// <summary>
    /// Replace the content and attach a creature system to every map with spawns that has none
    /// yet (world thread). Maps already running keep their system.
    /// </summary>
    public void Install(CreatureContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Volatile.Write(ref _content, content);
        foreach (uint mapId in content.MapsWithSpawns)
        {
            GetOrCreateSystem(mapId);
        }
    }

    /// <summary>Every creature system, shared maps and dungeon instances (world thread, or after the world stopped).</summary>
    public IEnumerable<CreatureMapSystem> Systems => _systems.Values.Concat(_instanceSystems.Values);

    // The durable respawn times (CreatureRespawnFeature), when that feature is active; looked up when a system is made, which is after every feature attached.
    private CreatureRespawnFeature? RespawnFeature => services.GetService<CreatureRespawnFeature>();

    /// <summary>The creature system of a map's shared copy (instance 0), if it has one (world thread).</summary>
    public CreatureMapSystem? FindSystem(uint mapId) => _systems.GetValueOrDefault(mapId);

    /// <summary>
    /// The creature system of a map instance, if it has one (world thread). Dungeon instances
    /// (docs/integration/instances.md) each get their own system and spawns.
    /// </summary>
    public CreatureMapSystem? FindSystem(Map map) => map.InstanceId == 0 ? FindSystem(map.MapId) : _instanceSystems.GetValueOrDefault(map);

    /// <summary>The creature system of a map instance, attaching one if needed (world thread).</summary>
    public CreatureMapSystem GetOrCreateSystem(Map map)
    {
        if (map.InstanceId == 0)
        {
            return GetOrCreateSystem(map.MapId);
        }

        if (!_instanceSystems.TryGetValue(map, out CreatureMapSystem? system))
        {
            WorldRuntime world = _world ?? throw new InvalidOperationException("the creature feature is not attached");
            system = new CreatureMapSystem(
                map, Content, Options, _height, new Random(), () => world.NowMs, logger, _aiServices, RespawnFeature?.Persistence, RespawnFeature?.Clock,
                display => displayModelMetadata?.Content.Find(display));
            SubscribeSpellLifecycle(system);
            map.AddUpdater(system);
            _instanceSystems[map] = system;
        }

        return system;
    }

    /// <summary>The creature system of a map, attaching one if needed (world thread).</summary>
    public CreatureMapSystem GetOrCreateSystem(uint mapId)
    {
        WorldRuntime world = _world ?? throw new InvalidOperationException("the creature feature is not attached");
        if (!_systems.TryGetValue(mapId, out CreatureMapSystem? system))
        {
            Map map = world.GetMap(mapId);
            system = new CreatureMapSystem(
                map, Content, Options, _height, new Random(), () => world.NowMs, logger, _aiServices, RespawnFeature?.Persistence, RespawnFeature?.Clock,
                display => displayModelMetadata?.Content.Find(display));
            SubscribeSpellLifecycle(system);
            map.AddUpdater(system);
            _systems[mapId] = system;
        }

        return system;
    }

    // A new instance of a map with spawns gets its own creature system (spawns are per
    // instance, never shared); an unloaded instance drops it.
    private void OnMapCreated(Map map)
    {
        if (map.InstanceId != 0 && Content.GetSpawns(map.MapId).Count > 0)
        {
            GetOrCreateSystem(map);
        }
    }

    private void OnMapUnloading(Map map)
    {
        // With Creatures:Respawn:SaveImmediately off the dead creatures of an unloading instance are saved now (vmangos Map::Remove / ObjectGridUnloader).
        if (_instanceSystems.Remove(map, out CreatureMapSystem? system))
        {
            system.SaveRespawnTimes();
        }
    }

    private void SubscribeSpellLifecycle(CreatureMapSystem system)
    {
        // Resolve lazily: feature attachment order places creatures before the spell feature.
        // Both callbacks run on the world thread before the retained object starts its next life.
        system.CorpseRemoving += creature => services.GetService<SpellFeature>()?.System.OnCreatureCorpseRemoving(creature);
        system.Respawning += creature => services.GetService<SpellFeature>()?.System.OnCreatureRespawning(creature);
    }

    // The services are assembled (and bound from the container by reflection) in CreatureAiServicesBinder.
    private CreatureAiServices BuildAiServices() => CreatureAiServicesBinder.Build(services, Options);
}
