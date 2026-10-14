using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Death.Resurrection;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests.Death;

/// <summary>CMSG_RESURRECT_RESPONSE over a real socket (vmangos HandleResurrectResponseOpcode, MiscHandler.cpp:605-622).</summary>
public sealed class ResurrectionWorldTests
{
    private static readonly ObjectGuid Healer = ObjectGuid.WithEntry(HighGuid.Unit, 6491, 77);

    private static byte[] Response(ObjectGuid resurrector, byte status)
    {
        var writer = new PacketWriter(9);
        writer.WriteUInt64(resurrector.Value);
        writer.WriteByte(status);
        return writer.ToArray();
    }

    private static async Task<WorldTestClient> GhostWithARequestAsync(WorldTestHost host, string account, string name)
    {
        WorldTestClient client = await host.EnterWorldAsync(account, name);
        await client.CollectAsync();
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer(name)!;
            player.Health = 0;
            player.Map!.Combat.KillPlayer(player);
            Assert.True(player.Map!.Combat.RepopPlayer(player));
            ResurrectionRequests.Set(player, new ResurrectionRequest(Healer, 0, 0, player.X, player.Y, player.Z, 0f, 5, 0));
        });
        return client;
    }

    [Fact]
    public async Task AcceptingTheRequestOfACreature_ResurrectsTheGhostWhereItStands_WithTheOfferedHealth()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await GhostWithARequestAsync(host, "RESP1", "Respone");

        await client.SendAsync(WorldOpcode.CmsgResurrectResponse, Response(Healer, 1));

        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Respone")!.IsAlive, "the player to be resurrected");
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Respone")!;
            Assert.Equal(5u, player.Health);
            Assert.Equal(0u, (uint)(player.Flags & PlayerFlags.Ghost));
            Assert.Null(player.Combat.Corpse);
        });
    }

    [Fact]
    public async Task DecliningTheRequest_ClearsIt_AndAnAcceptanceFromSomeoneElseDoesNothing()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await GhostWithARequestAsync(host, "RESP2", "Resptwo");

        await client.SendAsync(WorldOpcode.CmsgResurrectResponse, Response(ObjectGuid.WithEntry(HighGuid.Unit, 1, 5), 1)); // not the caster
        await client.SendAsync(WorldOpcode.CmsgResurrectResponse, Response(Healer, 0));                                    // decline

        await host.WaitForWorldAsync(() => ResurrectionRequests.Get(host.World.FindOnlinePlayer("Resptwo")!) is null, "the request to be cleared");
        Assert.False(await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Resptwo")!.IsAlive));
    }

    [Fact]
    public async Task MovingPacketWhileDeadAtBodyWithNoPendingRoot_ReordersForceMoveRootAndHealsDesync()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("DESYNC1", "Desynced");
        await client.CollectAsync();

        (float x, float y, float z) = await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Desynced")!;
            player.Health = 0;
            player.Map!.Combat.KillPlayer(player);
            // Simulate the initial root order already having been sent/lost while the client thinks it is alive.
            _ = player.Locomotion.Pending.ResolveAll().ToList();
            player.NeedsVisibilityUpdate = false;
            return (player.X, player.Y, player.Z);
        });
        await client.CollectAsync();

        var move = new MovementInfo
        {
            Flags = MovementFlags.Forward,
            Time = 100,
            X = x + 1f,
            Y = y,
            Z = z,
            Orientation = 0f,
        };
        var writer = new PacketWriter(64);
        move.Write(writer);
        await client.SendAsync(WorldOpcode.MsgMoveStartForward, writer.ToArray());

        await host.WaitForWorldAsync(
            () => host.World.FindOnlinePlayer("Desynced")!.Combat.LastLifeDesyncLogMs > 0,
            "the life-state desync guard to detect and heal the moving corpse");

        List<(WorldOpcode Opcode, byte[] Payload)> packets = await client.CollectAsync();
        Assert.Contains(packets, p => p.Opcode == WorldOpcode.SmsgForceMoveRoot);
    }
}
