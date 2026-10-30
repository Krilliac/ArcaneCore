using System.Net;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    ILoggerFactory loggers,
    WorkerAdmission? admission = null) : IHostedService, IAsyncDisposable
{
    private GatewaySessionLinkHost? _host;

    /// <summary>The acceptor of the running listener (null before start): its open sessions.</summary>
    public WorldGatewaySessionAcceptor? Acceptor { get; private set; }

    /// <summary>Whether new sessions are admitted (null: always, as in a fixture that builds the endpoint by hand).</summary>
    public WorkerAdmission? Admission => admission;

    /// <summary>The running link host (null before start).</summary>
    public GatewaySessionLinkHost? Host => _host;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        GatewayWorkerOptions config = options.Value;
        ILogger logger = loggers.CreateLogger<GatewayWorkerEndpoint>();
        Acceptor = new WorldGatewaySessionAcceptor(scopes, opcodes, world, registry, sessionOptions.Value, loggers, admission);
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
        // Clear the field before disposing so the second call (DisposeAsync after the host's StopAsync) is a no-op
        // for the link host even when the first disposal threw.
        GatewaySessionLinkHost? host = _host;
        _host = null;
        WorldGatewaySessionAcceptor? acceptor = Acceptor;
        await StopInOrderAsync(
            host is null ? null : host.DisposeAsync,
            acceptor is null ? null : acceptor.StopAsync).ConfigureAwait(false);
    }

    /// <summary>Stop the link host first, then the acceptor; the acceptor is stopped even when the host's disposal throws. If both throw, both exceptions are kept (AggregateException).</summary>
    internal static async Task StopInOrderAsync(Func<ValueTask>? stopHost, Func<Task>? stopAcceptor)
    {
        Exception? hostFailure = null;
        try
        {
            if (stopHost is not null)
            {
                await stopHost().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            hostFailure = ex;
        }

        try
        {
            if (stopAcceptor is not null)
            {
                await stopAcceptor().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (hostFailure is not null)
        {
            throw new AggregateException("the link host and the acceptor both failed to stop", hostFailure, ex);
        }

        if (hostFailure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(hostFailure).Throw();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);
}

/// <summary>Registration of worker mode (the one call in <c>AddWorldDaemon</c>).</summary>
public static class GatewayWorkerServiceCollectionExtensions
{
    /// <summary>
    /// Bind <see cref="GatewayWorkerOptions"/> and, only when Cluster:Worker:Enabled is true, register the
    /// <see cref="GatewayWorkerEndpoint"/> and make character fencing mandatory (<see cref="Clustering.CharacterFencingOptions.RequiredByWorkerMode"/>).
    /// Off (the default) registers nothing else: Combined mode is unchanged.
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
            // Fencing is mandatory in worker mode: the host refuses to start without it, and the characters write fence is attached regardless.
            services.PostConfigure<Clustering.CharacterFencingOptions>(fencing => fencing.RequiredByWorkerMode = true);
            bool standby = section.GetValue<bool>(nameof(GatewayWorkerOptions.Standby));
            services.AddSingleton(new WorkerAdmission(standby ? WorkerAdmission.StandbyReason : null));
            if (standby)
            {
                AddStandby(services);
            }

            services.AddSingleton<GatewayWorkerEndpoint>();
            services.AddHostedService(sp => sp.GetRequiredService<GatewayWorkerEndpoint>());
        }

        return services;
    }

    /// <summary>
    /// Standby (Tier 3b): the world loads its content, maps and caches and links to the gateway, but it is not a writer until it is promoted.
    /// Its schema check runs validate-only (no DDL: the active world runs on that schema), it takes no characters writer file lock and claims no
    /// ownership (the active world holds both; fencing, mandatory in worker mode, refuses its characters writes), and it has no client listener of
    /// its own (World:Port belongs to the active world; clients reach a promoted standby through the gateway only).
    /// </summary>
    private static void AddStandby(IServiceCollection services)
    {
        services.PostConfigure<Data.DatabaseOptions>(database => database.Upgrade.ValidateOnly = true);
        services.RemoveAll<WorldWriterGuard>();
        // Keeps the database options but defers the lock: Acquire (WorldStartup, WorldHost.StartAsync) takes nothing while the active world holds it; WorldHost.PromoteAsync takes it.
        services.AddSingleton(sp => new WorldWriterGuard(sp.GetService<Microsoft.Extensions.Options.IOptions<Data.DatabaseOptions>>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<WorldWriterGuard>>(), deferUntilPromotion: true));
        ServiceDescriptor? listener = services.LastOrDefault(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(WorldServer));
        if (listener is not null)
        {
            services.Remove(listener);
        }
    }
}
