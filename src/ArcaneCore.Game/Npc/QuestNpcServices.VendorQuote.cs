using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Npc;

namespace ArcaneCore.Game.Npc;

public sealed partial class QuestNpcServices
{
    /// <summary>
    /// Advisory price for one currently purchasable vendor bundle. This does not require
    /// interaction distance, reserve stock, store items or change money. Ordinary BuyItem
    /// rechecks every condition when the player reaches the NPC. Limited stock uses the
    /// same lazy restock calculation as the ordinary vendor list.
    /// </summary>
    public uint? GetVendorPurchasePrice(Player player, NpcInfo npc, uint itemId)
    {
        if (Ready(player) is null || !player.IsAlive || npc.IsGameObject || !npc.IsAlive
            || npc.IsHostile || npc.IsInCombat || npc.IsNotSelectable
            || (npc.NpcFlags & NpcFlags.Vendor) == 0 || player.Map?.MapId != npc.MapId
            || Deps.Items is not { } items || items.GetItem(itemId) is not { } proto)
            return null;

        VendorItem? row = VisibleVendorRow(player, npc, itemId);
        if (row is null
            || proto.BuyCount == 0 || proto.RequiredHonorRank != 0
            || (row.MaxCount != 0 && CurrentStock(npc.Guid, row, proto) < proto.BuyCount))
            return null;

        if (!MeetsVendorReputation(player, npc, proto))
        {
            return null;
        }

        uint price = Discounted(proto.BuyPrice, PriceDiscount(player, npc));
        if (player.Money < price || items.CanStoreNewItemAt(player, itemId, proto.BuyCount,
            Items.InventorySlots.NullBag, Items.InventorySlots.NullSlot) != Items.InventoryResult.Ok)
            return null;

        return price;
    }
}
