using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Peer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Cluster.Social;

/// <summary>
/// What the <see cref="PeerLinkHostedService"/> publishes for the rest of the process: the <see cref="PeerPresenceClient"/> over this node's <see cref="PeerLinkClient"/>
/// (null until the link is built, or when the peer link is not configured). Read by <see cref="LeaseRoutedPresence"/> on every call. Thread-safe (a volatile field).
/// </summary>
public sealed class PeerLinkRuntime
{
    private PeerPresenceClient? _presence;

    /// <summary>The presence client over the peer link to the Social holder; null while there is no link.</summary>
    public PeerPresenceClient? Presence
    {
        get => Volatile.Read(ref _presence);
        internal set => Volatile.Write(ref _presence, value);
    }
}

/// <summary>
/// The production wiring of the peer link for realm presence (cluster mode only, i.e. with a <see cref="RealmAuthorityLeaseKeeper"/>): every node runs one
/// <see cref="PeerLinkClient"/> (located through the lease store and <c>Cluster:Peer:Nodes</c>, so it dials the Social holder and never itself), and the node that holds the
/// Social or Economy lease also runs a <see cref="PeerLinkHost"/> on <c>Cluster:Peer:BindAddress:Port</c> whose <see cref="PeerLinkHost.PayloadHandler"/> is
/// <see cref="PresenceHolderService.Handle"/>. A small poll loop (every <see cref="PeerLinkOptions.HolderPollMs"/>) starts the host when the lease is gained and disposes it when the
/// lease is lost, so a failover moves the listener with the lease. Nothing starts without the secret in <see cref="PeerLinkOptions.SecretEnvironmentVariable"/> and valid
/// <c>Cluster:Peer</c> options: the node then logs a warning and cross-worker presence stays unavailable (the publisher keeps retrying).
/// <para>Thread affinity: none; the poll loop and the link tasks run on the thread pool. Ownership: this service owns the client, the host and the poll task and disposes them in StopAsync.</para>
/// </summary>
public sealed class PeerLinkHostedService(
    IOptions<PeerLinkOptions> peerOptions,
    IOptions<GatewayWorkerOptions> workerOptions,
    RealmAuthorityLeaseKeeper keeper,
    IRealmAuthorityLeaseStore store,
    PresenceHolderService holder,
    PeerLinkRuntime runtime,
    IServiceScopeFactory scopes,
    WorldRuntime world,
    ILoggerFactory loggers) : IHostedService
{
    private readonly ILogger _logger = loggers.CreateLogger<PeerLinkHostedService>();
    private readonly CancellationTokenSource _stop = new();
    private PeerLinkClient? _client;
    private PeerLinkHost? _host;
    private Task _poll = Task.CompletedTask;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        PeerLinkOptions options = peerOptions.Value;
        string[] problems = [.. options.Validate()];
        if (problems.Length > 0)
        {
            _logger.LogWarning("Cluster:Peer is invalid ({Problems}); the peer link is not started and presence of other workers is unavailable", string.Join("; ", problems));
            return;
        }

        string? secret = PeerLinkOptions.ReadSecretFromEnvironment();
        if (secret is null)
        {
            _logger.LogWarning(
                "{Variable} is unset or shorter than {Min} characters; the peer link is not started and presence of other workers is unavailable",
                PeerLinkOptions.SecretEnvironmentVariable, PeerLinkOptions.MinSecretLength);
            return;
        }

        GatewayWorkerOptions worker = workerOptions.Value;
        GatewayManifestVersions versions;
        if (worker.VersionsFromDatabase)
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            versions = await GatewayManifestVersions.ReadAsync(
                worker, scope.ServiceProvider.GetRequiredService<CharacterDbContext>(), scope.ServiceProvider.GetRequiredService<WorldDbContext>(),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            versions = GatewayManifestVersions.Resolve(worker, null, null);
        }

        ClusterNodeManifest manifest = worker.ToManifest(world.CharacterOwnership?.Incarnation ?? Guid.NewGuid(), versions);
        var leases = new KeeperPeerLeaseSource(keeper);
        var locator = new PeerHolderLocator(store, options);
        var client = new PeerLinkClient(options, secret, manifest, leases, locator.Locate, loggers.CreateLogger<PeerLinkClient>());
        client.Start();
        _client = client;
        runtime.Presence = new PeerPresenceClient(client);
        _poll = Task.Run(() => PollAsync(options, secret, manifest, leases, _stop.Token), CancellationToken.None);
        _logger.LogInformation("Peer link: client started (holder found through the lease store and Cluster:Peer:Nodes)");
    }

    private async Task PollAsync(PeerLinkOptions options, string secret, ClusterNodeManifest manifest, IPeerLeaseSource leases, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                bool holds = PeerLinkHost.HoldsPeerScope(leases);
                if (holds && _host is null)
                {
                    var host = new PeerLinkHost(options, secret, manifest, leases, loggers.CreateLogger<PeerLinkHost>())
                    {
                        PayloadHandler = holder.Handle,
                    };
                    host.Start();
                    _host = host;
                    _logger.LogInformation("Peer link: this node holds a peer scope; listening on {Endpoint}", host.LocalEndPoint);
                }
                else if (!holds && _host is not null)
                {
                    PeerLinkHost old = _host;
                    _host = null;
                    await old.DisposeAsync().ConfigureAwait(false);
                    _logger.LogInformation("Peer link: this node no longer holds a peer scope; listener stopped");
                }
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
            {
                _logger.LogFault("cluster.peerlink.host-poll", ex, "peer link host start/stop failed; retrying");
            }

            try
            {
                await Task.Delay(options.HolderPoll, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _poll.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        runtime.Presence = null;
        PeerLinkHost? host = _host;
        _host = null;
        if (host is not null)
        {
            await host.DisposeAsync().ConfigureAwait(false);
        }

        PeerLinkClient? client = _client;
        _client = null;
        if (client is not null)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>Registration of the peer link for presence (cluster mode only).</summary>
public static class ClusterPeerLinkHosting
{
    /// <summary>
    /// Binds <see cref="PeerLinkOptions"/> (<c>Cluster:Peer</c>) and registers <see cref="PeerLinkRuntime"/> always (so <see cref="LeaseRoutedPresence"/> can read it); the hosted
    /// <see cref="PeerLinkHostedService"/> is registered only when a <see cref="RealmAuthorityLeaseKeeper"/> is registered, i.e. Cluster:Node:Role is not Combined. Call after
    /// <see cref="ClusterNodeHosting.AddClusterNodeRole"/> and <see cref="ClusterPresenceHosting.AddClusterPresence"/>.
    /// </summary>
    public static IServiceCollection AddClusterPeerLink(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.Configure<PeerLinkOptions>(configuration.GetSection(PeerLinkOptions.SectionName));
        services.TryAddSingleton<PeerLinkRuntime>();
        if (services.Any(d => d.ServiceType == typeof(RealmAuthorityLeaseKeeper)))
        {
            services.AddHostedService<PeerLinkHostedService>();
        }

        return services;
    }
}
