using ArcaneCore.Game;
using ArcaneCore.Game.Conditions;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.GameObjects;

/// <summary>
/// Lane L9 end to end: the <see cref="ConditionFeature"/> decides conditioned loot rows through <c>LootService.Conditions</c>
/// (wired by <see cref="GameObjectLootFeature"/>), and a chest pays its template's <c>mingold..maxgold</c>, taken with
/// CMSG_LOOT_MONEY. Two chests stand next to the human start; the second is opened after the character's level changed.
/// </summary>
public sealed class LootConditionsWorldTests
{
    private const uint ChestEntry = 2850;
    private const uint SecondChestEntry = 2851;
    private const uint FirstSpawn = 77010;
    private const uint SecondSpawn = 77011;
    private const uint ChestLootId = 2850;
    private const uint LinenCloth = 2589;
    private const uint WoolCloth = 2592;
    private const uint SilkCloth = 4306;
    private const uint LevelFive = 40;        // conditions row: level >= 5
    private const uint UnknownCondition = 41; // no row: fails closed
    private const uint MinGold = 30;
    private const uint MaxGold = 40;

    private sealed class LevelConditionStore : IConditionContentStore
    {
        public Task<IReadOnlyList<ConditionRecord>> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ConditionRecord>>([new(LevelFive, (int)ConditionType.Level, 5, 1, 0, 0, 0)]);
    }

    [Fact]
    public async Task ConditionedRows_FollowTheConditionsFeature_AndTheChestPaysItsTemplateGold()
    {
        await using WorldTestHost host = StartWithChests(out GameObjectTestContext context);
        await using WorldTestClient client = await host.EnterWorldAsync("GOWCOND", "Condlooter");
        ulong first = ObjectGuid.WithEntry(HighGuid.GameObject, ChestEntry, FirstSpawn).Value;
        ulong second = ObjectGuid.WithEntry(HighGuid.GameObject, SecondChestEntry, SecondSpawn).Value;
        await GameObjectWorldTests.ReadUntilGameObjectCreateAsync(client, first);

        // The feature wired the seam to the conditions feature (null would skip every conditioned row).
        GameObjectLootFeature feature = context.Feature!;
        Assert.True(await host.OnWorldAsync(() => feature.FindSystem(0)!.Loot!.Conditions is not null));

        // Level 1: the level >= 5 row and the row with an unknown condition are both dropped; the plain row and the gold stay.
        await client.SendAsync(WorldOpcode.CmsgGameobjUse, BitConverter.GetBytes(first));
        (uint gold, uint[] items) = ParseLoot(await client.ReadUntilAsync(WorldOpcode.SmsgLootResponse), first);
        Assert.InRange(gold, MinGold, MaxGold);
        Assert.Equal([LinenCloth], items);

        uint before = await host.PlayerStateAsync("Condlooter", p => p.Money);
        await client.SendAsync(WorldOpcode.CmsgLootMoney, []);
        Assert.Empty(await client.ReadUntilAsync(WorldOpcode.SmsgLootClearMoney));
        Assert.Equal(before + gold, await host.PlayerStateAsync("Condlooter", p => p.Money));
        await client.SendAsync(WorldOpcode.CmsgLootRelease, BitConverter.GetBytes(first));
        await client.ReadUntilAsync(WorldOpcode.SmsgLootReleaseResponse);

        // Level 5: the conditioned wool drops too; the unknown condition still fails closed.
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Condlooter")!.Level = 5);
        await client.SendAsync(WorldOpcode.CmsgGameobjUse, BitConverter.GetBytes(second));
        (uint secondGold, uint[] secondItems) = ParseLoot(await client.ReadUntilAsync(WorldOpcode.SmsgLootResponse), second);
        Assert.InRange(secondGold, MinGold, MaxGold);
        Assert.Equal([LinenCloth, WoolCloth], secondItems.Order());
    }

    /// <summary>SMSG_LOOT_RESPONSE: guid, loot type, gold, item count, then per item slot, entry, count, display, 8 bytes, slot type.</summary>
    private static (uint Gold, uint[] Items) ParseLoot(byte[] payload, ulong expectedGuid)
    {
        var reader = new PacketReader(payload);
        Assert.Equal(expectedGuid, reader.ReadUInt64());
        Assert.Equal((byte)1, reader.ReadByte()); // LOOT_CORPSE
        uint gold = reader.ReadUInt32();
        byte count = reader.ReadByte();
        var items = new uint[count];
        for (int i = 0; i < count; i++)
        {
            reader.ReadByte();                 // slot
            items[i] = reader.ReadUInt32();
            reader.ReadUInt32();               // count
            reader.ReadUInt32();               // display id
            reader.Skip(8);
            reader.ReadByte();                 // slot type
        }

        Assert.Equal(0, reader.Remaining);
        return (gold, items);
    }

    private static WorldTestHost StartWithChests(out GameObjectTestContext context)
    {
        uint[] data = new uint[GameObjectTemplate.DataCount];
        data[1] = ChestLootId;
        var chest = new GameObjectTemplate { Entry = ChestEntry, Type = (uint)GameObjectType.Chest, DisplayId = 10, Name = "Gilded Chest", Data = data, MinGold = MinGold, MaxGold = MaxGold };
        GameObjectTemplate second = chest with { Entry = SecondChestEntry };

        // Human start is (-8949.95, -132.49, 83.53): both chests are about 2 yd away.
        var spawns = new GameObjectSpawn[]
        {
            new() { Guid = FirstSpawn, Entry = ChestEntry, MapId = 0, X = -8948f, Y = -132.5f, Z = 83.5f },
            new() { Guid = SecondSpawn, Entry = SecondChestEntry, MapId = 0, X = -8952f, Y = -132.5f, Z = 83.5f },
        };
        var goContent = new GameObjectContent([chest, second], spawns, [], [], []);
        var lootContent = new LootContent(
        [
            (LootTableKind.GameObject, new LootStoreRow(ChestLootId, LinenCloth, 100f, 0, 1, 1)),
            (LootTableKind.GameObject, new LootStoreRow(ChestLootId, WoolCloth, 100f, 0, 1, 1, LevelFive)),
            (LootTableKind.GameObject, new LootStoreRow(ChestLootId, SilkCloth, 100f, 0, 1, 1, UnknownCondition)),
        ], []);
        context = new GameObjectTestContext(goContent, lootContent);

        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = LinenCloth, Class = 7, Name = "Linen Cloth", DisplayId = 3776, Quality = 1, Stackable = 20 });
        items.Templates.Templates.Add(new ItemTemplate { Entry = WoolCloth, Class = 7, Name = "Wool Cloth", DisplayId = 6238, Quality = 1, Stackable = 20 });
        items.Templates.Templates.Add(new ItemTemplate { Entry = SilkCloth, Class = 7, Name = "Silk Cloth", DisplayId = 6239, Quality = 1, Stackable = 20 });
        GameObjectTestStore.Current.Value = context;
        try
        {
            using (items.Use())
            {
                return WorldTestHost.Start(configureServices: s => s.AddSingleton<IConditionContentStore, LevelConditionStore>());
            }
        }
        finally
        {
            GameObjectTestStore.Current.Value = null;
        }
    }
}
