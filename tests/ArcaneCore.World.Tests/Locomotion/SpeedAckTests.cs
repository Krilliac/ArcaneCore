using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests.Locomotion;

/// <summary>
/// CMSG_FORCE_*_SPEED_CHANGE_ACK (vmangos HandleForceSpeedChangeAckOpcodes, MovementHandler.cpp:415-534) through the real
/// opcode table, with the observers' MSG_MOVE_SET_*_SPEED relay.
/// </summary>
public sealed class SpeedAckTests
{
    private static readonly byte[] PackedGuid1 = [0x01, 0x01];

    private static byte[] Ack(uint counter, float speed)
    {
        var info = new MovementInfo { Flags = MovementFlags.Forward, Time = 500, X = -8949.95f, Y = -132.493f, Z = 83.5312f };
        var writer = new PacketWriter(56);
        writer.WriteUInt64(1);
        writer.WriteUInt32(counter);
        info.Write(writer);
        writer.WriteSingle(speed);
        return writer.ToArray();
    }

    private static byte[] Heartbeat()
    {
        var writer = new PacketWriter(48);
        new MovementInfo { Flags = MovementFlags.Forward, Time = 600, X = -8949.95f, Y = -132.493f, Z = 83.5312f }.Write(writer);
        return writer.ToArray();
    }

    [Theory]
    [InlineData(MoveType.Run)]
    [InlineData(MoveType.RunBack)]
    [InlineData(MoveType.Swim)]
    [InlineData(MoveType.Walk)]
    [InlineData(MoveType.SwimBack)]
    public async Task TheMatchingAck_AppliesTheSpeed_AndIsRelayedToObserversOnly(MoveType type)
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient mover = await host.EnterWorldAsync("MOVER", "Mover");
        await using WorldTestClient watcher = await host.EnterWorldAsync("WATCHER", "Watcher");
        await mover.CollectAsync();
        await watcher.CollectAsync();
        Player player = await host.PlayerAsync("Mover");
        float target = UnitSpeed.BaseSpeed(type) * 1.4f;

        await host.OnWorldAsync(() => UnitSpeed.SetRate(player, type, 1.4f));
        byte[] order = await mover.ReadUntilAsync(SpeedPackets.ForceOpcode(type));
        Assert.Equal(SpeedPackets.BuildForceChange(1, 0, target), order);
        Assert.Equal(UnitSpeed.BaseSpeed(type), await host.OnWorldAsync(() => UnitSpeed.Get(player, type))); // not before the ack

        // A wrong counter and a wrong speed are ignored (counted); the right one applies.
        await mover.SendAsync(SpeedPackets.AckOpcode(type), Ack(counter: 5, target));
        await mover.SendAsync(SpeedPackets.AckOpcode(type), Ack(counter: 0, target + 0.5f));
        await mover.SendAsync(SpeedPackets.AckOpcode(type), Ack(counter: 0, target));
        await mover.SendAsync(WorldOpcode.MsgMoveHeartbeat, Heartbeat());

        byte[] relayed = await watcher.ReadUntilAsync(SpeedPackets.ObserverOpcode(type));
        Assert.Equal(PackedGuid1, relayed[..2]);
        var reader = new PacketReader(relayed.AsSpan(2));
        MovementInfo block = MovementInfo.Read(ref reader);
        Assert.Equal(target, reader.ReadSingle());
        Assert.Equal(0, reader.Remaining);
        Assert.Equal(MovementFlags.Forward, block.Flags);

        Assert.Equal(target, await host.OnWorldAsync(() => UnitSpeed.Get(player, type)));
        Assert.Equal(2, await host.OnWorldAsync(() => player.Locomotion.WrongAckCount));
        Assert.DoesNotContain(await mover.CollectAsync(), p => p.Opcode == SpeedPackets.ObserverOpcode(type)); // never to the mover
    }

    [Fact]
    public async Task AReplayedAck_IsIgnored()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient mover = await host.EnterWorldAsync("MOVER", "Mover");
        await using WorldTestClient watcher = await host.EnterWorldAsync("WATCHER", "Watcher");
        await mover.CollectAsync();
        await watcher.CollectAsync();
        Player player = await host.PlayerAsync("Mover");

        await host.OnWorldAsync(() => UnitSpeed.SetRate(player, MoveType.Run, 1.4f));
        float target = 7.0f * 1.4f;
        await mover.SendAsync(WorldOpcode.CmsgForceRunSpeedChangeAck, Ack(0, target));
        await mover.SendAsync(WorldOpcode.CmsgForceRunSpeedChangeAck, Ack(0, target));
        await mover.SendAsync(WorldOpcode.MsgMoveHeartbeat, Heartbeat());

        int relays = 0;
        while (true)
        {
            (WorldOpcode opcode, _) = await watcher.ReadAsync();
            if (opcode == WorldOpcode.MsgMoveSetRunSpeed)
            {
                relays++;
            }
            else if (opcode == WorldOpcode.MsgMoveHeartbeat)
            {
                break;
            }
        }

        Assert.Equal(1, relays);
    }
}
