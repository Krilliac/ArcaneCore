using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>Registers the world daemon's map feed (docs/areas/map-feed.md) and its action channel and web pipe (docs/areas/atlas-actions.md).</summary>
public static class WorldMapFeedServiceCollectionExtensions
{
    /// <summary>
    /// Bind <c>World:Control:MapFeed</c> (with its <c>Actions</c> and <c>Web</c> sections) and <c>World:Control:PacketTools</c> and add the map feed hosted service, which does nothing unless <c>Enabled</c> is set. Call
    /// after <c>AddWorldDaemon</c> (hosted services stop in reverse order, so the feed stops before the listener and the world). Only the
    /// daemon's production registration calls this.
    /// </summary>
    public static IServiceCollection AddWorldMapFeed(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WorldMapFeedOptions>(configuration.GetSection(WorldMapFeedOptions.SectionName));
        services.Configure<WorldPacketToolsOptions>(configuration.GetSection(WorldPacketToolsOptions.SectionName));
        services.Configure<MapFeedActionsOptions>(configuration.GetSection(MapFeedActionsOptions.SectionName));
        services.Configure<WebFeedPipeOptions>(configuration.GetSection(WebFeedPipeOptions.SectionName));
        services.AddSingleton<PacketToolsPermit>();
        services.AddMapFeedActions();
        services.AddMapFeedWebLogin();
        services.AddWebFeedPipe();
        services.AddHostedService<WorldMapFeed>();

        // After the feed (hosted services start in registration order and stop in reverse): the web pipe is served by the running feed.
        services.AddHostedService(provider => provider.GetRequiredService<WebFeedPipe>());
        return services;
    }

    /// <summary>
    /// Register the action channel (docs/areas/atlas-actions.md): the <c>action</c> and <c>caps</c> ops, their durable audit and the extension the
    /// feed resolves when it starts. The channel is off unless <c>World:Control:MapFeed:Actions:Enabled</c> is set, and takes web actors only when a
    /// host also registers an <c>IFeedActorTickets</c> service. Bind <see cref="MapFeedActionsOptions"/> separately (<see cref="AddWorldMapFeed"/> does).
    /// </summary>
    public static IServiceCollection AddMapFeedActions(this IServiceCollection services)
    {
        services.AddSingleton<MapFeedWebAudit>();
        services.AddSingleton<MapFeedActions>();
        services.AddSingleton<IFeedExtension>(provider => provider.GetRequiredService<MapFeedActions>());
        return services;
    }

    /// <summary>
    /// Register the separate web pipe (<see cref="WebFeedPipe"/>). It does nothing unless <c>World:Control:MapFeed:Web:Enabled</c> is set; the host
    /// starts it as a hosted service (<see cref="AddWorldMapFeed"/> does), after the feed. Bind <see cref="WebFeedPipeOptions"/> separately.
    /// </summary>
    public static IServiceCollection AddWebFeedPipe(this IServiceCollection services)
    {
        services.AddSingleton<WebFeedPipe>();
        return services;
    }
}
