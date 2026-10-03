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
public sealed class CreatureWorldFeature(IServiceProvider services, ILogger<CreatureWorldFeature> logger) : IWorldFeature
{
    private readonly Dictionary<uint, CreatureMapSystem> _systems = [];
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
        services.GetService<IConfiguration>()?.GetSection(CreatureOptions.SectionName).Bind(Options);
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
        world.Post(() => Install(content));
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

    /// <summary>The creature system of a map, if it has one (world thread).</summary>
    public CreatureMapSystem? FindSystem(uint mapId) => _systems.GetValueOrDefault(mapId);

    /// <summary>The creature system of a map, attaching one if needed (world thread).</summary>
    public CreatureMapSystem GetOrCreateSystem(uint mapId)
    {
        WorldRuntime world = _world ?? throw new InvalidOperationException("the creature feature is not attached");
        if (!_systems.TryGetValue(mapId, out CreatureMapSystem? system))
        {
            Map map = world.GetMap(mapId);
            system = new CreatureMapSystem(map, Content, Options, _height, new Random(), () => world.NowMs, logger, _aiServices);
            map.AddUpdater(system);
            _systems[mapId] = system;
        }

        return system;
    }

    private CreatureAiServices BuildAiServices()
    {
        ICreatureHostility hostility = services.GetService<ICreatureHostility>()
            ?? new FactionCreatureHostility(services.GetService<FactionTemplateCatalog>()
                ?? (string.IsNullOrWhiteSpace(Options.FactionTemplateDbcPath)
                    ? FactionTemplateCatalog.Empty
                    : FactionTemplateDbcReader.Load(Options.FactionTemplateDbcPath)));
        SpellFeature? spells = services.GetService<SpellFeature>();
        return new CreatureAiServices
        {
            Hostility = hostility,
            Spells = spells is null ? null : new SpellSystemCreatureCaster(spells.System),
            Factory = services.GetService<CreatureAiFactory>() ?? new CreatureAiFactory(),
        };
    }
}
