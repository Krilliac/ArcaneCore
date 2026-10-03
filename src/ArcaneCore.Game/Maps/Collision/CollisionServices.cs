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

        string? vmaps = options.ResolveVMapDirectory(dataDirectory);
        string? mmaps = options.ResolveMMapDirectory(dataDirectory);
        logger.LogInformation(
            "Collision: no vmap reader installed yet (vmaps: {VMaps}, mmaps: {MMaps}); line of sight is open and paths are straight lines",
            vmaps ?? "<not configured>", mmaps ?? "<not configured>");
    }
}
