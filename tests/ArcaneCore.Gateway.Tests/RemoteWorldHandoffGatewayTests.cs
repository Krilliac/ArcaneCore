using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Gateway.Handoff;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.Protocol;
using ArcaneCore.World;
using ArcaneCore.World.Cluster.Control;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Handoff;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using static ArcaneCore.Gateway.Tests.StandbyWorkerTests;

namespace ArcaneCore.Gateway.Tests;

/// <summary>
/// C7 of _wf/handoff-control-DESIGN.md: the gateway hosts the whole-World handoff. A real <see cref="GatewayServer"/> with a real <see cref="GatewayHandoffService"/> drives two synthetic
/// worlds (A active, B standby, both with <c>Cluster:Worker:Handoff:Enabled</c>) through real control channels: A over the gateway's worker link, B over the session-less standby link.
/// The eleven scenarios of <see cref="WorldHandoffTests"/> run again with the remote participants; fakes sit only at the database boundary (a store that fails one abort write) and
/// at the process boundary (a "crash" is a hook that throws, or a service that is disposed and replaced).
/// </summary>
public sealed class RemoteWorldHandoffGatewayTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(150);
    private const string NodeA = "world-a";
    private const string NodeB = "world-b";

    [Fact]
    public async Task AFullHandoff_MovesTheOwnershipAndThePlayers_ToTheStandby_ThroughTheGatewayHandoffService()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Rig rig = await Rig.StartAsync(cts.Token);
        SyntheticArcaneServer a = rig.A;
        SyntheticArcaneServer b = rig.B;
        Assert.NotNull(rig.Gateway.Workers[0].ControlChannel);
        Assert.NotNull(rig.Service.StandbyChannel(NodeA));

        // A player in the world on A, through the gateway.
        await a.AddAccountAsync("HANDOFF", "PASSWORD", cts.Token);
        LogonResult logon = await LogonClient.AuthenticateAsync(a.RealmEndpoint, "HANDOFF", "PASSWORD", cts.Token);
        await using WorldClient first = await WorldClient.ConnectAsync(rig.Gateway.ListenEndPoint, cts.Token);
        Assert.Equal((byte)AuthResponseCode.Ok, await first.AuthenticateAsync("HANDOFF", logon.SessionKey, cts.Token));
        (_, byte[] loginVerify) = await CreateAndEnterAsync(first, a, "Handoff", cts.Token);
        CharacterOwnershipToken before = a.World.CharacterOwnership!;

        // A second client of another account, still at character select (the OpenAsync branch of the move).
        await a.AddAccountAsync("HANDOFFB", "PASSWORD", cts.Token);
        LogonResult logonB = await LogonClient.AuthenticateAsync(a.RealmEndpoint, "HANDOFFB", "PASSWORD", cts.Token);
        await using WorldClient selecting = await WorldClient.ConnectAsync(rig.Gateway.ListenEndPoint, cts.Token);
        Assert.Equal((byte)AuthResponseCode.Ok, await selecting.AuthenticateAsync("HANDOFFB", logonB.SessionKey, cts.Token));
        await selecting.SendAsync((ushort)WorldOpcode.CmsgCharEnum, ReadOnlyMemory<byte>.Empty, cts.Token);
        await selecting.ReadUntilAsync((ushort)WorldOpcode.SmsgCharEnum, cts.Token);

        var tapA = new QueryTimeTap();
        var tapB = new QueryTimeTap();
        Tap(a, tapA);
        Tap(b, tapB);

        var stages = new List<WorldHandoffStage>();
        WorldHandoffRecord done = await rig.Service.HandoffAsync(NodeA, async (stage, ct) =>
        {
            stages.Add(stage);
            if (stage == WorldHandoffStage.Frozen)
            {
                // Input sent while A is frozen: the gateway holds it and delivers it to B.
                await first.SendAsync((ushort)WorldOpcode.CmsgQueryTime, ReadOnlyMemory<byte>.Empty, ct);
            }
        }, cts.Token);

        Assert.Equal(
            [WorldHandoffStage.Begun, WorldHandoffStage.Warm, WorldHandoffStage.AdmissionsStopped, WorldHandoffStage.Frozen,
             WorldHandoffStage.Flushed, WorldHandoffStage.EpochTakeover, WorldHandoffStage.Rerouted, WorldHandoffStage.Retired],
            stages);
        Assert.Equal(WorldHandoffStage.Retired, done.Stage);
        Assert.Equal(PlayerTransferDecisionState.Completed, done.DecisionState);
        Assert.Equal(new CharacterOwnershipToken("world", before.Epoch + 1, Host(b).Incarnation), done.TargetOwnershipToken);
        Assert.Equal(done.TargetOwnershipToken, b.World.CharacterOwnership);
        Assert.False(Host(b).IsStandby);
        Assert.True(rig.Source.IsFrozen); // A stays parked: it never ticks again
        // The Retire verb reached A over the standby link, which moved to A's endpoint with the role swap.
        Assert.Equal(LocalWorldHandoffSource.RetiredReason, a.Services.GetRequiredService<WorkerAdmission>().ClosedReason);
        Assert.Equal(rig.BEndpoint.Host!.BoundEndPoint, rig.Gateway.WorkerEndPoint);
        Assert.Equal(rig.AEndpoint.Host!.BoundEndPoint, rig.Service.StandbyEndPoint(NodeA));

        var seen = new List<WorldFrame>();
        while (!(seen.Exists(f => f.Opcode == (ushort)WorldOpcode.SmsgNewWorld)
                 && seen.Exists(f => f.Opcode == (ushort)WorldOpcode.SmsgQueryTimeResponse)))
        {
            seen.Add(await first.ReadAsync(cts.Token));
        }

        int newWorld = seen.FindIndex(f => f.Opcode == (ushort)WorldOpcode.SmsgNewWorld);
        int pending = seen.FindIndex(f => f.Opcode == (ushort)WorldOpcode.SmsgTransferPending);
        Assert.Single(seen, f => f.Opcode == (ushort)WorldOpcode.SmsgNewWorld);
        Assert.Single(seen, f => f.Opcode == (ushort)WorldOpcode.SmsgTransferPending);
        Assert.True(pending >= 0 && pending < newWorld, "SMSG_TRANSFER_PENDING must precede SMSG_NEW_WORLD");

        // Golden bytes: SMSG_NEW_WORLD is exactly the SMSG_LOGIN_VERIFY_WORLD A sent, SMSG_TRANSFER_PENDING the u32 map, little endian.
        Assert.Equal(20, loginVerify.Length);
        byte[] expectedPending = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(expectedPending, BinaryPrimitives.ReadUInt32LittleEndian(loginVerify));
        Assert.Equal(loginVerify, seen[newWorld].Payload);
        Assert.Equal(expectedPending, seen[pending].Payload);
        using (var bounded = CancellationTokenSource.CreateLinkedTokenSource(cts.Token))
        {
            bounded.CancelAfter(TimeSpan.FromSeconds(20));
            await WaitUntilAsync(() => Task.FromResult(tapB.Count >= 1), bounded.Token);
        }

        Assert.Equal(1, tapB.Count);
        Assert.Equal(0, tapA.Count);
        Assert.Equal(2, rig.Gateway.ConnectionCount);
        await WaitUntilAsync(() => Task.FromResult(rig.BEndpoint.Acceptor!.Sessions.Count == 2), cts.Token);
        await WaitUntilAsync(async () => await b.World.InvokeAsync(() => b.World.OnlinePlayerCount) == 1, cts.Token);

        // The session is B's now: input sent after the handoff is answered too.
        await first.SendAsync((ushort)WorldOpcode.CmsgQueryTime, ReadOnlyMemory<byte>.Empty, cts.Token);
        await first.ReadUntilAsync((ushort)WorldOpcode.SmsgQueryTimeResponse, cts.Token);
        await selecting.SendAsync((ushort)WorldOpcode.CmsgCharEnum, ReadOnlyMemory<byte>.Empty, cts.Token);
        await selecting.ReadUntilAsync((ushort)WorldOpcode.SmsgCharEnum, cts.Token);

        CharacterOwnershipRow row = await OwnershipAsync(b);
        Assert.Equal(before.Epoch + 1, row.Epoch);
        Assert.Equal(Host(b).Incarnation.ToString("D"), row.OwnerIncarnation);
    }

    [Fact]
    public async Task AnAbortedHandoff_ReturnsTheInputHeldDuringTheFreeze_ToA_AndKeepsTheClientConnected()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Rig rig = await Rig.StartAsync(cts.Token);
        await rig.A.AddAccountAsync("HANDOFF", "PASSWORD", cts.Token);
        LogonResult logon = await LogonClient.AuthenticateAsync(rig.A.RealmEndpoint, "HANDOFF", "PASSWORD", cts.Token);
        await using WorldClient client = await WorldClient.ConnectAsync(rig.Gateway.ListenEndPoint, cts.Token);
        Assert.Equal((byte)AuthResponseCode.Ok, await client.AuthenticateAsync("HANDOFF", logon.SessionKey, cts.Token));
        await CreateAndEnterAsync(client, rig.A, "Handoff", cts.Token);

        // CMSG_QUERY_TIME is a session-level handler, so a frozen A would answer it without any hold: an answer before the abort means the hold is broken.
        var received = new ConcurrentQueue<WorldFrame>();
        Task? reader = null;
        bool answeredEarly = true;
        await Assert.ThrowsAsync<WorldHandoffAbortedException>(() =>
            rig.Service.HandoffAsync(NodeA, async (stage, ct) =>
            {
                if (stage == WorldHandoffStage.Frozen)
                {
                    reader = Task.Run(async () =>
                    {
                        while (true)
                        {
                            WorldFrame frame = await client.ReadAsync(cts.Token);
                            received.Enqueue(frame);
                            if (frame.Opcode == (ushort)WorldOpcode.SmsgQueryTimeResponse)
                            {
                                return;
                            }
                        }
                    }, CancellationToken.None);
                    await client.SendAsync((ushort)WorldOpcode.CmsgQueryTime, ReadOnlyMemory<byte>.Empty, ct);
                }

                if (stage == WorldHandoffStage.Flushed)
                {
                    await Task.Delay(500, ct);
                    answeredEarly = received.Any(f => f.Opcode == (ushort)WorldOpcode.SmsgQueryTimeResponse); // asserted after: the coordinator would swallow an assertion thrown here as the abort
                    throw new InvalidOperationException("injected failure at " + stage);
                }
            }, cts.Token));

        Assert.False(answeredEarly, "the input sent during the freeze was answered before the abort: the gateway did not hold it");
        Assert.NotNull(reader);
        await reader!.WaitAsync(cts.Token);
        Assert.Contains(received, f => f.Opcode == (ushort)WorldOpcode.SmsgQueryTimeResponse);
        Assert.Equal(1, rig.Gateway.ConnectionCount);
        Assert.True(Host(rig.B).IsStandby);
    }

    [Theory]
    [InlineData(WorldHandoffStage.Begun)]
    [InlineData(WorldHandoffStage.Warm)]
    [InlineData(WorldHandoffStage.AdmissionsStopped)]
    [InlineData(WorldHandoffStage.Frozen)]
    [InlineData(WorldHandoffStage.Flushed)]
    public async Task AFailureBeforeTheEpochTakeover_Aborts_AndAResumesUnderItsOwnership(WorldHandoffStage failAt)
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Rig rig = await Rig.StartAsync(cts.Token);
        CharacterOwnershipToken before = rig.A.World.CharacterOwnership!;
        IPEndPoint aEndpoint = rig.AEndpoint.Host!.BoundEndPoint;

        WorldHandoffAbortedException aborted = await Assert.ThrowsAsync<WorldHandoffAbortedException>(() =>
            rig.Service.HandoffAsync(NodeA, (stage, _) =>
                stage == failAt ? throw new InvalidOperationException("injected failure at " + stage) : Task.CompletedTask, cts.Token));

        Assert.Equal(WorldHandoffStage.Aborted, aborted.Record.Stage);
        Assert.Equal(PlayerTransferDecisionState.Aborted, aborted.Record.DecisionState);
        Assert.Equal("injected failure at " + failAt, aborted.Record.AbortReason);
        Assert.Equal(aborted.Record, await rig.Store.GetAsync(aborted.Record.HandoffId, cts.Token));
        AssertAResumed(rig, before);
        Assert.Equal(aEndpoint, rig.Gateway.WorkerEndPoint);
        Assert.Equal(before.Epoch, (await OwnershipAsync(rig.A)).Epoch);

        // The journal is free again: the next handoff runs.
        WorldHandoffRecord done = await rig.Service.HandoffAsync(NodeA, cancellationToken: cts.Token);
        Assert.Equal(WorldHandoffStage.Retired, done.Stage);
        Assert.Equal(rig.BEndpoint.Host!.BoundEndPoint, rig.Gateway.WorkerEndPoint);
    }

    [Fact]
    public async Task ASourceThatLostItsOwnership_AbortsAtTheTakeover_AndChangesNothing()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Rig rig = await Rig.StartAsync(cts.Token);
        CharacterOwnershipToken before = rig.A.World.CharacterOwnership!;
        IPEndPoint aEndpoint = rig.AEndpoint.Host!.BoundEndPoint;

        // Someone else took the characters over while A was draining (after the flush, before the takeover).
        Guid intruder = Guid.NewGuid();
        WorldHandoffAbortedException aborted = await Assert.ThrowsAsync<WorldHandoffAbortedException>(() =>
            rig.Service.HandoffAsync(NodeA, async (stage, ct) =>
            {
                if (stage == WorldHandoffStage.Flushed)
                {
                    await using AsyncServiceScope scope = rig.A.Services.CreateAsyncScope();
                    Assert.NotNull(await scope.ServiceProvider.GetRequiredService<ICharacterOwnershipStore>()
                        .TryTakeoverAsync(before, "intruder", intruder, ct));
                }
            }, cts.Token));

        Assert.Equal(WorldHandoffStage.Aborted, aborted.Record.Stage);
        Assert.Contains("no longer holds", aborted.Record.AbortReason);
        Assert.True(Host(rig.B).IsStandby);
        Assert.Equal(aEndpoint, rig.Gateway.WorkerEndPoint);
        CharacterOwnershipRow row = await OwnershipAsync(rig.A);
        Assert.Equal(intruder.ToString("D"), row.OwnerIncarnation); // the takeover did not overwrite the intruder
        Assert.Equal(before.Epoch + 1, row.Epoch);
    }

    [Fact]
    public async Task AGatewayServiceDisposedAfterTheEpochTakeover_IsFinishedByTheNextServicesRecovery_RollingForward()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Rig rig = await Rig.StartAsync(cts.Token);
        CharacterOwnershipToken before = rig.A.World.CharacterOwnership!;

        // The coordinator dies right after the point of no return: nothing aborts, B is not promoted yet.
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
        WorldHandoffRecord committed = (await rig.Store.GetAsync(handoffId, cts.Token))!;
        Assert.Equal(WorldHandoffStage.EpochTakeover, committed.Stage);
        Assert.Equal(PlayerTransferDecisionState.Committed, committed.DecisionState);
        Assert.True(Host(rig.B).IsStandby);
        Assert.Contains(committed, await rig.Store.GetUnresolvedAsync(cts.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Store.AbortAsync(handoffId, "too late", cts.Token)); // past the point of no return
        Assert.True(rig.Source.IsFrozen);

        // The gateway service goes away and a new one (a restarted gateway process) recovers from the journal: roll forward only.
        await rig.ReplaceServiceAsync(cts.Token);
        int resolved = await rig.Recovery.RecoverOnceAsync(cts.Token);

        Assert.Equal(1, resolved);
        WorldHandoffRecord done = (await rig.Store.GetAsync(handoffId, cts.Token))!;
        Assert.Equal(WorldHandoffStage.Retired, done.Stage);
        Assert.Equal(PlayerTransferDecisionState.Completed, done.DecisionState);
        Assert.False(Host(rig.B).IsStandby);
        Assert.Equal(new CharacterOwnershipToken("world", before.Epoch + 1, Host(rig.B).Incarnation), rig.B.World.CharacterOwnership);
        Assert.Equal(rig.BEndpoint.Host!.BoundEndPoint, rig.Gateway.WorkerEndPoint);
        Assert.Empty(await rig.Store.GetUnresolvedAsync(cts.Token));
        Assert.Equal(0, await rig.Recovery.RecoverOnceAsync(cts.Token)); // resolved: nothing left
    }

    [Fact]
    public async Task AGatewayServiceDisposedAfterFrozen_IsFinishedByTheNextServicesRecovery_AbortingAndResumingA()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Rig rig = await Rig.StartAsync(cts.Token);
        CharacterOwnershipToken before = rig.A.World.CharacterOwnership!;
        Guid handoffId = Guid.NewGuid();

        // The gateway died with A frozen and flushed (its abort never ran): the journal and the verbs are what it left behind.
        LinkControlChannel active = rig.Service.ActiveChannel(NodeA)!;
        LinkControlChannel standby = rig.Service.StandbyChannel(NodeA)!;
        await rig.Store.BeginAsync(handoffId, new WorkerNodeId(NodeA), new WorkerNodeId(NodeB), before, cts.Token);
        await standby.CallAsync(ControlKind.WorldHandoff, handoffId, ControlVerb.Warm, new ControlWarmArguments(Host(rig.B).Incarnation), cts.Token);
        await rig.Store.AdvancePreCommitAsync(handoffId, WorldHandoffStage.Warm, cts.Token);
        await active.CallAsync(ControlKind.WorldHandoff, handoffId, ControlVerb.StopAdmissions, ControlNoArguments.Instance, cts.Token);
        await rig.Store.AdvancePreCommitAsync(handoffId, WorldHandoffStage.AdmissionsStopped, cts.Token);
        await active.CallAsync(ControlKind.WorldHandoff, handoffId, ControlVerb.Freeze, ControlNoArguments.Instance, cts.Token);
        await rig.Store.AdvancePreCommitAsync(handoffId, WorldHandoffStage.Frozen, cts.Token);
        await active.CallAsync(ControlKind.WorldHandoff, handoffId, ControlVerb.Flush, ControlNoArguments.Instance, cts.Token);
        await rig.Store.AdvancePreCommitAsync(handoffId, WorldHandoffStage.Flushed, cts.Token);
        Assert.True(rig.Source.IsFrozen);

        await rig.ReplaceServiceAsync(cts.Token);
        int resolved = await rig.Recovery.RecoverOnceAsync(cts.Token);

        Assert.Equal(1, resolved);
        WorldHandoffRecord recovered = (await rig.Store.GetAsync(handoffId, cts.Token))!;
        Assert.Equal(WorldHandoffStage.Aborted, recovered.Stage);
        AssertAResumed(rig, before);
        Assert.Empty(await rig.Store.GetUnresolvedAsync(cts.Token));
    }

    [Fact]
    public async Task AnAbortWhoseJournalWriteFails_StillResumesA_AndRecoveryAbortsTheHandoff()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Rig rig = await Rig.StartAsync(cts.Token, store => new AbortFailsOnceStore(store));
        CharacterOwnershipToken before = rig.A.World.CharacterOwnership!;

        // A fails after the freeze, and the journal write of the abort fails too (a transient database error): A must not stay parked.
        await Assert.ThrowsAsync<TransientJournalFailure>(() =>
            rig.Service.HandoffAsync(NodeA, (stage, _) => stage == WorldHandoffStage.Flushed ? throw new InvalidOperationException("injected") : Task.CompletedTask, cts.Token));
        AssertAResumed(rig, before);
        WorldHandoffRecord open = Assert.Single(await rig.Store.GetUnresolvedAsync(cts.Token));
        Assert.Equal(WorldHandoffStage.Flushed, open.Stage); // still pre-commit: a recovery aborts it

        int resolved = await rig.Recovery.RecoverOnceAsync(cts.Token);

        Assert.Equal(1, resolved);
        Assert.Equal(WorldHandoffStage.Aborted, (await rig.Store.GetAsync(open.HandoffId, cts.Token))!.Stage);
        AssertAResumed(rig, before);
        Assert.Empty(await rig.Store.GetUnresolvedAsync(cts.Token));
    }

    [Fact]
    public async Task ASlotWithoutAStandby_CannotBeHandedOff_AndJournalsNothing()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Rig rig = await Rig.StartAsync(cts.Token);

        await Assert.ThrowsAsync<WorldHandoffUnavailableException>(() => rig.Service.HandoffAsync("no-such-worker", cancellationToken: cts.Token));

        Assert.Empty(await rig.Store.GetUnresolvedAsync(cts.Token));
        Assert.Equal(new[] { NodeA }, rig.Service.Slots.ToArray());
    }

    [Fact]
    public async Task ATargetThatIsNotAStandby_IsRefusedByThePreFlight_AndJournalsNothing()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Rig rig = await Rig.StartAsync(cts.Token);
        await rig.Service.HandoffAsync(NodeA, cancellationToken: cts.Token); // B is the active world now, A the retired one

        // The roles swapped: the standby link now points at A, which is not a standby (it is retired), so the next handoff of the slot is refused before it journals.
        // The standby link first has to reconnect to A (until then the refusal is WorldHandoffUnavailableException), so retry until the pre-flight itself answers.
        InvalidOperationException refusal;
        while (true)
        {
            try
            {
                await rig.Service.HandoffAsync(NodeA, cancellationToken: cts.Token);
                throw new Xunit.Sdk.XunitException("the handoff to a retired world must be refused");
            }
            catch (WorldHandoffUnavailableException)
            {
                await Task.Delay(50, cts.Token);
            }
            catch (InvalidOperationException ex)
            {
                refusal = ex;
                break;
            }
        }

        Assert.Contains("is not a standby", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(await rig.Store.GetUnresolvedAsync(cts.Token));
    }

    [Fact]
    public async Task ARetargetedStandbyLink_NeverHandsOutTheChannelOfTheProcessItLeft()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Rig rig = await Rig.StartAsync(cts.Token);
        GatewayStandbyLink link = rig.Service.StandbyLink(NodeA)!;
        Assert.NotNull(link.Channel);

        // Role swap: the link is retargeted to A's endpoint. At that instant the channel to B must be gone, so a verb meant for A cannot be sent to B.
        link.Retarget(rig.AEndpoint.Host!.BoundEndPoint);

        Assert.Null(link.Channel);
        Assert.Null(rig.Service.StandbyChannel(NodeA));
    }

    // ------------------------------------------------------------------ options and registration (no worlds)

    [Fact]
    public void ControlOptions_BindToTheChannelTimeouts()
    {
        var options = new GatewayControlOptions
        {
            Default = TimeSpan.FromSeconds(1),
            Freeze = TimeSpan.FromSeconds(2),
            Flush = TimeSpan.FromSeconds(3),
            Activate = TimeSpan.FromSeconds(4),
            Promote = TimeSpan.FromSeconds(5),
        };

        LinkControlTimeouts timeouts = options.ToTimeouts();

        Assert.Equal(TimeSpan.FromSeconds(1), timeouts.For(ControlVerb.Describe));
        Assert.Equal(TimeSpan.FromSeconds(2), timeouts.For(ControlVerb.Freeze));
        Assert.Equal(TimeSpan.FromSeconds(3), timeouts.For(ControlVerb.Flush));
        Assert.Equal(TimeSpan.FromSeconds(4), timeouts.For(ControlVerb.Activate));
        Assert.Equal(TimeSpan.FromSeconds(5), timeouts.For(ControlVerb.Promote));
        Assert.Equal(new LinkControlTimeouts(), new GatewayControlOptions().ToTimeouts()); // the defaults are the design's table
        Assert.Empty(new GatewayControlOptions().Validate());
        Assert.Contains(new GatewayControlOptions { Flush = TimeSpan.Zero }.Validate(), p => p.StartsWith("Gateway:Control:Flush", StringComparison.Ordinal));
    }

    [Fact]
    public void StandbyKeys_AreValidated_AndTheSlotNodeIdsMustBeUnique()
    {
        var good = new GatewayOptions
        {
            LinkSecret = "0123456789abcdef0123",
            Workers = [new GatewayWorkerEntry { NodeId = "a", Port = 8095, StandbyNodeId = "b", StandbyAddress = "127.0.0.1", StandbyPort = 8096 }],
        };
        Assert.Empty(good.Validate());

        var bad = new GatewayOptions
        {
            LinkSecret = "0123456789abcdef0123",
            Workers =
            [
                new GatewayWorkerEntry { NodeId = "a", Port = 8095, StandbyNodeId = "a", StandbyAddress = "not-an-ip", StandbyPort = 0 },
            ],
        };
        IReadOnlyList<string> problems = bad.Validate();
        Assert.Contains(problems, p => p.StartsWith("Gateway:Workers:0:StandbyNodeId", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("Gateway:Workers:0:StandbyAddress", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("Gateway:Workers:0:StandbyPort", StringComparison.Ordinal));
        Assert.Empty(new GatewayWorkerEntry().StandbyNodeId); // no standby by default

        var plain = new GatewayOptions { LinkSecret = "0123456789abcdef0123", Workers = [new GatewayWorkerEntry { NodeId = "a", Port = 8095, StandbyPort = 0 }] };
        Assert.Empty(plain.Validate()); // standby fields are ignored while StandbyNodeId is empty
    }

    [Fact]
    public void TheHandoffHosting_IsRegisteredOnlyWithItsOptInKey()
    {
        Assert.False(RegistersHandoff(new Dictionary<string, string?>()));
        Assert.False(RegistersHandoff(new Dictionary<string, string?> { ["Gateway:Handoff:Enabled"] = "false" }));
        Assert.True(RegistersHandoff(new Dictionary<string, string?> { ["Gateway:Handoff:Enabled"] = "true" }));

        static bool RegistersHandoff(Dictionary<string, string?> values)
        {
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            var services = new ServiceCollection();
            services.AddGateway(configuration);
            bool service = services.Any(d => d.ServiceType == typeof(GatewayHandoffService));
            bool characters = services.Any(d => d.ServiceType == typeof(ArcaneCore.Data.Characters.CharacterDbContext));
            Assert.Equal(service, characters); // the characters database is the handoff's journal and is registered with it, never alone
            return service;
        }
    }

    [Fact]
    public void ALinkControlChannel_RefusesAClientThatAlreadyHasOne()
    {
        var client = new GatewaySessionLinkClient(
            GatewayLinkManifest.Create(ClusterRole.Gateway, "gateway", Guid.NewGuid(), 1, "dev", 1, 1), new IPEndPoint(IPAddress.Loopback, 1), Secret);

        _ = new LinkControlChannel(client);

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => new LinkControlChannel(client));
        Assert.Contains("one control channel per client", refused.Message);
    }

    // ------------------------------------------------------------------ rig

    private static void AssertAResumed(Rig rig, CharacterOwnershipToken before)
    {
        Assert.False(rig.Source.IsFrozen);
        Assert.True(rig.A.Services.GetRequiredService<WorkerAdmission>().IsOpen);
        Assert.Equal(before, rig.A.World.CharacterOwnership);
        Assert.True(rig.A.World.InvokeAsync(() => true).Wait(TimeSpan.FromSeconds(10))); // A ticks again
        Assert.True(Host(rig.B).IsStandby);
        Assert.Null(rig.B.World.CharacterOwnership);
    }

    /// <summary>Two synthetic worlds on one set of databases behind a real gateway with the handoff hosting, a real handoff service and its recovery.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly Func<IWorldHandoffStore, IWorldHandoffStore>? _wrap;

        private Rig(
            SyntheticArcaneServer a, SyntheticArcaneServer b, GatewayWorkerEndpoint aEndpoint, GatewayWorkerEndpoint bEndpoint, GatewayServer gateway,
            IWorldHandoffStore store, Func<IWorldHandoffStore, IWorldHandoffStore>? wrap)
        {
            A = a;
            B = b;
            AEndpoint = aEndpoint;
            BEndpoint = bEndpoint;
            Gateway = gateway;
            Store = store;
            _wrap = wrap;
            Source = (LocalWorldHandoffSource)a.Services.GetRequiredService<IWorldHandoffSource>();
            Service = null!;
            Recovery = null!;
        }

        public SyntheticArcaneServer A { get; }

        public SyntheticArcaneServer B { get; }

        public GatewayWorkerEndpoint AEndpoint { get; }

        public GatewayWorkerEndpoint BEndpoint { get; }

        public GatewayServer Gateway { get; }

        /// <summary>The journal as the test reads it (never wrapped).</summary>
        public IWorldHandoffStore Store { get; }

        /// <summary>A's own handoff source, the one the control service drives (the test only reads its state and releases its parked thread).</summary>
        public LocalWorldHandoffSource Source { get; }

        public GatewayHandoffService Service { get; private set; }

        public GatewayHandoffRecoveryService Recovery { get; private set; }

        public static async Task<Rig> StartAsync(CancellationToken token, Func<IWorldHandoffStore, IWorldHandoffStore>? wrap = null)
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

                var rig = new Rig(a, b, aEndpoint, bEndpoint, gateway, services.GetRequiredService<IWorldHandoffStore>(), wrap);
                await rig.ReplaceServiceAsync(token);
                return rig;
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

        /// <summary>Stops the current handoff service (if any) and starts a new one, with its recovery, on the same gateway and journal: a restarted gateway process.</summary>
        public async Task ReplaceServiceAsync(CancellationToken token)
        {
            if (Service is not null)
            {
                await Service.DisposeAsync();
            }

            IWorldHandoffStore journal = _wrap?.Invoke(Store) ?? Store;
            Service = new GatewayHandoffService(Gateway, journal, NullLoggerFactory.Instance);
            await Service.StartAsync(token);
            Recovery = new GatewayHandoffRecoveryService(Service, journal, new GatewayHandoffOptions { Enabled = true });

            // Both control links are up before a test begins.
            await WaitUntilAsync(() => Task.FromResult(Service.ActiveChannel(NodeA) is not null && Service.StandbyChannel(NodeA) is not null), token);
        }

        public async ValueTask DisposeAsync()
        {
            Source.Dispose(); // release A's parked simulation thread before its host stops
            if (Service is not null)
            {
                await Service.DisposeAsync();
            }

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

            // The retired A logs its player out as it stops: every write it still attempts is refused by the characters fence (epoch N), nothing else fails.
            if (stopFailure is not null)
            {
                Assert.All(Leaves(stopFailure), leaf => Assert.IsType<CharacterOwnershipRejectedException>(leaf));
            }
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

    private static IEnumerable<Exception> Leaves(Exception error)
    {
        IEnumerable<Exception> inner = error is AggregateException aggregate ? aggregate.InnerExceptions
            : error.InnerException is { } one ? [one] : [];
        bool any = false;
        foreach (Exception child in inner)
        {
            any = true;
            foreach (Exception leaf in Leaves(child))
                yield return leaf;
        }

        if (!any)
            yield return error;
    }

    /// <summary>Creates a character and enters the world; returns its guid and the payload of the SMSG_LOGIN_VERIFY_WORLD A sent (u32 map, x, y, z, o).</summary>
    private static async Task<(ulong Guid, byte[] LoginVerifyWorld)> CreateAndEnterAsync(WorldClient client, SyntheticArcaneServer server, string name, CancellationToken token)
    {
        await client.SendAsync((ushort)WorldOpcode.CmsgCharCreate, CharacterCreate(name), token);
        Assert.Equal(new byte[] { 0x2E }, (await client.ReadUntilAsync((ushort)WorldOpcode.SmsgCharCreate, token)).Payload);
        await client.SendAsync((ushort)WorldOpcode.CmsgCharEnum, ReadOnlyMemory<byte>.Empty, token);
        WorldFrame list = await client.ReadUntilAsync((ushort)WorldOpcode.SmsgCharEnum, token);
        ulong guid = BinaryPrimitives.ReadUInt64LittleEndian(list.Payload.AsSpan(1, 8));
        byte[] login = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(login, guid);
        await client.SendAsync((ushort)WorldOpcode.CmsgPlayerLogin, login, token);
        WorldFrame verify = await client.ReadUntilAsync((ushort)WorldOpcode.SmsgLoginVerifyWorld, token);
        await WaitUntilAsync(async () => await server.World.InvokeAsync(() => server.World.OnlinePlayerCount) == 1, token);
        return (guid, verify.Payload);
    }

    private sealed class QueryTimeTap
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Hit() => Interlocked.Increment(ref _count);
    }

    /// <summary>Wraps the CMSG_QUERY_TIME session handler of <paramref name="server"/>'s opcode table so every dispatch to one of its sessions is counted.</summary>
    private static void Tap(SyntheticArcaneServer server, QueryTimeTap tap)
    {
        OpcodeTable table = server.Services.GetRequiredService<OpcodeTable>();
        Assert.True(table.TryGet(WorldOpcode.CmsgQueryTime, out OpcodeHandler original));
        SessionHandler inner = original.Session!;
        var wrapper = new OpcodeTable();
        wrapper.OnSession(WorldOpcode.CmsgQueryTime, original.AllowedStates, (session, payload) =>
        {
            tap.Hit();
            return inner(session, payload);
        });
        OpcodeTable? swapped = table.TrySwap([WorldOpcode.CmsgQueryTime], wrapper, out string? error);
        Assert.True(swapped is not null, error);
        table.Replace(swapped!);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken token)
    {
        while (!await condition())
        {
            await Task.Delay(20, token);
        }
    }

    private sealed class CoordinatorCrash : Exception;

    private sealed class TransientJournalFailure : Exception;

    private sealed class AbortFailsOnceStore(IWorldHandoffStore inner) : IWorldHandoffStore
    {
        private int _failed;

        public Task<WorldHandoffRecord> AbortAsync(Guid handoffId, string reason, CancellationToken cancellationToken = default)
            => Interlocked.Exchange(ref _failed, 1) == 0 ? throw new TransientJournalFailure() : inner.AbortAsync(handoffId, reason, cancellationToken);

        public Task<WorldHandoffRecord> BeginAsync(Guid handoffId, WorkerNodeId sourceWorkerId, WorkerNodeId targetWorkerId, CharacterOwnershipToken sourceOwnershipToken, CancellationToken cancellationToken = default)
            => inner.BeginAsync(handoffId, sourceWorkerId, targetWorkerId, sourceOwnershipToken, cancellationToken);
        public Task<WorldHandoffRecord> AdvancePreCommitAsync(Guid handoffId, WorldHandoffStage nextStage, CancellationToken cancellationToken = default)
            => inner.AdvancePreCommitAsync(handoffId, nextStage, cancellationToken);
        public Task<WorldHandoffRecord?> CommitEpochTakeoverAsync(Guid handoffId, string targetOwnerNode, Guid targetIncarnation, CancellationToken cancellationToken = default)
            => inner.CommitEpochTakeoverAsync(handoffId, targetOwnerNode, targetIncarnation, cancellationToken);
        public Task<WorldHandoffRecord?> CommitEpochTakeoverAsync(Guid handoffId, string targetOwnerNode, Guid targetIncarnation, IReadOnlyCollection<uint>? targetOwnedMaps, CancellationToken cancellationToken)
            => inner.CommitEpochTakeoverAsync(handoffId, targetOwnerNode, targetIncarnation, targetOwnedMaps, cancellationToken);
        public Task<WorldHandoffRecord> MarkReroutedAsync(Guid handoffId, CancellationToken cancellationToken = default) => inner.MarkReroutedAsync(handoffId, cancellationToken);
        public Task<WorldHandoffRecord> MarkRetiredAsync(Guid handoffId, CancellationToken cancellationToken = default) => inner.MarkRetiredAsync(handoffId, cancellationToken);
        public Task<WorldHandoffRecord?> GetAsync(Guid handoffId, CancellationToken cancellationToken = default) => inner.GetAsync(handoffId, cancellationToken);
        public Task<IReadOnlyList<WorldHandoffRecord>> GetUnresolvedAsync(CancellationToken cancellationToken = default) => inner.GetUnresolvedAsync(cancellationToken);
        public Task<IReadOnlyList<Guid>> GetTransferIdsAsync(Guid handoffId, CancellationToken cancellationToken = default) => inner.GetTransferIdsAsync(handoffId, cancellationToken);
        public ITransferDecisionStore ForHandoff(Guid handoffId) => inner.ForHandoff(handoffId);
    }
}
