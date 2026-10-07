using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.Game.Items;

public sealed partial class PlayerInventory
{
    /// <summary>vmangos Player::_CanTakeMoreSimilarItems (item_template.max_count).</summary>
    public InventoryResult CanTakeMoreSimilarItems(ItemTemplate template, uint count, Item? skip, out uint noSpaceCount)
    {
        ArgumentNullException.ThrowIfNull(template);
        noSpaceCount = 0;
        if (template.MaxCount > 0)
        {
            uint current = GetItemCount(template.Entry, inBankAlso: true, skip);
            if (current + count > template.MaxCount)
            {
                noSpaceCount = count + current - template.MaxCount;
                return InventoryResult.CantCarryMoreOfThis;
            }
        }

        return InventoryResult.Ok;
    }

    /// <summary>vmangos Player::FindEquipSlot.</summary>
    public byte FindEquipSlot(ItemTemplate template, byte slot, bool swap)
    {
        ArgumentNullException.ThrowIfNull(template);
        byte[] slots = template.AllowedEquipSlots(Class, Requirements.CanDualWield(this));
        if (slot != InventorySlots.NullSlot)
        {
            if (swap || GetItem(InventorySlots.Bag0, slot) is null)
            {
                return slots.Contains(slot) ? slot : InventorySlots.NullSlot;
            }

            return InventorySlots.NullSlot;
        }

        foreach (byte candidate in slots)
        {
            if (candidate != InventorySlots.NullSlot && GetItem(InventorySlots.Bag0, candidate) is null
                && (candidate != InventorySlots.OffHand || !IsTwoHandUsed))
            {
                return candidate;
            }
        }

        foreach (byte candidate in slots)
        {
            if (candidate != InventorySlots.NullSlot && swap)
            {
                return candidate;
            }
        }

        return InventorySlots.NullSlot;
    }

    /// <summary>vmangos Player::CanUseItem(ItemPrototype): class/race masks, skill, spell, honor rank, level, proficiency.</summary>
    public InventoryResult CanUseItem(ItemTemplate template, bool notLoading = true)
    {
        ArgumentNullException.ThrowIfNull(template);
        uint classMask = 1u << ((int)Class - 1);
        uint raceMask = 1u << ((int)Race - 1);
        if ((template.AllowableClass & classMask) == 0 || (template.AllowableRace & raceMask) == 0)
        {
            return InventoryResult.YouCanNeverUseThatItem;
        }

        if (template.RequiredSkill != 0)
        {
            uint skill = Requirements.SkillValue(this, template.RequiredSkill);
            if (skill == 0)
            {
                return InventoryResult.NoRequiredProficiency;
            }

            if (skill < template.RequiredSkillRank)
            {
                return InventoryResult.CantEquipSkill;
            }
        }

        if (template.RequiredSpell != 0 && !Requirements.HasSpell(this, template.RequiredSpell))
        {
            return InventoryResult.NoRequiredProficiency;
        }

        if (notLoading && Requirements.HonorRank(this) < template.RequiredHonorRank)
        {
            return InventoryResult.CantEquipRank;
        }

        if (Level < template.RequiredLevel)
        {
            return InventoryResult.CantEquipLevelI;
        }

        uint proficiency = template.ProficiencySkill();
        if (proficiency != 0 && Requirements.SkillValue(this, proficiency) == 0)
        {
            return InventoryResult.NoRequiredProficiency;
        }

        return InventoryResult.Ok;
    }

    /// <summary>vmangos Player::CanUseItem(Item): alive, ownership, template rules, reputation.</summary>
    public InventoryResult CanUseItem(Item item, bool notLoading = true)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (notLoading && Player is { IsAlive: false })
        {
            return InventoryResult.YouAreDead;
        }

        if (IsBoundToOther(item))
        {
            return InventoryResult.DontOwnThatItem;
        }

        InventoryResult result = CanUseItem(item.Template, notLoading);
        if (result != InventoryResult.Ok)
        {
            return result;
        }

        ItemTemplate t = item.Template;
        if (t.RequiredReputationFaction != 0 && Requirements.ReputationRank(this, t.RequiredReputationFaction) < t.RequiredReputationRank)
        {
            return InventoryResult.CantEquipReputation;
        }

