using System.Net;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Cluster.Gateway;

/// <summary>
/// The worker-mode listener (Cluster:Worker): a <see cref="GatewaySessionLinkHost"/> whose sessions are
/// <see cref="RemoteWorldSession"/>s of this world. Registered after <see cref="WorldServer"/>, so it starts after the world
/// host and stops (closing every gateway-backed session and waiting for it) before the world host drains its stores.
/// </summary>
public sealed class GatewayWorkerEndpoint(
    IServiceScopeFactory scopes,
    IOptions<GatewayWorkerOptions> options,
    IOptions<WorldSessionOptions> sessionOptions,
    OpcodeTable opcodes,
    WorldRuntime world,
    SessionRegistry registry,
    ILoggerFactory loggers) : IHostedService, IAsyncDisposable
{
    private GatewaySessionLinkHost? _host;

    /// <summary>The acceptor of the running listener (null before start): its open sessions.</summary>
    public WorldGatewaySessionAcceptor? Acceptor { get; private set; }

    /// <summary>The running link host (null before start).</summary>
    public GatewaySessionLinkHost? Host => _host;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        GatewayWorkerOptions config = options.Value;
        ILogger logger = loggers.CreateLogger<GatewayWorkerEndpoint>();
        Acceptor = new WorldGatewaySessionAcceptor(scopes, opcodes, world, registry, sessionOptions.Value, loggers);
        // Owner epoch: the fenced ownership token's epoch when fencing holds one, else 1 (unfenced single node), as the
        // readiness probe reports it. A change after a takeover makes every older packet stale.
        _host = new GatewaySessionLinkHost(
            config.ToManifest(world.CharacterOwnership?.Incarnation ?? Guid.NewGuid()),
            Acceptor,
            () => world.CharacterOwnership is { Epoch: > 0 } token ? token.Epoch : 1,
            config.LinkSecret,
            new IPEndPoint(IPAddress.Parse(config.BindAddress), config.Port),
            logger);
        _host.Start();
        logger.LogInformation("Worker mode: gateway link listening on {Endpoint}", _host.BoundEndPoint);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_host is { } host)
        {
            await host.DisposeAsync().ConfigureAwait(false);
            _host = null;
        }

        if (Acceptor is { } acceptor)
        {
            await acceptor.StopAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);
}

/// <summary>Registration of worker mode (the one call in <c>AddWorldDaemon</c>).</summary>
public static class GatewayWorkerServiceCollectionExtensions
{
    /// <summary>
    /// Bind <see cref="GatewayWorkerOptions"/> and, only when Cluster:Worker:Enabled is true, register the
    /// <see cref="GatewayWorkerEndpoint"/>. Off (the default) registers nothing else: Combined mode is unchanged.
    /// Call after <c>AddHostedService&lt;WorldServer&gt;()</c> so the endpoint stops before the world host.
    /// </summary>
    public static IServiceCollection AddGatewayWorker(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        IConfigurationSection section = configuration.GetSection(GatewayWorkerOptions.SectionName);
        services.Configure<GatewayWorkerOptions>(section);
        if (section.GetValue<bool>(nameof(GatewayWorkerOptions.Enabled)))
        {
            services.AddSingleton<GatewayWorkerEndpoint>();
            services.AddHostedService(sp => sp.GetRequiredService<GatewayWorkerEndpoint>());
        }

        return services;
    }
}
