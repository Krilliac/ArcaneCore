using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Honor;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Tests.Npc;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Honor;

/// <summary>
/// Rank items at vendors (vmangos Player::BuyItemFromVendor, Player.cpp:18431-18439): the CURRENT rank and the item's required level
/// must be met; the honor discount is added to the Honored one (Player.cpp:19470-19510). Equipping uses the highest rank instead.
/// </summary>
public sealed class VendorHonorGateTests
{
    private const uint RankHelm = 92001;   // rank 6, required level 55
    private const uint PlainHelm = 92002;  // no rank requirement
    private const uint VendorEntry = NpcServiceKit.Entry;

    private static readonly ItemTemplateStore Store = new(
    [
        new ItemTemplate { Entry = RankHelm, Class = 4, SubClass = 1, Name = "Rank Helm", DisplayId = 20, Quality = 3, InventoryType = 1, BuyPrice = 1000, Armor = 10, RequiredHonorRank = 6, RequiredLevel = 55, BuyCount = 1 },
        new ItemTemplate { Entry = PlainHelm, Class = 4, SubClass = 1, Name = "Plain Helm", DisplayId = 21, Quality = 1, InventoryType = 1, BuyPrice = 1000, Armor = 10, BuyCount = 1 },
    ], []);

    private static NpcContent Vendor(params uint[] items)
        => NpcContent.Empty with { VendorItems = [.. items.Select((item, i) => new VendorItem { Entry = VendorEntry, Item = item, Slot = (uint)i + 1 })] };

    private static NpcServiceKit Kit(IPlayerHonor? honor, byte level = 60, uint faction = 0, params uint[] items)
    {
        var itemService = new InventoryItemService(() => Store, RepairCostTable.Empty, BankBagSlotPriceTable.Empty, () => 1_000);
        var kit = new NpcServiceKit(NpcFlags.Vendor, Vendor(items.Length == 0 ? [RankHelm, PlainHelm] : items),
            extra: new QuestNpcDependencies { Items = itemService, Honor = honor });
        kit.Player.Inventory.Templates = Store;
        itemService.SessionStarted(kit.Player);
        kit.Player.Level = level;
        kit.Player.Money = 10_000;
        kit.Npc = kit.Npc with { FactionId = faction };
        kit.Drain();
        return kit;
    }

    private static byte BuyError(NpcServiceKit kit) => kit.Single(WorldOpcode.SmsgBuyFailed)[12];

    [Fact]
    public void A_rank_below_the_requirement_is_refused_with_rank_required()
    {
        using NpcServiceKit kit = Kit(new FakeHonor(current: 5, highest: 5));
        kit.Services.BuyItem(kit.Player, kit.Npc.Guid, RankHelm, 1);
        Assert.Equal((byte)BuyResult.RankRequire, BuyError(kit));
        Assert.Equal(10_000u, kit.Player.Money);
        Assert.Equal(0u, kit.Player.Inventory.GetItemCount(RankHelm));
    }

    [Fact]
    public void The_required_rank_with_too_low_a_level_is_refused()
    {
        using NpcServiceKit kit = Kit(new FakeHonor(current: 6, highest: 6), level: 54);
        kit.Services.BuyItem(kit.Player, kit.Npc.Guid, RankHelm, 1);
        Assert.Equal((byte)BuyResult.RankRequire, BuyError(kit));
    }

    [Fact]
    public void The_required_rank_and_level_buy_the_item_and_pay_for_it()
    {
        using NpcServiceKit kit = Kit(new FakeHonor(current: 6, highest: 6), level: 55);
        kit.Services.BuyItem(kit.Player, kit.Npc.Guid, RankHelm, 1);
        Assert.Equal(9_000u, kit.Player.Money);
        Assert.Equal(1u, kit.Player.Inventory.GetItemCount(RankHelm));
    }

    [Fact]
    public void Purchase_uses_the_current_rank_not_the_highest_one()
    {
        using NpcServiceKit kit = Kit(new FakeHonor(current: 5, highest: 9)); // equipping would use the 9
        kit.Services.BuyItem(kit.Player, kit.Npc.Guid, RankHelm, 1);
        Assert.Equal((byte)BuyResult.RankRequire, BuyError(kit));
    }

