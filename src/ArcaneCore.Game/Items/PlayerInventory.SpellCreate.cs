using ArcaneCore.Kernel.Items;

namespace ArcaneCore.Game.Items;

public sealed partial class PlayerInventory
{
    /// <summary>
    /// vmangos ItemPrototype::HasSignature (ItemPrototype.h:535): an unstackable item that is not a
    /// consumable or quest item, lacks ITEM_FLAG_NO_CREATOR and is not the Hearthstone carries its
    /// crafter ("Crafted by ...").
    /// </summary>
    public static bool HasSignature(ItemTemplate template)
        => template.MaxStackSize() == 1
            && (ItemClass)template.Class is not (ItemClass.Consumable or ItemClass.Quest)
            && !template.HasFlag(ItemTemplateFlags.NoCreator)
            && template.Entry != 6948;

    /// <summary>
    /// The inventory half of the spell effect CreateItem (vmangos Spell::DoCreateItem,
    /// SpellEffects.cpp:1885-1990): <paramref name="amount"/> (the effect's base points, at least 1) is
    /// clamped to the item's stack size; when the bags cannot take it all, what fits is stored (the
    /// no-space remainder is subtracted when the reason is a full inventory or a unique-count
    /// limit) and any other reason is reported to the client; the new item gets the crafter's GUID
    /// when it carries a signature; SMSG_ITEM_PUSH_RESULT is sent with received and created set.
    /// <para>
    /// Returns the store result of the whole amount (<see cref="InventoryResult.Ok"/>, or the
    /// full-inventory / unique-limit result of a partial store, or the reported error);
    /// <paramref name="created"/> is how many items were actually added (0 when nothing fitted,
    /// which vmangos does silently). Random properties are not rolled here: that mechanic needs the
    /// ItemRandomProperties data and is not delivered (docs/areas/items.md).
    /// </para>
    /// </summary>
    public InventoryResult CreateItemFromSpell(uint entry, int amount, out Item? item, out uint created)
    {
        item = null;
        created = 0;
        if (Player is { CanMutateQuestSettlementState: false })
        {
            return InventoryResult.CantDoRightNow;
        }

        if (Templates.Find(entry) is not { } template)
        {
            SendEquipError(InventoryResult.ItemNotFound, null, null);
            return InventoryResult.ItemNotFound;
        }

        uint numToAdd = (uint)Math.Min(Math.Max(amount, 1), (int)template.MaxStackSize());
        var dest = new List<ItemPosCount>();
        InventoryResult msg = CanStoreNewItem(entry, numToAdd, dest, out uint noSpace);
        if (msg != InventoryResult.Ok)
        {
            if (msg is InventoryResult.InventoryFull or InventoryResult.CantCarryMoreOfThis)
            {
                numToAdd -= noSpace; // "convert to possible store amount"
            }
            else
            {
                SendEquipError(msg, null, null, 0, entry);
                return msg;
            }
        }

        if (numToAdd == 0)
        {
            return msg;
        }

        item = StoreNewItem(dest, template, numToAdd);
        if (HasSignature(item.Template))
        {
            item.SetUInt64(UpdateFields.ItemFieldCreator, OwnerGuid.Value);
        }

        if (Player is { IsInWorld: true } player)
        {
            player.Session.Send(
                ArcaneCore.Protocol.WorldOpcode.SmsgItemPushResult,
                ItemPackets.ItemPushResult(player.Guid, item, numToAdd, received: true, created: true, showInChat: true));
        }

        created = numToAdd;
        return msg;
    }
}
