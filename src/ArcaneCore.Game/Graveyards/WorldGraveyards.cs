using System.Runtime.CompilerServices;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Kernel.WorldData;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Graveyards;

/// <summary>
/// The graveyards of one world (a weak side table on the <see cref="WorldRuntime"/>, like <see cref="WorldMaps"/>): the content
/// the world database holds and the <see cref="GraveyardCatalog"/> built from it. The catalog is built when it is first
/// needed, not at startup, because the area table that checks the zone ids is filled by the map feature, which may attach after
/// the graveyard feature.
/// <para>Thread affinity: <see cref="Load"/> at startup, everything else on the world thread.</para>
/// </summary>
public sealed class WorldGraveyards
{
    private static readonly ConditionalWeakTable<WorldRuntime, WorldGraveyards> Attached = new();

    private readonly WorldRuntime _world;
    private GraveyardContent? _pending;
    private ILogger? _logger;
    private GraveyardCatalog _catalog = GraveyardCatalog.Empty;

    private WorldGraveyards(WorldRuntime world) => _world = world;

    /// <summary>The graveyards of <paramref name="world"/> (empty until <see cref="Load"/>).</summary>
    public static WorldGraveyards Of(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return Attached.GetValue(world, w => new WorldGraveyards(w));
    }

    /// <summary>The catalog in force; built from the loaded content on first use.</summary>
    public GraveyardCatalog Catalog
    {
        get
        {
            if (_pending is { } content)
            {
                _pending = null;
                _catalog = BuildLogged(content, _logger);
            }

            return _catalog;
        }
    }

    /// <summary>Install the world database's graveyard content (startup); the catalog is built on first use and its skipped rows logged then.</summary>
    public void Load(GraveyardContent content, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(content);
        _pending = content;
        _logger = logger;
    }

    /// <summary>Build a catalog from <paramref name="content"/> with the zone check of this world's area table (skipped when none is loaded).</summary>
    public GraveyardCatalog Build(GraveyardContent content, out IReadOnlyList<string> diagnostics)
    {
        AreaCheck(out Func<uint, bool>? exists);
        return GraveyardCatalog.Build(content, exists, out diagnostics);
    }

    /// <summary>Swap the catalog (a reload) and return the one replaced (world thread).</summary>
    public GraveyardCatalog Replace(GraveyardCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _pending = null;
        GraveyardCatalog previous = _catalog;
        _catalog = catalog;
        return previous;
    }

    private GraveyardCatalog BuildLogged(GraveyardContent content, ILogger? logger)
    {
        GraveyardCatalog catalog = Build(content, out IReadOnlyList<string> diagnostics);
        if (logger is not null)
        {
            foreach (string message in diagnostics)
            {
                logger.LogWarning("{Message}", message);
            }

            logger.LogInformation("loaded {Graveyards} graveyards and {Links} graveyard-zone links", catalog.SafeLocCount, catalog.LinkCount);
        }

        return catalog;
    }

    private void AreaCheck(out Func<uint, bool>? exists)
    {
        Maps.Terrain.AreaTable areas = WorldMaps.Of(_world).Areas;
        exists = areas.Count > 0 ? id => areas.GetById(id) is not null : null;
    }
}
