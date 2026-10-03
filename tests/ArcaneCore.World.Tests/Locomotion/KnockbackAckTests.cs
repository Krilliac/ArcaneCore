using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Locomotion;

/// <summary>CMSG_MOVE_KNOCK_BACK_ACK (vmangos HandleMoveKnockBackAck, MovementHandler.cpp:747-802).</summary>
public sealed class KnockbackAckTests
{
    private static readonly byte[] PackedGuid1 = [0x01, 0x01];

    private static SpellSystem Spells(WorldTestHost host) => host.WorldServices.GetRequiredService<SpellFeature>().System;

    private static byte[] Ack(uint counter, float cos, float sin, float xy, float z, MovementFlags flags = MovementFlags.Jumping)
    {
        var info = new MovementInfo
        {
            Flags = flags, Time = 500, X = -8949.95f, Y = -132.493f, Z = 83.5312f,
            JumpCosAngle = cos, JumpSinAngle = sin, JumpXySpeed = xy, JumpZSpeed = z,
        };
        var writer = new PacketWriter(64);
        writer.WriteUInt64(1);
        writer.WriteUInt32(counter);
        info.Write(writer);
        return writer.ToArray();
    }

    private static byte[] Heartbeat()
    {
        var writer = new PacketWriter(48);
        new MovementInfo { Time = 600, X = -8949.95f, Y = -132.493f, Z = 83.5312f }.Write(writer);
        return writer.ToArray();
    }

    [Fact]
    public async Task TheAck_IsRelayedToObserversWithTheFourNumbers_OnlyWhenItRepeatsTheOrder()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient mover = await host.EnterWorldAsync("MOVER", "Mover");
        await using WorldTestClient watcher = await host.EnterWorldAsync("WATCHER", "Watcher");
        await mover.CollectAsync();
        await watcher.CollectAsync();
        Player player = await host.PlayerAsync("Mover");

        // The service sends the order exactly as a knock back effect does (the spell path is covered by the Game tests).
        await host.OnWorldAsync(() => KnockbackService.KnockBack(Spells(host), player, 0f, 7.5f, 5f));
        byte[] order = await mover.ReadUntilAsync(WorldOpcode.SmsgMoveKnockBack);
        Assert.Equal(KnockbackPackets.BuildOrder(1, 0, new KnockbackInfo(1f, 0f, 7.5f, -5f)), order);

        await mover.SendAsync(WorldOpcode.CmsgMoveKnockBackAck, Ack(counter: 3, 1, 0, 7.5f, -5f));     // never issued
        await mover.SendAsync(WorldOpcode.CmsgMoveKnockBackAck, Ack(counter: 0, 1, 0, 7.5f, -5.5f));   // wrong speed
        await mover.SendAsync(WorldOpcode.CmsgMoveKnockBackAck, Ack(counter: 0, 1, 0, 7.5f, -5f, MovementFlags.None)); // no jump block at all
        await mover.SendAsync(WorldOpcode.CmsgMoveKnockBackAck, Ack(counter: 0, 1, 0, 7.5f, -5f));
        await mover.SendAsync(WorldOpcode.MsgMoveHeartbeat, Heartbeat());

        byte[] relayed = await watcher.ReadUntilAsync(WorldOpcode.MsgMoveKnockBack);
        Assert.Equal(PackedGuid1, relayed[..2]);
        var reader = new PacketReader(relayed.AsSpan(2));
        MovementInfo block = MovementInfo.Read(ref reader);
        Assert.Equal((1f, 0f, 7.5f, -5f), (reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()));
        Assert.Equal(0, reader.Remaining);
        Assert.True(block.HasFlag(MovementFlags.Jumping));

        Assert.Equal(3, await host.OnWorldAsync(() => player.Locomotion.WrongAckCount));
        Assert.False(await host.OnWorldAsync(() => player.Locomotion.Pending.HasPending));
        Assert.DoesNotContain(await mover.CollectAsync(), p => p.Opcode == WorldOpcode.MsgMoveKnockBack);
    }

    [Fact]
    public async Task TheAck_EndsTheFallInProgress()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient mover = await host.EnterWorldAsync("MOVER", "Mover");
        await mover.CollectAsync();
        Player player = await host.PlayerAsync("Mover");
        await host.OnWorldAsync(() =>
        {
            player.Locomotion.FallStartZ = 120f;
            KnockbackService.KnockBack(Spells(host), player, 0f, 7.5f, 5f);
        });
        await mover.ReadUntilAsync(WorldOpcode.SmsgMoveKnockBack);

        await mover.SendAsync(WorldOpcode.CmsgMoveKnockBackAck, Ack(counter: 0, 1, 0, 7.5f, -5f, MovementFlags.Jumping | MovementFlags.FallingFar));
        await host.WaitForWorldAsync(() => !player.Locomotion.Pending.HasPending, "the ack to be processed");

        // The fall that was in progress is forgotten and the block that starts the launch does not start another one
        // (vmangos SetFallInformation(0), then a relocation without UpdateFallInformationIfNeed).
        Assert.False(await host.OnWorldAsync(() => player.Locomotion.IsFalling));
    }
}
