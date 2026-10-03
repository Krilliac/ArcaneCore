using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests.Locomotion;

/// <summary>
/// CMSG_MOVE_WATER_WALK_ACK / HOVER_ACK / FEATHER_FALL_ACK (vmangos HandleMovementFlagChangeToggleAck,
/// MovementHandler.cpp:536-644): u64 GUID, u32 counter, movement block, u32 apply.
/// </summary>
public sealed class FlagAckTests
{
    private static readonly byte[] PackedGuid1 = [0x01, 0x01];

    private static byte[] Ack(uint counter, MovementFlags flags, uint apply)
    {
        var info = new MovementInfo { Flags = flags, Time = 500, X = -8949.95f, Y = -132.493f, Z = 83.5312f };
        var writer = new PacketWriter(56);
        writer.WriteUInt64(1);
        writer.WriteUInt32(counter);
        info.Write(writer);
        writer.WriteUInt32(apply);
        return writer.ToArray();
    }

    private static byte[] Heartbeat(MovementFlags flags = MovementFlags.None)
    {
        var writer = new PacketWriter(48);
        new MovementInfo { Flags = flags, Time = 600, X = -8949.95f, Y = -132.493f, Z = 83.5312f }.Write(writer);
        return writer.ToArray();
    }

    public static TheoryData<MovementChangeType, WorldOpcode, WorldOpcode, WorldOpcode, MovementFlags> Cases => new()
    {
        { MovementChangeType.WaterWalk, WorldOpcode.SmsgMoveWaterWalk, WorldOpcode.CmsgMoveWaterWalkAck, WorldOpcode.MsgMoveWaterWalk, MovementFlags.WaterWalking },
        { MovementChangeType.Hover, WorldOpcode.SmsgMoveSetHover, WorldOpcode.CmsgMoveHoverAck, WorldOpcode.MsgMoveHover, MovementFlags.Hover },
        { MovementChangeType.FeatherFall, WorldOpcode.SmsgMoveFeatherFall, WorldOpcode.CmsgMoveFeatherFallAck, WorldOpcode.MsgMoveFeatherFall, MovementFlags.SafeFall },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task TheAck_AppliesTheFlag_AndIsRelayedToObserversButNotTheMover(
        MovementChangeType type, WorldOpcode order, WorldOpcode ack, WorldOpcode relay, MovementFlags flag)
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient mover = await host.EnterWorldAsync("MOVER", "Mover");
        await using WorldTestClient watcher = await host.EnterWorldAsync("WATCHER", "Watcher");
        await mover.CollectAsync();
        await watcher.CollectAsync();
        Player player = await host.PlayerAsync("Mover");

        await host.OnWorldAsync(() => MovementControl.Request(player, type, apply: true));
        Assert.Equal([.. PackedGuid1, 0, 0, 0, 0], await mover.ReadUntilAsync(order));
        Assert.False(await host.OnWorldAsync(() => player.Movement.HasFlag(flag))); // not before the ack

        // A wrong apply flag and a wrong counter are ignored; so is a made-up one.
        await mover.SendAsync(ack, Ack(counter: 0, flag, apply: 0));
        await mover.SendAsync(ack, Ack(counter: 7, flag, apply: 1));
        await mover.SendAsync(ack, Ack(counter: 0, flag, apply: 1));
        await mover.SendAsync(WorldOpcode.MsgMoveHeartbeat, Heartbeat(flag)); // a real client keeps sending the flag it was given

        byte[] relayed = await watcher.ReadUntilAsync(relay);
        Assert.Equal(PackedGuid1, relayed[..2]);
        var reader = new PacketReader(relayed.AsSpan(2));
        Assert.Equal(flag, MovementInfo.Read(ref reader).Flags & flag);
        Assert.Equal(0, reader.Remaining);

        Assert.True(await host.OnWorldAsync(() => player.Movement.HasFlag(flag)));
        Assert.Equal(2, await host.OnWorldAsync(() => player.Locomotion.WrongAckCount));
        Assert.DoesNotContain(await mover.CollectAsync(), p => p.Opcode == relay); // never to the mover itself
        Assert.False(await host.OnWorldAsync(() => player.Locomotion.Pending.HasPending));
    }

    [Fact]
    public async Task AReplayedAck_IsRelayedOnce()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient mover = await host.EnterWorldAsync("MOVER", "Mover");
        await using WorldTestClient watcher = await host.EnterWorldAsync("WATCHER", "Watcher");
        await mover.CollectAsync();
        await watcher.CollectAsync();
        Player player = await host.PlayerAsync("Mover");

        await host.OnWorldAsync(() => MovementControl.Request(player, MovementChangeType.Hover, apply: true));
        await mover.SendAsync(WorldOpcode.CmsgMoveHoverAck, Ack(0, MovementFlags.Hover, 1));
        await mover.SendAsync(WorldOpcode.CmsgMoveHoverAck, Ack(0, MovementFlags.Hover, 1));
        await mover.SendAsync(WorldOpcode.MsgMoveHeartbeat, Heartbeat());

        int hovers = 0;
        while (true)
        {
            (WorldOpcode opcode, _) = await watcher.ReadAsync();
            if (opcode == WorldOpcode.MsgMoveHover)
            {
                hovers++;
            }
            else if (opcode == WorldOpcode.MsgMoveHeartbeat)
            {
                break;
            }
        }

        Assert.Equal(1, hovers);
    }

    [Fact]
    public async Task ARootedPlayer_StaysRooted_WhenItsHeartbeatOmitsTheFlag()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("ROOTED", "Rooted");
        await using WorldTestClient watcher = await host.EnterWorldAsync("WATCHER", "Watcher");
        await client.CollectAsync();
        await watcher.CollectAsync();
        Player player = await host.PlayerAsync("Rooted");

        await client.SendAsync(WorldOpcode.CmsgLogoutRequest, []); // roots with counter 0
        await client.ReadUntilAsync(WorldOpcode.SmsgLogoutResponse);
        var info = new MovementInfo { Flags = MovementFlags.Root, Time = 500, X = -8949.95f, Y = -132.493f, Z = 83.5312f };
        var ack = new PacketWriter(48);
        ack.WriteUInt64(1);
        ack.WriteUInt32(0);
        info.Write(ack);
        await client.SendAsync(WorldOpcode.CmsgForceMoveRootAck, ack.ToArray());
        await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, Heartbeat()); // no Root flag in this block

        await watcher.ReadUntilAsync(WorldOpcode.MsgMoveHeartbeat); // the heartbeat was processed after the ack
        Assert.True(await host.OnWorldAsync(() => player.Movement.HasFlag(MovementFlags.Root)));
    }
}
