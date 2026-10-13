using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using Xunit;

namespace ArcaneCore.World.Tests.Handlers;

/// <summary>CMSG_WORLD_TELEPORT and CMSG_TELEPORT_TO_UNIT (vmangos MiscHandler.cpp HandleWorldTeleportOpcode / HandleTeleportToUnitOpcode).</summary>
public sealed class GmClientHandlersTests
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(200);

    private static byte[] WorldTeleport(uint map, float x, float y, float z, float o)
    {
        var w = new PacketWriter(24);
        w.WriteUInt32(0);
        w.WriteUInt32(map);
        w.WriteSingle(x);
        w.WriteSingle(y);
        w.WriteSingle(z);
        w.WriteSingle(o);
        return w.ToArray();
    }

    private static byte[] Name(string name)
    {
        var w = new PacketWriter(16);
        w.WriteCString(name);
        return w.ToArray();
    }

    private static async Task<MovementInfo> AcknowledgeAsync(WorldTestClient client, WorldTestHost host, string name)
    {
        var reader = new PacketReader(await client.ReadUntilAsync(WorldOpcode.MsgMoveTeleportAck));
        ulong guid = reader.ReadPackedGuid();
        uint counter = reader.ReadUInt32();
        MovementInfo info = MovementInfo.Read(ref reader);
        var reply = new PacketWriter(16);
        reply.WriteUInt64(guid);
        reply.WriteUInt32(counter);
        reply.WriteUInt32(0);
        await client.SendAsync(WorldOpcode.MsgMoveTeleportAck, reply.ToArray());
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(name)!.X == info.X, "teleport ack handled");
        return info;
    }

    [Fact]
    public async Task WorldTeleport_PlainPlayer_GetsNoPermissionNotice()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("GCWTP", "Gcwtp");
        await client.CollectAsync(Quiet);

        await client.SendAsync(WorldOpcode.CmsgWorldTeleport, WorldTeleport(0, -8800f, -100f, 90f, 0f));

        var notice = new PacketReader(await client.ReadUntilAsync(WorldOpcode.SmsgNotification));
        Assert.Equal(GmClientHandlers.NoPermission, notice.ReadCString());
        Assert.DoesNotContain(await client.CollectAsync(Quiet), p => p.Opcode == WorldOpcode.MsgMoveTeleportAck);
    }

    [Fact]
    public async Task WorldTeleport_Administrator_Teleports_AndShortPacketIsIgnored()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("GCWTA", "Gcwta", AccountSecurity.Administrator);
        await host.PlaceAsync("Gcwta", -8800f, -100f, 90f);
        await admin.CollectAsync(Quiet);

        await admin.SendAsync(WorldOpcode.CmsgWorldTeleport, [1, 2, 3]);
        await admin.SendAsync(WorldOpcode.CmsgWorldTeleport, WorldTeleport(0, -8700f, -100f, 91f, 1f));

        MovementInfo arrival = await AcknowledgeAsync(admin, host, "Gcwta");
        Assert.Equal((-8700f, -100f, 91f), (arrival.X, arrival.Y, arrival.Z));
    }

    [Fact]
    public async Task TeleportToUnit_RunsGoname_AndIgnoresOverlongNames()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("GCTUGM", "Gctugm", AccountSecurity.Administrator);
        await using WorldTestClient victim = await host.EnterWorldAsync("GCTUVIC", "Gctuvic");
        await host.PlaceAsync("Gctugm", -8800f, -100f, 90f);
        await host.PlaceAsync("Gctuvic", -8700f, -100f, 91f);
        await gm.CollectAsync(Quiet);
        await victim.CollectAsync(Quiet);

        await gm.SendAsync(WorldOpcode.CmsgTeleportToUnit, Name("Thisnameistoolong"));
        Assert.DoesNotContain(await gm.CollectAsync(Quiet), p => p.Opcode == WorldOpcode.MsgMoveTeleportAck);

        await gm.SendAsync(WorldOpcode.CmsgTeleportToUnit, Name("Gctuvic"));
        MovementInfo arrival = await AcknowledgeAsync(gm, host, "Gctugm");
        Assert.Equal((-8700f, -100f, 96f), (arrival.X, arrival.Y, arrival.Z));
    }
}
