using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Tests.Creatures;
using ArcaneCore.World.Tests.GameObjects;
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
    public async Task GoCreature_TeleportsToSpawnGuidAndTemplateEntry()
    {
        await using WorldTestHost host = StartWithSpawns();
        await using WorldTestClient gm = await host.EnterWorldAsync("GOCREGM", "Gocregm", AccountSecurity.GameMaster);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".go creature 991961");
        MovementInfo first = await AcknowledgeAsync(gm, host, "Gocregm");
        Assert.Equal((-8940f, -132f, 83.5f), (first.X, first.Y, first.Z));

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".go creature id 991962");
        MovementInfo second = await AcknowledgeAsync(gm, host, "Gocregm");
        Assert.Equal((-8930f, -132f, 83.5f), (second.X, second.Y, second.Z));
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

    private static WorldTestHost StartWithSpawns()
    {
        var wolf = new CreatureTemplate { Entry = 991962, Name = "GM test wolf", MinLevel = 2, MaxLevel = 2, DisplayIds = [903], Faction = 35 };
        var bear = new CreatureTemplate { Entry = 991965, Name = "GM test bear", MinLevel = 2, MaxLevel = 2, DisplayIds = [903], Faction = 35 };
        var chest = new GameObjectTemplate { Entry = 991964, Type = 3, DisplayId = 10, Name = "GM test chest", Data = new uint[GameObjectTemplate.DataCount] };
        CreatureTestStore.Current.Value = new CreatureTestContext(new CreatureContent([wolf, bear],
            [
                new CreatureSpawn { Guid = 991961, Entry = 991965, MapId = 0, X = -8940f, Y = -132f, Z = 83.5f },
                new CreatureSpawn { Guid = 991962, Entry = 991962, MapId = 0, X = -8930f, Y = -132f, Z = 83.5f },
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