        return InventoryResult.Ok;
    }

    /// <summary>CanStoreItem for an existing item (its own count).</summary>
    public InventoryResult CanStoreItem(byte bag, byte slot, List<ItemPosCount> dest, Item item, bool swap, out byte bagSlot)
    {
        ArgumentNullException.ThrowIfNull(item);
        return CanStoreItem(bag, slot, dest, item.Template, item.Count, item, swap, out bagSlot, out _);
    }

    /// <summary>
    /// vmangos Player::_CanStoreItem: where <paramref name="count"/> items of
    /// <paramref name="template"/> can go in the inventory (backpack, keyring, equipped bags).
    /// A specific (bag, slot) is tried first, then the specific bag, then merges into stacks
    /// (keyring, backpack, special bags, general bags), then free slots (keyring for keys,
    /// special bags, backpack, general bags).
    /// </summary>
    public InventoryResult CanStoreItem(
        byte bag, byte slot, List<ItemPosCount> dest, ItemTemplate template, uint count, Item? source, bool swap,
        out byte bagSlot, out uint noSpaceCount)
    {
        ArgumentNullException.ThrowIfNull(dest);
        ArgumentNullException.ThrowIfNull(template);
        bagSlot = 0;
        noSpaceCount = 0;

        if (source is not null && IsBoundToOther(source))
        {
            noSpaceCount = count;
            return InventoryResult.DontOwnThatItem;
        }

        InventoryResult res = CanTakeMoreSimilarItems(template, count, source, out uint noSimilar);
        if (res != InventoryResult.Ok)
        {
            if (count == noSimilar)
            {
                noSpaceCount = noSimilar;
                return res;
            }

            count -= noSimilar;
        }

        // A step's outcome: an error ends the search; all placed ends it (with the max-count
        // shortfall reported); otherwise the search continues (null).
        InventoryResult? Step(InventoryResult r, uint remaining, ref uint noSpace)
        {
            if (r != InventoryResult.Ok)
            {
                noSpace = remaining + noSimilar;
                return r;
            }

            if (remaining == 0)
            {
                noSpace = noSimilar;
                return noSimilar == 0 ? InventoryResult.Ok : InventoryResult.CantCarryMoreOfThis;
            }

            return null;
        }

        // in a specific slot
        if (bag != InventorySlots.NullBag && slot != InventorySlots.NullSlot)
        {
            if (bag == InventorySlots.Bag0 && slot < InventorySlots.ItemStart)
            {
                return InventoryResult.ItemDoesntGoToSlot;
            }

            res = CanStoreInSpecificSlot(bag, slot, dest, template, ref count, swap, source, ref bagSlot);
            if (Step(res, count, ref noSpaceCount) is { } done)
            {
                return done;
            }
        }

        // in a specific bag
        if (bag != InventorySlots.NullBag)
        {
            if (template.Stackable > 1)
            {
                if (bag == InventorySlots.Bag0)
                {
                    res = CanStoreInSlots(InventorySlots.KeyringStart, InventorySlots.KeyringEnd, dest, template, ref count, merge: true, source);
                    if (Step(res, count, ref noSpaceCount) is { } done1)
                    {
                        return done1;
                    }

                    res = CanStoreInSlots(InventorySlots.ItemStart, InventorySlots.ItemEnd, dest, template, ref count, merge: true, source);
                }
                else
                {
                    res = CanStoreInEitherBag(bag, dest, template, ref count, merge: true, source, ref bagSlot);
                }

                if (Step(res, count, ref noSpaceCount) is { } done2)
                {
                    return done2;
                }
            }

            if (bag == InventorySlots.Bag0)
            {
                if ((BagFamily)template.BagFamily == BagFamily.Keys)
                {
                    res = CanStoreInSlots(InventorySlots.KeyringStart, KeyringLimit, dest, template, ref count, merge: false, source);
                    if (Step(res, count, ref noSpaceCount) is { } done3)
                    {
                        return done3;
                    }
                }

                res = CanStoreInSlots(InventorySlots.ItemStart, InventorySlots.ItemEnd, dest, template, ref count, merge: false, source);
            }
            else
            {
                res = CanStoreInEitherBag(bag, dest, template, ref count, merge: false, source, ref bagSlot);
            }

            if (Step(res, count, ref noSpaceCount) is { } done4)
            {
                return done4;
            }
        }

        // merge into existing stacks anywhere
        if (template.Stackable > 1)
        {
            res = CanStoreInSlots(InventorySlots.KeyringStart, InventorySlots.KeyringEnd, dest, template, ref count, merge: true, source);
            if (Step(res, count, ref noSpaceCount) is { } done5)
            {
                return done5;
            }

            res = CanStoreInSlots(InventorySlots.ItemStart, InventorySlots.ItemEnd, dest, template, ref count, merge: true, source);
            if (Step(res, count, ref noSpaceCount) is { } done6)
            {
                return done6;
            }

            if (template.BagFamily != 0 && StoreInEquippedBags(dest, template, ref count, merge: true, nonSpecialized: false, source, bag, ref bagSlot))
            {
                return Step(InventoryResult.Ok, 0, ref noSpaceCount)!.Value;
            }

            if (StoreInEquippedBags(dest, template, ref count, merge: true, nonSpecialized: true, source, bag, ref bagSlot))
            {
                return Step(InventoryResult.Ok, 0, ref noSpaceCount)!.Value;
            }
        }

        // a free slot in a special bag (the keyring for keys)
        if (template.BagFamily != 0)
        {
            if ((BagFamily)template.BagFamily == BagFamily.Keys)
            {
                res = CanStoreInSlots(InventorySlots.KeyringStart, KeyringLimit, dest, template, ref count, merge: false, source);
                if (Step(res, count, ref noSpaceCount) is { } done7)
                {
                    return done7;
                }
            }

            if (StoreInEquippedBags(dest, template, ref count, merge: false, nonSpecialized: false, source, bag, ref bagSlot))
            {
                return Step(InventoryResult.Ok, 0, ref noSpaceCount)!.Value;
            }
        }

        // vmangos: "Normally it would be impossible to autostore not empty bags".
        if (source is Container { IsEmpty: false })
        {
            return InventoryResult.NonemptyBagOverOtherBag;
        }

        // a free slot in the backpack, then in general bags
        res = CanStoreInSlots(InventorySlots.ItemStart, InventorySlots.ItemEnd, dest, template, ref count, merge: false, source);
        if (Step(res, count, ref noSpaceCount) is { } done8)
        {
            return done8;
        }

        if (StoreInEquippedBags(dest, template, ref count, merge: false, nonSpecialized: true, source, bag, ref bagSlot))
        {
            return Step(InventoryResult.Ok, 0, ref noSpaceCount)!.Value;
        }

        noSpaceCount = count + noSimilar;
        return InventoryResult.InventoryFull;
    }

    /// <summary>
    /// vmangos Player::CanBankItem: the bank's slots (39-62) and bank bags (63-68). Putting a bag
    /// into a bank bag slot needs the slot bought (MUST_PURCHASE_THAT_BAG_SLOT).
    /// </summary>
    public InventoryResult CanBankItem(byte bag, byte slot, List<ItemPosCount> dest, Item item, bool swap, out byte bagSlot, bool notLoading = true)
    {
        ArgumentNullException.ThrowIfNull(dest);
        ArgumentNullException.ThrowIfNull(item);
        bagSlot = 0;
        ItemTemplate template = item.Template;
        uint count = item.Count;
        if (IsBoundToOther(item))
        {
            return InventoryResult.DontOwnThatItem;
        }

        InventoryResult res = CanTakeMoreSimilarItems(template, count, item, out _);
        if (res != InventoryResult.Ok)
        {
            return res;
        }

        if (bag != InventorySlots.NullBag && slot != InventorySlots.NullSlot)
        {
            if (bag == InventorySlots.Bag0 && (slot < InventorySlots.BankItemStart || slot >= InventorySlots.BankBagEnd))
            {
                return InventoryResult.ItemDoesntGoToSlot;
            }

            if (bag == InventorySlots.Bag0 && slot >= InventorySlots.BankBagStart)
            {
                if (!item.IsBag)
                {
                    return InventoryResult.ItemDoesntGoToSlot;
                }

                if (slot - InventorySlots.BankBagStart >= BankBagSlotCount)
                {
                    return InventoryResult.MustPurchaseThatBagSlot;
                }

                res = CanUseItem(item, notLoading);
                if (res != InventoryResult.Ok)
                {
                    return res;
                }
            }

            res = CanStoreInSpecificSlot(bag, slot, dest, template, ref count, swap, item, ref bagSlot);
            if (res != InventoryResult.Ok || count == 0)
            {
                return res;
            }
        }

        if (bag != InventorySlots.NullBag)
        {
            if (item is Container { IsEmpty: false })
            {
                return InventoryResult.NonemptyBagOverOtherBag;
            }

            if (template.Stackable > 1)
            {
                res = bag == InventorySlots.Bag0
                    ? CanStoreInSlots(InventorySlots.BankItemStart, InventorySlots.BankItemEnd, dest, template, ref count, merge: true, item)
                    : CanStoreInEitherBag(bag, dest, template, ref count, merge: true, item, ref bagSlot);
                if (res != InventoryResult.Ok || count == 0)
                {
                    return res;
                }
            }

            res = bag == InventorySlots.Bag0
                ? CanStoreInSlots(InventorySlots.BankItemStart, InventorySlots.BankItemEnd, dest, template, ref count, merge: false, item)
                : CanStoreInEitherBag(bag, dest, template, ref count, merge: false, item, ref bagSlot);
            if (res != InventoryResult.Ok || count == 0)
            {
                return res;
            }
        }

        if (template.Stackable > 1)
        {
            res = CanStoreInSlots(InventorySlots.BankItemStart, InventorySlots.BankItemEnd, dest, template, ref count, merge: true, item);
            if (res != InventoryResult.Ok || count == 0)
            {
                return res;
            }

            if ((template.BagFamily != 0 && StoreInBankBags(dest, template, ref count, merge: true, nonSpecialized: false, item, bag, ref bagSlot))
                || StoreInBankBags(dest, template, ref count, merge: true, nonSpecialized: true, item, bag, ref bagSlot))
            {
                return InventoryResult.Ok;
            }
        }

        if (template.BagFamily != 0 && StoreInBankBags(dest, template, ref count, merge: false, nonSpecialized: false, item, bag, ref bagSlot))
        {
            return InventoryResult.Ok;
        }

        res = CanStoreInSlots(InventorySlots.BankItemStart, InventorySlots.BankItemEnd, dest, template, ref count, merge: false, item);
        if (res != InventoryResult.Ok || count == 0)
        {
            return res;
        }

        return StoreInBankBags(dest, template, ref count, merge: false, nonSpecialized: true, item, bag, ref bagSlot)
            ? InventoryResult.Ok
            : InventoryResult.BankFull;
    }

    /// <summary>vmangos Player::CanEquipItem: the slot <paramref name="dest"/> an item can be worn in.</summary>
    public InventoryResult CanEquipItem(byte slot, out byte dest, ItemTemplate template, Item? item, bool swap, bool notLoading = true)
    {
        ArgumentNullException.ThrowIfNull(template);
        dest = 0;
        if (item is not null && IsBoundToOther(item))
        {
            return InventoryResult.DontOwnThatItem;
        }

        InventoryResult res = CanTakeMoreSimilarItems(template, item?.Count ?? 1, item, out _);
        if (res != InventoryResult.Ok)
        {
            return res;
        }

        if (notLoading && Player is { } player)
        {
            // Patch 1.6.0: no equipment changes while stunned.
            if ((player.UnitFlags & UnitFlags.Stunned) != 0)
            {
                return InventoryResult.YouAreStunned;
            }

            if (!template.CanChangeEquipStateInCombat() && (player.UnitFlags & UnitFlags.InCombat) != 0)
            {
                return InventoryResult.NotInCombat;
            }

            // vmangos: "prevent equip item in process logout"
            if (player.IsLoggingOut)
            {
                return InventoryResult.YouAreStunned;
            }
        }

        byte eslot = FindEquipSlot(template, slot, swap);
        if (eslot == InventorySlots.NullSlot)
        {
            return InventoryResult.ItemCantBeEquipped;
        }

        res = item is not null ? CanUseItem(item, notLoading) : CanUseItem(template, notLoading);
        if (res != InventoryResult.Ok)
        {
            return res;
        }

        if (!swap && GetItem(InventorySlots.Bag0, eslot) is not null)
        {
            return InventoryResult.NoEquipmentSlotAvailable;
        }

        // vmangos CanEquipUniqueItem (ITEM_FLAG_UNIQUE_EQUIPPED).
        if (template.HasFlag(ItemTemplateFlags.UniqueEquipped) && HasEquipped(template.Entry, item, swap ? eslot : InventorySlots.NullSlot))
        {
            return InventoryResult.ItemCantBeEquipped;
        }

        // only one quiver or ammo pouch
        if ((ItemClass)template.Class == ItemClass.Quiver)
        {
            for (byte i = InventorySlots.BagStart; i < InventorySlots.BagEnd; i++)
            {
                if (_items[i] is { } other && other != item && other.Template.Class == template.Class && (!swap || i != eslot))
                {
                    return other.Template.SubClass == ItemSubClasses.QuiverAmmoPouch
                        ? InventoryResult.CanEquipOnly1AmmoPouch
                        : InventoryResult.CanEquipOnly1Quiver;
                }
            }
        }

        InventoryType type = template.GetInventoryType();
        if (eslot == InventorySlots.OffHand)
        {
            if (type is InventoryType.Weapon or InventoryType.WeaponOffHand)
            {
                if (!Requirements.CanDualWield(this))
                {
                    return InventoryResult.CantDualWield;
                }
            }
            else if (type == InventoryType.TwoHandWeapon)
            {
                return InventoryResult.CantDualWield;
            }

            if (IsTwoHandUsed)
            {
                return InventoryResult.CantEquipWithTwohanded;
            }
        }

        // a two-hander needs the off hand free or able to go to the bags
        if (type == InventoryType.TwoHandWeapon)
        {
            if (eslot != InventorySlots.MainHand)
            {
                return InventoryResult.ItemCantBeEquipped;
            }

            if (_items[InventorySlots.OffHand] is { } offHand
                && (!notLoading
                    || CanUnequipItem(InventorySlots.Bag0, InventorySlots.OffHand, swap: false) != InventoryResult.Ok
                    || CanStoreItem(InventorySlots.NullBag, InventorySlots.NullSlot, [], offHand, swap: false, out _) != InventoryResult.Ok))
            {
                return swap ? InventoryResult.ItemsCantBeSwapped : InventoryResult.InventoryFull;
            }
        }

        dest = eslot;
        return InventoryResult.Ok;
    }

    /// <summary>vmangos Player::CanUnequipItem (equipment and bag slots only).</summary>
    public InventoryResult CanUnequipItem(byte bag, byte slot, bool swap)
    {
        if (!InventorySlots.IsEquipmentPos(bag, slot) && !InventorySlots.IsBagPos(bag, slot))
        {
            return InventoryResult.Ok;
        }

        if (GetItem(bag, slot) is not { } item)
        {
            return InventoryResult.Ok;
        }

        if (Player is { } player)
        {
            if (bag == InventorySlots.Bag0 && slot == InventorySlots.MainHand && (player.UnitFlags & UnitFlags.Disarmed) != 0)
            {
                return InventoryResult.NotWhileDisarmed;
            }

            if (!item.Template.CanChangeEquipStateInCombat() && (player.UnitFlags & UnitFlags.InCombat) != 0)
            {
                return InventoryResult.NotInCombat;
            }

            if (player.IsLoggingOut)
            {
                return InventoryResult.YouAreStunned;
            }
        }

        if (!swap && item is Container { IsEmpty: false })
        {
            return InventoryResult.CanOnlyDoWithEmptyBags;
        }

        return InventoryResult.Ok;
    }

    /// <summary>vmangos Player::IsValidPos, with the bag's real size for bag contents.</summary>
    public bool IsValidPosition(byte bag, byte slot, bool explicitPos)
    {
        if (!InventorySlots.IsValidPos(bag, slot, explicitPos))
        {
            return false;
        }

        if (bag != InventorySlots.Bag0 && bag != InventorySlots.NullBag)
        {
            if (_items[bag] is not Container container)
            {
                return false;
            }

            return (slot == InventorySlots.NullSlot && !explicitPos) || slot < container.Size;
        }

        return true;
    }

    private byte KeyringLimit => (byte)(InventorySlots.KeyringStart + InventorySlots.KeyringSize(Level));

    private bool IsBoundToOther(Item item) => item.OwnerGuid != _ownerGuid && item.IsSoulBound;

    internal static bool IsInBank(Item item)
    {
        byte slot = item.Container?.Slot ?? item.Slot;
        return slot >= InventorySlots.BankItemStart && slot < InventorySlots.BankBagEnd;
    }

    private bool HasEquipped(uint entry, Item? except, byte exceptSlot)
    {
        for (byte slot = 0; slot < InventorySlots.BagEnd; slot++)
        {
            if (slot != exceptSlot && _items[slot] is { } item && item != except && item.Entry == entry)
            {
                return true;
            }
        }

        return false;
    }

    private bool StoreInEquippedBags(List<ItemPosCount> dest, ItemTemplate template, ref uint count, bool merge, bool nonSpecialized, Item? source, byte skipBag, ref byte bagSlot)
        => StoreInBagRange(InventorySlots.BagStart, InventorySlots.BagEnd, dest, template, ref count, merge, nonSpecialized, source, skipBag, ref bagSlot);

    private bool StoreInBankBags(List<ItemPosCount> dest, ItemTemplate template, ref uint count, bool merge, bool nonSpecialized, Item? source, byte skipBag, ref byte bagSlot)
        => StoreInBagRange(InventorySlots.BankBagStart, InventorySlots.BankBagEnd, dest, template, ref count, merge, nonSpecialized, source, skipBag, ref bagSlot);

    /// <summary>The vmangos per-bag loop: errors from one bag move on to the next; true once everything is placed.</summary>
    private bool StoreInBagRange(byte begin, byte end, List<ItemPosCount> dest, ItemTemplate template, ref uint count, bool merge, bool nonSpecialized, Item? source, byte skipBag, ref byte bagSlot)
    {
        for (byte i = begin; i < end; i++)
        {
            if (CanStoreInBag(i, dest, template, ref count, merge, nonSpecialized, source, skipBag, ref bagSlot) == InventoryResult.Ok && count == 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>vmangos Player::_CanStoreItem_InSpecificSlot.</summary>
    private InventoryResult CanStoreInSpecificSlot(
        byte bag, byte slot, List<ItemPosCount> dest, ItemTemplate template, ref uint count, bool swap, Item? source, ref byte bagSlot)
    {
        Item? existing = GetItem(bag, slot);
        if (existing == source)
        {
            existing = null;
        }

        // vmangos: "empty specific slot - check item fit to slot"; a non-empty bag may only swap with a bag.
        if (source is Container { IsEmpty: false } && !(swap && existing is not null && existing.IsBag))
        {
            return InventoryResult.CanOnlyDoWithEmptyBags;
        }

        uint needSpace;
        if (existing is null || swap)
        {
            if (bag == InventorySlots.Bag0)
            {
                // Keyring slots take keys only, within the level's keyring size. Hardening (not in
                // vmangos): buyback slots and keyring slots past the size are refused too.
                bool keyring = slot >= InventorySlots.KeyringStart && slot < InventorySlots.KeyringEnd;
                if ((keyring && ((BagFamily)template.BagFamily != BagFamily.Keys || slot >= KeyringLimit))
                    || (slot >= InventorySlots.BuybackStart && slot < InventorySlots.BuybackEnd)
                    || slot >= InventorySlots.KeyringEnd)
                {
                    bagSlot = bag;
                    return InventoryResult.ItemDoesntGoIntoBag2;
                }
            }
            else
            {
                if (_items[bag] is not Container container || container == source || slot >= container.Size)
                {
                    bagSlot = bag;
                    return InventoryResult.IntBagError;
                }

                if (!template.CanGoIntoBag(container.Template))
                {
                    bagSlot = bag;
                    return InventoryResult.ItemDoesntGoIntoBag2;
                }
            }

            needSpace = template.MaxStackSize();
        }
        else
        {
            InventoryResult res = existing.CanBeMergedPartlyWith(template);
            if (res != InventoryResult.Ok)
            {
                return res;
            }

            needSpace = template.MaxStackSize() - existing.Count;
        }

        needSpace = Math.Min(needSpace, count);
        if (!Contains(dest, bag, slot))
        {
            dest.Add(new ItemPosCount(bag, slot, needSpace));
            count -= needSpace;
        }

        return InventoryResult.Ok;
    }

    /// <summary>The vmangos specific-bag pair: as a special bag first, then as a general bag.</summary>
    private InventoryResult CanStoreInEitherBag(byte bag, List<ItemPosCount> dest, ItemTemplate template, ref uint count, bool merge, Item? source, ref byte bagSlot)
    {
        InventoryResult res = CanStoreInBag(bag, dest, template, ref count, merge, nonSpecialized: false, source, InventorySlots.NullBag, ref bagSlot);
        return res == InventoryResult.Ok
            ? res
            : CanStoreInBag(bag, dest, template, ref count, merge, nonSpecialized: true, source, InventorySlots.NullBag, ref bagSlot);
    }

    /// <summary>vmangos Player::_CanStoreItem_InBag.</summary>
    private InventoryResult CanStoreInBag(
        byte bag, List<ItemPosCount> dest, ItemTemplate template, ref uint count, bool merge, bool nonSpecialized, Item? source,
        byte skipBag, ref byte bagSlot)
    {
        if (bag == skipBag)
        {
            bagSlot = bag;
            return InventoryResult.ItemDoesntGoIntoBag2;
        }

        if (_items[bag] is not Container container || container == source)
        {
            bagSlot = bag;
            return InventoryResult.IntBagError;
        }

        if (source is Container { IsEmpty: false })
        {
            return InventoryResult.CanOnlyDoWithEmptyBags;
        }

        if (nonSpecialized != container.Template.IsGeneralBag() || !template.CanGoIntoBag(container.Template))
        {
            bagSlot = bag;
            return InventoryResult.ItemDoesntGoIntoBag2;
        }

        for (byte j = 0; j < container.Size; j++)
        {
            if (TryPlace(container[j], bag, j, dest, template, ref count, merge, source))
            {
                return InventoryResult.Ok;
            }
        }

        return InventoryResult.Ok;
    }

    /// <summary>vmangos Player::_CanStoreItem_InInventorySlots.</summary>
    private InventoryResult CanStoreInSlots(byte begin, byte end, List<ItemPosCount> dest, ItemTemplate template, ref uint count, bool merge, Item? source)
    {
        if (source is Container { IsEmpty: false })
        {
            return InventoryResult.CanOnlyDoWithEmptyBags;
        }

        for (byte j = begin; j < end; j++)
        {
            if (TryPlace(_items[j], InventorySlots.Bag0, j, dest, template, ref count, merge, source))
            {
                return InventoryResult.Ok;
            }
        }

        return InventoryResult.Ok;
    }

    /// <summary>One slot of the free/merge scans; true when nothing is left to place.</summary>
    private static bool TryPlace(Item? existing, byte bag, byte slot, List<ItemPosCount> dest, ItemTemplate template, ref uint count, bool merge, Item? source)
    {
        if (existing == source)
        {
            existing = null;
        }

        if ((existing is not null) != merge)
        {
            return false;
        }

        uint needSpace = template.MaxStackSize();
        if (existing is not null)
        {
            if (existing.CanBeMergedPartlyWith(template) != InventoryResult.Ok)
            {
                return false;
            }

            needSpace -= existing.Count;
        }

        if (needSpace == 0 || Contains(dest, bag, slot))
        {
            return false;
        }

        needSpace = Math.Min(needSpace, count);
        dest.Add(new ItemPosCount(bag, slot, needSpace));
        count -= needSpace;
        return count == 0;
    }

    private static bool Contains(List<ItemPosCount> dest, byte bag, byte slot)
    {
        foreach (ItemPosCount pos in dest)
        {
            if (pos.Bag == bag && pos.Slot == slot)
            {
                return true;
            }
        }

        return false;
    }
}
