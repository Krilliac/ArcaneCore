using ArcaneCore.Game;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Reload;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Reload;
using ArcaneCore.World.Tests.GameObjects;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Reload;

/// <summary>
/// <c>.reload gameobject_template</c> (vmangos ServerCommands.cpp:1775-1790 → ObjectMgr::LoadGameObjectTemplates, ObjectMgr.cpp:8140): the
/// templates are replaced from the content table, the live objects are rebound to them, and the spawns, locks and quest relations are
/// left alone. Each test changes a row between the first load and the reload and reads it through a live chest or a client query.
/// </summary>
public sealed class GameObjectReloadTests
{
    private const uint ChestEntry = 2843;
    private const uint ChestSpawn = 77001;
    private const uint OldLootId = 2843;
    private const uint NewLootId = 2844;
    private const uint LinenCloth = 2589;
    private const uint WoolCloth = 2592;
    private const uint Added = 2999;

    private static GameObjectTemplate Chest(string name, uint lootId, uint displayId = 10)
    {
        uint[] data = new uint[GameObjectTemplate.DataCount];
        data[1] = lootId;
        return new GameObjectTemplate { Entry = ChestEntry, Type = (uint)GameObjectType.Chest, DisplayId = displayId, Name = name, Data = data };
    }

    private static GameObjectContent Content(params GameObjectTemplate[] templates)
    {
        // Human start is (-8949.95, -132.49, 83.53): the chest is 2 yd away.
        var spawn = new GameObjectSpawn { Guid = ChestSpawn, Entry = ChestEntry, MapId = 0, X = -8948f, Y = -132.5f, Z = 83.5f };
        return new GameObjectContent(templates, [spawn], [new LockEntry(5, [1], [4000], [0])], [(ChestEntry, 9001)], [(ChestEntry, 9002)]);
    }

    private static (WorldTestHost Host, GameObjectTestContext Context) Start()
    {
        var context = new GameObjectTestContext(
            Content(Chest("Battered Chest", OldLootId)),
            new LootContent(
            [
                (LootTableKind.GameObject, new LootStoreRow(OldLootId, LinenCloth, 100f, 0, 2, 2)),
                (LootTableKind.GameObject, new LootStoreRow(NewLootId, WoolCloth, 100f, 0, 3, 3)),
            ], []));
        var items = new ItemTestContent();
        items.Templates.Templates.AddRange(
        [
            new ItemTemplate { Entry = LinenCloth, Class = 7, Name = "Linen Cloth", DisplayId = 3776, Quality = 1, Stackable = 20 },
            new ItemTemplate { Entry = WoolCloth, Class = 7, Name = "Wool Cloth", DisplayId = 3777, Quality = 1, Stackable = 20 },
        ]);

        // The test's store reads the async-local context when a scope creates it, and a reload creates a scope after the host started,
        // so the context stays set for the rest of the test (each test runs in its own async context).
        GameObjectTestStore.Current.Value = context;
        using (items.Use())
        {
            return (WorldTestHost.Start(), context);
        }
    }

    private static ReloadCoordinator Coordinator(WorldTestHost host) => host.WorldServices.GetRequiredService<ReloadFeature>().Coordinator;

    private static GameObjectLootFeature Feature(WorldTestHost host) => host.WorldServices.GetRequiredService<GameObjectLootFeature>();

    private static ObjectGuid ChestGuid() => ObjectGuid.WithEntry(HighGuid.GameObject, ChestEntry, ChestSpawn);

    private static async Task<(string Name, uint Display)> QueryAsync(WorldTestClient client, uint entry)
    {
        var query = new PacketWriter(12);
        query.WriteUInt32(entry);
        query.WriteUInt64(ChestGuid().Value);
        await client.SendAsync(WorldOpcode.CmsgGameobjectQuery, query.ToArray());
        var reader = new PacketReader(await client.ReadUntilAsync(WorldOpcode.SmsgGameobjectQueryResponse));
        reader.Skip(4);
        reader.Skip(4);
        uint display = reader.ReadUInt32();
        return (reader.ReadCString(), display);
    }

    [Fact]
    public async Task AChangedRow_IsSeenByAClientQuery_AndByTheLiveChest()
    {
        (WorldTestHost host, GameObjectTestContext context) = Start();
        await using WorldTestHost _ = host;
        await using WorldTestClient client = await host.EnterWorldAsync("RGCLIENT", "Rgclient");
        await GameObjectWorldTests.ReadUntilGameObjectCreateAsync(client, ChestGuid().Value);
        Assert.Equal(("Battered Chest", 10u), await QueryAsync(client, ChestEntry));
        context.Content = Content(Chest("Reloaded Chest", NewLootId, displayId: 11));

        ReloadResult result = await Coordinator(host).ReloadAsync("gameobject_template");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Equal(("Reloaded Chest", 11u), await QueryAsync(client, ChestEntry));
        GameObject chest = await host.OnWorldAsync(() => Feature(host).FindSystem(0)!.Find(ChestGuid())!);
        Assert.Equal("Reloaded Chest", await host.OnWorldAsync(() => chest.Template.Name));
        Assert.Contains("1 game object templates", result.Message);
    }

