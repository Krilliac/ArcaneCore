using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Ops.Metrics;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Tests.GameObjects;
using ArcaneCore.World.Tests.Gm.Inspect;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Tools;

/// <summary>The <c>.inspect gobject/cooldowns</c>, <c>.alter dynflag/combatreach/boundingradius/gostate/goflag</c> and <c>.debug scheduler/opcodes</c> commands end to end.</summary>
public sealed class ToolsCommandTests
{
    private const uint DoorEntry = 20101;
    private const uint DoorSpawn = 88201;

    [Fact]
    public void NewCommandsHaveTheirSecurityLevels()
    {
        CommandTable table = ChatCommands.CreateTable();
        foreach (string path in new[] { "inspect gobject", "inspect cooldowns", "alter dynflag", "alter combatreach", "alter boundingradius", "alter gostate", "alter goflag",
            "debug scheduler", "debug opcodes" })
        {
            Assert.Null(table.Resolve(path, AccountSecurity.Moderator));
            Assert.NotNull(table.Resolve(path, AccountSecurity.GameMaster));
        }
    }

    [Fact]
    public async Task GameObjectsAreInspectedAndAltered()
    {
        var door = new GameObjectTemplate { Entry = DoorEntry, Type = (uint)GameObjectType.Door, DisplayId = 5, Name = "Tools Door", Data = DoorData() };
        var objects = new GameObjectTestContext(
            new GameObjectContent([door], [new GameObjectSpawn { Guid = DoorSpawn, Entry = DoorEntry, MapId = 0, X = -8948f, Y = -132.5f, Z = 83.5f }], [], [], []),
            new LootContent([], []));
        GameObjectTestStore.Current.Value = objects;
        WorldTestHost started;
        try
        {
            started = WorldTestHost.Start();
        }
        finally
        {
            GameObjectTestStore.Current.Value = null;
        }

        await using WorldTestHost host = started;
        await using WorldTestClient gm = await host.EnterWorldAsync("TOOLSGO", "Toolsgo", AccountSecurity.Administrator);
        await gm.CollectAsync();
        GameObject go = await host.OnWorldAsync(() => objects.Feature!.FindSystem(0u)!.GameObjects.Single(g => g.Spawn is not null));

        string[] info = await RunAsync(gm, ".inspect gobject");
        Assert.Contains(info, l => l.StartsWith($"Tools Door entry {DoorEntry} guid {go.Guid.Counter} type Door", StringComparison.Ordinal) && l.Contains($"database spawn {DoorSpawn}", StringComparison.Ordinal));
        Assert.Contains(info, l => l.StartsWith("  state Ready, loot state Ready, spawned", StringComparison.Ordinal));
        Assert.Contains(info, l => l.StartsWith("  user none, owner none", StringComparison.Ordinal));
        Assert.Contains(info, l => l.Contains("data3=1", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(gm, $".inspect gobject {go.Guid.Counter}"), l => l.StartsWith("Tools Door entry", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(gm, ".inspect gobject 999999"), l => l.Contains("999999 was not found", StringComparison.Ordinal));

        Assert.Contains(await RunAsync(gm, ".alter gostate active"), l => l.EndsWith("state Ready -> Active.", StringComparison.Ordinal));
        Assert.Equal(GameObjectState.Active, await host.OnWorldAsync(() => go.State));
        Assert.Contains(await RunAsync(gm, $".alter gostate 1 {go.Guid.Counter}"), l => l.EndsWith("state Active -> Ready.", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(gm, ".alter gostate sideways"), l => l.StartsWith("Syntax:", StringComparison.Ordinal));

        Assert.Contains(await RunAsync(gm, ".alter goflag locked on"), l => l.Contains("flag Locked set", StringComparison.Ordinal));
        Assert.True(await host.OnWorldAsync(() => (go.Flags & GameObjectFlags.Locked) != 0));
        Assert.Contains(await RunAsync(gm, ".alter goflag 0x2 off"), l => l.Contains("flag Locked cleared", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(gm, ".alter goflag sparkly on"), l => l.StartsWith("Unknown game object flag 'sparkly'", StringComparison.Ordinal));

        Assert.Contains(await RunAsync(gm, $".gobject spawnmany {DoorEntry} 3 6"), l => l.StartsWith($"Placed 3 temporary Tools Door (entry {DoorEntry}) within 6 yards", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnitSettersCooldownsAndQueuesAnswer()
    {
        await using WorldTestHost host = InspectCommandTests.StartWithWolf();
        await using WorldTestClient gm = await host.EnterWorldAsync("TOOLSWOLF", "Toolswolf", AccountSecurity.GameMaster);
        await gm.CollectAsync();
        Creature wolf = await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<CreatureWorldFeature>().FindSystem(0)!.Creatures.Last());
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Toolswolf")!.Selection = wolf.Guid);

        Assert.Contains(await RunAsync(gm, ".inspect cooldowns"), l => l.StartsWith("Young Wolf (entry 299, guid", StringComparison.Ordinal) && l.EndsWith("0 running cooldown(s)", StringComparison.Ordinal));

        Assert.Contains(await RunAsync(gm, ".alter dynflag lootable on"), l => l.EndsWith("dynamic flag Lootable set; dynamic flags now 0x1.", StringComparison.Ordinal));
        Assert.Equal(1u, await host.OnWorldAsync(() => wolf.GetUInt32(UpdateFields.UnitDynamicFlags) & 1u));
        Assert.Contains(await RunAsync(gm, ".alter dynflag 0x1 off"), l => l.EndsWith("dynamic flags now 0x0.", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(gm, ".alter dynflag nonsense on"), l => l.StartsWith("Unknown dynamic flag", StringComparison.Ordinal));

        Assert.Contains(await RunAsync(gm, ".alter combatreach 7.5"), l => l.EndsWith("-> 7.5 yd.", StringComparison.Ordinal));
        Assert.Equal(7.5f, await host.OnWorldAsync(() => wolf.GetFloat(UpdateFields.UnitFieldCombatreach)));
        Assert.Contains(await RunAsync(gm, ".alter boundingradius 2"), l => l.EndsWith("-> 2 yd.", StringComparison.Ordinal));
        Assert.Equal(2f, await host.OnWorldAsync(() => wolf.BoundingRadius));
        Assert.Contains(await RunAsync(gm, ".alter combatreach 99"), l => l.StartsWith("Syntax:", StringComparison.Ordinal));

        string[] scheduler = await RunAsync(gm, ".debug scheduler 3");
        Assert.Contains(scheduler, l => l.StartsWith("World tick scheduler: interval ", StringComparison.Ordinal));
        Assert.Contains(scheduler, l => l.StartsWith("  posted world commands waiting: ", StringComparison.Ordinal));
        Assert.Contains(scheduler, l => l.StartsWith("  map 0/0: ", StringComparison.Ordinal) && l.Contains("relocations", StringComparison.Ordinal));

        bool before = ArcaneMeters.Opcodes.LocalCounting;
        try
        {
            Assert.Contains(await RunAsync(gm, ".debug opcodes on"), l => l == "Per-opcode counting on; window reset.");
            await RunAsync(gm, ".gps");
            // The meter is process-wide and the suite runs other worlds in parallel: ask for the most rows the command gives (60), not a top 5
            // that busy neighbours can fill, so this session's own chat packet is among them.
            string[] opcodes = await RunAsync(gm, ".debug opcodes in 60");
            Assert.Contains(opcodes, l => l.StartsWith("Per-opcode traffic over the last ", StringComparison.Ordinal));
            Assert.Contains(opcodes, l => l.StartsWith("  world CMSG_MESSAGECHAT: in ", StringComparison.Ordinal) || l.StartsWith("  world CmsgMessagechat: in ", StringComparison.Ordinal));
            Assert.Contains(await RunAsync(gm, ".debug opcodes bogus"), l => l.StartsWith("Syntax:", StringComparison.Ordinal));
        }
        finally
        {
            ArcaneMeters.Opcodes.LocalCounting = before;
        }

        string[] botthink = await RunAsync(gm, ".debug botthink 3");
        Assert.Contains(botthink, l => l.StartsWith("Playerbot think: ", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(gm, ".debug botthink 0"), l => l.StartsWith("Syntax:", StringComparison.Ordinal));
    }

    private static uint[] DoorData()
    {
        uint[] data = new uint[GameObjectTemplate.DataCount];
        data[3] = 1;
        return data;
    }

    private static async Task<string[]> RunAsync(WorldTestClient client, string command)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, command);
        return await client.CollectChatLinesAsync();
    }
}

/// <summary><c>.inspect quests/instance/weather/worldstates</c>, <c>.quest objective</c>, <c>.alter instancedata/instancedata64</c>, <c>.event info/trigger</c>.</summary>
public sealed class WorldToolsCommandTests
{
    [Fact]
    public void NewCommandsHaveTheirSecurityLevels()
    {
        CommandTable table = ChatCommands.CreateTable();
        foreach (string path in new[] { "inspect quests", "inspect instance", "inspect weather", "inspect worldstates", "event info" })
        {
            Assert.Null(table.Resolve(path, AccountSecurity.Moderator));
            Assert.NotNull(table.Resolve(path, AccountSecurity.GameMaster));
        }

        foreach (string path in new[] { "quest objective", "alter instancedata", "alter instancedata64", "event trigger" })
        {
            Assert.Null(table.Resolve(path, AccountSecurity.GameMaster));
            Assert.NotNull(table.Resolve(path, AccountSecurity.Administrator));
        }
    }

    [Fact]
    public async Task QuestObjectivesAreListedAndSet()
    {
        var fixture = new ArcaneCore.World.Tests.Gm.Quest.QuestCommandFixture();
        ArcaneCore.World.Tests.Gm.Quest.QuestCommandTestServices.Current.Value = fixture;
        WorldTestHost started;
        try
        {
            started = WorldTestHost.Start();
        }
        finally
        {
            ArcaneCore.World.Tests.Gm.Quest.QuestCommandTestServices.Current.Value = null;
        }

        await using WorldTestHost host = started;
        await using WorldTestClient admin = await host.EnterWorldAsync("TOOLSQ", "Toolsq", AccountSecurity.Administrator);
        await admin.CollectAsync();
        const uint kill = ArcaneCore.World.Tests.Gm.Quest.QuestCommandFixture.KillQuest;
        await admin.SendChatAsync(ChatType.Say, Language.Common, $".quest add {kill}"); // answers nothing on success

        string[] log = await RunAsync(admin, ".inspect quests cull");
        Assert.Contains(log, l => l.StartsWith("Toolsq: 1 logged quest(s) matching 'cull'", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains($"{kill} [Cull the test] Incomplete: o0 npc 90 0/2", StringComparison.Ordinal));

        Assert.Contains(await RunAsync(admin, $".quest objective {kill} 0 5"), l => l.EndsWith("objective 0 now 2/2; quest Complete.", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(admin, ".inspect quests"), l => l.Contains("Complete: o0 npc 90 2/2", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(admin, $".quest objective {kill} 0 1"), l => l.EndsWith("objective 0 now 1/2; quest Incomplete.", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(admin, $".quest objective {kill} 2 1"), l => l.EndsWith($"Quest {kill} has no objective 2.", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(admin, $".quest objective {ArcaneCore.World.Tests.Gm.Quest.QuestCommandFixture.ItemQuest} 0 1"), l => l.EndsWith("does not have quest 910002 in progress.", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(admin, ".quest objective 1"), l => l.StartsWith("Syntax:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WorldViewsAnswer()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("TOOLSW", "Toolsw", AccountSecurity.Administrator);
        await admin.CollectAsync();

        string[] instance = await RunAsync(admin, ".inspect instance");
        Assert.Contains(instance, l => l.StartsWith("map 0/0 [", StringComparison.Ordinal) && l.Contains(": script ", StringComparison.Ordinal));
        Assert.Contains(instance, l => l.StartsWith("  data: ", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(admin, ".alter instancedata 1"), l => l.StartsWith("Syntax:", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(admin, ".alter instancedata64 0"), l => l.Contains("data64 0: 0x", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(admin, ".alter instancedata64 bogus"), l => l.StartsWith("Syntax:", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(admin, ".event info 99999"), l => l == "Event not exist!");
        Assert.Contains(await RunAsync(admin, ".event trigger 99999"), l => l.Contains("99999", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(admin, ".inspect weather"), l => l.StartsWith("Your zone ", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(admin, ".inspect worldstates"), l => l.Contains("world state(s) for map 0 zone", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(admin, ".inspect worldstates x"), l => l.StartsWith("Syntax:", StringComparison.Ordinal));
    }

    private static async Task<string[]> RunAsync(WorldTestClient client, string command)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, command);
        return await client.CollectChatLinesAsync();
    }
}

/// <summary><c>.creature spawnmany</c>, <c>.gobject spawnmany</c>, <c>.inspect items</c> and <c>.alter durability</c>.</summary>
public sealed class SpawnItemToolsCommandTests
{
    [Fact]
    public void NewCommandsHaveTheirSecurityLevels()
    {
        CommandTable table = ChatCommands.CreateTable();
        foreach (string path in new[] { "creature spawnmany", "inspect items", "alter durability" })
        {
            Assert.Null(table.Resolve(path, AccountSecurity.Moderator));
            Assert.NotNull(table.Resolve(path, AccountSecurity.GameMaster));
        }

        Assert.Null(table.Resolve("gobject spawnmany", AccountSecurity.GameMaster));
        Assert.NotNull(table.Resolve("gobject spawnmany", AccountSecurity.Administrator));
    }

    [Fact]
    public async Task CreaturesAreSpawnedInBulkAndItemsListed()
    {
        await using WorldTestHost host = InspectCommandTests.StartWithWolf();
        await using WorldTestClient gm = await host.EnterWorldAsync("TOOLSSPAWN", "Toolsspawn", AccountSecurity.Administrator);
        await gm.CollectAsync();
        CreatureMapSystem system = await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<CreatureWorldFeature>().FindSystem(0)!);
        int before = await host.OnWorldAsync(() => system.Creatures.Count(c => c.Spawn is null));

        Assert.Contains(await RunAsync(gm, ".creature spawnmany 299 12 10"), l => l == "Placed 12 temporary Young Wolf (entry 299) within 10 yards.");
        Assert.Equal(before + 12, await host.OnWorldAsync(() => system.Creatures.Count(c => c.Spawn is null)));
        Player me = await host.PlayerAsync("Toolsspawn");
        Assert.True(await host.OnWorldAsync(() => system.Creatures.Where(c => c.Spawn is null).All(c => MathF.Sqrt(((c.X - me.X) * (c.X - me.X)) + ((c.Y - me.Y) * (c.Y - me.Y))) <= 10.01f)));
        Assert.Contains(await RunAsync(gm, ".creature spawnmany 299 500"), l => l.StartsWith("Syntax:", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(gm, ".creature spawnmany 4242424 3"), l => l == "Creature template 4242424 not found.");
        Assert.Contains(await RunAsync(gm, ".creature despawntemp 20"), l => l.StartsWith("Removed 12 temporary", StringComparison.Ordinal));

        Assert.Contains(await RunAsync(gm, ".gobject spawnmany 4242424 3"), l => l == "Game object template 4242424 does not exist.");
        Assert.Contains(await RunAsync(gm, ".gobject spawnmany 1 99"), l => l.StartsWith("Syntax:", StringComparison.Ordinal));

        Assert.Contains(await RunAsync(gm, ".inspect items"), l => l.StartsWith("Toolsspawn: ", StringComparison.Ordinal) && l.EndsWith(" item(s)", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(gm, ".alter durability 50"), l => l.StartsWith("Toolsspawn: ", StringComparison.Ordinal) && l.Contains("worn item(s) set to 50% durability", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(gm, ".alter durability 150"), l => l.StartsWith("Syntax:", StringComparison.Ordinal));
    }

    private static async Task<string[]> RunAsync(WorldTestClient client, string command)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, command);
        return await client.CollectChatLinesAsync();
    }
}

/// <summary><c>.ai waypoints</c>, <c>.inspect aispells/reputation</c> and <c>.alter reputation</c>.</summary>
public sealed class AiAndReputationToolsCommandTests
{
    [Fact]
    public void NewCommandsHaveTheirSecurityLevels()
    {
        CommandTable table = ChatCommands.CreateTable();
        foreach (string path in new[] { "ai waypoints", "inspect aispells", "inspect reputation", "alter reputation", "debug botthink" })
        {
            Assert.Null(table.Resolve(path, AccountSecurity.Moderator));
            Assert.NotNull(table.Resolve(path, AccountSecurity.GameMaster));
        }
    }

    [Fact]
    public async Task AiWaypointsAiSpellsAndReputationAnswer()
    {
        await using WorldTestHost host = InspectCommandTests.StartWithWolf();
        await using WorldTestClient gm = await host.EnterWorldAsync("TOOLSAI", "Toolsai", AccountSecurity.GameMaster);
        await gm.CollectAsync();
        Creature wolf = await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<CreatureWorldFeature>().FindSystem(0)!.Creatures.Last());
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Toolsai")!.Selection = wolf.Guid);

        Assert.Contains(await RunAsync(gm, ".ai waypoints resume"), l => l.EndsWith("has no waypoint path (default); standing idle.", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(gm, ".ai waypoints 999999"), l => l.EndsWith("has no waypoint path 999999; standing idle.", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(gm, ".ai waypoints bogus"), l => l.StartsWith("Syntax:", StringComparison.Ordinal));

        string[] aiSpells = await RunAsync(gm, ".inspect aispells");
        Assert.Contains(aiSpells, l => l.StartsWith("Young Wolf (entry 299, guid", StringComparison.Ordinal) && l.Contains("has no EventAI or SmartAI spell table", StringComparison.Ordinal));

        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Toolsai")!.Selection = ObjectGuid.Empty);
        string[] rep = await RunAsync(gm, ".inspect reputation");
        Assert.Contains(rep, l => l.StartsWith("Reputation is not available", StringComparison.Ordinal) || l.StartsWith("Toolsai", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(gm, ".alter reputation 999999 100"), l => l.StartsWith("Reputation is not available", StringComparison.Ordinal) || l == "No faction matches '999999'.");
        Assert.Contains(await RunAsync(gm, ".alter reputation"), l => l.StartsWith("Syntax:", StringComparison.Ordinal));
    }

    private static async Task<string[]> RunAsync(WorldTestClient client, string command)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, command);
        return await client.CollectChatLinesAsync();
    }
}

