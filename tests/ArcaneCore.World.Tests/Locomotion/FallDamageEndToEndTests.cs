using ArcaneCore.Game.Locomotion;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests.Locomotion;

/// <summary>A fall through the real movement handler: JUMP, FALLING heartbeat, FALL_LAND (vmangos MovementHandler.cpp:333-344).</summary>
public sealed class FallDamageEndToEndTests
{
    private static byte[] Block(MovementFlags flags, float z, uint fallTime = 0)
    {
        var writer = new PacketWriter(64);
        new MovementInfo
        {
            Flags = flags, Time = 100, X = -8949.95f, Y = -132.493f, Z = z, Orientation = 1, FallTime = fallTime,
            JumpZSpeed = -7.95f, JumpCosAngle = 1, JumpSinAngle = 0, JumpXySpeed = 7,
        }.Write(writer);
        return writer.ToArray();
    }

    private static async Task<List<(WorldOpcode Opcode, byte[] Payload)>> FallAsync(WorldTestClient client, float from, float to, uint fallTime)
    {
        await client.SendAsync(WorldOpcode.MsgMoveJump, Block(MovementFlags.Jumping, from));
        await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, Block(MovementFlags.Jumping | MovementFlags.FallingFar, (from + to) / 2, 1500));
        await client.SendAsync(WorldOpcode.MsgMoveFallLand, Block(MovementFlags.None, to, fallTime));
        return await client.CollectAsync();
    }

    [Fact]
    public async Task AFortyYardFall_SendsOneFallDamageLog_AndHurtsThePlayer()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("FALLER", "Faller");
        await client.CollectAsync();
        await host.OnWorldAsync(() =>
        {
            ArcaneCore.Game.Entities.Player p = host.World.FindOnlinePlayer("Faller")!;
            p.MaxHealth = 3000;
            p.Health = 3000;
        });

        List<(WorldOpcode Opcode, byte[] Payload)> got = await FallAsync(client, 100, 60, 3000);

        byte[] log = Assert.Single(got, p => p.Opcode == WorldOpcode.SmsgEnvironmentaldamagelog).Payload;
        Assert.Equal(2, log[8]);
        Assert.Equal(1432u, BitConverter.ToUInt32(log, 9));
        Assert.Equal(1568u, await host.PlayerStateAsync("Faller", p => p.Health));
    }

    [Fact]
    public async Task ALandingReportedAfter1228Ms_IsHarmless()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("FALLER", "Faller");
        await client.CollectAsync();
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Faller")!.MaxHealth = 3000);

        List<(WorldOpcode Opcode, byte[] Payload)> got = await FallAsync(client, 100, 60, 1228);

        Assert.DoesNotContain(got, p => p.Opcode == WorldOpcode.SmsgEnvironmentaldamagelog);
        Assert.False(await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Faller")!.Locomotion.IsFalling));
    }
}