    [Fact]
    public void Without_an_honor_owner_rank_items_still_fail_closed_and_plain_items_sell()
    {
        using NpcServiceKit kit = Kit(honor: null);
        kit.Services.BuyItem(kit.Player, kit.Npc.Guid, RankHelm, 1);
        Assert.Equal((byte)BuyResult.RankRequire, BuyError(kit));

        kit.Services.BuyItem(kit.Player, kit.Npc.Guid, PlainHelm, 1);
        Assert.Equal(1u, kit.Player.Inventory.GetItemCount(PlainHelm));
    }

    [Fact]
    public void An_item_without_a_rank_never_consults_honor()
    {
        using NpcServiceKit kit = Kit(new ThrowingHonor());
        kit.Services.BuyItem(kit.Player, kit.Npc.Guid, PlainHelm, 1);
        Assert.Equal(1u, kit.Player.Inventory.GetItemCount(PlainHelm));
    }

    [Fact]
    public void A_capital_vendor_takes_ten_percent_off_at_visual_rank_3_in_the_list_and_the_purchase()
    {
        using NpcServiceKit kit = Kit(new FakeHonor(current: 7, highest: 7, visual: 3), faction: 76, items: PlainHelm);
        kit.Services.ListInventory(kit.Player, kit.Npc.Guid);
        var list = new PacketReader(kit.Single(WorldOpcode.SmsgListInventory));
        list.ReadUInt64();
        list.ReadByte();
        list.ReadUInt32(); // slot
        list.ReadUInt32(); // item
        list.ReadUInt32(); // display
        list.ReadUInt32(); // stock
        Assert.Equal(900u, list.ReadUInt32());

        kit.Services.BuyItem(kit.Player, kit.Npc.Guid, PlainHelm, 1);
        Assert.Equal(9_100u, kit.Player.Money);
    }

    [Fact]
    public void A_vendor_of_another_faction_or_a_lower_rank_gets_no_honor_discount()
    {
        using NpcServiceKit other = Kit(new FakeHonor(7, 7, visual: 3), faction: 529, items: PlainHelm);
        other.Services.BuyItem(other.Player, other.Npc.Guid, PlainHelm, 1);
        Assert.Equal(9_000u, other.Player.Money);

        using NpcServiceKit low = Kit(new FakeHonor(6, 6, visual: 2), faction: 76, items: PlainHelm);
        low.Services.BuyItem(low.Player, low.Npc.Guid, PlainHelm, 1);
        Assert.Equal(9_000u, low.Player.Money);
    }

    [Fact]
    public void Honor_adds_to_the_honored_discount_instead_of_multiplying()
    {
        var itemService = new InventoryItemService(() => Store, RepairCostTable.Empty, BankBagSlotPriceTable.Empty, () => 1_000);
        using var kit = new NpcServiceKit(NpcFlags.Vendor, Vendor(PlainHelm),
            extra: new QuestNpcDependencies
            {
                Items = itemService,
                Honor = new FakeHonor(7, 7, visual: 3),
                Reputation = new NpcVendorServiceTests.FixedReputation(0.9f), // Honored
            });
        kit.Player.Inventory.Templates = Store;
        itemService.SessionStarted(kit.Player);
        kit.Player.Money = 10_000;
        kit.Npc = kit.Npc with { FactionId = 76 };
        kit.Services.BuyItem(kit.Player, kit.Npc.Guid, PlainHelm, 1);
        // 1000 * (1 - 0.1 - 0.1) = 800, not the 810 a multiplicative stack would charge. The 799 vs 800 is the existing vendor
        // rounding (the NPC services floor, vmangos Player.cpp:18443 adds 0.5), which is not this lane's to change.
        Assert.InRange(10_000u - kit.Player.Money, 799u, 800u);
    }

    private sealed class FakeHonor(byte current, byte highest, sbyte visual = 0) : IPlayerHonor
    {
        public byte CurrentRank(Player player) => current;

        public byte HighestRank(Player player) => highest;

        public sbyte VisualRank(Player player) => visual;
    }

    private sealed class ThrowingHonor : IPlayerHonor
    {
        public byte CurrentRank(Player player) => throw new InvalidOperationException("honor consulted for an unranked item");

        public byte HighestRank(Player player) => throw new InvalidOperationException("honor consulted for an unranked item");

        public sbyte VisualRank(Player player) => 0;
    }
}