    [Fact]
    public async Task TheLiveChest_UsesTheReloadedLootId_ButKeepsItsCreationFields()
    {
        (WorldTestHost host, GameObjectTestContext context) = Start();
        await using WorldTestHost _ = host;
        await using WorldTestClient client = await host.EnterWorldAsync("RGLOOT", "Rgloot");
        await GameObjectWorldTests.ReadUntilGameObjectCreateAsync(client, ChestGuid().Value);
        context.Content = Content(Chest("Reloaded Chest", NewLootId, displayId: 11));

        await Coordinator(host).ReloadAsync("gameobject_template");

        await client.SendAsync(WorldOpcode.CmsgGameobjUse, BitConverter.GetBytes(ChestGuid().Value));
        var loot = new PacketReader(await client.ReadUntilAsync(WorldOpcode.SmsgLootResponse));
        loot.Skip(8 + 1 + 4 + 1 + 1);
        Assert.Equal(WoolCloth, loot.ReadUInt32());
        Assert.Equal(3u, loot.ReadUInt32());

        // The fields written when the object was created change only when it is created again (vmangos behaves the same way).
        uint display = await host.OnWorldAsync(() => Feature(host).FindSystem(0)!.Find(ChestGuid())!.GetUInt32(UpdateFields.GameobjectDisplayid));
        Assert.Equal(10u, display);
    }

    [Fact]
    public async Task SpawnsLocksAndQuestRelations_AreNotReloaded()
    {
        (WorldTestHost host, GameObjectTestContext context) = Start();
        await using WorldTestHost _ = host;
        GameObjectLootFeature feature = Feature(host);
        context.Content = new GameObjectContent([Chest("Reloaded Chest", NewLootId)], [], [], [], []);

        ReloadResult result = await Coordinator(host).ReloadAsync("gameobject_template");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Equal(1, feature.Content.SpawnCount);
        Assert.Equal(1, feature.Content.LockCount);
        Assert.Equal([9001u], feature.Content.QuestStartersOf(ChestEntry));
        Assert.Equal([9002u], feature.Content.QuestEndersOf(ChestEntry));
        Assert.Equal("Reloaded Chest", feature.Content.FindTemplate(ChestEntry)?.Name);
    }

    [Fact]
    public async Task AnAddedTemplate_IsLive_AndARemovedOneStaysLoaded_AndIsReported()
    {
        // ObjectMgr.cpp:8148-8153: the loader inserts or overwrites entries of its map and never removes one.
        (WorldTestHost host, GameObjectTestContext context) = Start();
        await using WorldTestHost _ = host;
        GameObjectLootFeature feature = Feature(host);
        context.Content = Content(new GameObjectTemplate { Entry = Added, Type = (uint)GameObjectType.Chest, Name = "Added By Reload", Data = new uint[GameObjectTemplate.DataCount] });

        ReloadResult result = await Coordinator(host).ReloadAsync("gameobject_template");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Equal("Added By Reload", feature.Content.FindTemplate(Added)?.Name);
        Assert.Equal("Battered Chest", feature.Content.FindTemplate(ChestEntry)?.Name);
        Assert.Contains(result.Notes, n => n.Contains("1 game object template(s) no longer in gameobject_template", StringComparison.Ordinal) && n.Contains($"{ChestEntry}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnEmptyTable_KeepsTheLoadedTemplates()
    {
        // ObjectMgr.cpp:8145-8146: an empty result returns before any template is touched.
        (WorldTestHost host, GameObjectTestContext context) = Start();
        await using WorldTestHost _ = host;
        GameObjectLootFeature feature = Feature(host);
        GameObjectContent before = feature.Content;
        context.Content = new GameObjectContent([], [], [], [], []);

        ReloadResult result = await Coordinator(host).ReloadAsync("gameobject_template");

        Assert.Equal(ReloadStatus.KeptCurrent, result.Status);
        Assert.Same(before, feature.Content);
    }

    [Fact]
    public async Task AFailingStore_ChangesNothing()
    {
        (WorldTestHost host, GameObjectTestContext context) = Start();
        await using WorldTestHost _ = host;
        GameObjectLootFeature feature = Feature(host);
        GameObjectContent before = feature.Content;
        context.ContentFailure = new InvalidOperationException("world database unavailable");

        ReloadResult result = await Coordinator(host).ReloadAsync("gameobject_template");

        Assert.Equal(ReloadStatus.Failed, result.Status);
        Assert.Contains("world database unavailable", result.Message);
        Assert.Same(before, feature.Content);
    }

    [Fact]
    public async Task AFailureLaterInTheSwap_PutsTheOldTemplatesBack_OnTheLiveObjectsToo()
    {
        (WorldTestHost host, GameObjectTestContext context) = Start();
        await using WorldTestHost _ = host;
        await using WorldTestClient client = await host.EnterWorldAsync("RGBACK", "Rgback");
        await GameObjectWorldTests.ReadUntilGameObjectCreateAsync(client, ChestGuid().Value);
        GameObjectLootFeature feature = Feature(host);
        GameObjectContent before = feature.Content;
        context.Content = Content(Chest("Never goes live", NewLootId));
        ArcaneCore.Game.Reload.ContentCandidate candidate = await new GameObjectContentReloadable(host.WorldServices).BuildAsync(CancellationToken.None);

        await host.OnWorldAsync(() =>
        {
            GameObject chest = feature.FindSystem(0)!.Find(ChestGuid())!;
            var transaction = new ArcaneCore.Game.Reload.ReloadTransaction();
            candidate.Commit(host.World, transaction);
            Assert.Equal("Never goes live", chest.Template.Name);
            Assert.Throws<InvalidOperationException>(() => transaction.Step("later step", () => throw new InvalidOperationException("boom"), () => { }));
            Assert.Empty(transaction.Rollback());
            Assert.Equal("Battered Chest", chest.Template.Name);
        });

        Assert.Same(before, feature.Content);
    }
}
