using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Kernel.WorldData.Pools;
using ArcaneCore.Kernel.WorldData.SpawnGroups;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Objects;
using ArcaneCore.World.Tests.Creatures;
using ArcaneCore.World.Tests.GameObjects;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Objects;

/// <summary>
/// <c>.spawngroup list|info|spawn|poolaudit</c> end to end over loopback, on classic-db z2815 rows moved next to the human start: spawn group 2
/// "Kargath Expeditionary Force" (five creatures 9082-9086, flags 3, formation fanned out behind, spread 4, path 6883) and pool 31225 "The
/// Barrens (The Merchant Coast) - Chest Pool" (max_limit 1; three of its Battered Chests 300129, 300132, 300141).
/// </summary>
public sealed class GmSpawnGroupCommandTests
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(250);

    private static readonly (uint Guid, uint Entry, int Slot)[] Kargath = [(6877, 9085, 3), (6880, 9083, 4), (6883, 9086, 0), (6885, 9082, 2), (6886, 9084, 1)];
    private static readonly uint[] Chests = [300129, 300132, 300141];
    private const uint BatteredChest = 3689;

    private sealed record Scene(WorldTestHost Host, GameObjectTestContext Objects, CreatureTestContext Creatures);

    private static Scene Start()
    {
        var chest = new GameObjectTemplate { Entry = BatteredChest, Type = (uint)GameObjectType.Chest, DisplayId = 10, Name = "Battered Chest" };
        var objects = new GameObjectTestContext(
            new GameObjectContent(
                [chest],
                Chests.Select((g, i) => new GameObjectSpawn { Guid = g, Entry = BatteredChest, MapId = 0, X = -8940f + (i * 3), Y = -140f, Z = 83.5f, SpawnTimeSeconds = 300 }),
                [], [], [])
            {
                Pools = PoolCatalog.Build(
                    [new PoolTemplateData(31225, 1, "The Barrens (The Merchant Coast) - Chest Pool")],
                    Chests.Select(g => new PoolSpawnLink(g, 31225, 0f)), [], [], Chests.ToDictionary(g => g, _ => (BatteredChest, 0u))),
            },
            new LootContent([], []));
        var creatures = new CreatureTestContext(new CreatureContent(
            Kargath.Select(k => new CreatureTemplate
            {
                Entry = k.Entry, Name = "Kargath Grunt", MinLevel = 40, MaxLevel = 40, DisplayIds = [903], Faction = 35, MinLevelHealth = 900, MaxLevelHealth = 900,
            }),
            Kargath.Select((k, i) => new CreatureSpawn { Guid = k.Guid, Entry = k.Entry, MapId = 0, X = -8930f + i, Y = -120f, Z = 83.5f }),
            [], [], [])
        {
            SpawnGroups = new SpawnGroupCatalog(
            [
                new SpawnGroupDefinition
                {
                    Id = 2,
                    Name = "Kargath Expeditionary Force c.entry 9082,9083,9084,9085,9086 & Linked to 9077 for RP",
                    Type = SpawnGroupType.Creature,
                    Flags = SpawnGroupFlags.AggroTogether | SpawnGroupFlags.RespawnTogether,
                    Members = [.. Kargath.Select(k => new SpawnGroupMember(k.Guid, k.Slot, 0))],
                    Formation = new SpawnGroupFormation(4, 4, 0, 6883, 2, "Kargath Expeditionary Force"),
                },
            ]),
        });
        GameObjectTestStore.Current.Value = objects;
        CreatureTestStore.Current.Value = creatures;
        try
        {
            return new Scene(WorldTestHost.Start(), objects, creatures);
        }
        finally
        {
            GameObjectTestStore.Current.Value = null;
            CreatureTestStore.Current.Value = null;
        }
    }

    private static async Task<List<string>> SendAsync(WorldTestClient client, string command)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, command);
        List<(WorldOpcode Opcode, byte[] Payload)> packets = await client.CollectFromAsync(WorldOpcode.SmsgMessagechat, Quiet);
        return [.. packets.Where(p => p.Opcode == WorldOpcode.SmsgMessagechat && p.Payload[0] == (byte)ChatType.System).Select(p => ChatMessage.Parse(p.Payload).Text)];
    }

    private static async Task<WorldTestClient> EnterAsync(Scene scene, string account, string name, AccountSecurity security)
    {
        WorldTestClient client = await scene.Host.EnterWorldAsync(account, name, security);
        await client.CollectAsync(Quiet);
        return client;
    }

    [Fact]
    public void Levels_AreGameMaster_RetailLevel3()
    {
        CommandTable table = ChatCommands.CreateTable();
        foreach (string path in new[] { "spawngroup", "spawngroup list", "spawngroup info", "spawngroup spawn", "spawngroup poolaudit" })
        {
            Assert.Null(table.Resolve(path, AccountSecurity.Moderator));
            Assert.Equal(3, table.Resolve(path, AccountSecurity.GameMaster)!.RequiredLevel(table.Gm));
        }
    }

    [Fact]
    public async Task SpawnGroup_ListInfoAndSpawn_ShowTheGroupItsFormationAndThePool()
    {
        Scene scene = Start();
        await using WorldTestHost host = scene.Host;
        await using WorldTestClient player = await EnterAsync(scene, "SGPLR", "Sgplr", AccountSecurity.Player);
        Assert.Equal("This command is not available to you.", Assert.Single(await SendAsync(player, ".spawngroup list")));

        await using WorldTestClient gm = await EnterAsync(scene, "SGGM", "Sggm", AccountSecurity.GameMaster);
        await host.WaitForWorldAsync(() => scene.Creatures.Feature!.FindSystem(0)!.Creatures.Count(c => c.Spawn is not null) == 5, "the group is in the world");

        List<string> list = await SendAsync(gm, ".spawngroup list");
        Assert.Equal("1 spawn group(s) on map 0:", list[0]);
        Assert.Equal("2 creature 5/5 Kargath Expeditionary Force c.entry 9082,9083,9084,9085,9086 & Linked to 9077 for RP", list[1]);

        List<string> info = await SendAsync(gm, ".spawngroup info 2");
        Assert.Equal("Spawn group 2: Kargath Expeditionary Force c.entry 9082,9083,9084,9085,9086 & Linked to 9077 for RP", info[0]);
        Assert.Equal("Type creature, in the world 5/5 (stored MaxCount 0), flags 3 (AggroTogether, RespawnTogether)", info[1]);
        Assert.Equal("Formation: fanned out behind, spread 4, path 6883 (waypoint), leader 6883", info[2]);
        Assert.Contains("  6883 slot 0: entry 9086, alive", info);
        Assert.Equal(8, info.Count);

        await host.OnWorldAsync(() =>
            host.World.FindOnlinePlayer("Sggm")!.Selection = ObjectGuid.WithEntry(HighGuid.Unit, 9082, 6885));
        Assert.Equal(info, await SendAsync(gm, ".spawngroup info"));
        Assert.Equal(GmSpawnGroupCommands.NoGroup(99), Assert.Single(await SendAsync(gm, ".spawngroup info 99")));

        List<string> member = await SendAsync(gm, ".spawngroup spawn 6880");
        Assert.Equal("creature spawn 6880: spawn group 2 \"Kargath Expeditionary Force c.entry 9082,9083,9084,9085,9086 & Linked to 9077 for RP\" (5/5 in the world); this one: entry 9083, alive", Assert.Single(member));

        uint outChest = await host.OnWorldAsync(() => Chests.Single(scene.Objects.Feature!.FindSystem(0u)!.IsPoolSpawned));
        uint otherChest = Chests.First(g => g != outChest);
        Assert.Equal($"gameobject spawn {outChest}: pool 31225 \"The Barrens (The Merchant Coast) - Chest Pool\" (1/1 out); this one is out",
            Assert.Single(await SendAsync(gm, $".spawngroup spawn {outChest}")));
        Assert.Equal($"gameobject spawn {otherChest}: pool 31225 \"The Barrens (The Merchant Coast) - Chest Pool\" (1/1 out); this one is not chosen",
            Assert.Single(await SendAsync(gm, $".spawngroup spawn {otherChest} gameobject")));
        Assert.Equal(
            ["creature pools on map 0: 0 checked, 0 spawn(s) out, 0 in the world, clean", "gameobject pools on map 0: 1 checked, 1 spawn(s) out, 1 in the world, clean"],
            await SendAsync(gm, ".spawngroup poolaudit"));
        Assert.Equal(["gameobject pools on map 0: 1 checked, 1 spawn(s) out, 1 in the world, clean"], await SendAsync(gm, ".spawngroup poolaudit gameobject"));
        Assert.StartsWith("Syntax: .spawngroup poolaudit", (await SendAsync(gm, ".spawngroup poolaudit dragons"))[0], StringComparison.Ordinal);
        Assert.Equal(GmSpawnGroupCommands.NotASpawn(1), Assert.Single(await SendAsync(gm, ".spawngroup spawn 1")));
        Assert.StartsWith("Syntax: .spawngroup spawn", (await SendAsync(gm, ".spawngroup spawn 6880 dragons"))[0], StringComparison.Ordinal);
    }
}
