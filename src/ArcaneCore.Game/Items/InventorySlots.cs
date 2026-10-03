namespace ArcaneCore.Game.Items;

/// <summary>
/// The player's slot layout (vmangos Player.h EquipmentSlots / InventorySlots / BankItemSlots /
/// BankBagSlots / BuyBackSlots / KeyRingSlots and Bag.h MAX_BAG_SIZE, build 5875). A position is
/// (bag, slot): bag 255 is the player's own slots, otherwise the bag slot (19-22 equipped bags,
/// 63-68 bank bags) holding the container.
/// </summary>
public static class InventorySlots
{
    /// <summary>INVENTORY_SLOT_BAG_0: the player's own slots.</summary>
    public const byte Bag0 = 255;

    /// <summary>NULL_BAG: "any bag" in a store request.</summary>
    public const byte NullBag = 0;

    /// <summary>NULL_SLOT: "any slot" in a store request.</summary>
    public const byte NullSlot = 255;

    public const byte Head = 0;
    public const byte Neck = 1;
    public const byte Shoulders = 2;
    public const byte Body = 3;
    public const byte Chest = 4;
    public const byte Waist = 5;
    public const byte Legs = 6;
    public const byte Feet = 7;
    public const byte Wrists = 8;
    public const byte Hands = 9;
    public const byte Finger1 = 10;
    public const byte Finger2 = 11;
    public const byte Trinket1 = 12;
    public const byte Trinket2 = 13;
    public const byte Back = 14;
    public const byte MainHand = 15;
    public const byte OffHand = 16;
    public const byte Ranged = 17;
    public const byte Tabard = 18;

    /// <summary>EQUIPMENT_SLOT_END.</summary>
    public const byte EquipmentEnd = 19;

    public const byte BagStart = 19;
    public const byte BagEnd = 23;
    public const byte ItemStart = 23;
    public const byte ItemEnd = 39;
    public const byte BankItemStart = 39;
    public const byte BankItemEnd = 63;
    public const byte BankBagStart = 63;
    public const byte BankBagEnd = 69;
    public const byte BuybackStart = 69;
    public const byte BuybackEnd = 81;
    public const byte KeyringStart = 81;

    /// <summary>KEYRING_SLOT_END (16 usable keyring slots; the update fields have room for 32, MAX_KEYRING_SLOTS).</summary>
    public const byte KeyringEnd = 97;

    /// <summary>MAX_BAG_SIZE.</summary>
    public const int MaxBagSize = 36;

    /// <summary>Slots shown in SMSG_CHAR_ENUM: equipment plus the first bag slot (vmangos BuildEnumData: INVENTORY_SLOT_BAG_START + 1).</summary>
    public const int CharEnumSlots = 20;

    /// <summary>vmangos Player::GetMaxKeyringSize (build &gt; 1.10.2).</summary>
    public static int KeyringSize(byte level) => level > 60 ? 16 : level >= 50 ? 12 : level >= 40 ? 8 : 4;

    /// <summary>vmangos Player::IsInventoryPos.</summary>
    public static bool IsInventoryPos(byte bag, byte slot)
        => (bag == Bag0 && slot == NullSlot)
        || (bag == Bag0 && slot >= ItemStart && slot < ItemEnd)
        || (bag >= BagStart && bag < BagEnd)
        || (bag == Bag0 && slot >= KeyringStart && slot < KeyringEnd);

    /// <summary>vmangos Player::IsEquipmentPos (equipment and equipped-bag slots).</summary>
    public static bool IsEquipmentPos(byte bag, byte slot)
        => bag == Bag0 && (slot < EquipmentEnd || (slot >= BagStart && slot < BagEnd));

    /// <summary>vmangos Player::IsBankPos.</summary>
    public static bool IsBankPos(byte bag, byte slot)
        => (bag == Bag0 && slot >= BankItemStart && slot < BankItemEnd)
        || (bag == Bag0 && slot >= BankBagStart && slot < BankBagEnd)
        || (bag >= BankBagStart && bag < BankBagEnd);

    /// <summary>vmangos Player::IsBagPos (equipped or bank bag slots).</summary>
    public static bool IsBagPos(byte bag, byte slot)
        => bag == Bag0 && ((slot >= BagStart && slot < BagEnd) || (slot >= BankBagStart && slot < BankBagEnd));

    /// <summary>vmangos Player::IsValidPos.</summary>
    public static bool IsValidPos(byte bag, byte slot, bool explicitPos)
    {
        if (bag == NullBag && !explicitPos)
        {
            return true;
        }

        if (bag == Bag0)
        {
            return (slot == NullSlot && !explicitPos)
                || slot < EquipmentEnd
                || (slot >= BagStart && slot < BagEnd)
                || (slot >= ItemStart && slot < ItemEnd)
                || (slot >= KeyringStart && slot < KeyringEnd)
                || (slot >= BankItemStart && slot < BankItemEnd)
                || (slot >= BankBagStart && slot < BankBagEnd);
        }

        if ((bag >= BagStart && bag < BagEnd) || (bag >= BankBagStart && bag < BankBagEnd))
        {
            // vmangos: "bag content slots"; the slot is checked against the bag size later.
            return (slot == NullSlot && !explicitPos) || slot < MaxBagSize;
        }

        return false;
    }
}
