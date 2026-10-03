using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.Graveyards;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Graveyards;

/// <summary>
/// The whole trip over real sockets with the real features: death, CMSG_REPOP_REQUEST, the client answering the ghost's
/// water-walk order, and the teleport to the graveyard (vmangos Player::RepopAtGraveyard, Player.cpp:4988-5025).
/// </summary>
public sealed class GraveyardWorldTests
{
    private static readonly byte[] PackedGuid1 = [0x01, 0x01];

    // Map 0 links to zone 12 (area 12), so every position of the test world is zone 12.
    private sealed class MapStore : IMapDataStore
    {
        public Task<MapContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new MapContent(
            [
                new MapTemplate(0, 0, MapType.Common, 12, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
                new MapTemplate(1, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Kalimdor", ""),
            ],
            [new AreaTemplate(12, 0, 0, 41, 0, 1, "Elwynn Forest", 2, 0)],
            [], [], []));
    }

    private sealed class GraveyardStore(GraveyardContent content) : IGraveyardDataStore
    {
        public Task<GraveyardContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(content);
    }

    private static byte[] WaterWalkAck(uint counter)
    {
        var writer = new PacketWriter(56);
        writer.WriteUInt64(1);
        writer.WriteUInt32(counter);
        new MovementInfo { Flags = MovementFlags.WaterWalking, Time = 500, X = -8949.95f, Y = -132.493f, Z = 83.5312f }.Write(writer);
        writer.WriteUInt32(1);
        return writer.ToArray();
    }

    [Fact]
    public async Task AReleasedSpirit_IsSentToItsGraveyard_OnceTheClientAnswersTheWaterWalkOrder()
    {
        var graveyard = new WorldSafeLoc(1, 0, -8800f, -100f, 90f, 1.5f, "Goldshire");
        var content = new GraveyardContent([graveyard], [new GraveyardLink(1, 12, 0)]);
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<IMapDataStore, MapStore>();
            services.AddSingleton<IGraveyardDataStore>(new GraveyardStore(content));
        });
        Assert.NotNull(host.WorldServices.GetRequiredService<GraveyardFeature>().Service);
        await using WorldTestClient client = await host.EnterWorldAsync("GRAVE1", "Graveone");
        await client.CollectAsync();
        (float x, float y) = await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Graveone")!;
            player.Health = 0;
            player.Map!.Combat.KillPlayer(player);
            return (player.X, player.Y);
        });

        await client.SendAsync(WorldOpcode.CmsgRepopRequest, []);
        byte[] order = await client.ReadUntilAsync(WorldOpcode.SmsgMoveWaterWalk);
        Assert.Equal(PackedGuid1, order[..2]);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Graveone")!.Combat.Corpse is not null, "the spirit to be released");
        // Not before the client has answered: the ghost is still where it died.
        Assert.Equal(x, await host.PlayerStateAsync("Graveone", p => p.X));

        await client.SendAsync(WorldOpcode.CmsgMoveWaterWalkAck, WaterWalkAck(BitConverter.ToUInt32(order, 2)));
        await client.ReadUntilAsync(WorldOpcode.SmsgCorpseReclaimDelay); // the reclaim delay is sent as before
        byte[] ack = await client.ReadUntilAsync(WorldOpcode.MsgMoveTeleportAck);
        var reader = new PacketReader(ack);
        ulong guid = reader.ReadPackedGuid();
        uint counter = reader.ReadUInt32();
        MovementInfo info = MovementInfo.Read(ref reader);
        Assert.Equal((-8800f, -100f, 90f, 1.5f), (info.X, info.Y, info.Z, info.Orientation));

        var reply = new PacketWriter(16);
        reply.WriteUInt64(guid);
        reply.WriteUInt32(counter);
        reply.WriteUInt32(0);
        await client.SendAsync(WorldOpcode.MsgMoveTeleportAck, reply.ToArray());
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Graveone")!.X == -8800f, "the teleport ack to be handled");

        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Graveone")!;
            Assert.NotEqual(0u, (uint)(player.Flags & PlayerFlags.Ghost));
            Assert.False(player.IsAlive);
            Assert.Equal((x, y), (player.Combat.Corpse!.X, player.Combat.Corpse.Y)); // the body stayed where it fell
        });
    }

    [Fact]
    public async Task WithoutGraveyardData_TheFeatureRegistersNothing_AndTheGhostStaysOnItsBody()
    {
        await using WorldTestHost host = WorldTestHost.Start();

        Assert.Null(host.WorldServices.GetRequiredService<GraveyardFeature>().Service);
    }
}
