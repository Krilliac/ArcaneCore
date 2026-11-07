using System.Buffers.Binary;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using Xunit;

namespace ArcaneCore.World.Tests.Handlers;

/// <summary>
/// Movement relays batched into SMSG_COMPRESSED_MOVES (<c>World:CompressMovementCount</c>, default 0 = off; <see cref="MovementPacketBatch"/>, vmangos
/// WorldSession::SendMovementPacket): the framing of a batch, and through two real sessions that an observer is sent the relay plain with the option off,
/// plain below the threshold and as one compressed packet above it. Written without a build or test run (lane rule); unproven until the integration gate.
/// </summary>
public sealed class MovementCompressionTests
{
    [Fact]
    public void ABatch_FramesEachPacketAsSizeOpcodeBody_AndBuildsTheSizePrefixedZlibStream()
    {
        var batch = new MovementPacketBatch();
        Assert.False(batch.HasData);
        batch.Add(WorldOpcode.MsgMoveHeartbeat, [1, 2, 3]);
        batch.Add(WorldOpcode.SmsgMonsterMove, [9]);
        Assert.True(batch.HasData);

        byte[] inflated = WorldTestClient.Inflate(batch.Build());
        ushort heartbeat = (ushort)WorldOpcode.MsgMoveHeartbeat;
        ushort monster = (ushort)WorldOpcode.SmsgMonsterMove;
        Assert.Equal([5, (byte)heartbeat, (byte)(heartbeat >> 8), 1, 2, 3, 3, (byte)monster, (byte)(monster >> 8), 9], inflated);

        batch.Clear();
        Assert.False(batch.HasData);
    }

    [Fact]
    public void ABatch_AfterClear_IsEmptyAndBuildsOnlyWhatWasAddedSince()
    {
        var batch = new MovementPacketBatch();
        batch.Add(WorldOpcode.MsgMoveHeartbeat, [1, 2, 3]);
        byte[] first = batch.Build();
        batch.Clear();
        Assert.False(batch.HasData);

        batch.Add(WorldOpcode.SmsgMonsterMove, [9]);
        byte[] second = batch.Build();

        ushort monster = (ushort)WorldOpcode.SmsgMonsterMove;
        Assert.Equal([3, (byte)monster, (byte)(monster >> 8), 9], WorldTestClient.Inflate(second)); // none of the first batch is left in it
        Assert.Equal(1 + 2 + 3, WorldTestClient.Inflate(first).Length); // and clearing did not reach into the packet already built
    }

    [Fact]
    public void ABatch_RefusesWhatTheSizeByteOrTheClientCannotCarry()
    {
        var batch = new MovementPacketBatch();
        Assert.True(batch.CanAdd(253));  // 253 + 2 = 255 fits the u8 size
        Assert.False(batch.CanAdd(254)); // vmangos CanAddPacket: wpos + 2 > 0xFF
        byte[] big = new byte[253];
        while (batch.CanAdd(big.Length))
        {
            batch.Add(WorldOpcode.MsgMoveHeartbeat, big);
        }

        byte[] one = [0];
        while (batch.CanAdd(one.Length))
        {
            batch.Add(WorldOpcode.MsgMoveHeartbeat, one);
        }

        Assert.False(batch.CanAdd(1)); // the client crashes on a batch of 900000 bytes or more (vmangos "[CRASH-CLIENT] Too large packet size")
        Assert.False(batch.CanAdd(253));
    }

    [Fact]
    public void OnlyTheMovementRelaysAreBatchable()
    {
        Assert.True(MovementPacketBatch.IsBatchable(WorldOpcode.MsgMoveHeartbeat));
        Assert.True(MovementPacketBatch.IsBatchable(WorldOpcode.MsgMoveStartForward));
        Assert.True(MovementPacketBatch.IsBatchable(WorldOpcode.SmsgMonsterMove));
        Assert.True(MovementPacketBatch.IsBatchable(WorldOpcode.SmsgSplineSetRunSpeed));
        Assert.True(MovementPacketBatch.IsBatchable(WorldOpcode.SmsgSplineMoveStartSwim));
        Assert.False(MovementPacketBatch.IsBatchable(WorldOpcode.SmsgMessagechat));
        Assert.False(MovementPacketBatch.IsBatchable(WorldOpcode.SmsgUpdateObject));
        Assert.False(MovementPacketBatch.IsBatchable(WorldOpcode.SmsgCompressedMoves));
    }

    private static uint _clock = 9000;

