using System.Net;
using System.Net.Sockets;
using ArcaneCore.Gateway.Handoff;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.World;
using ArcaneCore.World.Cluster.Control;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Handoff;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using static ArcaneCore.Gateway.Tests.StandbyWorkerTests;

namespace ArcaneCore.Gateway.Tests;

/// <summary>
/// The hardening of the gateway's handoff hosting after the xc-c7 review (design _wf/handoff-control-DESIGN.md sections 3.3 and 6, "C7 deviations"): the slot roles a restarted gateway learns
/// (a completed handoff, a crash after the reroute), the standby link that never publishes a channel to an endpoint it left, recovery that wakes when a link comes up, the anomaly of a
/// record a busy slot keeps skipping, the option validation that follows the opt-in key, the Gateway:Control binding of a hosted gateway, and a coordinator that really dies mid-handoff.
/// Two synthetic worlds (A active, B standby) sit behind real gateways; a "restarted gateway" is a second <see cref="GatewayServer"/> with the original configuration on the same workers.
/// </summary>
public sealed class GatewayHandoffHardeningTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(150);
    private const string NodeA = "world-a";
    private const string NodeB = "world-b";

    [Fact]
    public async Task AGatewayRestartedAfterACompletedHandoff_FollowsTheProcessThatIsNotTheRetiredSource()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Rig rig = await Rig.StartAsync(cts.Token);
        WorldHandoffRecord done = await rig.Service.HandoffAsync(NodeA, cancellationToken: cts.Token);
        Assert.Equal(WorldHandoffStage.Retired, done.Stage);
        Assert.Equal(LocalWorldHandoffSource.RetiredReason, rig.A.Services.GetRequiredService<WorkerAdmission>().ClosedReason);

        // The gateway process restarts: its configuration names A as the slot's worker and B as the standby again, and A (retired, not standby) is still running.
        await rig.RestartGatewayAsync(cts.Token);
        Assert.Equal(new[] { NodeA, NodeA, NodeB }, rig.Service.NodeNames(NodeA).ToArray());
        await rig.Service.ReconcileAsync(cts.Token);

        Assert.Equal(new[] { NodeA, NodeB, NodeA }, rig.Service.NodeNames(NodeA).ToArray()); // B is the active process, A the standby
        Assert.Equal(rig.BEndpoint.Host!.BoundEndPoint, rig.Gateway.WorkerEndPoint);
        Assert.Equal(rig.AEndpoint.Host!.BoundEndPoint, rig.Service.StandbyEndPoint(NodeA));
        Assert.Equal(NodeB, (await DescribeAsync(rig.Service.ActiveChannel(NodeA)!, cts.Token)).WorkerId);
    }

    [Fact]
    public async Task AGatewayRestartedAfterACrashFollowingTheReroute_FollowsTheStandbyAndRetiresTheSource()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Rig rig = await Rig.StartAsync(cts.Token);
        Guid handoffId = Guid.Empty;
        await Assert.ThrowsAsync<CoordinatorCrash>(() => rig.Service.HandoffAsync(NodeA, async (stage, _) =>
        {
            if (stage == WorldHandoffStage.Begun)
            {
                handoffId = (await rig.Store.GetUnresolvedAsync(cts.Token)).Single().HandoffId;
            }

            if (stage == WorldHandoffStage.Rerouted)
            {
                throw new CoordinatorCrash(); // after MarkRerouted, before the Retire verb and MarkRetired
            }
        }, cts.Token));
        Assert.Equal(WorldHandoffStage.Rerouted, (await rig.Store.GetAsync(handoffId, cts.Token))!.Stage);
        Assert.False(Host(rig.B).IsStandby);
        Assert.NotEqual(LocalWorldHandoffSource.RetiredReason, rig.A.Services.GetRequiredService<WorkerAdmission>().ClosedReason); // A was never retired

        await rig.RestartGatewayAsync(cts.Token);
        await rig.Service.ReconcileAsync(cts.Token);

        // Neither process reports Standby, yet the promoted standby is the active one.
        Assert.Equal(new[] { NodeA, NodeB, NodeA }, rig.Service.NodeNames(NodeA).ToArray());
        Assert.Equal(rig.BEndpoint.Host!.BoundEndPoint, rig.Gateway.WorkerEndPoint);
        Assert.Equal(rig.AEndpoint.Host!.BoundEndPoint, rig.Service.StandbyEndPoint(NodeA));

        // Recovery then finishes the record: the retire verb reaches A over the standby link.
        Assert.Equal(1, await rig.Recovery.RecoverOnceAsync(cts.Token));
        Assert.Equal(WorldHandoffStage.Retired, (await rig.Store.GetAsync(handoffId, cts.Token))!.Stage);
        Assert.Equal(LocalWorldHandoffSource.RetiredReason, rig.A.Services.GetRequiredService<WorkerAdmission>().ClosedReason);
        Assert.Empty(await rig.Store.GetUnresolvedAsync(cts.Token));
    }

    [Fact]
    public async Task AStandbyLinkRetargetedToAnEndpointNobodyListensOn_PublishesNoChannel_UntilItIsPointedBackAtTheStandby()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Rig rig = await Rig.StartAsync(cts.Token);
        GatewayStandbyLink link = rig.Service.StandbyLink(NodeA)!;
        Assert.NotNull(link.Channel);

        link.Retarget(new IPEndPoint(IPAddress.Loopback, FreePort()));
        await Task.Delay(600, cts.Token); // several connect attempts to the dead endpoint, and the old connection is gone
        Assert.Null(link.Channel);
        Assert.Null(rig.Service.StandbyChannel(NodeA));

        link.Retarget(rig.BEndpoint.Host!.BoundEndPoint);
        LinkControlChannel channel = await link.WaitForChannelAsync(cts.Token);
        Assert.Equal(NodeB, (await DescribeAsync(channel, cts.Token)).WorkerId);
    }

    [Fact]
    public async Task AStandbyLinkRetargetedWhileItsConnectionToTheOldEndpointIsBeingEstablished_NeverPublishesThatConnection()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Rig rig = await Rig.StartAsync(cts.Token);
        GatewayStandbyLink link = rig.Service.StandbyLink(NodeA)!;
        using var proxy = new StallingProxy(rig.AEndpoint.Host!.BoundEndPoint);

        // Point the link at the proxy: it connects the socket but the handshake stalls, so the connection attempt to the old endpoint stays in flight.
        link.Retarget(proxy.EndPoint);
        await proxy.Accepted.WaitAsync(cts.Token);
        int ups = 0;
        link.Up += () => Interlocked.Increment(ref ups);

        // Retarget while that attempt is held, then let the old connection complete its handshake.
        link.Retarget(new IPEndPoint(IPAddress.Loopback, FreePort()));
        proxy.Release();
        for (int i = 0; i < 30; i++)
        {
            Assert.Null(link.Channel); // a connection made to the old endpoint is never published
            await Task.Delay(50, cts.Token);
        }

        Assert.Null(rig.Service.StandbyChannel(NodeA));
        Assert.Equal(0, Volatile.Read(ref ups));

        link.Retarget(rig.BEndpoint.Host!.BoundEndPoint);
        LinkControlChannel channel = await link.WaitForChannelAsync(cts.Token);
        Assert.Equal(NodeB, (await DescribeAsync(channel, cts.Token)).WorkerId);
    }

    [Fact]
    public async Task RecoveryRunsWhenASlotsLinkComesUp_NotOnlyOnItsInterval()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Rig rig = await Rig.StartAsync(cts.Token);
        var options = new GatewayHandoffOptions { Enabled = true, RecoveryInterval = TimeSpan.FromHours(1) };
        using var recovery = new GatewayHandoffRecoveryService(rig.Service, rig.Store, options);
        await recovery.StartAsync(cts.Token); // its first pass finds nothing
        await Task.Delay(500, cts.Token);

        Guid id = Guid.NewGuid();
        await rig.Store.BeginAsync(id, new WorkerNodeId(NodeA), new WorkerNodeId(NodeB), rig.A.World.CharacterOwnership!, cts.Token);
        await Task.Delay(1000, cts.Token);
        Assert.Contains(await rig.Store.GetUnresolvedAsync(cts.Token), r => r.HandoffId == id); // the hour-long interval has not woken the loop

        // The standby link reconnects (a restarted standby, a role swap): that is a link-up, and the pass runs at once.
        GatewayStandbyLink link = rig.Service.StandbyLink(NodeA)!;
        link.Retarget(new IPEndPoint(IPAddress.Loopback, FreePort()));
        link.Retarget(rig.BEndpoint.Host!.BoundEndPoint);
        await WaitUntilAsync(async () => (await rig.Store.GetUnresolvedAsync(cts.Token)).All(r => r.HandoffId != id), cts.Token);
        Assert.Equal(WorldHandoffStage.Aborted, (await rig.Store.GetAsync(id, cts.Token))!.Stage);
        await recovery.StopAsync(cts.Token);
    }

    [Fact]
    public async Task ARecordItsBusySlotKeepsSkipping_StillRaisesTheStuckAnomaly_ButAForeignRecordDoesNot()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Rig rig = await Rig.StartAsync(cts.Token);
        Guid handoffId = Guid.Empty;
        await Assert.ThrowsAsync<CoordinatorCrash>(() => rig.Service.HandoffAsync(NodeA, async (stage, _) =>
        {
            if (stage == WorldHandoffStage.Begun)
            {
                handoffId = (await rig.Store.GetUnresolvedAsync(cts.Token)).Single().HandoffId;
            }

            if (stage == WorldHandoffStage.EpochTakeover)
            {
                throw new CoordinatorCrash();
            }
        }, cts.Token));
        string key = AnomalyLedger.KeyOf("cluster", "handoff-recovery-stuck");
        using AnomalyCapture stuck = AnomalyLedger.Shared.Capture(n => n.Key == key && n.Subject == handoffId.ToString());
        var options = new GatewayHandoffOptions { Enabled = true, AnomalyAfter = TimeSpan.FromMilliseconds(100) };
        using var recovery = new GatewayHandoffRecoveryService(rig.Service, rig.Store, options);

        GatewayHandoffSlot slot = rig.Service.SlotOf(NodeA)!;
        Assert.True(slot.TryEnter()); // another run holds the slot: every recovery of the record is skipped
        try
        {
            Assert.Equal(0, await recovery.RecoverOnceAsync(cts.Token));
            await Task.Delay(250, cts.Token);
            Assert.Equal(0, await recovery.RecoverOnceAsync(cts.Token));
            Assert.Equal(WorldHandoffStage.EpochTakeover, (await rig.Store.GetAsync(handoffId, cts.Token))!.Stage);
        }
        finally
        {
            slot.Exit();
        }

        Assert.Equal(1, stuck.Count(key)); // once, however many passes skipped it

        // A record that names no process of this gateway's slots is not ours: it never raises the anomaly here.
        WorldHandoffRecord ours = (await rig.Store.GetAsync(handoffId, cts.Token))!;
        WorldHandoffRecord foreign = ours with { SourceWorkerId = new WorkerNodeId("other-a"), TargetWorkerId = new WorkerNodeId("other-b") };
        Assert.False(rig.Service.Owns(foreign));
        Assert.True(rig.Service.Owns(ours));
    }

    [Fact]
    public async Task ACoordinatorThatDiesMidHandoff_IsFinishedByTheNextServicesRecovery_AndItsOwnLateReleaseOfTheSlotIsHarmless()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Rig rig = await Rig.StartAsync(cts.Token);
        CharacterOwnershipToken before = rig.A.World.CharacterOwnership!;
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var zombieGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        GatewayHandoffService doomed = rig.Service;

        // The handoff runs on the service; after the point of no return the coordinator hangs (a process that is about to be killed) and ignores every cancellation.
        Task<WorldHandoffRecord> running = doomed.HandoffAsync(NodeA, async (stage, _) =>
        {
            if (stage == WorldHandoffStage.EpochTakeover)
            {
                committed.TrySetResult();
                await zombieGate.Task;
            }
        }, CancellationToken.None);
        await committed.Task.WaitAsync(cts.Token);
        Guid handoffId = (await rig.Store.GetUnresolvedAsync(cts.Token)).Single().HandoffId;
        Assert.True(doomed.IsRunning(handoffId));

        // The host stops: the stop token is already expired, so it cannot wait for the handoff (it is past its takeover and not cancellable).
        await doomed.StopAsync(new CancellationToken(canceled: true));
        await doomed.DisposeAsync();
        Assert.False(running.IsCompleted);

        // A new service (the restarted gateway) rolls the committed handoff forward from the journal alone.
        await rig.ReplaceServiceAsync(cts.Token, disposePrevious: false);
        Assert.Equal(1, await rig.Recovery.RecoverOnceAsync(cts.Token));
        WorldHandoffRecord done = (await rig.Store.GetAsync(handoffId, cts.Token))!;
        Assert.Equal(WorldHandoffStage.Retired, done.Stage);
        Assert.Equal(new CharacterOwnershipToken("world", before.Epoch + 1, Host(rig.B).Incarnation), rig.B.World.CharacterOwnership);
        Assert.Equal(rig.BEndpoint.Host!.BoundEndPoint, rig.Gateway.WorkerEndPoint);

        // The dead coordinator wakes up late: its links are gone, so it fails, and giving back its (disposed) slot must not throw anything of its own.
        zombieGate.SetResult();
        Exception? late = await Record.ExceptionAsync(() => running);
        Assert.IsNotType<ObjectDisposedException>(late);
        Assert.Empty(await rig.Store.GetUnresolvedAsync(cts.Token));
        Assert.Equal(WorldHandoffStage.Retired, (await rig.Store.GetAsync(handoffId, cts.Token))!.Stage);
    }

    [Fact]
    public async Task ADisposedService_RefusesNewHandoffs_AndItsSlotsAreDisposed()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Rig rig = await Rig.StartAsync(cts.Token);
        GatewayHandoffService service = rig.Service;
        GatewayHandoffSlot slot = service.SlotOf(NodeA)!;
        await service.DisposeAsync();

        Assert.False(slot.TryEnter());
        slot.Exit(); // no throw after disposal
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.HandoffAsync(NodeA, cancellationToken: cts.Token));
        Assert.Throws<ObjectDisposedException>(() => slot.Busy.Wait(0));
        await rig.ReplaceServiceAsync(cts.Token, disposePrevious: false); // the rig's teardown disposes the new service only
    }

    // ------------------------------------------------------------------ registration (no worlds)

    [Fact]
    public void AHostedGateway_BindsGatewayControlFromConfiguration_IntoTheControlTimeouts()
    {
        using ServiceProvider provider = Provider(new Dictionary<string, string?>
        {
            ["Gateway:Handoff:Enabled"] = "true",
            ["Gateway:Control:Default"] = "00:00:07",
            ["Gateway:Control:Freeze"] = "00:00:08",
            ["Gateway:Control:Flush"] = "00:00:09",
            ["Gateway:Control:Activate"] = "00:00:11",
            ["Gateway:Control:Promote"] = "00:00:12",
        });

        GatewayServer server = provider.GetRequiredService<GatewayServer>(); // the singleton AddGateway registers as the hosted service

        LinkControlTimeouts timeouts = Assert.IsType<LinkControlTimeouts>(server.ControlTimeouts);
        Assert.Equal(TimeSpan.FromSeconds(7), timeouts.For(ControlVerb.Describe));
        Assert.Equal(TimeSpan.FromSeconds(8), timeouts.For(ControlVerb.Freeze));
        Assert.Equal(TimeSpan.FromSeconds(9), timeouts.For(ControlVerb.Flush));
        Assert.Equal(TimeSpan.FromSeconds(11), timeouts.For(ControlVerb.Activate));
        Assert.Equal(TimeSpan.FromSeconds(12), timeouts.For(ControlVerb.Promote));
    }

    [Fact]
    public void HandoffAndControlOptions_AreValidatedAtStart_OnlyWhileTheHandoffIsEnabled()
    {
        var bad = new Dictionary<string, string?>
        {
            ["Gateway:Handoff:RecoveryInterval"] = "00:00:00",
            ["Gateway:Control:Flush"] = "00:00:00",
        };

        // On: both bad values stop the start.
        using (ServiceProvider on = Provider(With(bad, ("Gateway:Handoff:Enabled", "true"))))
        {
            string failure = StartValidation(on);
            Assert.Contains("Gateway:Handoff:RecoveryInterval", failure, StringComparison.Ordinal);
            Assert.Contains("Gateway:Control:Flush", failure, StringComparison.Ordinal);
            Assert.NotEmpty(on.GetServices<IValidateOptions<GatewayHandoffOptions>>());
        }

        // Off: the same values are never looked at.
        using (ServiceProvider off = Provider(With(bad, ("Gateway:Handoff:Enabled", "false"))))
        {
            string failure = StartValidation(off);
            Assert.DoesNotContain("Gateway:Handoff", failure, StringComparison.Ordinal);
            Assert.DoesNotContain("Gateway:Control", failure, StringComparison.Ordinal);
            Assert.Empty(off.GetServices<IValidateOptions<GatewayHandoffOptions>>());
            Assert.Empty(off.GetServices<IValidateOptions<GatewayControlOptions>>());
            Assert.NotNull(off.GetRequiredService<IOptions<GatewayHandoffOptions>>().Value); // reading the options is not an error either
        }

        // The control timeouts also serve the transfer hosting, so they are validated when only that is on.
        using ServiceProvider transfers = Provider(With(bad, ("Gateway:Transfers:Enabled", "true")));
        string transferFailure = StartValidation(transfers);
        Assert.Contains("Gateway:Control:Flush", transferFailure, StringComparison.Ordinal);
        Assert.DoesNotContain("Gateway:Handoff:RecoveryInterval", transferFailure, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ helpers

    private static Dictionary<string, string?> With(Dictionary<string, string?> values, (string Key, string Value) extra)
    {
        var copy = new Dictionary<string, string?>(values) { [extra.Key] = extra.Value };
        return copy;
    }

    /// <summary>AddGateway with a valid gateway section plus <paramref name="values"/>; nothing is started.</summary>
    private static ServiceProvider Provider(Dictionary<string, string?> values) => Provider(values, out _);

    private static ServiceProvider Provider(Dictionary<string, string?> values, out IServiceCollection services)
    {
        var all = new Dictionary<string, string?>(values)
        {
            ["Gateway:LinkSecret"] = "0123456789abcdef0123",
            ["Gateway:Workers:0:NodeId"] = "a",
            ["Gateway:Workers:0:Port"] = "8095",
        };
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(all).Build();
        services = new ServiceCollection();
        services.AddGateway(configuration);
        return services.BuildServiceProvider();
    }

    /// <summary>Runs the host's options-validation start step (the IStartupValidator that ValidateOnStart registers; Host.StartAsync calls it) and returns everything it reported ("" when it passed or nothing is registered).</summary>
    private static string StartValidation(IServiceProvider provider)
    {
#pragma warning disable SYSLIB0066 // the synchronous validator is what ValidateOnStart registers; that is the thing under test
        IStartupValidator? validator = provider.GetService<IStartupValidator>();
        if (validator is null)
        {
            return string.Empty;
        }

        Exception? failure = Record.Exception(validator.Validate);
        return failure?.ToString() ?? string.Empty;
#pragma warning restore SYSLIB0066
    }

    private static async Task<ControlDescribePayload> DescribeAsync(LinkControlChannel channel, CancellationToken token)
    {
        ControlReply reply = await channel.CallAsync(ControlKind.WorldHandoff, Guid.NewGuid(), ControlVerb.Describe, ControlNoArguments.Instance, token);
        return Assert.IsType<ControlDescribePayload>(reply.Payload);
    }

    /// <summary>A loopback TCP forwarder to <c>upstream</c> that forwards nothing from the client until <see cref="Release"/>, so a handshake through it can be held open.</summary>
    private sealed class StallingProxy : IDisposable
    {
        private readonly TcpListener _listener = ArcaneCore.TestSupport.LoopbackPorts.CreateListener();
        private readonly IPEndPoint _upstream;
        private readonly CancellationTokenSource _stop = new();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public StallingProxy(IPEndPoint upstream)
        {
            _upstream = upstream;
            _listener.Start();
            _ = Task.Run(AcceptAsync);
        }

        public IPEndPoint EndPoint => (IPEndPoint)_listener.LocalEndpoint;

        /// <summary>Completes when a client has connected to the proxy.</summary>
        public Task Accepted => _accepted.Task;

        public void Release() => _release.TrySetResult();

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _accepted.TrySetResult();
                    _ = Task.Run(() => PumpAsync(client));
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // Stopped.
            }
        }

        private async Task PumpAsync(TcpClient client)
        {
            using (client)
            using (var upstream = new TcpClient())
            {
                try
                {
                    await upstream.ConnectAsync(_upstream, _stop.Token);
                    await _release.Task.WaitAsync(_stop.Token);
                    NetworkStream down = client.GetStream();
                    NetworkStream up = upstream.GetStream();
                    await Task.WhenAny(down.CopyToAsync(up, _stop.Token), up.CopyToAsync(down, _stop.Token));
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or SocketException)
                {
                    // The link or the proxy went away.
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }
    }

    private static int FreePort()
    {
        var probe = ArcaneCore.TestSupport.LoopbackPorts.CreateListener();
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken token)
    {
        while (!await condition())
        {
            await Task.Delay(20, token);
        }
    }

    private sealed class CoordinatorCrash : Exception;

    /// <summary>Two synthetic worlds on one set of databases behind a real gateway that can be "restarted" (replaced by a fresh one with the original configuration), a handoff service and its recovery.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private Rig(SyntheticArcaneServer a, SyntheticArcaneServer b, GatewayWorkerEndpoint aEndpoint, GatewayWorkerEndpoint bEndpoint, IWorldHandoffStore store)
        {
            A = a;
            B = b;
            AEndpoint = aEndpoint;
            BEndpoint = bEndpoint;
            Store = store;
            Gateway = null!;
            Service = null!;
            Recovery = null!;
        }

        public SyntheticArcaneServer A { get; }

        public SyntheticArcaneServer B { get; }

        public GatewayWorkerEndpoint AEndpoint { get; }

        public GatewayWorkerEndpoint BEndpoint { get; }

        public IWorldHandoffStore Store { get; }

        public GatewayServer Gateway { get; private set; }

        public GatewayHandoffService Service { get; private set; }

        public GatewayHandoffRecoveryService Recovery { get; private set; }

        public static async Task<Rig> StartAsync(CancellationToken token)
        {
            SyntheticArcaneServer a = await SyntheticArcaneServer.StartAsync(
                new SyntheticServerOptions { EnableFencing = true, Configuration = Worker(NodeA, standby: false) }, configureServices: null, token);
            SyntheticArcaneServer? b = null;
            try
            {
                b = await SyntheticArcaneServer.StartAsync(
                    new SyntheticServerOptions { EnableFencing = true, SharedDatabaseDirectory = a.DataDirectory, Configuration = Worker(NodeB, standby: true) }, configureServices: null, token);
                GatewayWorkerEndpoint aEndpoint = a.Services.GetRequiredService<GatewayWorkerEndpoint>();
                GatewayWorkerEndpoint bEndpoint = b.Services.GetRequiredService<GatewayWorkerEndpoint>();
                await aEndpoint.StartAsync(token);
                await bEndpoint.StartAsync(token);
                var rig = new Rig(a, b, aEndpoint, bEndpoint, a.Services.GetRequiredService<IWorldHandoffStore>());
                try
                {
                    await rig.StartGatewayAsync(token);
                    await rig.ReplaceServiceAsync(token, disposePrevious: false);
                    return rig;
                }
                catch
                {
                    if (rig.Service is not null)
                    {
                        await rig.Service.DisposeAsync();
                    }

                    if (rig.Gateway is not null)
                    {
                        await rig.StopGatewayAsync();
                    }

                    await bEndpoint.StopAsync(CancellationToken.None);
                    await aEndpoint.StopAsync(CancellationToken.None);
                    throw;
                }
            }
            catch
            {
                if (b is not null)
                {
                    await b.DisposeAsync();
                }

                await a.DisposeAsync();
                throw;
            }
        }

        /// <summary>Stops the current gateway (and its handoff service) and starts a new one with the original configuration: A is the slot's worker and B its standby, whatever happened since.</summary>
        public async Task RestartGatewayAsync(CancellationToken token)
        {
            if (Service is not null)
            {
                await Service.DisposeAsync();
            }

            await StopGatewayAsync();
            await StartGatewayAsync(token);
            await ReplaceServiceAsync(token, disposePrevious: false);
        }

        private async Task StartGatewayAsync(CancellationToken token)
        {
            IServiceProvider services = A.Services;
            int realmId = services.GetService<IOptions<GatewayWorkerOptions>>()?.Value.RealmId ?? new GatewayWorkerOptions().RealmId;
            var warden = new GatewayWarden(
                realmId,
                services.GetService<IOptions<ArcaneCore.World.Warden.WardenOptions>>()?.Value ?? new ArcaneCore.World.Warden.WardenOptions(),
                services.GetRequiredService<ILoggerFactory>().CreateLogger<GatewayWarden>());
            Gateway = new GatewayServer(
                services.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new GatewayOptions
                {
                    BindAddress = IPAddress.Loopback.ToString(),
                    Port = ArcaneCore.TestSupport.LoopbackPorts.Next(),
                    Workers =
                    [
                        new GatewayWorkerEntry
                        {
                            NodeId = NodeA,
                            Address = IPAddress.Loopback.ToString(),
                            Port = AEndpoint.Host!.BoundEndPoint.Port,
                            StandbyNodeId = NodeB,
                            StandbyAddress = IPAddress.Loopback.ToString(),
                            StandbyPort = BEndpoint.Host!.BoundEndPoint.Port,
                        },
                    ],
                    LinkSecret = Secret,
                    LinkRetryDelay = TimeSpan.FromMilliseconds(100),
                }),
                services.GetRequiredService<ILoggerFactory>(),
                warden: warden,
                handoff: Options.Create(new GatewayHandoffOptions { Enabled = true }),
                control: Options.Create(new GatewayControlOptions()));
            await Gateway.StartAsync(token);
            await Gateway.WaitForLinkAsync(token);
        }

        private async Task StopGatewayAsync()
        {
            await Gateway.StopAsync(CancellationToken.None);
            Gateway.Dispose();
        }

        /// <summary>Starts a new handoff service, with its recovery, on the current gateway and the shared journal; the previous service is disposed first unless <paramref name="disposePrevious"/> is false.</summary>
        public async Task ReplaceServiceAsync(CancellationToken token, bool disposePrevious = true)
        {
            if (Service is not null && disposePrevious)
            {
                await Service.DisposeAsync();
            }

            Recovery?.Dispose();
            Service = new GatewayHandoffService(Gateway, Store, NullLoggerFactory.Instance);
            await Service.StartAsync(token);
            Recovery = new GatewayHandoffRecoveryService(Service, Store, new GatewayHandoffOptions { Enabled = true });
            await WaitUntilAsync(() => Task.FromResult(Service.ActiveChannel(NodeA) is not null && Service.StandbyChannel(NodeA) is not null), token);
        }

        public async ValueTask DisposeAsync()
        {
            ((LocalWorldHandoffSource)A.Services.GetRequiredService<IWorldHandoffSource>()).Dispose(); // release A's parked simulation thread before its host stops
            if (Service is not null)
            {
                await Service.DisposeAsync();
            }

            Recovery?.Dispose();
            if (Gateway is not null)
            {
                await StopGatewayAsync();
            }

            await BEndpoint.StopAsync(CancellationToken.None);
            await AEndpoint.StopAsync(CancellationToken.None);
            Exception? stopFailure = null;
            try
            {
                await B.DisposeAsync();
            }
            catch (Exception ex)
            {
                stopFailure = ex;
            }

            try
            {
                await A.DisposeAsync();
            }
            catch (Exception ex)
            {
                stopFailure ??= ex;
            }

            // A retired source drops the writes of the characters that moved to the new owner (CharacterRetirementRegistry), so it stops cleanly: nothing is refused by the fence.
            Assert.Null(stopFailure);
        }

        private static Dictionary<string, string?> Worker(string nodeId, bool standby) => new()
        {
            ["Cluster:Worker:Enabled"] = "true",
            ["Cluster:Worker:Standby"] = standby ? "true" : "false",
            ["Cluster:Worker:NodeId"] = nodeId,
            ["Cluster:Worker:Port"] = ArcaneCore.TestSupport.LoopbackPorts.Next().ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Cluster:Worker:LinkSecret"] = Secret,
            ["Cluster:Worker:Handoff:Enabled"] = "true",
        };
    }
}
