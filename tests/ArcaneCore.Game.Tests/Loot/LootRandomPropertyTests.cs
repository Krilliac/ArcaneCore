using ArcaneCore.Game.Crafting.Enchanting;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Loot;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Loot;
using Xunit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;

namespace ArcaneCore.Game.Tests.Loot;

/// <summary>
/// The random property ("of the Bear") of a loot item is rolled once when the loot is generated (vmangos LootMgr.cpp:343-344,
/// Item::GenerateItemRandomPropertyId), shown in SMSG_LOOT_RESPONSE and the roll packets (Server/Packets/Loot.cpp:55-140, 165-190) and kept by the
/// item that reaches the bags (LootHandler.cpp:191 StoreNewItem(..., item->randomPropertyId)).
/// </summary>
public sealed class LootRandomPropertyTests
{
    private const uint Sword = 94201;
    private const uint PropertyX = 1001;
    private const uint PropertyY = 1002;
    private const uint CorpseEntry = 4401;

    private static readonly ObjectGuid Corpse = ObjectGuid.Player(900);

    private static readonly ItemTemplate SwordTemplate = new()
    {
        Entry = Sword, Class = 2, SubClass = 7, Name = "Loot Sword", DisplayId = 7, InventoryType = 13, Stackable = 1, Quality = 2, RandomProperty = 5,
    };

    private static readonly ItemTemplateStore Store = new([.. ItemTestData.Templates, SwordTemplate], ItemTestData.StartingItems);

    private static uint[] GuidWords(ObjectGuid guid) => [(uint)guid.Value, (uint)(guid.Value >> 32)];