    private static async Task<byte[]> BlockAsync(WorldTestHost host, float dx)
    {
        (float x, float y, float z, float o) = await host.PlayerStateAsync("Mvmover", p => (p.X, p.Y, p.Z, p.Orientation));
        var writer = new PacketWriter(48);
        new MovementInfo { Flags = MovementFlags.None, Time = _clock += 100, X = x + dx, Y = y, Z = z, Orientation = o }.Write(writer);
        return writer.ToArray();
    }

    private static async Task<(WorldTestHost Host, WorldTestClient Mover, WorldTestClient Watcher)> StartAsync(int count)
    {
        WorldTestHost host = WorldTestHost.Start(configure: o => o.CompressMovementCount = count);
        WorldTestClient mover = await host.EnterWorldAsync("MVMOVER", "Mvmover");
        WorldTestClient watcher = await host.EnterWorldAsync("MVWATCH", "Mvwatcher");
        await mover.CollectAsync();
        await watcher.CollectAsync();
        return (host, mover, watcher);
    }

    /// <summary>The entries (opcode, body) of an SMSG_COMPRESSED_MOVES body.</summary>
    private static List<(ushort Opcode, byte[] Body)> Entries(byte[] compressedMoves)
    {
        byte[] inflated = WorldTestClient.Inflate(compressedMoves);
        Assert.Equal(BinaryPrimitives.ReadUInt32LittleEndian(compressedMoves), (uint)inflated.Length);
        var entries = new List<(ushort, byte[])>();
        for (int at = 0; at < inflated.Length;)
        {
            int size = inflated[at];
            Assert.True(size >= 2 && at + 1 + size <= inflated.Length, "a well framed entry");
            entries.Add((BinaryPrimitives.ReadUInt16LittleEndian(inflated.AsSpan(at + 1)), inflated.AsSpan(at + 3, size - 2).ToArray()));
            at += 1 + size;
        }

        return entries;
    }

    /// <summary>One relay from the mover to the watcher on the manual clock: true when it arrived inside SMSG_COMPRESSED_MOVES, false when plain (it is exactly one of the two).</summary>
    private static async Task<bool> RelayIsBatchedAsync(WorldTestHost host, WorldTestClient mover, WorldTestClient watcher, float dx)
    {
        await mover.SendAsync(WorldOpcode.MsgMoveHeartbeat, await BlockAsync(host, dx));
        await host.AdvanceAsync(100); // the relay, then the tick that flushes a batch
        List<(WorldOpcode Opcode, byte[] Payload)> packets = await watcher.CollectAsync(TimeSpan.FromMilliseconds(500));
        bool plain = packets.Any(p => p.Opcode == WorldOpcode.MsgMoveHeartbeat);
        bool batched = packets.Any(p => p.Opcode == WorldOpcode.SmsgCompressedMoves);
        Assert.True(plain ^ batched, $"the relay arrived plain: {plain}, batched: {batched}");
        return batched;
    }

    /// <summary>
    /// The session counts the movement packets it relayed in this and the last 10 s interval (<c>_movePacketsLastInterval</c>, WorldSession.cpp:505-513): the
    /// count of the interval that just ended still batches the first relay of the next one, and falls away at the roll after that.
    /// </summary>
    [Fact]
    public async Task TheLastIntervalsCount_StillBatchesTheNextIntervalsFirstRelay_AndFallsAwayAtTheRollAfterIt()
    {
        await using WorldTestHost host = WorldTestHost.Start(clock: TestClock.Manual(), configure: o => o.CompressMovementCount = 2);
        await using WorldTestClient mover = await host.EnterWorldAsync("MVMOVER", "Mvmover");
        await using WorldTestClient watcher = await host.EnterWorldAsync("MVWATCH", "Mvwatcher");
        await host.AdvanceAsync(1_000); // the watcher session has started its first interval
        await mover.CollectAsync();
        await watcher.CollectAsync();

        Assert.False(await RelayIsBatchedAsync(host, mover, watcher, 1f)); // 1 in this interval, 0 in the last: under the threshold of 2
        Assert.True(await RelayIsBatchedAsync(host, mover, watcher, 2f));  // 2 in this interval

        await host.AdvanceAsync(11_000); // the interval rolls: 2 become the last interval's count, this one starts at 0
        await watcher.CollectAsync();
        Assert.True(await RelayIsBatchedAsync(host, mover, watcher, 3f));  // only 1 in this interval, but the last one had 2

        await host.AdvanceAsync(11_000); // the next roll: the last interval is now the one with a single relay
        await watcher.CollectAsync();
        Assert.False(await RelayIsBatchedAsync(host, mover, watcher, 4f)); // 1 in this interval, 1 in the last: plain again
    }

