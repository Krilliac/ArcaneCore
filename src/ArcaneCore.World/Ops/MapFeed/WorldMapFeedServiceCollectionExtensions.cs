using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>Registers the world daemon's map feed (docs/areas/map-feed.md).</summary>
public static class WorldMapFeedServiceCollectionExtensions
{
    /// <summary>
    /// Bind <c>World:Control:MapFeed</c> and <c>World:Control:PacketTools</c> and add the map feed hosted service, which does nothing unless <c>Enabled</c> is set. Call
    /// after <c>AddWorldDaemon</c> (hosted services stop in reverse order, so the feed stops before the listener and the world). Only the
    /// daemon's production registration calls this.
    /// </summary>
    public static IServiceCollection AddWorldMapFeed(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WorldMapFeedOptions>(configuration.GetSection(WorldMapFeedOptions.SectionName));
        services.Configure<WorldPacketToolsOptions>(configuration.GetSection(WorldPacketToolsOptions.SectionName));
        services.AddSingleton<PacketToolsPermit>();
        services.AddHostedService<WorldMapFeed>();
        return services;
    }
}
