using System.Runtime.CompilerServices;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.World.Collision;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// The world's <see cref="PlayerbotCapitalCatalog"/>, built once in the background the first time a bot asks and rebuilt when its sources change
/// (the creature definitions, the area table, the game objects, the faction templates). One instance per world, attached without changing
/// <see cref="WorldRuntime"/> (a weak side table, as <see cref="WorldMaps"/>).
/// <para>
/// Threading: <see cref="Current"/> and <see cref="Install"/> are world-thread calls. The build runs on a pool thread and reads only content that is
/// immutable once installed (the creature definitions are swapped, never edited) and the terrain through its detached reader, which never touches
/// the world thread's tile cache; its result is posted back and taken on the world thread. While a build runs the previous catalog (or none) is
/// returned, so a bot never waits and no think scans spawns. Cost: one pass over the capital maps' service spawns and a few hundred area lookups,
/// once per source change.
/// </para>
/// </summary>
internal sealed class PlayerbotCapitals
{
    private static readonly ConditionalWeakTable<WorldRuntime, PlayerbotCapitals> Attached = new();

    private sealed record Sources(CreatureContent Creatures, int Version, AreaTable Areas, GameObjectContent? Objects, FactionTemplateCatalog? Factions)
    {
        internal bool Same(CreatureContent creatures, AreaTable areas, GameObjectContent? objects, FactionTemplateCatalog? factions)
            => ReferenceEquals(Creatures, creatures) && Version == creatures.DefinitionsVersion && ReferenceEquals(Areas, areas)
                && ReferenceEquals(Objects, objects) && ReferenceEquals(Factions, factions);
    }

    private Sources? _sources;
    private PlayerbotCapitalCatalog? _catalog;
    private PlayerbotCapitalCatalog? _installed;
    private object? _building;

    private PlayerbotCapitals()
    {
    }

    internal static PlayerbotCapitals Of(WorldRuntime world) => Attached.GetValue(world, _ => new PlayerbotCapitals());

    /// <summary>
    /// Where a position stands, (zone, area, with the height): tests install their own; the default is a <see cref="PlayerbotCapitalLocator"/>
    /// (the WMO group the position is inside, else the terrain's area flag; (0, 0) without terrain data). Called on the building thread.
    /// </summary>
    internal Func<uint, float, float, float, (uint ZoneId, uint AreaId)>? Locator { get; set; }

    /// <summary>Whether a build is running (tests).</summary>
    internal bool Building => _building is not null;

    /// <summary>Use a ready-made catalog and never build (tests).</summary>
    internal void Install(PlayerbotCapitalCatalog catalog) => _installed = catalog;

    /// <summary>
    /// The catalog for the world <paramref name="session"/> is on, or null while the first build runs, when no terrain data is loaded
    /// (nothing can be located) or when the build failed. Starts the build if the sources are new. Allocation-free while they are not.
    /// </summary>
    internal PlayerbotCapitalCatalog? Current(WorldSession session, PlayerbotCityOptions city)
    {
        if (_installed is not null) return _installed;
        CreatureContent? creatures = session.Services.GetService<CreatureWorldFeature>()?.Content;
        if (creatures is null) return _catalog;
        WorldMaps maps = WorldMaps.Of(session.World);
        GameObjectContent? objects = session.Services.GetService<GameObjectLootFeature>()?.Content;
        FactionTemplateCatalog? factions = session.Services.GetService<QuestNpcFeature>()?.FactionTemplates;
        if (_sources is { } known && known.Same(creatures, maps.Areas, objects, factions)) return _catalog;
        var sources = new Sources(creatures, creatures.DefinitionsVersion, maps.Areas, objects, factions);
        _sources = sources;
        if (Locator is null && !maps.Terrain.Enabled) return _catalog;
        string? vmaps = session.Services.GetService<CollisionFeature>()?.Options.ResolveVMapDirectory(session.World.Options.Maps.DataDirectory);
        Start(session, sources, maps.Terrain, vmaps, city.WmoAreaTableDbcPath);
        return _catalog;
    }

    private void Start(WorldSession session, Sources sources, TerrainManager terrain, string? vmapDirectory, string wmoAreaTablePath)
    {
        var token = new object();
        _building = token;
        WorldRuntime world = session.World;
        ILogger? logger = session.Services.GetService<ILoggerFactory>()?.CreateLogger<PlayerbotCapitals>();
        Func<uint, float, float, float, (uint ZoneId, uint AreaId)>? custom = Locator;
        _ = Task.Run(() =>
        {
            PlayerbotCapitalCatalog? built = null;
            bool wmoAreas = custom is not null;
            try
            {
                Func<uint, float, float, float, (uint ZoneId, uint AreaId)> locate;
                if (custom is not null) locate = custom;
                else
                {
                    var locator = PlayerbotCapitalLocator.Create(terrain, sources.Areas, vmapDirectory, wmoAreaTablePath, logger);
                    wmoAreas = locator.HasWmoAreas;
                    locate = locator.Locate;
                }

                built = PlayerbotCapitalCatalog.Build(sources.Areas.All, sources.Creatures, sources.Objects, sources.Factions, locate);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger?.LogWarning(ex, "Playerbot capital catalog: the build failed; bots make no capital city trips until the content changes");
            }

            world.Post(() =>
            {
                if (!ReferenceEquals(_building, token)) return;
                _building = null;
                if (built is null) return;
                _catalog = built;
                logger?.LogInformation("Playerbot capital catalog: {Capitals} capital cities ({Names}), {Spawns} service spawns",
                    built.Capitals.Count, string.Join(", ", built.Capitals.Select(c => c.Name)), built.Capitals.Sum(c => c.Spawns.Count));
                if (!wmoAreas)
                    logger?.LogWarning("Playerbot capital catalog: no WMO area data (vmaps with World:Collision and World:Playerbots:City:WmoAreaTableDbcPath, or ClientData:DbcDirectory), so cities built as one model (Ironforge, Undercity) are not found; "
                        + "found: {Names}", string.Join(", ", built.Capitals.Select(c => c.Name)));
            });
        });
    }
}
