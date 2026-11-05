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
