using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.World.Cluster.Control;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Handoff;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Cluster.Transfer;

/// <summary>
/// Production registration of the worker end of the unified control channel (design _wf/handoff-control-DESIGN.md sections 2.6 and 4.2): the
/// <see cref="WorkerControlService"/> and, with <c>Cluster:Worker:Transfers:Enabled</c>, this worker's production transfer participants
/// (<see cref="WorkerTransferSource"/> over the save queue, <see cref="WorkerTransferTarget"/> over the adopted gateway session). Registered only in worker mode
/// (<c>Cluster:Worker:Enabled</c>) and only when at least one of the two opt-in keys is on; with both off (the default) nothing is registered and the link host
/// answers every control request <see cref="ControlStatus.Disabled"/>. Combined and single-process worlds register nothing.
/// <para>
/// The service is built lazily, when <see cref="GatewayWorkerEndpoint"/> starts its link host: by then every hosted service exists, so the world host can be looked up
/// without creating a cycle (the same lookup the endpoint uses for the link incarnation).
/// </para>
/// </summary>
public static class WorkerTransferHosting
{
    /// <summary>How many transfers a target holds a reservation for at once (the <see cref="WorkerTransferTarget"/> default).</summary>
    public const int MaxTargetReservations = 64;

    /// <summary>Binds the two opt-in keys and, in worker mode with a key on, registers the <see cref="WorkerControlService"/>. <c>TryAdd</c>: one registered earlier (a test) wins.</summary>
    public static IServiceCollection AddWorkerControl(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        IConfigurationSection handoff = configuration.GetSection(WorkerHandoffControlOptions.SectionName);
        IConfigurationSection transfers = configuration.GetSection(WorkerTransferControlOptions.SectionName);
        services.Configure<WorkerHandoffControlOptions>(handoff);
        services.Configure<WorkerTransferControlOptions>(transfers);

        bool workerMode = configuration.GetSection(GatewayWorkerOptions.SectionName).GetValue<bool>(nameof(GatewayWorkerOptions.Enabled));
        bool handoffOn = handoff.GetValue<bool>(nameof(WorkerHandoffControlOptions.Enabled));
        bool transfersOn = transfers.GetValue<bool>(nameof(WorkerTransferControlOptions.Enabled));
        if (!workerMode || (!handoffOn && !transfersOn))
        {
            return services;
        }

        services.TryAddSingleton(sp => Build(sp, handoffOn, transfersOn));
        return services;
    }

    private static WorkerControlService Build(IServiceProvider services, bool handoffOn, bool transfersOn)
    {
        GatewayWorkerOptions options = services.GetRequiredService<IOptions<GatewayWorkerOptions>>().Value;
        WorldHost host = services.GetServices<IHostedService>().OfType<WorldHost>().FirstOrDefault()
            ?? throw new InvalidOperationException("worker control needs the world host (Cluster:Worker:Handoff:Enabled / Cluster:Worker:Transfers:Enabled)");
        WorldRuntime world = services.GetRequiredService<WorldRuntime>();
        ILoggerFactory? loggers = services.GetService<ILoggerFactory>();
        var nodeId = new WorkerNodeId(options.NodeId);
        bool standby = options.Standby;

        WorkerControlHandoff? handoff = null;
        if (handoffOn && services.GetService<IWorldHandoffStore>() is { } handoffJournal)
        {
            // An active worker is the draining source, a standby the target (a promoted standby stays addressable as the target of its own handoff).
            handoff = new WorkerControlHandoff(
                handoffJournal,
                standby ? null : services.GetService<IWorldHandoffSource>(),
                standby ? new StandbyWorldHandoffTarget(nodeId, host) : null);
        }

        WorkerControlTransfer? transfer = null;
        if (transfersOn && !standby && services.GetService<ITransferDecisionStore>() is { } transferJournal)
        {
            CharacterSaveQueue saves = services.GetRequiredService<CharacterSaveQueue>();
            WorkerAdmission admission = services.GetRequiredService<WorkerAdmission>();
            var source = new WorkerTransferSource(
                nodeId, world, new SaveQueueTransferPersistence(saves, services.GetService<CharacterPersistenceBarrier>()), new FeatureTransferPlayerProbe(services, saves),
                logger: loggers?.CreateLogger<WorkerTransferSource>());
            var target = new WorkerTransferTarget(
                nodeId, world, new CharacterHooksShadowLoader(bundle => AdoptedSession(services, bundle)), new WorldSessionTransferEntry(), admission,
                MaxTargetReservations, host.Incarnation, loggers?.CreateLogger<WorkerTransferTarget>(),
                instances: AllocateInstance(services));
            transfer = new WorkerControlTransfer(transferJournal, source, target, () => admission.IsOpen, () => MaxTargetReservations - target.ReservedCount);
        }

        WorkerControlIdentity Identity() => new(
            nodeId,
            host.IsStandby,
            host.Incarnation,
            host.OwnerNode,
            world.CharacterOwnership?.Scope ?? string.Empty,
            host.WorkerOwnedMaps is { } maps ? maps.Order().Take(ClusterControlWire.MaxOwnedMaps).ToArray() : Array.Empty<uint>());

        return new WorkerControlService(handoffOn, handoff, transfersOn, transfer, Identity, loggers?.CreateLogger<WorkerControlService>());
    }

    /// <summary>
    /// Q8: the allocator an instance-0 request for a dungeon uses (an instance reserved on this worker, bound at activation, released on abort), over the instance feature; null without the feature,
    /// so the requested id stands. <see cref="WorkerTransferTarget"/> runs it on the world thread.
    /// </summary>
    private static ITransferInstanceAllocator? AllocateInstance(IServiceProvider services)
    {
        ArcaneCore.World.Instances.InstanceFeature? feature = services.GetService<ArcaneCore.World.Instances.InstanceFeature>();
        return feature is null ? null : new InstanceManagerTransferAllocator(feature.Instances);
    }

    /// <summary>
    /// The adopted gateway session of the account on this worker (the gateway opens it when it stages the world port), or null while the client has not moved here:
    /// the shadow load of <see cref="CharacterHooksShadowLoader"/> fails without it and is repeated.
    /// </summary>
    private static ArcaneCore.World.Net.WorldSession? AdoptedSession(IServiceProvider services, PlayerTransferBundle bundle)
    {
        WorldGatewaySessionAcceptor? acceptor = services.GetService<GatewayWorkerEndpoint>()?.Acceptor;
        if (acceptor is null)
        {
            return null;
        }

        foreach (KeyValuePair<long, RemoteWorldSession> open in acceptor.Sessions)
        {
            if (open.Value.Session.AccountId == bundle.AccountId)
            {
                return open.Value.Session;
            }
        }

        return null;
    }
}
