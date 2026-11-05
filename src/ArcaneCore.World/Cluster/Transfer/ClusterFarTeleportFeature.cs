using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Cluster.Control;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Features;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Cluster.Transfer;

/// <summary>
/// Turns a teleport to another worker's map into a cluster transfer (design _wf/handoff-control-DESIGN.md section 4.5, commit C10): in worker mode with <c>Cluster:Worker:Transfers:Enabled</c> and a non-empty
/// <c>Cluster:Worker:OwnedMaps</c> it builds the <see cref="GatewayClusterFarTeleportRouter"/>, hands it to the link host as the sink of the gateway's replies and notices
/// (<see cref="GatewayWorkerEndpoint"/> does that when it starts its host) and assigns it to <see cref="TeleportService.ClusterFarRouter"/> once the world runs.
/// <para>
/// Everything else (Combined and single-process worlds, a standby, a worker with the key off or no owned map list) leaves <see cref="Router"/> null and <c>ClusterFarRouter</c> unset, so the refusal of a teleport to an
/// unowned map in <see cref="TeleportService"/> stays exactly as it was. Setting the router also routes battleground entry and the passengers of a ship that moves to an unowned map through this path (design risk R7).
/// </para>
/// Ownership: the feature owns the router; it is a singleton with the world. <see cref="Attach"/> runs before the world thread starts and only builds objects; the assignment to the teleport service runs on the world thread
/// in the after-start hook, because <see cref="TeleportFeature"/> (which creates the service) attaches after this feature by type name.
/// </summary>
public sealed class ClusterFarTeleportFeature(IServiceProvider services) : IWorldFeature
{
    private GatewayClusterFarTeleportRouter? _router;

    /// <summary>The router, or null when this world does not route far teleports to other workers.</summary>
    public GatewayClusterFarTeleportRouter? Router => _router;

    /// <summary>Whether a world with these options routes far teleports: worker mode, not a standby, transfers on, a non-empty owned map list.</summary>
    internal static bool ShouldRoute(GatewayWorkerOptions worker, WorkerTransferControlOptions transfers) => ShouldRoute(worker, transfers, promoted: false);

    /// <summary>
    /// As <see cref="ShouldRoute(GatewayWorkerOptions, WorkerTransferControlOptions)"/>, for a world that may have been a standby: <c>Cluster:Worker:Standby</c> stays true in the options for the life of the process,
    /// so a standby that was <paramref name="promoted"/> (its features attach only then) routes like an active worker.
    /// </summary>
    internal static bool ShouldRoute(GatewayWorkerOptions worker, WorkerTransferControlOptions transfers, bool promoted)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(transfers);
        return worker.Enabled && (!worker.Standby || promoted) && transfers.Enabled && !string.IsNullOrWhiteSpace(worker.OwnedMaps);
    }

    /// <summary>
    /// Hands <paramref name="router"/> to the link host as the sink of the gateway's replies and notices. The endpoint resolves the router once, when it starts; a standby has none then (its features attach at
    /// promotion), so the promotion installs it here. A null host (the endpoint is stopped) is a no-op.
    /// </summary>
    internal static void InstallSubmitSink(GatewaySessionLinkHost? host, GatewayClusterFarTeleportRouter router)
    {
        ArgumentNullException.ThrowIfNull(router);
        if (host is not null)
        {
            host.SubmitSink = router;
        }
    }

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        GatewayWorkerOptions? worker = services.GetService<IOptions<GatewayWorkerOptions>>()?.Value;
        WorkerTransferControlOptions? transfers = services.GetService<IOptions<WorkerTransferControlOptions>>()?.Value;
        // A standby attaches its features only when WorldHost.PromoteAsync runs, after it cleared IsStandby: an attach with the standby option set is therefore a promoted standby.
        bool promoted = worker is { Standby: true } && services.GetServices<IHostedService>().OfType<WorldHost>().FirstOrDefault() is { IsStandby: false };
        if (worker is null || transfers is null || !ShouldRoute(worker, transfers, promoted))
        {
            return;
        }

        ILoggerFactory? loggers = services.GetService<ILoggerFactory>();
        _router = new GatewayClusterFarTeleportRouter(
            world,
            new FeatureTransferPlayerProbe(services, services.GetService<CharacterSaveQueue>()),
            player => FindGeneration(player),
            (generation, submit) => services.GetService<GatewayWorkerEndpoint>()?.Host is { } host && host.TrySendControlSubmit(generation, submit),
            loggers?.CreateLogger<GatewayClusterFarTeleportRouter>());
        services.GetService<IWorldLifecycleHooks>()?.Register(WorldLifecycleStage.AfterWorldStart, "cluster.far-teleport-router", ct => InstallAsync(world, ct));
    }

    /// <summary>The generation of the gateway session that carries <paramref name="player"/>, or null when it has none on this worker.</summary>
    private long? FindGeneration(Player player)
    {
        WorldGatewaySessionAcceptor? acceptor = services.GetService<GatewayWorkerEndpoint>()?.Acceptor;
        if (acceptor is null)
        {
            return null;
        }

        foreach (KeyValuePair<long, RemoteWorldSession> open in acceptor.Sessions)
        {
            if (ReferenceEquals(open.Value.Session, player.Session))
            {
                return open.Key;
            }
        }

        return null;
    }

    private async Task InstallAsync(WorldRuntime world, CancellationToken cancellationToken)
    {
        GatewayClusterFarTeleportRouter? router = _router;
        if (router is null)
        {
            return;
        }

        InstallSubmitSink(services.GetService<GatewayWorkerEndpoint>()?.Host, router);
        if (services.GetService<TeleportFeature>()?.TeleportsIfAttached is not { } teleports)
        {
            return;
        }

        try
        {
            await world.InvokeAsync(
                () =>
                {
                    teleports.ClusterFarRouter = router;
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            FaultLedger.Shared.Record("cluster.far-teleport-router", ex);
        }
    }
}
