using ArcaneCore.Game;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Kernel.Configuration;
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
/// The loot table reloads (vmangos ServerCommands.cpp:916-923 and :1174-1254 → LootStore::LoadLootTable, LootMgr.cpp:94-189;
/// ObjectMgr::LoadFishingBaseSkillLevel, ObjectMgr.cpp:10407): the loot tables of the loot module are replaced from the content
/// tables, so a chest or corpse filled afterwards rolls from the new rows while loot that is already open keeps what it rolled.
/// Each test changes a row between the first load and the reload and reads it through a live holder.
/// </summary>
public sealed class LootReloadTests
{
    private const uint ChestEntry = 2843;
    private const uint ChestSpawn = 77001;
    private const uint ChestLootId = 2843;
    private const uint LinenCloth = 2589;
    private const uint WoolCloth = 2592;
    private const uint CreatureEntry = 900;
    private const uint FishArea = 71;

    private static readonly LootTableKind[] Kinds = Enum.GetValues<LootTableKind>();

    public static TheoryData<LootTableKind> AllKinds => [.. Kinds];

    private static string ReloadName(LootTableKind kind) => kind switch
    {
        LootTableKind.Creature => "creature_loot_template",
        LootTableKind.GameObject => "gameobject_loot_template",
        LootTableKind.Item => "item_loot_template",
        LootTableKind.Skinning => "skinning_loot_template",
        LootTableKind.Reference => "reference_loot_template",
        LootTableKind.Fishing => "fishing_loot_template",
        LootTableKind.Pickpocketing => "pickpocketing_loot_template",
        LootTableKind.Disenchant => "disenchant_loot_template",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static uint EntryOf(LootTableKind kind) => 5000 + (uint)kind;

    /// <summary>One row in every table, one creature, one pickpocket id, one fishing base level; the item ids carry <paramref name="number"/>.</summary>
    private static LootContent Full(uint number) => new(
        Kinds.Select(k => (k, new LootStoreRow(EntryOf(k), (number * 1000) + (uint)k, 100f, 0, 1, 1))),
        [new CreatureLootInfo(CreatureEntry, number, number, 0, 0)],
        [new KeyValuePair<uint, int>(FishArea, (int)number * 100)],
        [new KeyValuePair<uint, uint>(CreatureEntry, number)]);

    private static string Observe(LootContent content, LootTableKind kind)
        => string.Join(",", content.GetRows(kind, EntryOf(kind)).Select(r => r.Item));

    private static string ObserveAll(LootContent content)
        => string.Join(" ", Kinds.Select(k => Observe(content, k))) + $" | {content.FindCreature(CreatureEntry)?.LootId} {content.FishingBaseSkill(FishArea)} {content.FindPickpocketLootId(CreatureEntry)}";

    /// <remarks>
    /// The test's store (<see cref="GameObjectTestStore"/>) is scoped and reads the async-local context when a scope creates it, and a reload
    /// creates a scope long after the host started, so the context stays set for the rest of the test (each test runs in its own async context).
    /// </remarks>
    private static WorldTestHost Start(GameObjectTestContext context, ItemTestContent? items = null)
    {
        GameObjectTestStore.Current.Value = context;
        using (items?.Use())
        {
            return WorldTestHost.Start();
        }
    }
    private static GameObjectTestContext Context(LootContent loot) => new(GameObjectContent.Empty, loot);

    private static ReloadCoordinator Coordinator(WorldTestHost host) => host.WorldServices.GetRequiredService<ReloadFeature>().Coordinator;

    private static GameObjectLootFeature Feature(WorldTestHost host) => host.WorldServices.GetRequiredService<GameObjectLootFeature>();

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task AChangedTable_IsSeenByTheLootModule_AndByTheLootServiceOfAMap_AndOnlyThatTableChanges(LootTableKind kind)
    {
        GameObjectTestContext context = Context(Full(1));
        await using WorldTestHost host = Start(context);
        GameObjectLootFeature feature = Feature(host);
        context.Loot = Full(2);

        ReloadResult result = await Coordinator(host).ReloadAsync(ReloadName(kind));

        Assert.Equal(ReloadStatus.Applied, result.Status);
        foreach (LootTableKind other in Kinds)
        {
            Assert.Equal(other == kind ? $"{(2000 + (uint)other)}" : $"{(1000 + (uint)other)}", Observe(feature.LootContent, other));
        }

        // The creature, pickpocket and fishing columns are not part of a *_loot_template reload.
        Assert.EndsWith("| 1 100 1", ObserveAll(feature.LootContent));
        Assert.Same(feature.LootContent, await host.OnWorldAsync(() => feature.GetOrCreateSystem(0u).Loot!.Content));
    }

    [Fact]
    public async Task AllLoot_ReplacesEveryTable_AndTheColumnsTheLootModuleReadsWithThem()
    {
        GameObjectTestContext context = Context(Full(1));
        await using WorldTestHost host = Start(context);
        GameObjectLootFeature feature = Feature(host);
        context.Loot = Full(2);

        ReloadResult result = await Coordinator(host).ReloadAsync("all_loot");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Equal(ObserveAll(Full(2)), ObserveAll(feature.LootContent));
        Assert.Same(feature.LootContent, await host.OnWorldAsync(() => feature.GetOrCreateSystem(0u).Loot!.Content));
        Assert.Contains($"{Kinds.Length} loot rows", result.Message);
    }

    [Fact]
    public async Task SkillFishingBaseLevel_ReplacesOnlyTheFishingBaseLevels()
    {
        GameObjectTestContext context = Context(Full(1));
        await using WorldTestHost host = Start(context);
        GameObjectLootFeature feature = Feature(host);
        context.Loot = Full(2);

        ReloadResult result = await Coordinator(host).ReloadAsync("skill_fishing_base_level");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Equal(200, feature.LootContent.FishingBaseSkill(FishArea));
        Assert.Equal(ObserveAll(Full(1)).Replace("100 1", "200 1", StringComparison.Ordinal), ObserveAll(feature.LootContent));
    }

    [Fact]
    public async Task AMapCreatedAfterTheReload_UsesTheNewTables()
    {
        GameObjectTestContext context = Context(Full(1));
        await using WorldTestHost host = Start(context);
        GameObjectLootFeature feature = Feature(host);
        context.Loot = Full(2);
        await Coordinator(host).ReloadAsync("all_loot");

        LootContent seen = await host.OnWorldAsync(() => feature.GetOrCreateSystem(1u).Loot!.Content);

        Assert.Equal(ObserveAll(Full(2)), ObserveAll(seen));
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task AnEmptyTable_EmptiesTheRows_AsVmangosDoes(LootTableKind kind)
    {
        // LootMgr.cpp:100 Clear() runs before the query result is looked at.
        GameObjectTestContext context = Context(Full(1));
        await using WorldTestHost host = Start(context);
        GameObjectLootFeature feature = Feature(host);
        context.Loot = new LootContent(Full(1).Rows.Where(r => r.Kind != kind), [], [], []);

        ReloadResult result = await Coordinator(host).ReloadAsync(ReloadName(kind));

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Equal(string.Empty, Observe(feature.LootContent, kind));
        Assert.Equal(Kinds.Length - 1, feature.LootContent.RowCount);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task AnEmptyTable_KeepsTheRows_WhenTheOptionSaysKeepLoaded(LootTableKind kind)
    {
        GameObjectTestContext context = Context(Full(1));
        await using WorldTestHost host = Start(context);
        host.WorldServices.GetRequiredService<ReloadFeature>().Options.EmptyTables = EmptyTablePolicy.KeepLoaded;
        GameObjectLootFeature feature = Feature(host);
        LootContent before = feature.LootContent;
        context.Loot = new LootContent(Full(1).Rows.Where(r => r.Kind != kind), [], [], []);

        ReloadResult result = await Coordinator(host).ReloadAsync(ReloadName(kind));

        Assert.Equal(ReloadStatus.KeptCurrent, result.Status);
        Assert.Same(before, feature.LootContent);
    }

    [Fact]
    public async Task AFailingStore_ChangesNothing()
    {
        GameObjectTestContext context = Context(Full(1));
        await using WorldTestHost host = Start(context);
        GameObjectLootFeature feature = Feature(host);
        LootContent before = feature.LootContent;
        context.LootFailure = new InvalidOperationException("world database unavailable");

        ReloadResult result = await Coordinator(host).ReloadAsync("all_loot");

        Assert.Equal(ReloadStatus.Failed, result.Status);
        Assert.Contains("world database unavailable", result.Message);
        Assert.Same(before, feature.LootContent);
    }

    [Fact]
    public async Task AFailureLaterInTheSwap_PutsTheOldTablesBack()
    {
        GameObjectTestContext context = Context(Full(1));
        await using WorldTestHost host = Start(context);
        GameObjectLootFeature feature = Feature(host);
        LootContent before = feature.LootContent;
        await host.OnWorldAsync(() => feature.GetOrCreateSystem(0u));
        context.Loot = Full(2);
        ArcaneCore.Game.Reload.ContentCandidate candidate = await new AllLootContentReloadable(host.WorldServices).BuildAsync(CancellationToken.None);

        await host.OnWorldAsync(() =>
        {
            var transaction = new ArcaneCore.Game.Reload.ReloadTransaction();
            candidate.Commit(host.World, transaction);
            Assert.NotSame(before, feature.LootContent);
            Assert.Throws<InvalidOperationException>(() => transaction.Step("later step", () => throw new InvalidOperationException("boom"), () => { }));
            Assert.Empty(transaction.Rollback());
        });

        Assert.Same(before, feature.LootContent);
        Assert.Same(before, await host.OnWorldAsync(() => feature.GetOrCreateSystem(0u).Loot!.Content));
    }

    // --- a chest on a live map -------------------------------------------------------------

    private static (WorldTestHost Host, GameObjectTestContext Context) StartWithChest()
    {
        uint[] data = new uint[GameObjectTemplate.DataCount];
        data[1] = ChestLootId;
        var chest = new GameObjectTemplate { Entry = ChestEntry, Type = (uint)GameObjectType.Chest, DisplayId = 10, Name = "Battered Chest", Data = data };

        // Human start is (-8949.95, -132.49, 83.53): the chest is 2 yd away.
        var spawn = new GameObjectSpawn { Guid = ChestSpawn, Entry = ChestEntry, MapId = 0, X = -8948f, Y = -132.5f, Z = 83.5f };
        var context = new GameObjectTestContext(
            new GameObjectContent([chest], [spawn], [], [], []),
            new LootContent([(LootTableKind.GameObject, new LootStoreRow(ChestLootId, LinenCloth, 100f, 0, 2, 2))], []));
        var items = new ItemTestContent();
        items.Templates.Templates.AddRange(
        [
            new ItemTemplate { Entry = LinenCloth, Class = 7, Name = "Linen Cloth", DisplayId = 3776, Quality = 1, Stackable = 20 },
            new ItemTemplate { Entry = WoolCloth, Class = 7, Name = "Wool Cloth", DisplayId = 3777, Quality = 1, Stackable = 20 },
        ]);
        return (Start(context, items), context);
    }

    private static ulong ChestGuid() => ObjectGuid.WithEntry(HighGuid.GameObject, ChestEntry, ChestSpawn).Value;

    private static async Task<(uint Item, uint Count)> OpenChestAsync(WorldTestClient client)
    {
        await client.SendAsync(WorldOpcode.CmsgGameobjUse, BitConverter.GetBytes(ChestGuid()));
        var loot = new PacketReader(await client.ReadUntilAsync(WorldOpcode.SmsgLootResponse));
        loot.Skip(8 + 1 + 4 + 1 + 1);           // guid, loot type, gold, item count, slot
        uint item = loot.ReadUInt32();
        uint count = loot.ReadUInt32();
        return (item, count);
    }

    [Fact]
    public async Task AChestOpenedAfterTheReload_RollsFromTheNewTable()
    {
        (WorldTestHost host, GameObjectTestContext context) = StartWithChest();
        await using WorldTestHost _ = host;
        await using WorldTestClient client = await host.EnterWorldAsync("RLCHEST", "Rlchest");
        await GameObjectWorldTests.ReadUntilGameObjectCreateAsync(client, ChestGuid());
        context.Loot = new LootContent([(LootTableKind.GameObject, new LootStoreRow(ChestLootId, WoolCloth, 100f, 0, 3, 3))], []);

        ReloadResult result = await Coordinator(host).ReloadAsync("gameobject_loot_template");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Equal((WoolCloth, 3u), await OpenChestAsync(client));
    }

    [Fact]
    public async Task LootThatIsAlreadyOpen_KeepsWhatItRolled()
    {
        // vmangos rolls when the chest is filled (Loot::FillLoot), not when the window opens or the item is taken.
        (WorldTestHost host, GameObjectTestContext context) = StartWithChest();
        await using WorldTestHost _ = host;
        await using WorldTestClient client = await host.EnterWorldAsync("RLOPEN", "Rlopen");
        await GameObjectWorldTests.ReadUntilGameObjectCreateAsync(client, ChestGuid());
        Assert.Equal((LinenCloth, 2u), await OpenChestAsync(client));
        context.Loot = new LootContent([(LootTableKind.GameObject, new LootStoreRow(ChestLootId, WoolCloth, 100f, 0, 3, 3))], []);

        ReloadResult result = await Coordinator(host).ReloadAsync("gameobject_loot_template");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        await client.SendAsync(WorldOpcode.CmsgAutostoreLootItem, [0]);
        await client.ReadUntilAsync(WorldOpcode.SmsgLootRemoved);
        await host.WaitForWorldAsync(
            () => host.World.FindOnlinePlayer("Rlopen")!.Inventory.AllItems.Any(i => i.Entry == LinenCloth && i.Count == 2),
            "the rolled cloth reaches the bags");
        Assert.DoesNotContain(await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Rlopen")!.Inventory.AllItems.Select(i => i.Entry).ToArray()), e => e == WoolCloth);
    }

}
