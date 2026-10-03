using ArcaneCore.Game.Maps.Collision.VMaps;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Maps.Collision;

/// <summary>Builds a world's collision services from <see cref="CollisionOptions"/> (the world daemon calls this at startup).</summary>
public static class CollisionServices
{
    /// <summary>
    /// Install the services the configuration and the data on disk allow. Missing directories
    /// keep the open defaults and are logged; nothing here throws for absent data.
    /// </summary>
    public static void Install(WorldCollision collision, CollisionOptions options, string? dataDirectory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(collision);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        collision.Install(CreateLineOfSight(options, dataDirectory, logger), CreatePathfinder(options, dataDirectory, logger));
    }

    /// <summary>The vmap reader, or null (keep the open default) when disabled or the directory is missing.</summary>
    public static ILineOfSight? CreateLineOfSight(CollisionOptions options, string? dataDirectory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        string? directory = options.ResolveVMapDirectory(dataDirectory);
        if (!options.EnableLineOfSight && !options.EnableHeight)
        {
            logger.LogInformation("Collision: vmaps disabled (World:Collision:EnableLineOfSight and EnableHeight are false); line of sight is open");
            return null;
        }

        if (directory is null || !Directory.Exists(directory))
        {
            logger.LogInformation("Collision: no vmap directory ({Directory}); line of sight is open and model heights are unknown", directory ?? "<not configured>");
            return null;
        }

        logger.LogInformation(
            "Collision: vmaps from {Directory} (line of sight {Los}, heights {Height})",
            directory, options.EnableLineOfSight ? "on" : "off", options.EnableHeight ? "on" : "off");
        return new VMapManager(directory, options.EnableLineOfSight, options.EnableHeight, logger);
    }

    /// <summary>The navmesh pathfinder, or null (keep straight-line paths) when disabled or the directory is missing.</summary>
    public static IPathfinder? CreatePathfinder(CollisionOptions options, string? dataDirectory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        string? directory = options.ResolveMMapDirectory(dataDirectory);
        logger.LogInformation("Collision: no navmesh reader yet (mmaps: {Directory}); paths are straight lines", directory ?? "<not configured>");
        return null;
    }
}
