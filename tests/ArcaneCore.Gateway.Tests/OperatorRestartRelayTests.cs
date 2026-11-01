using System.Buffers.Binary;
using System.Net;
using System.Text;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Gateway.Handoff;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.Protocol;
using ArcaneCore.World;
using ArcaneCore.World.Cluster.Control;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Handoff;
using ArcaneCore.World.Cluster.Supervision;
using ArcaneCore.World.Cluster.Tooling;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using static ArcaneCore.Gateway.Tests.StandbyWorkerTests;

namespace ArcaneCore.Gateway.Tests;

/// <summary>
/// C14 of _wf/handoff-control-DESIGN.md: the operator <c>.cluster restart</c> over the control channel. A real <see cref="GatewayServer"/> with a real
/// <see cref="GatewayHandoffService"/> and <see cref="GatewayRestartOrchestration"/> in front of two synthetic worlds (A active, B standby, <c>Cluster:Worker:Handoff:Enabled</c>
/// on both), a real operator client connected through the gateway, and the worker's real <see cref="ClusterRestartRelay"/> on the real link. The only fake is the process
/// boundary: the supervisor request (<see cref="FakeRestartRequester"/>), which records what the gateway asked for.
/// </summary>
public sealed class OperatorRestartRelayTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(150);
    private const string Secret = "operator-restart-link-secret-0123456789";
    private const string NodeA = "restart-a";
    private const string NodeB = "restart-b";
    private const string OperatorAccount = "ADMINOP";

    [Fact]
    public async Task Started_ArrivesAtOnce_AndTheVerdictReachesTheSameClientAfterItMovedToB()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Rig rig = await Rig.StartAsync(cts.Token);
        await using Operator op = await rig.ConnectOperatorAsync(AccountSecurity.Administrator, cts.Token);

        // The handoff is held at Frozen: the answer must arrive while it is still running.
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Restart.AfterStageForTests = async (stage, ct) =>
        {
            if (stage == WorldHandoffStage.Frozen)
            {
                held.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
        };

        ClusterRestartSubmission started = await op.Relay.SubmitRestartAsync(op.Session, AccountSecurity.Administrator, NodeA, cts.Token);
        Assert.True(started.Accepted, started.Reason);
        Assert.NotEqual(Guid.Empty, started.OperationId);
        await held.Task.WaitAsync(cts.Token);
        WorldHandoffRecord open = Assert.Single(await rig.Store.GetUnresolvedAsync(cts.Token)); // "started" arrived while the handoff was still unresolved
        Assert.Empty(rig.Requester.Requests);

        release.SetResult();
        WorldFrame verdict = await op.ReadSystemTextAsync("handed off", cts.Token);
        Assert.Contains(NodeA, Encoding.UTF8.GetString(verdict.Payload));

        // The same client, now on B: its session is B's, and the retired A was asked to come back as the next standby.
        Assert.Equal(rig.BEndpoint.Host!.BoundEndPoint, rig.Gateway.WorkerEndPoint);
        Assert.Single(rig.BEndpoint.Acceptor!.Sessions);
        Assert.False(Host(rig.B).IsStandby);
        Assert.Equal(NodeA, rig.Requester.Requests.Single().Worker);
        Assert.Equal(OperatorAccount, rig.Requester.Requests.Single().RequestedBy);
        Assert.Empty(await rig.Store.GetUnresolvedAsync(cts.Token));

        // '.cluster handoff status' reads the same outcome from the journal.
        string status = await ClusterCommands.FormatHandoffStatusAsync(rig.Store, started.OperationId, cts.Token);
        Assert.Contains("stage=Retired", status);
        Assert.Contains(started.OperationId.ToString("D"), status);
        Assert.Equal(open.HandoffId, started.OperationId);
    }

    [Fact]
    public async Task ASlotIdNamedAfterTheBlueGreenSwap_TargetsTheProcessThatIsActiveNow()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Rig rig = await Rig.StartAsync(cts.Token);
        await using Operator op = await rig.ConnectOperatorAsync(AccountSecurity.Administrator, cts.Token);
        var driver = new GatewaySlotHandoffDriver(rig.Service);
        Assert.Equal(NodeA, driver.SlotOf(NodeA));
        Assert.Null(driver.SlotOf(NodeB)); // before the swap B is the standby, not an active process

        ClusterRestartSubmission started = await op.Relay.SubmitRestartAsync(op.Session, AccountSecurity.Administrator, NodeA, cts.Token);
        Assert.True(started.Accepted, started.Reason);
        await op.ReadSystemTextAsync("handed off", cts.Token);

        // The swap happened: the slot key is still NodeA, its active process is B now.
        GatewayHandoffSlot slot = rig.Service.SlotOf(NodeA)!;
        Assert.Equal(NodeB, slot.ActiveNode);
        Assert.Equal(NodeA, slot.StandbyNode);
        Assert.Equal(NodeA, driver.SlotOf(NodeA)); // the slot id still names the slot, whose active process is B
        Assert.Equal(NodeA, driver.SlotOf(NodeB)); // and so does the process that is active now
        Assert.True(slot.IsActive(NodeB));
        Assert.Null(driver.SlotOf("no-such-worker"));
    }

    [Fact]
    public async Task AnAbortedHandoff_SendsItsVerdictToTheOperatorOnA()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Rig rig = await Rig.StartAsync(cts.Token);
        await using Operator op = await rig.ConnectOperatorAsync(AccountSecurity.Administrator, cts.Token);
        IPEndPoint aEndpoint = rig.AEndpoint.Host!.BoundEndPoint;
        rig.Restart.AfterStageForTests = (stage, _) =>
            stage == WorldHandoffStage.Flushed ? throw new InvalidOperationException("injected failure at " + stage) : Task.CompletedTask;

        ClusterRestartSubmission started = await op.Relay.SubmitRestartAsync(op.Session, AccountSecurity.Administrator, NodeA, cts.Token);
        Assert.True(started.Accepted, started.Reason);

        WorldFrame verdict = await op.ReadSystemTextAsync("aborted", cts.Token);
        Assert.Contains("injected failure at Flushed", Encoding.UTF8.GetString(verdict.Payload));
        Assert.Equal(aEndpoint, rig.Gateway.WorkerEndPoint); // the operator is still on A
        Assert.True(Host(rig.B).IsStandby);
        Assert.Empty(rig.Requester.Requests); // an aborted handoff restarts nothing
        Assert.Single(rig.AEndpoint.Acceptor!.Sessions); // the operator's session never left A
    }

    [Fact]
    public async Task ASecurityLevelTheAuthDatabaseContradicts_IsRefused_AndNothingRuns()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Rig rig = await Rig.StartAsync(cts.Token);
        // A plain player account whose worker claims Administrator (a worker that is wrong, or compromised).
        await using Operator op = await rig.ConnectOperatorAsync(AccountSecurity.Player, cts.Token);

        ClusterRestartSubmission refused = await op.Relay.SubmitRestartAsync(op.Session, AccountSecurity.Administrator, NodeA, cts.Token);

        Assert.False(refused.Accepted);
        Assert.Contains("contradicts", refused.Reason);
        Assert.Equal(Guid.Empty, refused.OperationId);
        Assert.Empty(await rig.Store.GetUnresolvedAsync(cts.Token));
        Assert.True(Host(rig.B).IsStandby);
        Assert.Empty(rig.Requester.Requests);

        // A truthful claim below Administrator is refused as plainly.
        ClusterRestartSubmission plain = await op.Relay.SubmitRestartAsync(op.Session, AccountSecurity.Player, NodeA, cts.Token);
        Assert.False(plain.Accepted);
        Assert.Contains("not available", plain.Reason);
    }

    [Fact]
    public async Task AnUnknownWorker_IsRefusedByTheGatewayBeforeAnythingStarts()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Rig rig = await Rig.StartAsync(cts.Token);
        await using Operator op = await rig.ConnectOperatorAsync(AccountSecurity.Administrator, cts.Token);

        ClusterRestartSubmission refused = await op.Relay.SubmitRestartAsync(op.Session, AccountSecurity.Administrator, "no-such-worker", cts.Token);

        Assert.False(refused.Accepted);
        Assert.Contains("Unknown worker", refused.Reason);
        Assert.Equal(ControlReasonCode.UnknownWorker, refused.Code);
        Assert.Empty(await rig.Store.GetUnresolvedAsync(cts.Token));
    }

    [Fact]
    public async Task HandoffStatus_ListsUnresolvedHandoffs_AndSaysSoWhenThereAreNone()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Rig rig = await Rig.StartAsync(cts.Token);
        Assert.Contains("No unresolved", await ClusterCommands.FormatHandoffStatusAsync(rig.Store, Guid.Empty, cts.Token));
        Guid unknown = Guid.NewGuid();
        Assert.Contains($"No handoff {unknown:D}", await ClusterCommands.FormatHandoffStatusAsync(rig.Store, unknown, cts.Token));
    }

    private sealed record Requested(string Worker, string RequestedBy);

    /// <summary>The supervisor boundary: records the restart the gateway asked for and accepts it.</summary>
    private sealed class FakeRestartRequester : IWorkerRestartRequester
    {
        public List<Requested> Requests { get; } = [];

        public Task<bool> RequestRestartAsync(WorkerNodeId worker, string requestedBy, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(new Requested(worker.Value, requestedBy));
            }

            return Task.FromResult(true);
        }
    }

    /// <summary>A real operator client connected through the gateway to A, with A's world session and the worker's relay.</summary>
    private sealed class Operator : IAsyncDisposable
    {
        private readonly WorldClient _client;

        public Operator(WorldClient client, WorldSession session, IClusterRestartRelay relay)
        {
            _client = client;
            Session = session;
            Relay = relay;
        }

        public WorldSession Session { get; }

        public IClusterRestartRelay Relay { get; }

        /// <summary>Reads frames until a system chat message containing <paramref name="fragment"/> arrives.</summary>
        public async Task<WorldFrame> ReadSystemTextAsync(string fragment, CancellationToken token)
        {
            while (true)
            {
                WorldFrame frame = await _client.ReadAsync(token);
                if (frame.Opcode == (ushort)WorldOpcode.SmsgMessagechat && Encoding.UTF8.GetString(frame.Payload).Contains(fragment, StringComparison.Ordinal))
                {
                    return frame;
                }
            }
        }

        public ValueTask DisposeAsync() => _client.DisposeAsync();
    }

    /// <summary>Two synthetic worlds on one set of databases behind a real gateway with the handoff hosting and the operator restart orchestration.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private Rig(
            SyntheticArcaneServer a, SyntheticArcaneServer b, GatewayWorkerEndpoint aEndpoint, GatewayWorkerEndpoint bEndpoint, GatewayServer gateway,
            GatewayHandoffService service, GatewayRestartOrchestration restart, FakeRestartRequester requester)
        {
            A = a;
            B = b;
            AEndpoint = aEndpoint;
            BEndpoint = bEndpoint;
            Gateway = gateway;
            Service = service;
            Restart = restart;
            Requester = requester;
            Store = a.Services.GetRequiredService<IWorldHandoffStore>();
            Source = (LocalWorldHandoffSource)a.Services.GetRequiredService<IWorldHandoffSource>();
        }

        public SyntheticArcaneServer A { get; }

        public SyntheticArcaneServer B { get; }

        public GatewayWorkerEndpoint AEndpoint { get; }

        public GatewayWorkerEndpoint BEndpoint { get; }

        public GatewayServer Gateway { get; }

        public GatewayHandoffService Service { get; }

        public GatewayRestartOrchestration Restart { get; }

        public FakeRestartRequester Requester { get; }

        public IWorldHandoffStore Store { get; }

        private LocalWorldHandoffSource Source { get; }

        public static async Task<Rig> StartAsync(CancellationToken token)
        {
            SyntheticArcaneServer a = await SyntheticArcaneServer.StartAsync(
                new SyntheticServerOptions { EnableFencing = true, Configuration = Worker(NodeA, standby: false) }, configureServices: null, token);
            SyntheticArcaneServer? b = null;
            GatewayWorkerEndpoint? aEndpoint = null;
            GatewayWorkerEndpoint? bEndpoint = null;
            GatewayServer? gateway = null;
            try
            {
                b = await SyntheticArcaneServer.StartAsync(
                    new SyntheticServerOptions { EnableFencing = true, SharedDatabaseDirectory = a.DataDirectory, Configuration = Worker(NodeB, standby: true) }, configureServices: null, token);
                aEndpoint = a.Services.GetRequiredService<GatewayWorkerEndpoint>();
                bEndpoint = b.Services.GetRequiredService<GatewayWorkerEndpoint>();
                await aEndpoint.StartAsync(token);
                await bEndpoint.StartAsync(token);

                IServiceProvider services = a.Services;
                int realmId = services.GetService<IOptions<GatewayWorkerOptions>>()?.Value.RealmId ?? new GatewayWorkerOptions().RealmId;
                var warden = new GatewayWarden(
                    realmId,
                    services.GetService<IOptions<ArcaneCore.World.Warden.WardenOptions>>()?.Value ?? new ArcaneCore.World.Warden.WardenOptions(),
                    services.GetRequiredService<ILoggerFactory>().CreateLogger<GatewayWarden>());
                gateway = new GatewayServer(
                    services.GetRequiredService<IServiceScopeFactory>(),
                    Options.Create(new GatewayOptions
                    {
                        BindAddress = IPAddress.Loopback.ToString(),
                        Port = 0,
                        Workers =
                        [
                            new GatewayWorkerEntry
                            {
                                NodeId = NodeA,
                                Address = IPAddress.Loopback.ToString(),
                                Port = aEndpoint.Host!.BoundEndPoint.Port,
                                StandbyNodeId = NodeB,
                                StandbyAddress = IPAddress.Loopback.ToString(),
                                StandbyPort = bEndpoint.Host!.BoundEndPoint.Port,
                            },
                        ],
                        LinkSecret = Secret,
                        LinkRetryDelay = TimeSpan.FromMilliseconds(100),
                    }),
                    services.GetRequiredService<ILoggerFactory>(),
                    warden: warden,
                    handoff: Options.Create(new GatewayHandoffOptions { Enabled = true }),
                    control: Options.Create(new GatewayControlOptions()));
                await gateway.StartAsync(token);
                await gateway.WaitForLinkAsync(token);

                var service = new GatewayHandoffService(gateway, services.GetRequiredService<IWorldHandoffStore>(), NullLoggerFactory.Instance);
                await service.StartAsync(token);
                var requester = new FakeRestartRequester();
                var restart = new GatewayRestartOrchestration(gateway, service, requester, NullLoggerFactory.Instance);
                await restart.StartAsync(token);
                await WaitUntilAsync(() => service.ActiveChannel(NodeA) is not null && service.StandbyChannel(NodeA) is not null, token);
                return new Rig(a, b, aEndpoint, bEndpoint, gateway, service, restart, requester);
            }
            catch
            {
                if (gateway is not null)
                {
                    await gateway.StopAsync(CancellationToken.None);
                    gateway.Dispose();
                }

                if (bEndpoint is not null)
                {
                    await bEndpoint.StopAsync(CancellationToken.None);
                }

                if (aEndpoint is not null)
                {
                    await aEndpoint.StopAsync(CancellationToken.None);
                }

                if (b is not null)
                {
                    await b.DisposeAsync();
                }

                await a.DisposeAsync();
                throw;
            }
        }

        /// <summary>Creates the operator account at <paramref name="security"/>, logs it in through the gateway and enters the world on A.</summary>
        public async Task<Operator> ConnectOperatorAsync(AccountSecurity security, CancellationToken token)
        {
            int accountId = await A.AddAccountAsync(OperatorAccount, "PASSWORD", token);
            await using (AsyncServiceScope scope = A.Services.CreateAsyncScope())
            {
                if (security != AccountSecurity.Player)
                {
                    Assert.True(await scope.ServiceProvider.GetRequiredService<IAccountStore>().UpdateSecurityAsync(OperatorAccount, security, token));
                }
            }

            LogonResult logon = await LogonClient.AuthenticateAsync(A.RealmEndpoint, OperatorAccount, "PASSWORD", token);
            WorldClient client = await WorldClient.ConnectAsync(Gateway.ListenEndPoint, token);
            Assert.Equal((byte)AuthResponseCode.Ok, await client.AuthenticateAsync(OperatorAccount, logon.SessionKey, token));
            await client.SendAsync((ushort)WorldOpcode.CmsgCharCreate, CharacterCreate("Operatorx"), token);
            Assert.Equal(new byte[] { 0x2E }, (await client.ReadUntilAsync((ushort)WorldOpcode.SmsgCharCreate, token)).Payload);
            await client.SendAsync((ushort)WorldOpcode.CmsgCharEnum, ReadOnlyMemory<byte>.Empty, token);
            WorldFrame list = await client.ReadUntilAsync((ushort)WorldOpcode.SmsgCharEnum, token);
            byte[] login = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(login, BinaryPrimitives.ReadUInt64LittleEndian(list.Payload.AsSpan(1, 8)));
            await client.SendAsync((ushort)WorldOpcode.CmsgPlayerLogin, login, token);
            await client.ReadUntilAsync((ushort)WorldOpcode.SmsgLoginVerifyWorld, token);
            await WaitUntilAsync(() => A.Services.GetRequiredService<SessionRegistry>().Find(accountId) is not null, token);

            WorldSession session = A.Services.GetRequiredService<SessionRegistry>().Find(accountId)!;
            return new Operator(client, session, A.Services.GetRequiredService<IClusterRestartRelay>());
        }

        public async ValueTask DisposeAsync()
        {
            Source.Dispose(); // release A's parked simulation thread before its host stops
            await Restart.StopAsync(CancellationToken.None);
            await Service.DisposeAsync();
            await Gateway.StopAsync(CancellationToken.None);
            Gateway.Dispose();
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
            ["Cluster:Worker:Port"] = "0",
            ["Cluster:Worker:LinkSecret"] = Secret,
            ["Cluster:Worker:Handoff:Enabled"] = "true",
        };
    }

    private static byte[] CharacterCreate(string name)
    {
        byte[] text = Encoding.ASCII.GetBytes(name);
        byte[] payload = new byte[text.Length + 10];
        text.CopyTo(payload, 0);
        payload[text.Length + 1] = 1; // human
        payload[text.Length + 2] = 1; // warrior
        return payload;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken token)
    {
        while (!condition())
        {
            await Task.Delay(20, token);
        }
    }
}
