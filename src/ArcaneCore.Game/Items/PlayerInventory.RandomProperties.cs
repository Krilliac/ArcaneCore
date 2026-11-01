using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Items;

public sealed partial class PlayerInventory
{
    /// <summary>
    /// The random property content new items roll from (<see cref="ItemRandomProperties"/>; the items feature sets it at login). Null: new items
    /// get no random property, which is what an install without ItemRandomProperties.dbc gets.
    /// </summary>
    public IItemRandomPropertySource? RandomProperties { get; set; }

    /// <summary>
    /// <see cref="AddItem"/> for a loot item (vmangos LootHandler.cpp:191 StoreNewItem(dest, itemid, true, item->randomPropertyId)): the new item keeps
    /// <paramref name="randomPropertyId"/> from the loot roll instead of rolling again; 0 means the loot carries none and the item rolls as before.
    /// All or nothing; on failure nothing changes and the caller reports the result. Added to the bags with the standard push result.
    /// </summary>
    public InventoryResult AddLootItem(uint entry, uint count, int randomPropertyId, out Item? item)
    {
        item = null;
        if (Player is { CanMutateQuestSettlementState: false })
        {
            return InventoryResult.CantDoRightNow;
        }

        var dest = new List<ItemPosCount>();
        InventoryResult result = CanStoreNewItem(entry, count, dest, out _);
        if (result != InventoryResult.Ok)
        {
            return result;
        }

        item = StoreNewItem(dest, Templates.Find(entry)!, count, randomPropertyId);
        if (Player is { IsInWorld: true } player)
        {
            player.Session.Send(WorldOpcode.SmsgItemPushResult, ItemPackets.ItemPushResult(player.Guid, item, count, received: false, created: false, showInChat: true));
        }

        return InventoryResult.Ok;
    }

    /// <summary>
    /// vmangos StoreNewItem(…, randomPropertyId) with Item::GenerateItemRandomPropertyId at its callers: a new item takes
    /// <paramref name="randomPropertyId"/> when one is given (a loot roll already made, a copy), otherwise a fresh roll; 0 means none.
    /// </summary>
    private void ApplyNewItemRandomProperty(Item item, int? randomPropertyId)
    {
        int id = randomPropertyId ?? RandomProperties?.Generate(item.Template) ?? 0;
        if (id != 0 && RandomProperties?.Find(id) is { } property)
        {
            ItemRandomProperties.Apply(item, property);
        }
    }
}