    /// <summary>A logout clears the batch and both interval counts (ClearMovementBatch, vmangos clears the compressor in LogoutPlayer): the next login relays plain again.</summary>
    [Fact]
    public async Task AfterTheWatcherLogsOutAndInAgain_TheIntervalCountsStartOver_AndTheFirstRelayIsPlain()
    {
        (WorldTestHost host, WorldTestClient mover, WorldTestClient watcher) = await StartAsync(count: 2);
        await using (host)
        await using (mover)
        await using (watcher)
        {
            await mover.SendAsync(WorldOpcode.MsgMoveHeartbeat, await BlockAsync(host, 1f));
            await watcher.ReadUntilAsync(WorldOpcode.MsgMoveHeartbeat);          // 1 in this interval: plain
            await mover.SendAsync(WorldOpcode.MsgMoveHeartbeat, await BlockAsync(host, 2f));
            await watcher.ReadUntilAsync(WorldOpcode.SmsgCompressedMoves);       // 2: batched, the threshold is reached
            ulong watcherId = await host.PlayerStateAsync("Mvwatcher", p => (ulong)p.Guid.Low);

            await watcher.SendAsync(WorldOpcode.CmsgLogoutRequest, []);
            await watcher.ReadUntilAsync(WorldOpcode.SmsgLogoutComplete);
            await WorldTestHost.WaitForAsync(() => host.World.OnlinePlayerCount == 1, "the watcher left the world");
            await watcher.LoginAsync(watcherId);
            await watcher.CollectAsync();

            // Without the clear the counts of the first login (2 this interval) would batch this relay as well.
            await mover.SendAsync(WorldOpcode.MsgMoveHeartbeat, await BlockAsync(host, 3f));
            List<(WorldOpcode Opcode, byte[] Payload)> after = await watcher.CollectAsync(TimeSpan.FromMilliseconds(500));
            Assert.Contains(after, p => p.Opcode == WorldOpcode.MsgMoveHeartbeat);
            Assert.DoesNotContain(after, p => p.Opcode == WorldOpcode.SmsgCompressedMoves);
        }
    }

    [Fact]
    public async Task WithTheOptionOff_AnObserverIsSentTheRelayPlain()
    {
        (WorldTestHost host, WorldTestClient mover, WorldTestClient watcher) = await StartAsync(count: 0);
        await using (host)
        await using (mover)
        await using (watcher)
        {
            await mover.SendAsync(WorldOpcode.MsgMoveHeartbeat, await BlockAsync(host, 1f));
            Assert.Contains(await watcher.CollectFromAsync(WorldOpcode.MsgMoveHeartbeat), p => p.Opcode == WorldOpcode.MsgMoveHeartbeat);
            await watcher.AssertNoPacketsAsync(WorldOpcode.SmsgCompressedMoves);
        }
    }

    [Fact]
    public async Task OnceTheThresholdIsReached_TheRelayArrivesInsideOneCompressedMovesPacket_AfterTheOnesBelowItWerePlain()
    {
        (WorldTestHost host, WorldTestClient mover, WorldTestClient watcher) = await StartAsync(count: 2);
        await using (host)
        await using (mover)
        await using (watcher)
        {
            byte[] first = await BlockAsync(host, 1f);
            await mover.SendAsync(WorldOpcode.MsgMoveHeartbeat, first);
            byte[] plain = await watcher.ReadUntilAsync(WorldOpcode.MsgMoveHeartbeat); // the first relay is under the threshold (1 < 2): sent at once

            byte[] second = await BlockAsync(host, 2f);
            await mover.SendAsync(WorldOpcode.MsgMoveHeartbeat, second);
            List<(ushort Opcode, byte[] Body)> batched = Entries(await watcher.ReadUntilAsync(WorldOpcode.SmsgCompressedMoves)); // the second reaches it: batched
            (ushort opcode, byte[] body) = Assert.Single(batched);

            Assert.Equal((ushort)WorldOpcode.MsgMoveHeartbeat, opcode);
            Assert.Equal(plain.Length, body.Length); // the same relay, the mover's packed guid and block
            Assert.Equal(plain.AsSpan(0, 2).ToArray(), body.AsSpan(0, 2).ToArray()); // same packed guid
            Assert.NotEqual(plain, body); // not the first block again: the second one moved further
        }
    }
}
