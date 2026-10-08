using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Tests.Creatures;
using ArcaneCore.World.Tests.GameObjects;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm;

public sealed class GmGoSpawnCommandTests
{
    [Fact]
    public void GoSpawnCommands_NeedTicketMasterRetailLevel()
    {
        CommandTable table = ChatCommands.CreateTable();
        foreach (string path in new[] { "go creature", "go object" })
        {
            Assert.Null(table.Resolve(path, AccountSecurity.Moderator));
            Assert.Equal(path, table.Lookup(path, AccountSecurity.GameMaster).Path);
            Assert.Equal(2, table.Lookup(path, AccountSecurity.GameMaster).Command?.RequiredLevel(table.Gm));
        }
    }

    [Fact]
    public async Task GoCreature_TeleportsToSpawnGuidTemplateEntryAndName()
    {
        await using WorldTestHost host = StartWithSpawns();
        await using WorldTestClient gm = await host.EnterWorldAsync("GOCREGM", "Gocregm", AccountSecurity.GameMaster);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".go creature 991961");
        MovementInfo first = await AcknowledgeAsync(gm, host, "Gocregm");
        Assert.Equal((-8940f, -132f, 83.5f), (first.X, first.Y, first.Z));

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".go creature id 991962");
        MovementInfo second = await AcknowledgeAsync(gm, host, "Gocregm");
        Assert.Equal((-8930f, -132f, 83.5f), (second.X, second.Y, second.Z));

        // A word that is not a number is a creature name part (TeleportCommands.cpp CREATURE_LINK_RAW).
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".go creature test bear");
        MovementInfo third = await AcknowledgeAsync(gm, host, "Gocregm");
        Assert.Equal((-8940f, -132f, 83.5f), (third.X, third.Y, third.Z));
    }

    [Fact]
    public async Task GoObject_TeleportsToSpawnGuid()
    {
        await using WorldTestHost host = StartWithSpawns();
        await using WorldTestClient gm = await host.EnterWorldAsync("GOOBJGM", "Goobjgm", AccountSecurity.GameMaster);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".go object 991963");
        MovementInfo arrival = await AcknowledgeAsync(gm, host, "Goobjgm");
        Assert.Equal((-8920f, -132f, 83.5f), (arrival.X, arrival.Y, arrival.Z));
    }

    [Fact]
    public async Task GoCreature_UsesTheLoadedCreaturesLivePosition()
    {
        await using WorldTestHost host = StartWithSpawns();
        await using WorldTestClient gm = await host.EnterWorldAsync("GOLIVEGM", "Golivegm", AccountSecurity.GameMaster);
        Creature? live = null;
        await host.WaitForWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Golivegm")!;
            live = host.WorldServices.GetRequiredService<CreatureWorldFeature>().GetOrCreateSystem(player.Map!)
                .Creatures.FirstOrDefault(c => c.Spawn?.Guid == 991966);
            return live is not null;
        }, "spawn 991966 loaded");

        // TeleportCommands.cpp:498-503: a creature of the spawn on the caller's map gives its current position, not the stored one.
        await host.OnWorldAsync(() => live!.Relocate(-8925f, -140f, 83.5f, 0, host.World.NowMs));
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".go creature 991966");
        MovementInfo arrival = await AcknowledgeAsync(gm, host, "Golivegm");
        Assert.Equal((-8925f, -140f, 83.5f), (arrival.X, arrival.Y, arrival.Z));
    }

    [Fact]
    public async Task GoCreatureAndObject_UnknownSpawn_ReplyNotFound()
    {
        await using WorldTestHost host = StartWithSpawns();
        await using WorldTestClient gm = await host.EnterWorldAsync("GONONEGM", "Gononegm", AccountSecurity.GameMaster);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".go creature 12345");
        Assert.Equal("Creature not found!", (await gm.ReadChatAsync()).Text);
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".go object 12345");
        Assert.Equal("Object not found!", (await gm.ReadChatAsync()).Text);
    }

    private static WorldTestHost StartWithSpawns()
    {
        var wolf = new CreatureTemplate { Entry = 991962, Name = "GM test wolf", MinLevel = 2, MaxLevel = 2, DisplayIds = [903], Faction = 35 };
        var bear = new CreatureTemplate { Entry = 991965, Name = "GM test bear", MinLevel = 2, MaxLevel = 2, DisplayIds = [903], Faction = 35 };
        var chest = new GameObjectTemplate { Entry = 991964, Type = 3, DisplayId = 10, Name = "GM test chest", Data = new uint[GameObjectTemplate.DataCount] };
        CreatureTestStore.Current.Value = new CreatureTestContext(new CreatureContent([wolf, bear],
            [
                new CreatureSpawn { Guid = 991961, Entry = 991965, MapId = 0, X = -8940f, Y = -132f, Z = 83.5f },
                new CreatureSpawn { Guid = 991966, Entry = 991962, MapId = 0, X = -8930f, Y = -132f, Z = 83.5f },
            ], [], [], []));
        GameObjectTestStore.Current.Value = new GameObjectTestContext(new GameObjectContent([chest],
            [new GameObjectSpawn { Guid = 991963, Entry = 991964, MapId = 0, X = -8920f, Y = -132f, Z = 83.5f }], [], [], []), LootContent.Empty);
        try
        {
            return WorldTestHost.Start();
        }
        finally
        {
            CreatureTestStore.Current.Value = null;
            GameObjectTestStore.Current.Value = null;
        }
    }

    private static async Task<MovementInfo> AcknowledgeAsync(WorldTestClient client, WorldTestHost host, string name)
    {
        byte[] packet = await client.ReadUntilAsync(WorldOpcode.MsgMoveTeleportAck);
        var reader = new PacketReader(packet);
        ulong guid = reader.ReadPackedGuid();
        uint counter = reader.ReadUInt32();
        MovementInfo info = MovementInfo.Read(ref reader);
        var reply = new PacketWriter(16);
        reply.WriteUInt64(guid);
        reply.WriteUInt32(counter);
        reply.WriteUInt32(0);
        await client.SendAsync(WorldOpcode.MsgMoveTeleportAck, reply.ToArray());
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(name)!.X == info.X, "teleport acknowledgement");
        return info;
    }
}
