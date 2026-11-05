using System.Buffers.Binary;
using System.Net;
using System.Text;
using ArcaneCore.Data.Characters.Transfers;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Gateway.Transfers;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.Protocol;
using ArcaneCore.World;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Transfer;
using ArcaneCore.World.Net;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.Gateway.Tests;

/// <summary>
/// Commit C10 of _wf/handoff-control-DESIGN.md (sections 4.3 and 4.5), end to end and through the production hosting on both worlds. Two real worlds on one set of databases (node "w-east" owns map 0, node
/// "w-kalimdor" owns map 1) behind one real gateway with transfers on, a real mock client in the world on the Eastern Kingdoms worker, and the real <see cref="GatewayTransferService"/>. Nothing here builds a
/// transfer request by hand: the player's teleport (<see cref="TeleportService.TeleportTo(Player, uint, float, float, float, float)"/>, the call the <c>.tele</c> command makes) reaches the worker's
/// <see cref="GatewayClusterFarTeleportRouter"/> that <see cref="ClusterFarTeleportFeature"/> installed, which sends a <see cref="ControlSubmit"/> over the gateway link; the gateway's service coordinates the six
/// stages and the client sees the retail far-teleport sequence from the Kalimdor worker.
/// </summary>
public sealed class GatewayCrossWorkerTeleportTests
{
    private const string Secret = "cross-worker-teleport-link-secret-0123456789";
    private const string EastNode = "w-east";
    private const string KalimdorNode = "w-kalimdor";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(150);
    private static readonly TeleportDestination Landing = new(1, -1196.22f, 29.0f, 176.949f, 3.1f);

    /// <summary>
    /// Blocks the gateway's read-loop thread inside a test hook until <paramref name="released"/> is set or the test is cancelled: a real kernel wait on a signal, bounded by <see cref="Deadline"/>
    /// and by the test's own token, with no polling on the hook's thread. The router exposes only its two counters (the route switch completed and nothing is staged), so the test side watches them
    /// with <see cref="ReleaseWhenRouteSwitchedAsync"/> (an asynchronous poll that holds no thread) and sets the signal.
    /// </summary>
    private static void HoldUntil(ManualResetEventSlim released, CancellationToken cancellation)
    {
        try
        {
            released.Wait(Deadline, cancellation);
        }
        catch (OperationCanceledException)
        {
            // The test ended: let the read loop go.
        }
    }

    /// <summary>
    /// Sets <paramref name="release"/> once the hook has captured <paramref name="held"/> and the route switch has completed: the connection's session generation moved off
    /// <paramref name="readUnder"/> (never 0 once the hook ran) and nothing is staged. Poll on the test's thread pool, not on the gateway's read loop.
    /// </summary>
    private static async Task ReleaseWhenRouteSwitchedAsync(
        Cluster cluster, Func<GatewayClientConnection?> held, Func<long> readUnder, ManualResetEventSlim release, CancellationToken token)
    {
        await Cluster.WaitUntilAsync(
            () => Task.FromResult(held() is { } connection && readUnder() != 0 && connection.SessionGeneration != readUnder() && cluster.Gateway.Transfers.StagedCount == 0), token);
        release.Set();
    }

    [Fact]
    public async Task ATeleportToKalimdor_OnTheEasternKingdomsWorker_MovesThePlayerToTheKalimdorWorker_AndTheJournalSaysCompleted()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Cluster cluster = await Cluster.StartAsync(cts.Token);
        await using WorldClient client = await cluster.EnterEastAsync("TELEGREEN", "Telegreen", cts.Token);
        GatewayClusterFarTeleportRouter router = cluster.Router;
        ObjectGuid guid = ObjectGuid.Player((uint)cluster.CharacterId);
        // The worker's feature assigned its router to the teleport service once the world ran.
        await Cluster.WaitUntilAsync(() => Task.FromResult(cluster.EastTeleports.ClusterFarRouter is not null), cts.Token);

        Assert.True(await cluster.TeleportAsync(guid, Landing));

        // The retail far-teleport sequence, sent by the Kalimdor worker (the east worker sends no loading-screen packet of its own).
        WorldFrame pending = await ReadPatientlyAsync(client, WorldOpcode.SmsgTransferPending, cts.Token);
        Assert.Equal(Landing.MapId, BinaryPrimitives.ReadUInt32LittleEndian(pending.Payload));
        WorldFrame newWorld = await ReadPatientlyAsync(client, WorldOpcode.SmsgNewWorld, cts.Token);
        Assert.Equal(Landing.MapId, BinaryPrimitives.ReadUInt32LittleEndian(newWorld.Payload));
        Assert.True(await cluster.Kalimdor.World.InvokeAsync(() => cluster.Kalimdor.World.FindOnlinePlayer(guid) is not null, cts.Token));
        Assert.False(await cluster.Eastern.World.InvokeAsync(() => cluster.Eastern.World.FindOnlinePlayer(guid) is not null, cts.Token));

        await client.SendAsync((ushort)WorldOpcode.MsgMoveWorldportAck, ReadOnlyMemory<byte>.Empty, cts.Token);
        // The journal turns Completed at the arrival registration, not when the client has loaded: wait for the arrival login packets before anything else is sent or asserted.
        await ReadPatientlyAsync(client, WorldOpcode.SmsgInitWorldStates, cts.Token);

