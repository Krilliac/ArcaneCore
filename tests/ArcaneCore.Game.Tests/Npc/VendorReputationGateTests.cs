using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Npc.NpcServiceKit;

namespace ArcaneCore.Game.Tests.Npc;

/// <summary>
/// Pins the vendor reputation gate of the three purchase paths (BuyItem, GetVendorPurchasePrice, QuoteVendorPrice): the
/// required faction is the item's own, or the vendor's when the item names a rank but no faction; the player's rank must reach
/// the required rank (equal passes); no reputation owner fails closed. Only BuyItem answers with a packet.
/// </summary>
public sealed class VendorReputationGateTests
{
    private const uint ItemFaction = 529;
    private const uint VendorFaction = 70;

    private sealed class RecordingReputation(Func<uint, byte> rank) : IPlayerReputation
    {
        public List<uint> Queried { get; } = [];

        public int GetReputation(Player player, uint factionId) => 0;

        public byte GetReputationRank(Player player, uint factionId)
        {
            Queried.Add(factionId);
            return rank(factionId);
        }

        public float GetPriceDiscount(Player player, NpcInfo npc) => 1f;
    }

    private static NpcServiceKit Kit(uint itemFaction, uint itemRank, IPlayerReputation? reputation)
    {
        ItemTemplate bread = Templates[0] with { RequiredReputationFaction = itemFaction, RequiredReputationRank = itemRank };
        var store = new ItemTemplateStore([bread, .. Templates.Skip(1)], []);
        var content = NpcContent.Empty with { VendorItems = [new VendorItem { Entry = Entry, Item = Bread }] };
        QuestNpcDependencies? extra = reputation is null ? null : new QuestNpcDependencies(Reputation: reputation);
        var kit = new NpcServiceKit(NpcFlags.Vendor, content, extra, itemStore: store);
        kit.Npc = kit.Npc with { FactionId = VendorFaction };
        kit.Player.Money = 1000;
        return kit;
    }

    // The vendor faction is queried by the NPC interaction gate (refuses at Unfriendly or worse), never by the item gate here.
    private static void AssertQueriedItemFactionAndInteractionGateOnly(RecordingReputation reputation)
    {
        Assert.Contains(ItemFaction, reputation.Queried);
        Assert.All(reputation.Queried, faction => Assert.True(faction == ItemFaction || faction == VendorFaction));
    }

    private static void AssertRefusedEverywhere(NpcServiceKit kit, BuyResult expectedBuy)
    {
        Assert.Null(kit.Services.GetVendorPurchasePrice(kit.Player, kit.Npc, Bread));
        Assert.Null(kit.Services.QuoteVendorPrice(kit.Player, kit.Npc, Bread));
        Assert.Empty(kit.Drain());
        kit.Services.BuyItem(kit.Player, kit.Npc.Guid, Bread, 1);
        Assert.Equal((byte)expectedBuy, kit.Single(WorldOpcode.SmsgBuyFailed)[12]);
        Assert.Equal(1000u, kit.Player.Money);
        Assert.Equal(0u, kit.Player.Inventory.GetItemCount(Bread));
    }

    private static void AssertSoldEverywhere(NpcServiceKit kit)
    {
        Assert.Equal(25u, kit.Services.GetVendorPurchasePrice(kit.Player, kit.Npc, Bread));
        Assert.Equal(25u, kit.Services.QuoteVendorPrice(kit.Player, kit.Npc, Bread));
        Assert.Empty(kit.Drain());
        kit.Services.BuyItem(kit.Player, kit.Npc.Guid, Bread, 1);
        Assert.Equal(975u, kit.Player.Money);
        Assert.Equal(5u, kit.Player.Inventory.GetItemCount(Bread));
    }

    [Fact]
    public void ItemFaction_RankBelowRequired_RefusesAllThreePaths_BuyReportsReputationRequire()
    {
        var reputation = new RecordingReputation(faction => faction == ItemFaction ? (byte)4 : (byte)7);
        using NpcServiceKit kit = Kit(ItemFaction, 5, reputation);
        AssertRefusedEverywhere(kit, BuyResult.ReputationRequire);
        AssertQueriedItemFactionAndInteractionGateOnly(reputation);
    }

    [Fact]
    public void ItemFaction_RankEqualToRequired_PassesAllThreePaths()
    {
        var reputation = new RecordingReputation(faction => faction == ItemFaction ? (byte)5 : (byte)4);
        using NpcServiceKit kit = Kit(ItemFaction, 5, reputation);
        AssertSoldEverywhere(kit);
        AssertQueriedItemFactionAndInteractionGateOnly(reputation);
    }

    [Fact]
    public void ItemWithoutFaction_UsesVendorFaction_RankMetPassesAllThreePaths()
    {
        var reputation = new RecordingReputation(faction => faction == VendorFaction ? (byte)5 : (byte)0);
        using NpcServiceKit kit = Kit(0, 5, reputation);
        AssertSoldEverywhere(kit);
        Assert.NotEmpty(reputation.Queried);
        Assert.All(reputation.Queried, faction => Assert.Equal(VendorFaction, faction));
    }

    [Fact]
    public void ItemWithoutFaction_VendorFactionRankTooLow_RowIsHiddenSoBuyReportsCantFindItem()
    {
        var reputation = new RecordingReputation(faction => faction == VendorFaction ? (byte)4 : (byte)7);
        using NpcServiceKit kit = Kit(0, 5, reputation);
        AssertRefusedEverywhere(kit, BuyResult.CantFindItem);
    }

    [Fact]
    public void RankZero_IsNotGated_EvenWithAFactionAndNoReputationOwner()
    {
        using NpcServiceKit kit = Kit(ItemFaction, 0, reputation: null);
        AssertSoldEverywhere(kit);
    }

    [Fact]
    public void NoReputationOwner_FailsClosedOnAllThreePaths()
    {
        using NpcServiceKit kit = Kit(ItemFaction, 5, reputation: null);
        AssertRefusedEverywhere(kit, BuyResult.ReputationRequire);
    }
}
