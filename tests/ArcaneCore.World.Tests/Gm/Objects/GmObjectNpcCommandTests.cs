using System.Globalization;
using System.Text;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Gm.Npc;
using ArcaneCore.World.Gm.Objects;
using ArcaneCore.World.Teleport;
using ArcaneCore.World.Tests.Creatures;
using ArcaneCore.World.Tests.GameObjects;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Objects;

/// <summary>
/// <c>.gobject</c>, <c>.npc</c>, <c>.respawn</c> and <c>.spawninfo</c> end to end over loopback
/// (docs/integration/gm-objects-npc-lane.md): the replies, what the live systems end up holding, and the account gating.
/// </summary>
public sealed class GmObjectNpcCommandTests
{
    private const uint DoorEntry = 20001;
    private const uint DoorSpawn = 88001;
    private const uint FarDoorSpawn = 88002;
    private const uint WolfEntry = 20002;
    private const uint WolfSpawn = 88101;
    private const uint FarWolfSpawn = 88102;

    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(250);

    private sealed record Scene(WorldTestHost Host, GameObjectTestContext Objects, CreatureTestContext Creatures);

    private static Scene Start()
    {
        uint[] doorData = new uint[GameObjectTemplate.DataCount];
        doorData[3] = 1; // data3 (noDamageImmune): the door may despawn, so a database spawn of it has a respawn timer
        var door = new GameObjectTemplate { Entry = DoorEntry, Type = (uint)GameObjectType.Door, DisplayId = 5, Name = "Test Door", Data = doorData };
        // The human start is (-8949.95, -132.49, 83.53): the door is 2 yards away, the far door 150.
        var objects = new GameObjectTestContext(
            new GameObjectContent(
                [door],
                [
                    new GameObjectSpawn { Guid = DoorSpawn, Entry = DoorEntry, MapId = 0, X = -8948f, Y = -132.5f, Z = 83.5f },
                    new GameObjectSpawn { Guid = FarDoorSpawn, Entry = DoorEntry, MapId = 0, X = -8800f, Y = -132.5f, Z = 83.5f },
                ],
                [], [], []),
            new LootContent([], []));
        var wolf = new CreatureTemplate
        {
            Entry = WolfEntry, Name = "Test Wolf", MinLevel = 2, MaxLevel = 2, DisplayIds = [903], Faction = 35, CreatureType = 1, Family = 1,
            MinLevelHealth = 55, MaxLevelHealth = 55,
        };
        var creatures = new CreatureTestContext(new CreatureContent(
            [wolf],
            [
                new CreatureSpawn { Guid = WolfSpawn, Entry = WolfEntry, MapId = 0, X = -8940f, Y = -132f, Z = 83.5f },
                new CreatureSpawn { Guid = FarWolfSpawn, Entry = WolfEntry, MapId = 0, X = -8800f, Y = -132f, Z = 83.5f },
            ],
            [], [], []));
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

    private sealed record Run(List<string> Replies, List<(WorldOpcode Opcode, byte[] Payload)> Packets)
    {
        public string Single => Assert.Single(Replies);
    }

    private static async Task<Run> SendAsync(WorldTestClient client, string command, WorldOpcode answer = WorldOpcode.SmsgMessagechat)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, command);
        // Wait for the command's answer (a chat line, unless the caller names another packet) before collecting,
        // so a slow world tick is not mistaken for an empty answer.
        List<(WorldOpcode Opcode, byte[] Payload)> packets = await client.CollectFromAsync(answer, Quiet);
        List<string> replies = [.. packets
            .Where(p => p.Opcode == WorldOpcode.SmsgMessagechat && p.Payload[0] == (byte)ChatType.System)
            .Select(p => ChatMessage.Parse(p.Payload).Text)];
        return new Run(replies, packets);
    }

    private static List<(WorldOpcode Opcode, byte[] Payload)> Chat(Run run, ChatType type)
        => [.. run.Packets.Where(p => p.Opcode == WorldOpcode.SmsgMessagechat && p.Payload[0] == (byte)type)];

    private static bool Contains(byte[] payload, string text) => Encoding.UTF8.GetString(payload).Contains(text, StringComparison.Ordinal);

    private static async Task<WorldTestClient> GmAsync(Scene scene, string account, string name, AccountSecurity security = AccountSecurity.GameMaster)
    {
        WorldTestClient gm = await scene.Host.EnterWorldAsync(account, name, security);
        await gm.CollectAsync(Quiet);
        return gm;
    }

    private static GameObjectMapSystem ObjectSystem(Scene scene) => scene.Objects.Feature!.FindSystem(0u)!;

    private static CreatureMapSystem CreatureSystem(Scene scene) => scene.Creatures.Feature!.FindSystem(0)!;

    private static Task SelectAsync(Scene scene, string player, uint spawn)
        => scene.Host.OnWorldAsync(() =>
        {
            scene.Host.World.FindOnlinePlayer(player)!.Selection = ObjectGuid.WithEntry(HighGuid.Unit, WolfEntry, spawn);
        });

    private static Task<uint> PlaceDoorAsync(Scene scene)
        => scene.Host.OnWorldAsync(() => ObjectSystem(scene).Summon(DoorEntry, -8950f, -133f, 83.5f, 1f)!.Guid.Counter);

    [Fact]
    public void Levels_FollowTheLaneDocument()
    {
        CommandTable table = ChatCommands.CreateTable();
        string[] readOnly = ["gobject", "gobject near", "gobject info", "npc", "npc info", "npc near", "spawninfo", "spawninfo creature", "spawninfo gameobject", "spawninfo summary"];
        string[] mutating =
        [
            "gobject add", "gobject delete", "gobject move", "gobject turn", "gobject activate",
            "npc add", "npc delete", "npc say", "npc yell", "npc textemote", "npc whisper", "npc playemote", "respawn",
        ];
        foreach (string path in readOnly.Concat(mutating))
        {
            Assert.Null(table.Resolve(path, AccountSecurity.Moderator));
            Assert.NotNull(table.Resolve(path, AccountSecurity.GameMaster));
        }

        foreach (string path in readOnly)
        {
            Assert.Equal(2, table.Resolve(path, AccountSecurity.GameMaster)!.RequiredLevel(table.Gm));
        }

        foreach (string path in mutating)
        {
            Assert.Equal(3, table.Resolve(path, AccountSecurity.GameMaster)!.RequiredLevel(table.Gm));
        }
    }

    [Fact]
    public async Task EveryCommand_IsRefusedForAnAccountBelowItsLevel()
    {
        Scene scene = Start();
        await using WorldTestHost host = scene.Host;
        await using WorldTestClient moderator = await GmAsync(scene, "GONMOD", "Gonmod", AccountSecurity.Moderator);
        await using WorldTestClient player = await GmAsync(scene, "GONPLR", "Gonplr", AccountSecurity.Player);

        foreach (string command in new[] { ".gobject add 1", ".gobject near", ".npc say hi", ".npc info", ".respawn", ".spawninfo summary" })
        {
            Assert.Equal("This command is not available to you.", (await SendAsync(moderator, command)).Single);
            Assert.Equal("This command is not available to you.", (await SendAsync(player, command)).Single);
        }

        Assert.DoesNotContain(ObjectSystem(scene).GameObjects, g => g.Spawn is null);
    }

    [Fact]
    public async Task GobjectAdd_PlacesARuntimeObject_AndRefusesBadInput()
    {
        Scene scene = Start();
        await using WorldTestHost host = scene.Host;
        await using WorldTestClient gm = await GmAsync(scene, "GOAADD", "Goaadd");

        Run added = await SendAsync(gm, $".gobject add {DoorEntry}");
        Assert.StartsWith($"Game object Test Door (entry {DoorEntry}, guid ", added.Single, StringComparison.Ordinal);
        Assert.EndsWith(". It is not saved.", added.Single, StringComparison.Ordinal);
        GameObject runtime = await host.OnWorldAsync(() => ObjectSystem(scene).GameObjects.Single(g => g.Spawn is null));
        Assert.Equal(DoorEntry, runtime.Entry);
        Assert.True(runtime.IsSpawned);
        Assert.Contains(added.Packets, p => p.Opcode is WorldOpcode.SmsgUpdateObject or WorldOpcode.SmsgCompressedUpdateObject);

        Assert.Equal(GmObjectCommands.TemplateMissing(424242), (await SendAsync(gm, ".gobject add 424242")).Single);
        Assert.StartsWith("Syntax: .gobject add", (await SendAsync(gm, ".gobject add")).Replies[0], StringComparison.Ordinal);
        Assert.StartsWith("Syntax: .gobject add", (await SendAsync(gm, ".gobject add 0")).Replies[0], StringComparison.Ordinal);
        Assert.StartsWith("Syntax: .gobject add", (await SendAsync(gm, $".gobject add {DoorEntry} 5 extra")).Replies[0], StringComparison.Ordinal);
        Assert.StartsWith("Syntax: .gobject add", (await SendAsync(gm, ".gobject add -3")).Replies[0], StringComparison.Ordinal);
        Assert.Single(await host.OnWorldAsync(() => ObjectSystem(scene).GameObjects.Where(g => g.Spawn is null).ToArray()));
    }

    [Fact]
    public async Task GobjectAdd_WithADespawnTime_ExpiresOnItsOwn()
    {
        Scene scene = Start();
        await using WorldTestHost host = scene.Host;
        await using WorldTestClient gm = await GmAsync(scene, "GOAEXP", "Goaexp");

        await SendAsync(gm, $".gobject add {DoorEntry} 1");
        Assert.Single(await host.OnWorldAsync(() => ObjectSystem(scene).GameObjects.Where(g => g.Spawn is null).ToArray()));
        await host.WaitForWorldAsync(() => !ObjectSystem(scene).GameObjects.Any(g => g.Spawn is null), "the timed object expires");
    }

    [Fact]
    public async Task GobjectDelete_RemovesARuntimeObject_AndLeavesDatabaseSpawnsAlone()
    {
        Scene scene = Start();
        await using WorldTestHost host = scene.Host;
        await using WorldTestClient gm = await GmAsync(scene, "GODEL", "Godel");
        uint guid = await PlaceDoorAsync(scene);
        await gm.CollectAsync(Quiet);

        Run removed = await SendAsync(gm, $".gobject delete {guid}");
        Assert.Equal(GmObjectCommands.Removed("Test Door", DoorEntry, guid), removed.Single);
        Assert.Contains(removed.Packets, p => p.Opcode == WorldOpcode.SmsgDestroyObject);
        Assert.Empty(await host.OnWorldAsync(() => ObjectSystem(scene).GameObjects.Where(g => g.Guid.Counter == guid).ToArray()));

        Assert.Equal(GmObjectCommands.NotFound(guid), (await SendAsync(gm, $".gobject delete {guid}")).Single);
        Assert.Equal(GmObjectCommands.DatabaseSpawn(DoorSpawn), (await SendAsync(gm, $".gobject delete {DoorSpawn}")).Single);
        Assert.True(await host.OnWorldAsync(() => ObjectSystem(scene).GameObjects.Single(g => g.Guid.Counter == DoorSpawn).IsSpawned));
        Assert.StartsWith("Syntax: .gobject delete", (await SendAsync(gm, ".gobject delete")).Replies[0], StringComparison.Ordinal);
        Assert.StartsWith("Syntax: .gobject delete", (await SendAsync(gm, ".gobject delete abc")).Replies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task GobjectMoveAndTurn_RelocateARuntimeObject_AndRefuseOthers()
    {
        Scene scene = Start();
        await using WorldTestHost host = scene.Host;
        await using WorldTestClient gm = await GmAsync(scene, "GOMOVE", "Gomove");
        uint guid = await PlaceDoorAsync(scene);
        await gm.CollectAsync(Quiet);

        Run moved = await SendAsync(gm, $".gobject move {guid} -8945 -130 84");
        Assert.Equal(GmObjectCommands.Moved(guid, -8945f, -130f, 84f), moved.Single);
        Assert.Contains(moved.Packets, p => p.Opcode == WorldOpcode.SmsgDestroyObject);
        Assert.Contains(moved.Packets, p => p.Opcode is WorldOpcode.SmsgUpdateObject or WorldOpcode.SmsgCompressedUpdateObject);
        (float x, float y, float z, bool spawned) = await host.OnWorldAsync(() =>
        {
            GameObject go = ObjectSystem(scene).GameObjects.Single(g => g.Guid.Counter == guid);
            return (go.X, go.Y, go.Z, go.IsSpawned);
        });
        Assert.Equal((-8945f, -130f, 84f, true), (x, y, z, spawned));

        // Without coordinates the object goes to the invoker.
        await host.PlaceAsync("Gomove", -8940f, -120f, 85f);
        Assert.Equal(GmObjectCommands.Moved(guid, -8940f, -120f, 85f), (await SendAsync(gm, $".gobject move {guid}")).Single);

        Run turned = await SendAsync(gm, $".gobject turn {guid} 1.5");
        Assert.Equal(GmObjectCommands.Turned(guid, 1.5f), turned.Single);
        float orientation = await host.OnWorldAsync(() => ObjectSystem(scene).GameObjects.Single(g => g.Guid.Counter == guid).Orientation);
        Assert.Equal(1.5f, orientation);
        Assert.StartsWith($"Game object {guid} turned to orientation ", (await SendAsync(gm, $".gobject turn {guid}")).Single, StringComparison.Ordinal);

        Assert.Equal(GmObjectCommands.DatabaseSpawn(DoorSpawn), (await SendAsync(gm, $".gobject move {DoorSpawn} 1 2 3")).Single);
        Assert.Equal(GmObjectCommands.DatabaseSpawn(DoorSpawn), (await SendAsync(gm, $".gobject turn {DoorSpawn} 1")).Single);
        Assert.Equal(GmObjectCommands.NotFound(999999), (await SendAsync(gm, ".gobject move 999999 1 2 3")).Single);
        Assert.Equal(GmObjectCommands.NotFound(999999), (await SendAsync(gm, ".gobject turn 999999 1")).Single);
        Assert.StartsWith("Syntax: .gobject move", (await SendAsync(gm, $".gobject move {guid} 1 2")).Replies[0], StringComparison.Ordinal);
        Assert.StartsWith("Syntax: .gobject move", (await SendAsync(gm, ".gobject move")).Replies[0], StringComparison.Ordinal);
        Assert.StartsWith("Syntax: .gobject turn", (await SendAsync(gm, $".gobject turn {guid} north")).Replies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task GobjectMoveAndTurn_RefuseInvalidCoordinates_LikeTeleport()
    {
        Scene scene = Start();
        await using WorldTestHost host = scene.Host;
        await using WorldTestClient gm = await GmAsync(scene, "GOBAD", "Gobad");
        uint guid = await PlaceDoorAsync(scene);
        await gm.CollectAsync(Quiet);

        // ExtractFloat accepts exponents: "1e40" overflows a float to infinity, "1e9" is finite but far outside the map.
        string infinite = string.Format(CultureInfo.InvariantCulture, TeleportCommands.InvalidTargetText, float.PositiveInfinity, 0f, 0u);
        Run moved = await SendAsync(gm, $".gobject move {guid} 1e40 0 0");
        Assert.Equal(infinite, moved.Single);
        Assert.DoesNotContain(moved.Packets, p => p.Opcode == WorldOpcode.SmsgDestroyObject);
        Assert.Equal(string.Format(CultureInfo.InvariantCulture, TeleportCommands.InvalidTargetText, 1e9f, 0f, 0u), (await SendAsync(gm, $".gobject move {guid} 1e9 0 0")).Single);
        Assert.Equal(string.Format(CultureInfo.InvariantCulture, TeleportCommands.InvalidTargetText, -8950f, -133f, 0u), (await SendAsync(gm, $".gobject move {guid} -8950 -133 1e40")).Single);
        Assert.Equal(string.Format(CultureInfo.InvariantCulture, TeleportCommands.InvalidTargetText, -8950f, -133f, 0u), (await SendAsync(gm, $".gobject turn {guid} 1e40")).Single);

        (float x, float y, float z, float orientation, bool spawned) = await host.OnWorldAsync(() =>
        {
            GameObject go = ObjectSystem(scene).GameObjects.Single(g => g.Guid.Counter == guid);
            return (go.X, go.Y, go.Z, go.Orientation, go.IsSpawned);
        });
        Assert.Equal((-8950f, -133f, 83.5f, 1f, true), (x, y, z, orientation, spawned));
    }

    [Fact]
    public async Task GobjectMove_AcrossAGridLine_KeepsTheObjectTrackedInItsNewGrid()
    {
        Scene scene = Start();
        await using WorldTestHost host = scene.Host;
        await using WorldTestClient gm = await GmAsync(scene, "GOFAR", "Gofar");
        uint guid = await PlaceDoorAsync(scene);
        await gm.CollectAsync(Quiet);

        // 600 yards east is at least one grid (533.33 yards) away, outside the invoker's activation range: the object system has not loaded it.
        Assert.False(await host.OnWorldAsync(() => ObjectSystem(scene).IsGridLoaded(-8350f, -133f)));
        Assert.Equal(GmObjectCommands.Moved(guid, -8350f, -133f, 83.5f), (await SendAsync(gm, $".gobject move {guid} -8350 -133 83.5")).Single);
        Assert.True(await host.OnWorldAsync(() => ObjectSystem(scene).IsGridLoaded(-8350f, -133f)));

        // Unloading that grid takes the object with it, as it does for a runtime object placed at the invoker.
        bool gone = await host.OnWorldAsync(() =>
        {
            GameObjectMapSystem system = ObjectSystem(scene);
            GameObject go = system.GameObjects.Single(g => g.Guid.Counter == guid);
            Map map = host.World.FindOnlinePlayer("Gofar")!.Map!;
            return map.Grids.UnloadGrid(map.Grids.CellOf(go)!.Value.Grid, force: true) && system.Find(go.Guid) is null && map.FindObject(go.Guid) is null;
        });
        Assert.True(gone);
        Assert.Equal(GmObjectCommands.NotFound(guid), (await SendAsync(gm, $".gobject info {guid}")).Single);
    }

    [Fact]
    public async Task GobjectActivate_FlipsTheState_AndRefusesADespawnedOrUnknownObject()
    {
        Scene scene = Start();
        await using WorldTestHost host = scene.Host;
        await using WorldTestClient gm = await GmAsync(scene, "GOACT", "Goact");

        GameObjectState before = await host.OnWorldAsync(() => ObjectSystem(scene).GameObjects.Single(g => g.Guid.Counter == DoorSpawn).State);
        Assert.Equal(GameObjectState.Ready, before);
        Assert.Equal(GmObjectCommands.Activated(DoorSpawn, GameObjectState.Active), (await SendAsync(gm, $".gobject activate {DoorSpawn}")).Single);
        Assert.Equal(GameObjectState.Active, await host.OnWorldAsync(() => ObjectSystem(scene).GameObjects.Single(g => g.Guid.Counter == DoorSpawn).State));

        // Activating again flips it back (the object is made ready first, as vmangos does).
        Assert.Equal(GmObjectCommands.Activated(DoorSpawn, GameObjectState.Ready), (await SendAsync(gm, $".gobject activate {DoorSpawn}")).Single);

        await host.OnWorldAsync(() => ObjectSystem(scene).Despawn(ObjectSystem(scene).GameObjects.Single(g => g.Guid.Counter == DoorSpawn)));
        Assert.Equal(GmObjectCommands.NotSpawned(DoorSpawn), (await SendAsync(gm, $".gobject activate {DoorSpawn}")).Single);
        Assert.Equal(GmObjectCommands.NotFound(999999), (await SendAsync(gm, ".gobject activate 999999")).Single);
        Assert.StartsWith("Syntax: .gobject activate", (await SendAsync(gm, ".gobject activate")).Replies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task GobjectNearAndInfo_ListWhatIsAroundAndDescribeIt()
    {
        Scene scene = Start();
        await using WorldTestHost host = scene.Host;
        await using WorldTestClient gm = await GmAsync(scene, "GONEAR", "Gonear");

        Run near = await SendAsync(gm, ".gobject near");
        Assert.Equal("Game objects within 10 yards: 1", near.Replies[0]);
        Assert.StartsWith($"{DoorSpawn} entry {DoorEntry} Test Door (Door) spawned at -8948.00 -132.50 83.50, ", near.Replies[1], StringComparison.Ordinal);
        Assert.Equal(2, near.Replies.Count);

        Assert.Equal("Game objects within 1 yards: 0", (await SendAsync(gm, ".gobject near 1")).Single);
        Assert.Equal("Game objects within 1000 yards: 2", (await SendAsync(gm, ".gobject near 1000")).Replies[0]);
        foreach (string bad in new[] { ".gobject near 0", ".gobject near -5", ".gobject near 1001", ".gobject near ten", ".gobject near 5 5" })
        {
            Assert.StartsWith("Syntax: .gobject near", (await SendAsync(gm, bad)).Replies[0], StringComparison.Ordinal);
        }

        Run info = await SendAsync(gm, $".gobject info {DoorSpawn}");
        Assert.Equal(4, info.Replies.Count);
        Assert.StartsWith($"Test Door entry {DoorEntry} guid {DoorSpawn} type Door display 5", info.Replies[0], StringComparison.Ordinal);
        Assert.Equal($"database spawn {DoorSpawn}; spawned", info.Replies[1]);
        Assert.StartsWith("state Ready loot state Ready", info.Replies[2], StringComparison.Ordinal);
        Assert.Equal(GmObjectCommands.NotFound(999999), (await SendAsync(gm, ".gobject info 999999")).Single);
        Assert.StartsWith("Syntax: .gobject info", (await SendAsync(gm, ".gobject info")).Replies[0], StringComparison.Ordinal);

        uint guid = await PlaceDoorAsync(scene);
        Assert.Contains("runtime (not saved); spawned", (await SendAsync(gm, $".gobject info {guid}")).Replies);
        await host.OnWorldAsync(() => ObjectSystem(scene).Despawn(ObjectSystem(scene).GameObjects.Single(g => g.Guid.Counter == DoorSpawn)));
        Assert.Contains((await SendAsync(gm, ".gobject near")).Replies, r => r.StartsWith($"{DoorSpawn} entry {DoorEntry} Test Door (Door) despawned at ", StringComparison.Ordinal));
        Assert.Matches(@"^database spawn 88001; respawns in \d+ s$", (await SendAsync(gm, $".gobject info {DoorSpawn}")).Replies[1]);
    }

    [Fact]
    public async Task NpcSpeech_GoesOutThroughTheCreatureChatPackets()
    {
        Scene scene = Start();
        await using WorldTestHost host = scene.Host;
        await using WorldTestClient gm = await GmAsync(scene, "NPCSAY", "Npcsay");

        Run noSelection = await SendAsync(gm, ".npc say hello there");
        Assert.Equal(GmNpcCommands.SelectCreature, noSelection.Single);
        Assert.Empty(Chat(noSelection, ChatType.MonsterSay));

        await SelectAsync(scene, "Npcsay", WolfSpawn);
        Run say = await SendAsync(gm, ".npc say hello there");
        Assert.Equal(GmNpcCommands.Done("said it", "Test Wolf", WolfEntry, WolfSpawn), say.Single);
        (WorldOpcode _, byte[] said) = Assert.Single(Chat(say, ChatType.MonsterSay));
        Assert.True(Contains(said, "hello there") && Contains(said, "Test Wolf"));

        Run yell = await SendAsync(gm, ".npc yell LOUD words");
        Assert.Equal(GmNpcCommands.Done("yelled it", "Test Wolf", WolfEntry, WolfSpawn), yell.Single);
        Assert.True(Contains(Assert.Single(Chat(yell, ChatType.MonsterYell)).Payload, "LOUD words"));

        Run emote = await SendAsync(gm, ".npc textemote waves");
        Assert.Equal(GmNpcCommands.Done("emoted it", "Test Wolf", WolfEntry, WolfSpawn), emote.Single);
        Assert.True(Contains(Assert.Single(Chat(emote, ChatType.MonsterEmote)).Payload, "waves"));

        foreach (string verb in new[] { "say", "yell", "textemote" })
        {
            Run empty = await SendAsync(gm, $".npc {verb}");
            Assert.StartsWith($"Syntax: .npc {verb}", empty.Replies[0], StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task NpcSpeech_RefusesADeadCreature_AndASelectionThatIsNotACreature()
    {
        Scene scene = Start();
        await using WorldTestHost host = scene.Host;
        await using WorldTestClient gm = await GmAsync(scene, "NPCDEAD", "Npcdead");
        await SelectAsync(scene, "Npcdead", WolfSpawn);
        await host.OnWorldAsync(() => CreatureSystem(scene).KillCreature(CreatureSystem(scene).Creatures.Single(c => c.Spawn?.Guid == WolfSpawn)));
        await gm.CollectAsync(Quiet);

        foreach (string command in new[] { ".npc say hi", ".npc yell hi", ".npc textemote hi", ".npc playemote 1" })
        {
            Run run = await SendAsync(gm, command);
            Assert.Equal(GmNpcCommands.NotAlive("Test Wolf"), run.Single);
            Assert.Empty(Chat(run, ChatType.MonsterSay));
        }

        // Another player is not a creature, and a creature of another spawn that does not exist is not found.
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Npcdead")!.Selection = host.World.FindOnlinePlayer("Npcdead")!.Guid);
        Assert.Equal(GmNpcCommands.SelectCreature, (await SendAsync(gm, ".npc say hi")).Single);
        Assert.Equal(GmNpcCommands.SelectCreature, (await SendAsync(gm, ".npc info")).Single);
    }

    [Fact]
    public async Task NpcWhisper_ReachesTheNamedPlayerOnly_AndFailsOnBadTargets()
    {
        Scene scene = Start();
        await using WorldTestHost host = scene.Host;
        await using WorldTestClient gm = await GmAsync(scene, "NPCWHG", "Npcwhg");
        await using WorldTestClient listener = await GmAsync(scene, "NPCWHL", "Npcwhl", AccountSecurity.Player);
        await SelectAsync(scene, "Npcwhg", WolfSpawn);

        Run whisper = await SendAsync(gm, ".npc whisper Npcwhl psst over here");
        Assert.Equal(GmNpcCommands.Done("whispered it to Npcwhl", "Test Wolf", WolfEntry, WolfSpawn), whisper.Single);
        Assert.Empty(Chat(whisper, ChatType.MonsterWhisper));
        List<(WorldOpcode Opcode, byte[] Payload)> heard = await listener.CollectAsync(Quiet);
        (WorldOpcode _, byte[] payload) = Assert.Single(heard, p => p.Opcode == WorldOpcode.SmsgMessagechat && p.Payload[0] == (byte)ChatType.MonsterWhisper);
        Assert.True(Contains(payload, "psst over here"));

        Assert.Equal("Player not found!", (await SendAsync(gm, ".npc whisper Nobodyhere hi")).Single);
        Assert.StartsWith("Syntax: .npc whisper", (await SendAsync(gm, ".npc whisper Npcwhl")).Replies[0], StringComparison.Ordinal);
        Assert.StartsWith("Syntax: .npc whisper", (await SendAsync(gm, ".npc whisper")).Replies[0], StringComparison.Ordinal);

        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Npcwhg")!.Selection = default);
        Assert.Equal(GmNpcCommands.SelectCreature, (await SendAsync(gm, ".npc whisper Npcwhl hi")).Single);
    }

    [Fact]
    public async Task NpcPlayEmote_TellsTheObservers_AndChecksItsArgument()
    {
        Scene scene = Start();
        await using WorldTestHost host = scene.Host;
        await using WorldTestClient gm = await GmAsync(scene, "NPCEMO", "Npcemo");
        await SelectAsync(scene, "Npcemo", WolfSpawn);

        Run emote = await SendAsync(gm, ".npc playemote 18");
        Assert.Equal(GmNpcCommands.Done("played emote 18", "Test Wolf", WolfEntry, WolfSpawn), emote.Single);
        byte[] expected = ArcaneCore.Game.Creatures.CreatureChatPackets.BuildEmote(18, ObjectGuid.WithEntry(HighGuid.Unit, WolfEntry, WolfSpawn));
        Assert.Contains(emote.Packets, p => p.Opcode == WorldOpcode.SmsgEmote && p.Payload.AsSpan().SequenceEqual(expected));

        foreach (string bad in new[] { ".npc playemote", ".npc playemote -1", ".npc playemote cheer", ".npc playemote 1 2" })
        {
            Assert.StartsWith("Syntax: .npc playemote", (await SendAsync(gm, bad)).Replies[0], StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task NpcInfoNearAddDelete_ReadAndEditTheLiveCreatures()
    {
        Scene scene = Start();
        await using WorldTestHost host = scene.Host;
        await using WorldTestClient gm = await GmAsync(scene, "NPCINF", "Npcinf");
        await SelectAsync(scene, "Npcinf", WolfSpawn);

        Run info = await SendAsync(gm, ".npc info");
        Assert.Equal(4, info.Replies.Count);
        Assert.StartsWith($"Test Wolf entry {WolfEntry} guid {WolfSpawn} level 2 health ", info.Replies[0], StringComparison.Ordinal);
        Assert.Equal($"database spawn {WolfSpawn}; alive", info.Replies[1]);
        Assert.StartsWith("Syntax: .npc info", (await SendAsync(gm, ".npc info now")).Replies[0], StringComparison.Ordinal);

        Run near = await SendAsync(gm, ".npc near");
        Assert.Equal("Creatures within 10 yards: 1", near.Replies[0]);
        Assert.StartsWith($"{WolfSpawn} entry {WolfEntry} Test Wolf level 2 alive at -8940.00 -132.00 83.50, ", near.Replies[1], StringComparison.Ordinal);
        Assert.StartsWith("Syntax: .npc near", (await SendAsync(gm, ".npc near 0")).Replies[0], StringComparison.Ordinal);

        Run added = await SendAsync(gm, $".npc add {WolfEntry}");
        Assert.StartsWith("Spawned Test Wolf", added.Single, StringComparison.Ordinal);
        Creature temporary = await host.OnWorldAsync(() => CreatureSystem(scene).Creatures.Single(c => c.Spawn is null));
        Assert.Equal("Creatures within 10 yards: 2", (await SendAsync(gm, ".npc near")).Replies[0]);
        Assert.Equal("Creature template 424242 does not exist.", (await SendAsync(gm, ".npc add 424242")).Single);
        Assert.StartsWith("Syntax: .npc add", (await SendAsync(gm, ".npc add")).Replies[0], StringComparison.Ordinal);

        // A database spawn cannot be deleted; a temporary one can.
        Assert.Equal("Database spawns cannot be deleted in game yet.", (await SendAsync(gm, ".npc delete")).Single);
        Assert.NotNull(await host.OnWorldAsync(() => CreatureSystem(scene).FindCreature(ObjectGuid.WithEntry(HighGuid.Unit, WolfEntry, WolfSpawn))));
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Npcinf")!.Selection = temporary.Guid);
        await SendAsync(gm, ".npc delete", WorldOpcode.SmsgDestroyObject); // a temporary creature goes silently
        Assert.Null(await host.OnWorldAsync(() => CreatureSystem(scene).FindCreature(temporary.Guid)));
    }

    [Fact]
    public async Task Respawn_BringsBackWhatIsInRange_AndOnlyThat()
    {
        Scene scene = Start();
        await using WorldTestHost host = scene.Host;
        await using WorldTestClient gm = await GmAsync(scene, "RESP", "Resp");
        await host.OnWorldAsync(() =>
        {
            CreatureMapSystem creatures = CreatureSystem(scene);
            foreach (Creature c in creatures.Creatures.Where(c => c.Spawn is not null).ToArray())
            {
                creatures.KillCreature(c);
            }

            GameObjectMapSystem objects = ObjectSystem(scene);
            foreach (GameObject go in objects.GameObjects.Where(g => g.Spawn is not null).ToArray())
            {
                objects.Despawn(go);
            }
        });
        await gm.CollectAsync(Quiet);

        // The near wolf and door are in range (9 and 2 yards), the far ones (150) are not.
        Assert.Equal(GmSpawnCommands.Respawned(1, 1, 100f), (await SendAsync(gm, ".respawn")).Single);
        Assert.Equal(
            (CreatureDeathState.Alive, CreatureDeathState.Corpse, true, false),
            await host.OnWorldAsync(() => (
                CreatureSystem(scene).Creatures.Single(c => c.Spawn?.Guid == WolfSpawn).DeathState,
                CreatureSystem(scene).Creatures.Single(c => c.Spawn?.Guid == FarWolfSpawn).DeathState,
                ObjectSystem(scene).GameObjects.Single(g => g.Guid.Counter == DoorSpawn).IsSpawned,
                ObjectSystem(scene).GameObjects.Single(g => g.Guid.Counter == FarDoorSpawn).IsSpawned)));

        // Nothing left in range; a larger radius reaches the far ones.
        Assert.Equal(GmSpawnCommands.Respawned(0, 0, 100f), (await SendAsync(gm, ".respawn")).Single);
        Assert.Equal(GmSpawnCommands.Respawned(1, 1, 1000f), (await SendAsync(gm, ".respawn 1000")).Single);

        foreach (string bad in new[] { ".respawn 0", ".respawn -1", ".respawn 1001", ".respawn far", ".respawn 5 5" })
        {
            Assert.StartsWith("Syntax: .respawn", (await SendAsync(gm, bad)).Replies[0], StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Respawn_LeavesTemporaryCreaturesAndRuntimeObjectsAlone()
    {
        Scene scene = Start();
        await using WorldTestHost host = scene.Host;
        await using WorldTestClient gm = await GmAsync(scene, "RESPT", "Respt");
        await SendAsync(gm, $".npc add {WolfEntry}");
        await SendAsync(gm, $".gobject add {DoorEntry}");
        await host.OnWorldAsync(() =>
        {
            CreatureMapSystem creatures = CreatureSystem(scene);
            creatures.KillCreature(creatures.Creatures.Single(c => c.Spawn is null));
        });

        Assert.Equal(GmSpawnCommands.Respawned(0, 0, 100f), (await SendAsync(gm, ".respawn")).Single);
        Assert.Equal(CreatureDeathState.Corpse, await host.OnWorldAsync(() => CreatureSystem(scene).Creatures.Single(c => c.Spawn is null).DeathState));
    }

    [Fact]
    public async Task NpcInfoAndSpawnInfo_SayADeadTemporaryCreatureDoesNotRespawn()
    {
        Scene scene = Start();
        await using WorldTestHost host = scene.Host;
        await using WorldTestClient gm = await GmAsync(scene, "NPCTMP", "Npctmp");
        await SendAsync(gm, $".npc add {WolfEntry}");
        Creature temporary = await host.OnWorldAsync(() =>
        {
            CreatureMapSystem creatures = CreatureSystem(scene);
            Creature added = creatures.Creatures.Single(c => c.Spawn is null);
            creatures.KillCreature(added);
            host.World.FindOnlinePlayer("Npctmp")!.Selection = added.Guid;
            return added;
        });
        await gm.CollectAsync(Quiet);

        // OnCreatureDied sets RespawnAtMs from the template delay, but only creatures with a Spawn are respawned by the update loop.
        Run info = await SendAsync(gm, ".npc info");
        Assert.Equal(4, info.Replies.Count);
        Assert.StartsWith($"Test Wolf entry {WolfEntry} guid {temporary.Guid.Counter} ", info.Replies[0], StringComparison.Ordinal);
        Assert.Equal("temporary (not saved); corpse, no respawn", info.Replies[1]);

        Run near = await SendAsync(gm, ".spawninfo creature");
        Assert.Equal("Creatures within 40 yards: 2", near.Replies[0]);
        Assert.Contains(near.Replies, r => r.StartsWith($"{temporary.Guid.Counter} entry {WolfEntry} Test Wolf: temporary, corpse, no respawn, ", StringComparison.Ordinal));
        Assert.Contains(near.Replies, r => r.StartsWith($"{WolfSpawn} entry {WolfEntry} Test Wolf: database spawn {WolfSpawn}, alive, ", StringComparison.Ordinal));
        Assert.DoesNotContain(near.Replies, r => r.Contains("temporary, corpse, respawns in", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SpawnInfo_ReportsOriginAndRespawnState_WithoutChangingAnything()
    {
        Scene scene = Start();
        await using WorldTestHost host = scene.Host;
        await using WorldTestClient gm = await GmAsync(scene, "SPINF", "Spinf");

        Run creatures = await SendAsync(gm, ".spawninfo creature");
        Assert.Equal("Creatures within 40 yards: 1", creatures.Replies[0]);
        Assert.StartsWith($"{WolfSpawn} entry {WolfEntry} Test Wolf: database spawn {WolfSpawn}, alive, ", creatures.Replies[1], StringComparison.Ordinal);

        await host.OnWorldAsync(() => CreatureSystem(scene).KillCreature(CreatureSystem(scene).Creatures.Single(c => c.Spawn?.Guid == WolfSpawn)));
        await SendAsync(gm, $".npc add {WolfEntry}");
        Run dead = await SendAsync(gm, ".spawninfo creature");
        Assert.Equal("Creatures within 40 yards: 2", dead.Replies[0]);
        Assert.Contains(dead.Replies, r => System.Text.RegularExpressions.Regex.IsMatch(r, $@"^{WolfSpawn} entry {WolfEntry} Test Wolf: database spawn {WolfSpawn}, corpse, respawns in \d+ s, "));
        Assert.Contains(dead.Replies, r => r.Contains(": temporary, alive, ", StringComparison.Ordinal));

        await host.OnWorldAsync(() => ObjectSystem(scene).Despawn(ObjectSystem(scene).GameObjects.Single(g => g.Guid.Counter == DoorSpawn)));
        await SendAsync(gm, $".gobject add {DoorEntry}");
        Run objects = await SendAsync(gm, ".spawninfo gameobject");
        Assert.Equal("Game objects within 40 yards: 2", objects.Replies[0]);
        Assert.Contains(objects.Replies, r => System.Text.RegularExpressions.Regex.IsMatch(r, $@"^{DoorSpawn} entry {DoorEntry} Test Door: database spawn {DoorSpawn}, despawned, respawns in \d+ s, "));
        Assert.Contains(objects.Replies, r => r.Contains(": runtime, spawned, ", StringComparison.Ordinal));

        Run summary = await SendAsync(gm, ".spawninfo summary");
        Assert.Equal(
            "Creatures on map 0: 3 loaded (2 database, 1 temporary), 2 alive, 1 corpses, 0 dead; 0 respawn time(s) kept for unloaded grids.",
            summary.Replies[0]);
        Assert.Equal(
            "Game objects on map 0: 3 loaded (2 database, 1 runtime), 2 spawned, 1 waiting to respawn, 0 despawned with no timer; 0 respawn time(s) kept for unloaded grids.",
            summary.Replies[1]);

        // Read-only: the states are as they were left.
        Assert.Equal(CreatureDeathState.Corpse, await host.OnWorldAsync(() => CreatureSystem(scene).Creatures.Single(c => c.Spawn?.Guid == WolfSpawn).DeathState));
        foreach (string bad in new[] { ".spawninfo creature 0", ".spawninfo gameobject abc", ".spawninfo summary now", ".spawninfo creature 1001" })
        {
            Assert.StartsWith("Syntax: .spawninfo", (await SendAsync(gm, bad)).Replies[0], StringComparison.Ordinal);
        }
    }
}