        await Cluster.WaitUntilAsync(
            async () => await cluster.Kalimdor.World.InvokeAsync(() => cluster.Kalimdor.World.FindOnlinePlayer(guid) is { Map.MapId: 1 }, cts.Token), cts.Token);
        Guid transferId = router.LastTransferId;
        Assert.NotEqual(Guid.Empty, transferId);
        await Cluster.WaitUntilAsync(
            async () => (await cluster.Store.GetDecisionAsync(transferId, cts.Token))?.DecisionState == PlayerTransferDecisionState.Completed, cts.Token);
        await Cluster.WaitUntilAsync(() => Task.FromResult(router.InFlightCount == 0), cts.Token); // the outcome notice reached the source
        Assert.False(await cluster.Eastern.World.InvokeAsync(() => cluster.Eastern.World.FindOnlinePlayer(guid) is not null, cts.Token));
        Assert.Equal(0, cluster.Gateway.Transfers.StagedCount);
        await Cluster.WaitUntilAsync(
            () => Task.FromResult(cluster.Gateway.Workers[1].Link!.SessionCount == 1 && cluster.Gateway.Workers[0].Link!.SessionCount == 0), cts.Token);
    }

    /// <summary>
    /// A real ghost through the whole transfer, from a body made by a real death. The source captures it into the production bundle and its settlement save writes it, the journal holds the
    /// bundle, the target rebuilds the ghost from the stored life and, not owning the body's continent, keeps the body outside every map; the source's map loses the body at the retire, and
    /// the target's logout save stores it unchanged, so nothing lost it on the way.
    /// </summary>
    [Fact]
    public async Task AGhostTeleportedToKalimdor_ArrivesAsAGhost_AndItsBodySurvivesTheTransfer_FromBundleToTheNextSave()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Cluster cluster = await Cluster.StartAsync(cts.Token);
        WorldClient client = await cluster.EnterEastAsync("TELEGHOST", "Teleghost", cts.Token);
        await using var ownedClient = client;
        ObjectGuid guid = ObjectGuid.Player((uint)cluster.CharacterId);
        await Cluster.WaitUntilAsync(() => Task.FromResult(cluster.EastTeleports.ClusterFarRouter is not null), cts.Token);

        CorpseSnapshot body = await cluster.Eastern.World.InvokeAsync(() =>
        {
            Player player = cluster.Eastern.World.FindOnlinePlayer(guid) ?? throw new InvalidOperationException("the character is not online");
            player.Health = 0;
            player.Map!.Combat.KillPlayer(player);
            Assert.True(player.Map!.Combat.RepopPlayer(player));
            return PlayerLife.Capture(player).Corpse ?? throw new InvalidOperationException("the release made no body");
        }, cts.Token);
        Assert.Equal(0u, body.MapId);

        Assert.True(await cluster.TeleportAsync(guid, Landing));
        await ReadPatientlyAsync(client, WorldOpcode.SmsgNewWorld, cts.Token);
        await client.SendAsync((ushort)WorldOpcode.MsgMoveWorldportAck, ReadOnlyMemory<byte>.Empty, cts.Token);
        // The journal turns Completed at the arrival registration, not when the client has loaded: wait for the arrival login packets before anything else is sent or asserted.
        await ReadPatientlyAsync(client, WorldOpcode.SmsgInitWorldStates, cts.Token);
        await Cluster.WaitUntilAsync(
            async () => await cluster.Kalimdor.World.InvokeAsync(() => cluster.Kalimdor.World.FindOnlinePlayer(guid) is { Map.MapId: 1, Combat.Corpse: not null }, cts.Token), cts.Token);

        Guid transferId = cluster.Router.LastTransferId;
        await Cluster.WaitUntilAsync(
            async () => (await cluster.Store.GetDecisionAsync(transferId, cts.Token))?.DecisionState == PlayerTransferDecisionState.Completed, cts.Token);
        PlayerTransferBundle journalled = Assert.IsType<PlayerTransferBundle>((await cluster.Store.GetDecisionAsync(transferId, cts.Token))!.CapturedBundle);
        Assert.True(journalled.IsGhost);
        Assert.Equal(body, journalled.Corpse);

        await cluster.Kalimdor.World.InvokeAsync(() =>
        {
            Player player = cluster.Kalimdor.World.FindOnlinePlayer(guid) ?? throw new InvalidOperationException("the character is not online");
            Assert.NotEqual(0u, (uint)(player.Flags & PlayerFlags.Ghost));
            Assert.False(player.IsAlive);
            Assert.Equal(1u, player.Health);
            Assert.Equal(body, PlayerLife.Capture(player).Corpse);
            Assert.Null(player.Combat.Corpse!.Map); // map 0 belongs to the east worker
            Assert.Null(cluster.Kalimdor.World.FindMap(0));
            return true;
        }, cts.Token);
        await Cluster.WaitUntilAsync(
            async () => await cluster.Eastern.World.InvokeAsync(() => cluster.Eastern.World.GetMap(0).Combat.Corpses.Count == 0, cts.Token), cts.Token);

        await client.DisposeAsync(); // logout on the target: its save is the next write of the body
        await Cluster.WaitUntilAsync(async () => await cluster.Store.GetActiveOwnershipAsync((ulong)cluster.CharacterId, cts.Token) is null, cts.Token);
        await using AsyncServiceScope scope = cluster.Kalimdor.Services.CreateAsyncScope();
        CharacterLife stored = Assert.IsType<CharacterLife>(await scope.ServiceProvider.GetRequiredService<ICharacterLifeStore>().LoadAsync(cluster.CharacterId, cts.Token));
        Assert.True(stored.IsGhost);
        Assert.Equal(body, stored.Corpse);
    }

    [Fact]
    public async Task ATeleportToAWorkerThatIsUpButBetweenLinkDials_WaitsForItsLink_AndThePlayerMoves()
    {
        // The race a starting gateway (or a restarting worker) runs: the target's listener opened just after a failed dial, so the worker is up and the link supervisor sits out
        // its backoff (up to 30 s) with nobody to wake it. Until the link registers the target owns nothing for routing; a submit that answered from that moment refused the teleport
        // with map-unassigned, the player saw "The teleport could not be completed." and no journal row existed.
        using var cts = new CancellationTokenSource(Deadline);
        await using Cluster cluster = await Cluster.StartAsync(cts.Token, kalimdorLinkBackoff: TimeSpan.FromSeconds(30));
        await using WorldClient client = await cluster.EnterEastAsync("TELEBACKOFF", "Telebackoff", cts.Token);
        ObjectGuid guid = ObjectGuid.Player((uint)cluster.CharacterId);
        await Cluster.WaitUntilAsync(() => Task.FromResult(cluster.EastTeleports.ClusterFarRouter is not null), cts.Token);
        await Cluster.WaitUntilAsync(() => Task.FromResult(cluster.Gateway.Workers[1].Snapshot() is { Failures: > 0, Status: GatewayWorkerLinkStatus.Backoff }), cts.Token);
        Assert.Null(cluster.Gateway.Workers[1].Link);

        await cluster.KalimdorWorker.StartAsync(cts.Token);
        Assert.True(await cluster.TeleportAsync(guid, Landing));

        WorldFrame pending = await ReadPatientlyAsync(client, WorldOpcode.SmsgTransferPending, cts.Token);
        Assert.Equal(Landing.MapId, BinaryPrimitives.ReadUInt32LittleEndian(pending.Payload));
        WorldFrame newWorld = await ReadPatientlyAsync(client, WorldOpcode.SmsgNewWorld, cts.Token);
        Assert.Equal(Landing.MapId, BinaryPrimitives.ReadUInt32LittleEndian(newWorld.Payload));
        await client.SendAsync((ushort)WorldOpcode.MsgMoveWorldportAck, ReadOnlyMemory<byte>.Empty, cts.Token);
        // The journal turns Completed at the arrival registration, not when the client has loaded: wait for the arrival login packets before anything else is sent or asserted.
        await ReadPatientlyAsync(client, WorldOpcode.SmsgInitWorldStates, cts.Token);

        await Cluster.WaitUntilAsync(
            async () => await cluster.Kalimdor.World.InvokeAsync(() => cluster.Kalimdor.World.FindOnlinePlayer(guid) is { Map.MapId: 1 }, cts.Token), cts.Token);
        Guid transferId = cluster.Router.LastTransferId;
        await Cluster.WaitUntilAsync(
            async () => (await cluster.Store.GetDecisionAsync(transferId, cts.Token))?.DecisionState == PlayerTransferDecisionState.Completed, cts.Token);
    }

    [Fact]
    public async Task ARefusalAfterTheSourceLinkBlippedDuringTheWait_ReachesTheSourceOnItsNewLink_AndEndsTheInFlightEntry()
    {
        // The worker's in-flight entry has no link-loss hook: only the gateway's answer ends it, and without one the character's far teleports stay blocked for InFlightExpiry.
        // The Kalimdor worker never starts, so the submit waits out MapOwnerLinkWait; the first link's retry delay is short so the east link is back well before that.
        using var cts = new CancellationTokenSource(Deadline);
        await using Cluster cluster = await Cluster.StartAsync(cts.Token, kalimdorLinkBackoff: TimeSpan.FromSeconds(1));
        await using WorldClient client = await cluster.EnterEastAsync("TELEBLIP", "Teleblip", cts.Token);
        ObjectGuid guid = ObjectGuid.Player((uint)cluster.CharacterId);
        await Cluster.WaitUntilAsync(() => Task.FromResult(cluster.EastTeleports.ClusterFarRouter is not null), cts.Token);
        GatewayWorkerLink east = cluster.Gateway.Workers[0];
        GatewayWorkerLink kalimdor = cluster.Gateway.Workers[1];
        GatewaySessionLinkClient firstLink = Assert.IsType<GatewaySessionLinkClient>(east.Link);

        Assert.True(await cluster.TeleportAsync(guid, Landing));
        Assert.Equal(1, cluster.Router.InFlightCount);
        await Cluster.WaitUntilAsync(() => Task.FromResult(kalimdor.PromptDialWaiters == 1), cts.Token); // the submit took its link and is waiting

        await east.DisposeLinkAsync();
        await Cluster.WaitUntilAsync(() => Task.FromResult(east.Link is { } link && !ReferenceEquals(link, firstLink)), cts.Token);
        Assert.Equal(1, kalimdor.PromptDialWaiters); // the link came back inside the wait, so the answer is still to come

        using var answered = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        answered.CancelAfter(GatewayTransferService.MapOwnerLinkWait + TimeSpan.FromSeconds(30));
        await Cluster.WaitUntilAsync(() => Task.FromResult(cluster.Router.InFlightCount == 0), answered.Token);
    }

    /// <summary>
    /// Reads frames until one with <paramref name="opcode"/> arrives. A transfer is seconds of silence, and a short bound on a whole search flakes under load (this assembly's TestDeadlines
    /// sets the mock client's per-operation bound to 30 s), so only the wait for a frame's first byte is long here. A teleport that was refused sends nothing that ends this; the 60 s bound does.
    /// </summary>
    private static async Task<WorldFrame> ReadPatientlyAsync(WorldClient client, WorldOpcode opcode, CancellationToken token)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(token);
        bound.CancelAfter(TimeSpan.FromSeconds(60));
        while (true)
        {
            await client.WaitForTrafficAsync(bound.Token);
            WorldFrame frame = await client.ReadAsync(bound.Token);
            if (frame.Opcode == (ushort)opcode)
            {
                return frame;
            }
        }
    }

    [Fact]
    public async Task TheWaitForAMapOwnersLink_EndsAtOnceForAnUnownedMap_AndAtItsTimeForAnOwnerThatStaysDown()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Cluster cluster = await Cluster.StartAsync(cts.Token, kalimdorLinkBackoff: TimeSpan.FromSeconds(30));

        var unowned = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(await cluster.Gateway.WaitForMapOwnerLinkAsync(999, 0, TimeSpan.FromMinutes(1), cts.Token));
        Assert.True(unowned.Elapsed < TimeSpan.FromSeconds(30), "a map no configured worker owns has nothing to wait for");

        var down = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(await cluster.Gateway.WaitForMapOwnerLinkAsync(1, 0, TimeSpan.FromMilliseconds(300), cts.Token));
        Assert.True(down.Elapsed >= TimeSpan.FromMilliseconds(250), $"the wait ended after {down.Elapsed.TotalMilliseconds:F0} ms, before its time");
        Assert.Null(cluster.Gateway.Workers[1].Link);
    }

    [Fact]
    public async Task AWorldportAckReadBeforeTheRouteSwitch_AndRoutedAfterIt_ReachesTheKalimdorWorker()
    {
        // The client answers SMSG_NEW_WORLD (sent by the Kalimdor worker during the activation) at once, and the gateway's route switch runs right after the
        // activation, on the link receive loop. The gateway's read loop can read the ack (and its session generation, still the Eastern Kingdoms one) before
        // the switch and route it after the switch retired that generation. The ack must still reach the Kalimdor worker: dropped as stale, the player never
        // enters the map, every later packet is dropped there ("while player.Map is null"), and the client sits on its loading screen.
        using var cts = new CancellationTokenSource(Deadline);
        await using Cluster cluster = await Cluster.StartAsync(cts.Token);
        await using WorldClient client = await cluster.EnterEastAsync("TELEPORT", "Telerace", cts.Token);
        GatewayClusterFarTeleportRouter router = cluster.Router;
        ObjectGuid guid = ObjectGuid.Player((uint)cluster.CharacterId);
        await Cluster.WaitUntilAsync(() => Task.FromResult(cluster.EastTeleports.ClusterFarRouter is not null), cts.Token);

        long ackReadUnder = 0;
        long ackRoutedAfter = 0;
        bool ackReadWhileStaged = false;
        GatewayClientConnection? ackConnection = null;
        using var ackRead = new ManualResetEventSlim();
        using var ackRelease = new ManualResetEventSlim();
        cluster.Gateway.BeforeRouteClientPacket = (connection, opcode) =>
        {
            if (opcode != WorldOpcode.MsgMoveWorldportAck)
            {
                return;
            }

            // Hold the read loop until the route switch has completed: the session moved to a new generation and nothing is staged any more.
            long read = connection.SessionGeneration;
            Volatile.Write(ref ackConnection, connection);
            Interlocked.Exchange(ref ackReadUnder, read);
            Volatile.Write(ref ackReadWhileStaged, cluster.Gateway.Transfers.StagedCount == 1);
            ackRead.Set();
            HoldUntil(ackRelease, cts.Token);

            Interlocked.Exchange(ref ackRoutedAfter, connection.SessionGeneration);
        };

        // The Kalimdor world thread waits until the gateway has read the ack, so the activation (a command on that thread) and the route switch after it
        // cannot run before the read: the ack is read under the Eastern Kingdoms generation, while the transfer is staged, every time.
        cluster.Kalimdor.World.Post(() => ackRead.Wait(cts.Token));
        // On a failing test the poll must not outlive it, the hook must not outlive the signal it waits on, and the held read loop is let go.
        using var pollStop = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        Task releasing = ReleaseWhenRouteSwitchedAsync(cluster, () => Volatile.Read(ref ackConnection), () => Interlocked.Read(ref ackReadUnder), ackRelease, pollStop.Token);
        try
        {
            Assert.True(await cluster.TeleportAsync(guid, Landing));
            await ReadPatientlyAsync(client, WorldOpcode.SmsgTransferPending, cts.Token); // the gateway's, at the end of stage 4
            await client.SendAsync((ushort)WorldOpcode.MsgMoveWorldportAck, ReadOnlyMemory<byte>.Empty, cts.Token);
            await ReadPatientlyAsync(client, WorldOpcode.SmsgNewWorld, cts.Token);

            await Cluster.WaitUntilAsync(
                async () => await cluster.Kalimdor.World.InvokeAsync(() => cluster.Kalimdor.World.FindOnlinePlayer(guid) is { Map.MapId: 1 }, cts.Token), cts.Token);
            cluster.Gateway.BeforeRouteClientPacket = null;
            await releasing;
        }
        finally
        {
            cluster.Gateway.BeforeRouteClientPacket = null;
            pollStop.Cancel();
            ackRelease.Set();
        }

        // The race was the one described: the ack was read under one generation and routed after the switch to another.
        long readUnder = Interlocked.Read(ref ackReadUnder);
        long routedAfter = Interlocked.Read(ref ackRoutedAfter);
        Assert.True(Volatile.Read(ref ackReadWhileStaged));
        Assert.NotEqual(0, readUnder);
        Assert.NotEqual(0, routedAfter);
        Assert.NotEqual(readUnder, routedAfter);
        Guid transferId = router.LastTransferId;
        await Cluster.WaitUntilAsync(
            async () => (await cluster.Store.GetDecisionAsync(transferId, cts.Token))?.DecisionState == PlayerTransferDecisionState.Completed, cts.Token);
    }

    [Fact]
    public async Task AWorldportAckLetThroughBeforeTheStage_AndRoutedAfterTheSwitch_ReachesTheKalimdorWorker()
    {
        // The other re-route branch: nothing is staged when the transfer router lets the packet through, so it is not held and not dropped; the whole transfer then runs before the link is
        // chosen. The generation under the redirect gate is no longer the one the packet was read under, and the packet must go down the new link. The player can only enter the map through
        // this one ack, so sending it down the old link or dropping it fails the wait below.
        using var cts = new CancellationTokenSource(Deadline);
        await using Cluster cluster = await Cluster.StartAsync(cts.Token);
        await using WorldClient client = await cluster.EnterEastAsync("TELEROUTE", "Teleroute", cts.Token);
        ObjectGuid guid = ObjectGuid.Player((uint)cluster.CharacterId);
        await Cluster.WaitUntilAsync(() => Task.FromResult(cluster.EastTeleports.ClusterFarRouter is not null), cts.Token);

        int taken = 0;
        long readUnder = 0;
        GatewayClientConnection? passedConnection = null;
        using var passed = new ManualResetEventSlim();
        using var passedRelease = new ManualResetEventSlim();
        cluster.Gateway.AfterInterceptClientPacket = (connection, opcode) =>
        {
            if (opcode != WorldOpcode.MsgMoveWorldportAck || Interlocked.Exchange(ref taken, 1) != 0)
            {
                return;
            }

            long read = connection.SessionGeneration;
            Volatile.Write(ref passedConnection, connection);
            Interlocked.Exchange(ref readUnder, read);
            passed.Set();
            HoldUntil(passedRelease, cts.Token);
        };

        using var pollStop = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        Task releasing = ReleaseWhenRouteSwitchedAsync(cluster, () => Volatile.Read(ref passedConnection), () => Interlocked.Read(ref readUnder), passedRelease, pollStop.Token);
        try
        {
            await client.SendAsync((ushort)WorldOpcode.MsgMoveWorldportAck, ReadOnlyMemory<byte>.Empty, cts.Token);
            passed.Wait(cts.Token);
            Assert.True(await cluster.TeleportAsync(guid, Landing));
            await ReadPatientlyAsync(client, WorldOpcode.SmsgNewWorld, cts.Token);

            await Cluster.WaitUntilAsync(
                async () => await cluster.Kalimdor.World.InvokeAsync(() => cluster.Kalimdor.World.FindOnlinePlayer(guid) is { Map.MapId: 1 }, cts.Token), cts.Token);
            cluster.Gateway.AfterInterceptClientPacket = null;
            await releasing;
        }
        finally
        {
            cluster.Gateway.AfterInterceptClientPacket = null;
            pollStop.Cancel();
            passedRelease.Set();
        }
        Assert.NotEqual(0, Interlocked.Read(ref readUnder));
    }

    [Fact]
    public async Task ATeleportToAFullKalimdorWorker_LeavesThePlayerOnTheEasternKingdomsWorker_AndTellsThem()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Cluster cluster = await Cluster.StartAsync(cts.Token);
        // A second player fills the Kalimdor worker, then its player limit is 1: the gateway's placement answers worker-at-player-capacity.
        await using WorldClient filler = await cluster.EnterKalimdorAsync("TELEFILL", "Telefill", cts.Token);
        await using WorldClient client = await cluster.EnterEastAsync("TELEFULL", "Telefull", cts.Token);
        GatewayClusterFarTeleportRouter router = cluster.Router;
        ObjectGuid guid = ObjectGuid.Player((uint)cluster.CharacterId);
        await Cluster.WaitUntilAsync(() => Task.FromResult(cluster.EastTeleports.ClusterFarRouter is not null), cts.Token);
        cluster.Kalimdor.Services.GetRequiredService<WorldLoginAdmission>().SetLimit(1);
        cluster.KalimdorWorker.Host!.BroadcastWorkerStatus();
        await Cluster.WaitUntilAsync(() => Task.FromResult(cluster.Gateway.Workers[1].LastStatus is { PlayerLimit: 1, Sessions: >= 1 }), cts.Token);

        Assert.True(await cluster.TeleportAsync(guid, Landing)); // the worker queued the request; the refusal comes back from the gateway

        WorldFrame aborted = await client.ReadUntilAsync((ushort)WorldOpcode.SmsgTransferAborted, cts.Token);
        Assert.Equal(new[] { (byte)TransferAbortReason.MaxPlayers }, aborted.Payload);
        await client.ReadUntilAsync((ushort)WorldOpcode.SmsgMessagechat, cts.Token);
        await Cluster.WaitUntilAsync(() => Task.FromResult(router.InFlightCount == 0), cts.Token);
        Assert.True(await cluster.Eastern.World.InvokeAsync(() => cluster.Eastern.World.FindOnlinePlayer(guid) is { Map.MapId: 0 }, cts.Token));
        Assert.False(await cluster.Kalimdor.World.InvokeAsync(() => cluster.Kalimdor.World.FindOnlinePlayer(guid) is not null, cts.Token));
        Assert.Null(await cluster.Store.GetDecisionAsync(router.LastTransferId, cts.Token)); // refused before anything was journalled
    }

    [Fact]
    public async Task ATransferRefusedBecauseAWorkerSeesTwoGateways_IsToldToThePlayer_AndNothingIsJournalled()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Cluster cluster = await Cluster.StartAsync(cts.Token);
        await using WorldClient client = await cluster.EnterEastAsync("TELETWOGW", "Teletwogw", cts.Token);
        GatewayClusterFarTeleportRouter router = cluster.Router;
        ObjectGuid guid = ObjectGuid.Player((uint)cluster.CharacterId);
        await Cluster.WaitUntilAsync(() => Task.FromResult(cluster.EastTeleports.ClusterFarRouter is not null), cts.Token);
        // A second gateway process is admitted by the Kalimdor worker: one gateway per realm is the contract, so the coordinator refuses.
        GatewaySessionLinkHost kalimdorHost = cluster.KalimdorWorker.Host!;
        await using var secondGateway = new GatewaySessionLinkClient(
            new GatewayOptions { NodeId = "gateway-b" }.ToManifest(Guid.NewGuid()), kalimdorHost.BoundEndPoint, Secret);
        await secondGateway.ConnectAsync(cts.Token);
        await Cluster.WaitUntilAsync(() => Task.FromResult(kalimdorHost.AdmittedGatewayCount == 2), cts.Token);

        Assert.True(await cluster.TeleportAsync(guid, Landing));

        WorldFrame told;
        do
        {
            told = await client.ReadUntilAsync((ushort)WorldOpcode.SmsgMessagechat, cts.Token); // the login message of the day arrives as a chat frame first
            // The gateway's internal refusal text never reaches the client; only the generic line does.
            Assert.DoesNotContain("admitted gateways", Encoding.UTF8.GetString(told.Payload), StringComparison.Ordinal);
        }
        while (!Encoding.UTF8.GetString(told.Payload).Contains(GatewayClusterFarTeleportRouter.PlayerFailureText, StringComparison.Ordinal));
        await Cluster.WaitUntilAsync(() => Task.FromResult(router.InFlightCount == 0), cts.Token);
        Assert.True(await cluster.Eastern.World.InvokeAsync(() => cluster.Eastern.World.FindOnlinePlayer(guid) is { Map.MapId: 0 }, cts.Token));
        Assert.Null(await cluster.Store.GetDecisionAsync(router.LastTransferId, cts.Token));
    }

    [Fact]
    public async Task InACombinedWorld_TheFeatureBuildsNoRouter_SoTheTeleportServiceKeepsItsRefusalOfAnUnownedMap()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using SyntheticArcaneServer combined = await SyntheticArcaneServer.StartAsync(cts.Token);

        // No Cluster:Worker keys: the feature attached and built nothing, and no router stands behind the refusal of TeleportService (map-not-owned), which FarTeleportRoutingTests and
        // WorkerOwnershipRefusalTests pin and which this lane leaves untouched.
        Assert.Null(combined.Services.GetRequiredService<ClusterFarTeleportFeature>().Router);
        Assert.Null(combined.Services.GetRequiredService<TeleportFeature>().Teleports.ClusterFarRouter);
    }

    private static MapOwnership ParseOwnership(string maps)
    {
        Assert.True(MapOwnership.TryParse(maps, allowInstanceMaps: false, out MapOwnership ownership, out string? error), error);
        return ownership;
    }

    /// <summary>Two real worlds on one set of databases behind one real gateway with transfers on, and the gateway's real transfer service. Both worker ends are the production hosting.</summary>
    private sealed class Cluster : IAsyncDisposable
    {
        public required SyntheticArcaneServer Eastern { get; init; }

        public required SyntheticArcaneServer Kalimdor { get; init; }

        public required GatewayWorkerEndpoint EastWorker { get; init; }

        public required GatewayWorkerEndpoint KalimdorWorker { get; init; }

        public required GatewayServer Gateway { get; init; }

        public required EfTransferDecisionStore Store { get; init; }

        public required GatewayTransferService Service { get; init; }

        public int CharacterId { get; private set; }

        public TeleportService EastTeleports => Eastern.Services.GetRequiredService<TeleportFeature>().Teleports;

        /// <summary>The east worker's far-teleport router, built by the production feature.</summary>
        public GatewayClusterFarTeleportRouter Router =>
            Eastern.Services.GetRequiredService<ClusterFarTeleportFeature>().Router
            ?? throw new InvalidOperationException("the east worker built no far-teleport router");

        /// <param name="token">Cancels the start.</param>
        /// <param name="kalimdorLinkBackoff">When set, the Kalimdor worker's link listener is not started (the caller starts <see cref="KalimdorWorker"/>), only the east link is awaited, and this is the gateway's first reconnect delay (Gateway:LinkRetryDelay); both links are up and the delay is 100 ms when not given.</param>
        public static async Task<Cluster> StartAsync(CancellationToken token, TimeSpan? kalimdorLinkBackoff = null)
        {
            SyntheticArcaneServer eastern = await SyntheticArcaneServer.StartAsync(
                new SyntheticServerOptions { Configuration = WorkerScopeConfiguration(EastNode, "0") }, configureServices: null, token);
            SyntheticArcaneServer? kalimdor = null;
            GatewayWorkerEndpoint? eastWorker = null;
            GatewayWorkerEndpoint? kalimdorWorker = null;
            GatewayServer? gateway = null;
            GatewayTransferService? service = null;
            int kalimdorPort = ArcaneCore.TestSupport.LoopbackPorts.Next();
            try
            {
                kalimdor = await SyntheticArcaneServer.StartAsync(
                    new SyntheticServerOptions { SharedDatabaseDirectory = eastern.DataDirectory, Configuration = WorkerScopeConfiguration(KalimdorNode, "1", kalimdorPort) },
                    configureServices: null,
                    token);
                eastern.World.Ownership = Owns("0");
                kalimdor.World.Ownership = Owns("1");
                eastWorker = await StartWorkerAsync(eastern, token);
                kalimdorWorker = kalimdor.Services.GetRequiredService<GatewayWorkerEndpoint>();
                if (kalimdorLinkBackoff is null)
                {
                    await kalimdorWorker.StartAsync(token);
                }

                // The container's worker endpoint verifies the gateway's Warden posture, so the gateway must run the same Warden the container configured (as the production host does).
                int realmId = eastern.Services.GetService<IOptions<GatewayWorkerOptions>>()?.Value.RealmId ?? new GatewayWorkerOptions().RealmId;
                var warden = new GatewayWarden(
                    realmId,
                    eastern.Services.GetService<IOptions<ArcaneCore.World.Warden.WardenOptions>>()?.Value ?? new ArcaneCore.World.Warden.WardenOptions(),
                    eastern.Services.GetRequiredService<ILoggerFactory>().CreateLogger<GatewayWarden>());
                gateway = new GatewayServer(
                    eastern.Services.GetRequiredService<IServiceScopeFactory>(),
                    Options.Create(new GatewayOptions
                    {
                        BindAddress = IPAddress.Loopback.ToString(),
                        Port = ArcaneCore.TestSupport.LoopbackPorts.Next(),
                        LinkSecret = Secret,
                        LinkRetryDelay = kalimdorLinkBackoff ?? TimeSpan.FromMilliseconds(100),
                        Workers =
                        [
                            new GatewayWorkerEntry { NodeId = EastNode, Address = IPAddress.Loopback.ToString(), Port = eastWorker.Host!.BoundEndPoint.Port, Maps = [0] },
                            new GatewayWorkerEntry { NodeId = KalimdorNode, Address = IPAddress.Loopback.ToString(), Port = kalimdorPort, Maps = [1] },
                        ],
                        LoginWorker = EastNode,
                    }),
                    eastern.Services.GetRequiredService<ILoggerFactory>(),
                    warden: warden,
                    transfers: Options.Create(new GatewayTransferOptions { Enabled = true }));
                await gateway.StartAsync(token);
                if (kalimdorLinkBackoff is null)
                {
                    await gateway.WaitForAllLinksAsync(token);
                }
                else
                {
                    await gateway.Workers[0].WaitForLinkAsync(token);
                }

                EfTransferDecisionStore store = eastern.Services.GetRequiredService<EfTransferDecisionStore>();
                service = new GatewayTransferService(
                    gateway, store, store, new GatewayTransferOptions { Enabled = true }, loggers: eastern.Services.GetRequiredService<ILoggerFactory>());
                return new Cluster
                {
                    Eastern = eastern, Kalimdor = kalimdor, EastWorker = eastWorker, KalimdorWorker = kalimdorWorker, Gateway = gateway, Store = store, Service = service,
                };
            }
            catch
            {
                if (service is not null)
                {
                    await service.DisposeAsync();
                }

                if (gateway is not null)
                {
                    await gateway.StopAsync(CancellationToken.None);
                    gateway.Dispose();
                }

                if (kalimdorWorker is not null)
                {
                    await kalimdorWorker.StopAsync(CancellationToken.None);
                }

                if (eastWorker is not null)
                {
                    await eastWorker.StopAsync(CancellationToken.None);
                }

                if (kalimdor is not null)
                {
                    await kalimdor.DisposeAsync();
                }

                await eastern.DisposeAsync();
                throw;
            }
        }

        /// <summary>A client of a fresh account whose character is saved in Eastern Kingdoms (map 0) and is now in the world on the east worker (the login worker). <see cref="CharacterId"/> is its character.</summary>
        public async Task<WorldClient> EnterEastAsync(string account, string character, CancellationToken token)
        {
            (WorldClient client, int id) = await ConnectAsync(account, character, mapId: 0, token);
            try
            {
                CharacterId = id;
                await WaitUntilAsync(async () => await Eastern.World.InvokeAsync(() => Eastern.World.FindOnlinePlayer(ObjectGuid.Player((uint)id)) is not null, token), token);
                await WaitUntilAsync(() => Task.FromResult(Gateway.Workers[0].Link!.SessionCount >= 1), token);
                return client;
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }
        }

        /// <summary>A client whose character is saved in Kalimdor (map 1); the login redirect moved it to the Kalimdor worker.</summary>
        public async Task<WorldClient> EnterKalimdorAsync(string account, string character, CancellationToken token)
        {
            (WorldClient client, int id) = await ConnectAsync(account, character, mapId: 1, token);
            try
            {
                await WaitUntilAsync(async () => await Kalimdor.World.InvokeAsync(() => Kalimdor.World.FindOnlinePlayer(ObjectGuid.Player((uint)id)) is not null, token), token);
                await WaitUntilAsync(() => Task.FromResult(Gateway.Workers[1].Link!.SessionCount >= 1), token);
                // The login worker's own session of this account must be gone on the worker side too.
                await WaitUntilAsync(() => Task.FromResult(EastWorker.Acceptor!.Sessions.IsEmpty), token);
                return client;
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }
        }

        /// <summary>The teleport call a <c>.tele</c> command makes, on the east world thread.</summary>
        public Task<bool> TeleportAsync(ObjectGuid guid, TeleportDestination destination) => Eastern.World.InvokeAsync(
            () => Eastern.World.FindOnlinePlayer(guid) is { } player
                && EastTeleports.TeleportTo(player, destination.MapId, destination.X, destination.Y, destination.Z, destination.Orientation));

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            await Gateway.StopAsync(CancellationToken.None);
            Gateway.Dispose();
            await KalimdorWorker.StopAsync(CancellationToken.None);
            await EastWorker.StopAsync(CancellationToken.None);
            await Kalimdor.DisposeAsync(); // the shutdown saves of players that arrived by transfer run here: a refusal fails the test
            await Eastern.DisposeAsync();
        }

        public static async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken token)
        {
            while (!await condition())
            {
                await Task.Delay(20, token);
            }
        }

        private async Task<(WorldClient Client, int CharacterId)> ConnectAsync(string account, string character, uint mapId, CancellationToken token)
        {
            await Eastern.AddAccountAsync(account, "PASSWORD", token);
            LogonResult logon = await LogonClient.AuthenticateAsync(Eastern.RealmEndpoint, account, "PASSWORD", token);
            WorldClient client = await WorldClient.ConnectAsync(Gateway.ListenEndPoint, token);
            try
            {
                Assert.Equal((byte)AuthResponseCode.Ok, await client.AuthenticateAsync(account, logon.SessionKey, token));
                int id = await CreateCharacterOnMapAsync(client, character, mapId, token);
                byte[] login = new byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(login, (ulong)id);
                await client.SendAsync((ushort)WorldOpcode.CmsgPlayerLogin, login, token);
                await client.ReadUntilAsync((ushort)WorldOpcode.SmsgLoginVerifyWorld, token);
                return (client, id);
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }
        }

        private async Task<int> CreateCharacterOnMapAsync(WorldClient client, string name, uint mapId, CancellationToken token)
        {
            byte[] text = Encoding.ASCII.GetBytes(name);
            byte[] create = new byte[text.Length + 10];
            text.CopyTo(create, 0);
            create[text.Length + 1] = 1; // human
            create[text.Length + 2] = 1; // warrior
            await client.SendAsync((ushort)WorldOpcode.CmsgCharCreate, create, token);
            WorldFrame created = await client.ReadUntilAsync((ushort)WorldOpcode.SmsgCharCreate, token);
            Assert.Equal(new byte[] { 0x2E }, created.Payload);
            await client.SendAsync((ushort)WorldOpcode.CmsgCharEnum, ReadOnlyMemory<byte>.Empty, token);
            WorldFrame list = await client.ReadUntilAsync((ushort)WorldOpcode.SmsgCharEnum, token);
            Assert.Equal(1, list.Payload[0]);
            int id = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(list.Payload.AsSpan(1, 8)));

            await using AsyncServiceScope scope = Eastern.Services.CreateAsyncScope();
            ICharacterStore store = scope.ServiceProvider.GetRequiredService<ICharacterStore>();
            // The worlds run on Worker scope with fencing on: a tokenless write is refused while any ownership row is active, so write under the login world's own token.
            CharacterOwnershipToken? owner = Eastern.Services.GetRequiredService<ICharacterOwnershipContext>().Current;
            await store.SaveStateAsync(new CharacterState(id, mapId, 1, 100f, 100f, 50f, 0f, 1, 0, Ownership: owner), token);
            CharacterRecord saved = Assert.IsType<CharacterRecord>(await store.GetByIdAsync(id, token));
            Assert.Equal(mapId, saved.MapId);
            return id;
        }

        private static Dictionary<string, string?> WorkerScopeConfiguration(string nodeId, string ownedMaps, int? workerPort = null) => new()
        {
            ["Cluster:Fencing:Enabled"] = "true",
            ["Cluster:Fencing:Scope"] = "Worker",
            ["Cluster:Fencing:OwnerNode"] = nodeId,
            ["Cluster:Fencing:AllowSqliteWorkerScope"] = "true",
            ["Cluster:Worker:Enabled"] = "true",
            ["Cluster:Worker:NodeId"] = nodeId,
            ["Cluster:Worker:OwnedMaps"] = ownedMaps,
            ["Cluster:Worker:AllowInstanceMaps"] = "false",
            ["Cluster:Worker:Port"] = (workerPort ?? ArcaneCore.TestSupport.LoopbackPorts.Next()).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Cluster:Worker:LinkSecret"] = Secret,
            ["Cluster:Worker:Transfers:Enabled"] = "true",
            ["Cluster:Node:Role"] = "MapWorker",
        };

        private static MapOwnership Owns(string maps) => ParseOwnership(maps);

        /// <summary>The container's own endpoint (the production registration), started the way the host would start it.</summary>
        private static async Task<GatewayWorkerEndpoint> StartWorkerAsync(SyntheticArcaneServer server, CancellationToken token)
        {
            GatewayWorkerEndpoint endpoint = server.Services.GetRequiredService<GatewayWorkerEndpoint>();
            await endpoint.StartAsync(token);
            return endpoint;
        }
    }
}