    private static byte[] Words(params uint[] values)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        foreach (uint value in values)
        {
            writer.Write(value);
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    private static LootBag BagWith(int randomPropertyId)
    {
        var bag = new LootBag(Corpse, LootSourceKind.Creature, LootType.Corpse);
        bag.Add(new LootItem(0, Sword, 1, false, false, 7) { RandomPropertyId = randomPropertyId });
        return bag;
    }

    /// <summary>Indexes where two equally long packets differ.</summary>
    private static int[] Diff(byte[] a, byte[] b)
    {
        Assert.Equal(a.Length, b.Length);
        return [.. Enumerable.Range(0, a.Length).Where(i => a[i] != b[i])];
    }

    [Fact]
    public void LootResponse_GoldenBytes_CarryThePropertyInTheRandomPropertyWord_AndNothingElseChanges()
    {
        (Player viewer, _) = Player(7301);
        byte[] without = LootPackets.LootResponse(BagWith(0), viewer);
        byte[] with = LootPackets.LootResponse(BagWith((int)PropertyX), viewer);

        // guid, type 1, gold 0, count 1, then slot 0, item, count 1, display 7, suffix 0, property, slot type 0 (AllowLoot).
        byte[] Expected(uint property) => Concat(
            Words(GuidWords(Corpse)),
            [1],
            Words(0),
            [1, 0],
            Words(Sword, 1, 7, 0, property),
            [0]);
        Assert.Equal(Expected(PropertyX), with);
        Assert.Equal(Expected(0), without);

        // The property word starts after guid 8 + type 1 + gold 4 + count 1 + slot 1 + item 4 + count 4 + display 4 + suffix 4 = 31;
        // 1001 = 0x03E9, so only its two low bytes differ from the property-less packet.
        Assert.Equal(new[] { 31, 32 }, Diff(without, with));
        Assert.Equal(PropertyX, BitConverter.ToUInt32(with, 31));
    }

    [Fact]
    public void StartRoll_GoldenBytes_CarryTheProperty_AndOnlyThatWordDiffers()
    {
        byte[] without = GroupLootPackets.StartRoll(Corpse, 3, Sword, 60000);
        byte[] with = GroupLootPackets.StartRoll(Corpse, 3, Sword, 60000, (int)PropertyX);

        uint[] corpse = GuidWords(Corpse);
        Assert.Equal(Words(corpse[0], corpse[1], 3, Sword, 0, PropertyX, 60000), with);
        Assert.Equal(28, with.Length);
        Assert.Equal(new[] { 20, 21 }, Diff(without, with)); // corpse 8 + slot 4 + item 4 + suffix 4 = 20; 0x03E9 low two bytes
    }

    [Fact]
    public void VoteWonAndAllPassedPackets_CarryTheProperty_InTheirOwnFieldOrder()
    {
        ObjectGuid roller = ObjectGuid.Player(7302);
        uint[] corpse = GuidWords(Corpse);
        uint[] who = GuidWords(roller);

        // SMSG_LOOT_ROLL: corpse, slot, roller, item, suffix 0, property, roll number, vote (need = 0, 0).
        Assert.Equal(
            Concat(Words(corpse[0], corpse[1], 3, who[0], who[1], Sword, 0, PropertyX), [0, 0]),
            GroupLootPackets.VoteAnnouncement(Corpse, 3, roller, Sword, RollVote.Need, (int)PropertyX));

        // SMSG_LOOT_ROLL_WON: corpse, slot, item, suffix 0, property, winner, roll, vote.
        Assert.Equal(
            Concat(Words(corpse[0], corpse[1], 3, Sword, 0, PropertyX, who[0], who[1]), [77, (byte)RollVote.Greed]),
            GroupLootPackets.RollWon(Corpse, 3, Sword, roller, 77, RollVote.Greed, (int)PropertyX));

        // SMSG_LOOT_ALL_PASSED: corpse, slot, item, property, suffix 0 (the order differs from the other three).
        Assert.Equal(
            Words(corpse[0], corpse[1], 3, Sword, PropertyX, 0),
            GroupLootPackets.AllPassed(Corpse, 3, Sword, (int)PropertyX));
    }

    [Fact]
    public void RollPackets_DefaultToNoProperty()
    {
        Assert.Equal(0, BitConverter.ToInt32(GroupLootPackets.StartRoll(Corpse, 1, Sword, 1000), 20));
        Assert.Equal(0, BitConverter.ToInt32(GroupLootPackets.AllPassed(Corpse, 1, Sword), 16));
    }

    /// <summary>
    /// A service whose property roll yields the given ids in turn, then Y: a second roll at store time would show as a different id. The player's
    /// inventory only knows the catalog (so the property can be applied), it has no roll of its own that matters.
    /// </summary>
    private static (LootService Loot, Player Player) Rig(params uint[] rolled)
    {
        var catalog = new ItemRandomPropertyCatalog(
            [new ItemRandomPropertyRecord(PropertyX, "of the Bear", [74, 75, 0]), new ItemRandomPropertyRecord(PropertyY, "of the Eagle", [76, 0, 0])],
            [new ItemEnchantmentChance(5, PropertyX, 50f), new ItemEnchantmentChance(5, PropertyY, 50f)]);
        var sequence = new Queue<float>(rolled.Select(id => id == PropertyX ? 10f : 90f));
        var properties = new ItemRandomProperties(catalog, () => sequence.Count > 0 ? sequence.Dequeue() : 90f);
        var content = new LootContent([(LootTableKind.Creature, Row(CorpseEntry, Sword, 100))], []);
        (Player player, _) = Player(7303);
        player.Inventory.Templates = Store;
        player.Inventory.RandomProperties = properties;
        var loot = new LootService(content, random: new Random(3)) { Items = Store };
        return (loot, player);
    }

    [Fact]
    public void LootWindowShowsPropertyX_AndTheLootedItemHasX_WithoutARerollAtStore()
    {
        // The service takes its source from the recipient's inventory. The sequence gives X to the loot item; a re-roll at store time would get Y.
        (LootService loot, Player player) = Rig(PropertyX);
        LootBag bag = loot.Generate(Corpse, LootSourceKind.Creature, LootType.Corpse, LootTableKind.Creature, CorpseEntry, [player]);
        LootItem item = Assert.Single(bag.Items);
        Assert.Equal((int)PropertyX, item.RandomPropertyId);

        byte[] window = LootPackets.LootResponse(bag, player);
        Assert.Equal(PropertyX, BitConverter.ToUInt32(window, 31));

        Assert.Equal(InventoryResult.Ok, loot.AwardItem(player, bag, item));
        Item looted = Assert.Single(player.Inventory.AllItems, i => i.Entry == Sword);
        Assert.Equal((int)PropertyX, looted.RandomPropertyId);
        Assert.Equal(74u, ItemEnchantments.Id(looted, EnchantSlots.Property0));
    }

    [Fact]
    public void LootedItem_KeepsTheLootRoll_NotAFreshOne()
    {
        (LootService loot, Player player) = Rig(PropertyY);
        LootBag bag = loot.Generate(Corpse, LootSourceKind.Creature, LootType.Corpse, LootTableKind.Creature, CorpseEntry, [player]);
        LootItem item = Assert.Single(bag.Items);
        Assert.Equal((int)PropertyY, item.RandomPropertyId);

        // Swap the inventory's roll for one that always yields X: the item must still be Y because the loot already rolled it.
        var catalog = new ItemRandomPropertyCatalog(
            [new ItemRandomPropertyRecord(PropertyX, "of the Bear", [74, 75, 0]), new ItemRandomPropertyRecord(PropertyY, "of the Eagle", [76, 0, 0])],
            [new ItemEnchantmentChance(5, PropertyX, 100f)]);
        player.Inventory.RandomProperties = new ItemRandomProperties(catalog, () => 10f);

        Assert.Equal(InventoryResult.Ok, loot.AwardItem(player, bag, item));
        Item looted = Assert.Single(player.Inventory.AllItems, i => i.Entry == Sword);
        Assert.Equal((int)PropertyY, looted.RandomPropertyId);
        Assert.Equal(76u, ItemEnchantments.Id(looted, EnchantSlots.Property0));
    }

    [Fact]
    public void ItemWhoseTemplateHasNoRandomProperty_ShowsNone()
    {
        (LootService _, Player player) = Rig();
        var content = new LootContent([(LootTableKind.Creature, Row(CorpseEntry, ItemTestData.ToughJerky, 100))], []);
        var plain = new LootService(content, random: new Random(3)) { Items = Store };
        LootBag bag = plain.Generate(Corpse, LootSourceKind.Creature, LootType.Corpse, LootTableKind.Creature, CorpseEntry, [player]);
        Assert.Equal(0, Assert.Single(bag.Items).RandomPropertyId);
    }

    [Fact]
    public void DurableChestRecord_RoundTripsTheProperty()
    {
        var bag = new LootBag(Corpse, LootSourceKind.GameObject, LootType.Corpse);
        bag.Add(new LootItem(0, Sword, 1, false, false, 7) { RandomPropertyId = (int)PropertyX });
        bag.Add(new LootItem(1, Sword, 1, false, false, 7) { RandomPropertyId = -(int)PropertyY });

        ArcaneCore.Kernel.Loot.LootStateRecord record = bag.ToRecord(new ArcaneCore.Kernel.Loot.LootStateKey(5, 6), 3000, 1, 0);
        Assert.Equal([(int)PropertyX, -(int)PropertyY], record.Items.OrderBy(i => i.Slot).Select(i => i.RandomPropertyId));

        LootBag restored = LootBag.FromRecord(Corpse, LootType.Corpse, record, _ => 7u)!;
        Assert.Equal([(int)PropertyX, -(int)PropertyY], restored.Items.OrderBy(i => i.Slot).Select(i => i.RandomPropertyId));
    }

    [Fact]
    public void DurableChestGeneration_RollsTheProperty_LikeAnyOtherBag()
    {
        (LootService loot, Player player) = Rig(PropertyY);
        LootBag bag = loot.Generate(Corpse, LootSourceKind.GameObject, LootType.Corpse, LootTableKind.Creature, CorpseEntry, [player]);
        Assert.Equal((int)PropertyY, Assert.Single(bag.Items).RandomPropertyId);
        Assert.Equal((int)PropertyY, Assert.Single(bag.ToRecord(new ArcaneCore.Kernel.Loot.LootStateKey(5, 6), 3000, 1, 0).Items).RandomPropertyId);
    }
}
