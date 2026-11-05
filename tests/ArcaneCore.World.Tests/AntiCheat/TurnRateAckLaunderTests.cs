using System.Collections.Concurrent;
using ArcaneCore.Game;
using ArcaneCore.Game.AntiCheat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.AntiCheat;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.AntiCheat;

/// <summary>
/// CMSG_FORCE_TURN_RATE_CHANGE_ACK must not store a position the anticheat never judged. An unsolicited ack naming the current rate
/// (no pending order) used to be stored, and the next real movement packet then saw the server position moved
/// (MovementAntiCheat.CheckServerPosition), took it for a server relocation and re-baselined, so the jump was never scored. vmangos
/// accepts a speed ack only against a pending change (HandleForceSpeedChangeAckOpcodes, MovementHandler.cpp:464-483 at vmangos d9173eca)
/// and runs the cheat tests before relocating (:489-494).
/// </summary>
public sealed class TurnRateAckLaunderTests
{
    [Fact]
    public async Task AnUnsolicitedTurnRateAck_CannotLaunderABlink_TheNextBlockIsJudgedFromTheOldPosition()
    {
        await using var host = WorldTestHost.Start();
        AntiCheatFeature feature = host.WorldServices.GetRequiredService<AntiCheatFeature>();
        await host.OnWorldAsync(() => feature.ApplyOptions(new AntiCheatOptions { BaselineGapMs = 600_000 }));
        var seen = new ConcurrentQueue<(string Player, AntiCheatFinding Finding)>();
        feature.FindingRecorded += (player, finding, _, _) => seen.Enqueue((player.Name, finding));
        await using WorldTestClient client = await host.EnterWorldAsync("TURNLAUNDER", "Turnlaunder");
        await client.CollectAsync();
        Player player = await host.PlayerAsync("Turnlaunder");
        (ulong guid, float rate, float x, float y, float z) = await host.OnWorldAsync(() => (player.Guid.Value, player.TurnRate, player.X, player.Y, player.Z));

        static byte[] Block(uint time, float x, float y, float z)
        {
            var writer = new PacketWriter(48);
            new MovementInfo { Flags = MovementFlags.Forward, Time = time, X = x, Y = y, Z = z }.Write(writer);
            return writer.ToArray();
        }

        uint time = 100_000;
        await client.SendAsync(WorldOpcode.MsgMoveStartForward, Block(time, x, y, z));
        await Task.Delay(100);
        time += 100;
        await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, Block(time, x + 0.7f, y, z));
        await client.CollectThroughBarrierAsync();
        Assert.Empty(seen);

        // No turn rate order was ever sent: an ack of the current rate with a block 80 yards away, then a heartbeat from there.
        await Task.Delay(100);
        time += 100;
        var ack = new PacketWriter(56);
        ack.WriteUInt64(guid);
        ack.WriteUInt32(0);
        new MovementInfo { Flags = MovementFlags.Forward, Time = time, X = x + 80.7f, Y = y, Z = z }.Write(ack);
        ack.WriteSingle(rate);
        await client.SendAsync(WorldOpcode.CmsgForceTurnRateChangeAck, ack.ToArray());
        await Task.Delay(100);
        time += 100;
        await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, Block(time, x + 80.7f, y, z));
        await client.CollectThroughBarrierAsync();

        // RED on origin/main 46e2ecf6d: the ack stored the block unjudged and the heartbeat was then trusted as a server relocation.
        Assert.Contains(seen, s => s.Player == "Turnlaunder" && s.Finding.Type == AntiCheatViolation.Teleport);
    }
}
