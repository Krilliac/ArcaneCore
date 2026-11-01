using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Reflection;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.Protocol;
using ArcaneCore.World;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Handoff;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.Gateway.Tests.StandbyWorkerTests;

namespace ArcaneCore.Gateway.Tests;

/// <summary>
/// Cross-process handoff control channel, commit C13: the handoff hold survives the loss of the source's link once the handoff reached Flushed (the source's saves are
/// durable, so the promoted world can adopt the character). Same layout as <see cref="WorldHandoffTests"/>: two worlds on one set of databases (A active, B standby), a real
/// gateway, the mock client, the durable handoff journal. A's link "dies" by closing the gateway's end of it, which is what the gateway sees when the source process is gone:
/// every session on the link closes with LinkLost. Before Flushed that disconnects the client (its state is not safe); after Flushed the client stays and lands on B.
/// </summary>
public sealed class HandoffHoldLinkLossTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(150);

    [Fact]
    public async Task ALinkLostAfterFlushed_KeepsTheClient_AndItLandsOnTheStandby()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using SyntheticArcaneServer a = await StartActiveAsync(cts.Token);
        await using SyntheticArcaneServer b = await StartStandbyAsync(a, cts.Token);
        await using Linked linked = await LinkAsync(a, cts.Token);
        GatewayWorkerEndpoint bEndpoint = b.Services.GetRequiredService<GatewayWorkerEndpoint>();
        await bEndpoint.StartAsync(cts.Token);
        using LocalWorldHandoffSource source = Source(a);
        try
        {
            await a.AddAccountAsync("HOLDLOSS", "PASSWORD", cts.Token);
            LogonResult logon = await LogonClient.AuthenticateAsync(a.RealmEndpoint, "HOLDLOSS", "PASSWORD", cts.Token);
            await using WorldClient client = await WorldClient.ConnectAsync(linked.Gateway.ListenEndPoint, cts.Token);
            Assert.Equal((byte)AuthResponseCode.Ok, await client.AuthenticateAsync("HOLDLOSS", logon.SessionKey, cts.Token));
            (_, byte[] loginVerify) = await CreateAndEnterAsync(client, a, "Holdloss", cts.Token);

            GatewayServer gateway = linked.Gateway;
            int marked = -1;
            int connectionsDuringOutage = -1;
            bool kickedDuringOutage = true;
            var stages = new List<WorldHandoffStage>();
            WorldHandoffRecord done = await Coordinator(a, b, source, new GatewayWorldHandoffRouter(gateway, bEndpoint.Host!.BoundEndPoint))
                .RunAsync(Guid.NewGuid(), async (stage, ct) =>
                {
                    stages.Add(stage);
                    if (stage == WorldHandoffStage.Flushed)
                    {
                        // The handoff reached Flushed (what the gateway's handoff service signals after the Flush reply), then A's link is lost.
                        marked = gateway.MarkHandoffFlushed();
                        await KillLinkAsync(gateway, ct);
                        await Task.Delay(500, ct); // time for the gateway to act on the loss: a connection that was going to be kicked is gone by now
                        connectionsDuringOutage = gateway.ConnectionCount;
                        kickedDuringOutage = AllConnections(gateway).Any(c => c.KickReason is not null);
                    }
                }, cts.Token);

            // Asserted after the run: the coordinator would swallow an assertion thrown in the hook as the abort.
            Assert.Equal(1, marked);
            Assert.Equal(1, connectionsDuringOutage);
            Assert.False(kickedDuringOutage, "the client was disconnected by the loss of the source's link after Flushed");
            Assert.Equal(WorldHandoffStage.Retired, done.Stage);
            Assert.Equal(
                [WorldHandoffStage.Begun, WorldHandoffStage.Warm, WorldHandoffStage.AdmissionsStopped, WorldHandoffStage.Frozen,
                 WorldHandoffStage.Flushed, WorldHandoffStage.EpochTakeover, WorldHandoffStage.Rerouted, WorldHandoffStage.Retired],
                stages);
            Assert.Equal(bEndpoint.Host.BoundEndPoint, gateway.WorkerEndPoint);

            // The client landed on B: the synthesized world port at the position of its save, then B answers it.
            var seen = new List<WorldFrame>();
            while (!seen.Exists(f => f.Opcode == (ushort)WorldOpcode.SmsgNewWorld))
            {
                seen.Add(await client.ReadAsync(cts.Token));
            }

            Assert.Equal(loginVerify, seen.Find(f => f.Opcode == (ushort)WorldOpcode.SmsgNewWorld)!.Payload);
            Assert.Equal(1, gateway.ConnectionCount);
            await WaitUntilAsync(async () => await b.World.InvokeAsync(() => b.World.OnlinePlayerCount) == 1, cts.Token);
            await WaitUntilAsync(() => Task.FromResult(bEndpoint.Acceptor!.Sessions.Count == 1), cts.Token);
            await client.SendAsync((ushort)WorldOpcode.CmsgQueryTime, ReadOnlyMemory<byte>.Empty, cts.Token);
            await client.ReadUntilAsync((ushort)WorldOpcode.SmsgQueryTimeResponse, cts.Token);
            Assert.False(Host(b).IsStandby);
        }
        finally
        {
            source.Dispose();
            await bEndpoint.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ALinkLostBeforeFlushed_DisconnectsTheClient_AndTheHandoffAborts()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using SyntheticArcaneServer a = await StartActiveAsync(cts.Token);
        await using SyntheticArcaneServer b = await StartStandbyAsync(a, cts.Token);
        await using Linked linked = await LinkAsync(a, cts.Token);
        GatewayWorkerEndpoint bEndpoint = b.Services.GetRequiredService<GatewayWorkerEndpoint>();
        await bEndpoint.StartAsync(cts.Token);
        using LocalWorldHandoffSource source = Source(a);
        try
        {
            await a.AddAccountAsync("HOLDLOSS", "PASSWORD", cts.Token);
            LogonResult logon = await LogonClient.AuthenticateAsync(a.RealmEndpoint, "HOLDLOSS", "PASSWORD", cts.Token);
            await using WorldClient client = await WorldClient.ConnectAsync(linked.Gateway.ListenEndPoint, cts.Token);
            Assert.Equal((byte)AuthResponseCode.Ok, await client.AuthenticateAsync("HOLDLOSS", logon.SessionKey, cts.Token));
            await CreateAndEnterAsync(client, a, "Holdloss", cts.Token);

            GatewayServer gateway = linked.Gateway;
            bool kicked = false;
            await Assert.ThrowsAsync<WorldHandoffAbortedException>(() =>
                Coordinator(a, b, source, new GatewayWorldHandoffRouter(gateway, bEndpoint.Host!.BoundEndPoint)).RunAsync(Guid.NewGuid(), async (stage, ct) =>
                {
                    if (stage == WorldHandoffStage.Frozen)
                    {
                        // The hold is in place but the handoff is not flushed: the loss of the link ends the session.
                        await KillLinkAsync(gateway, ct);
                        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        bounded.CancelAfter(TimeSpan.FromSeconds(20));
                        await WaitUntilAsync(() => Task.FromResult(gateway.ConnectionCount == 0), bounded.Token);
                        kicked = true;
                        throw new InvalidOperationException("injected failure after the link loss at " + stage);
                    }
                }, cts.Token));

            Assert.True(kicked, "the client stayed connected after the source's link was lost before Flushed");
            Assert.Equal(0, gateway.ConnectionCount);
            Assert.True(Host(b).IsStandby);
        }
        finally
        {
            source.Dispose();
            await bEndpoint.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AnAbortAfterTheFlushMark_ReleasesTheHold_AndClearsTheMark()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using SyntheticArcaneServer a = await StartActiveAsync(cts.Token);
        await using SyntheticArcaneServer b = await StartStandbyAsync(a, cts.Token);
        await using Linked linked = await LinkAsync(a, cts.Token);
        GatewayWorkerEndpoint bEndpoint = b.Services.GetRequiredService<GatewayWorkerEndpoint>();
        await bEndpoint.StartAsync(cts.Token);
        using LocalWorldHandoffSource source = Source(a);
        try
        {
            await a.AddAccountAsync("HOLDLOSS", "PASSWORD", cts.Token);
            LogonResult logon = await LogonClient.AuthenticateAsync(a.RealmEndpoint, "HOLDLOSS", "PASSWORD", cts.Token);
            await using WorldClient client = await WorldClient.ConnectAsync(linked.Gateway.ListenEndPoint, cts.Token);
            Assert.Equal((byte)AuthResponseCode.Ok, await client.AuthenticateAsync("HOLDLOSS", logon.SessionKey, cts.Token));
            await CreateAndEnterAsync(client, a, "Holdloss", cts.Token);

            GatewayServer gateway = linked.Gateway;
            Assert.Equal(0, gateway.MarkHandoffFlushed()); // nothing holds yet: nothing to mark
            Assert.Throws<ArgumentException>("nodeId", () => gateway.MarkHandoffFlushed("w-unknown"));

            int marked = -1;
            await Assert.ThrowsAsync<WorldHandoffAbortedException>(() =>
                Coordinator(a, b, source, new GatewayWorldHandoffRouter(gateway, bEndpoint.Host!.BoundEndPoint)).RunAsync(Guid.NewGuid(), (stage, ct) =>
                {
                    if (stage == WorldHandoffStage.Flushed)
                    {
                        marked = gateway.MarkHandoffFlushed();
                        throw new InvalidOperationException("injected failure at " + stage);
                    }

                    return Task.CompletedTask;
                }, cts.Token));

            Assert.Equal(1, marked);
            GatewayClientConnection connection = Assert.Single(AllConnections(gateway));
            FieldInfo holding = typeof(GatewayClientConnection).GetField("_handoffHolding", BindingFlags.NonPublic | BindingFlags.Instance)!;
            FieldInfo flushed = typeof(GatewayClientConnection).GetField("_handoffFlushed", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Assert.False((bool)holding.GetValue(connection)!);
            Assert.False((bool)flushed.GetValue(connection)!);
            Assert.Null(connection.KickReason);

            // A is the active world again and the client is still connected to it.
            await client.SendAsync((ushort)WorldOpcode.CmsgQueryTime, ReadOnlyMemory<byte>.Empty, cts.Token);
            await client.ReadUntilAsync((ushort)WorldOpcode.SmsgQueryTimeResponse, cts.Token);
            Assert.True(Host(b).IsStandby);
        }
        finally
        {
            source.Dispose();
            await bEndpoint.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>The gateway's end of the first worker's link closes, as when the worker process dies: every session on it closes with LinkLost, the gateway reconnects or backs off.</summary>
    private static async Task KillLinkAsync(GatewayServer gateway, CancellationToken token)
    {
        GatewaySessionLinkClient dead = gateway.Workers[0].Link ?? throw new InvalidOperationException("the worker link is not up");
        await dead.DisposeAsync();
        while (ReferenceEquals(gateway.Workers[0].Link, dead))
        {
            await Task.Delay(20, token);
        }
    }

    private static IEnumerable<GatewayClientConnection> AllConnections(GatewayServer gateway)
    {
        var connections = (ConcurrentDictionary<GatewayClientConnection, Task>)typeof(GatewayServer)
            .GetField("_connections", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(gateway)!;
        return connections.Keys;
    }

    private static IWorldHandoffStore Store(SyntheticArcaneServer server) => server.Services.GetRequiredService<IWorldHandoffStore>();

    private static LocalWorldHandoffSource Source(SyntheticArcaneServer a) => new(
        new WorkerNodeId("a"), a.World, a.Services.GetRequiredService<CharacterSaveQueue>(), a.Services.GetRequiredService<WorkerAdmission>(),
        a.Services.GetRequiredService<WorldWriterGuard>(), a.Services.GetRequiredService<CharacterPersistenceBarrier>());

    private static WorldHandoffCoordinator Coordinator(SyntheticArcaneServer a, SyntheticArcaneServer b, IWorldHandoffSource source, IWorldHandoffRouter router)
        => new(Store(a), source, new StandbyWorldHandoffTarget(new WorkerNodeId("b"), Host(b)), router);

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

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken token)
    {
        while (!await condition())
        {
            await Task.Delay(20, token);
        }
    }
}
