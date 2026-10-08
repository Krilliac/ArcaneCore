using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Crafting.Enchanting;

/// <summary>
/// The enchantment slots of an item (vmangos Objects/ItemDefines.h:144-155 <c>EnchantmentSlot</c>; the client slot number minus one). Each slot is
/// three item fields: enchantment id, duration in ms, charges (<c>ITEM_FIELD_ENCHANTMENT + slot * 3 + offset</c>).
/// </summary>
public static class EnchantSlots
{
    public const int Permanent = 0;

    public const int Temporary = 1;

    /// <summary>The slots below this are shown on the inspect window and in the visible item fields (vmangos <c>MAX_INSPECTED_ENCHANTMENT_SLOT</c>).</summary>
    public const int MaxInspected = 2;

    /// <summary>Random-property slots 0-3 (vmangos PROP_ENCHANTMENT_SLOT_0..3, 3-6): the enchantments of an item random suffix (ItemRandomProperties.Apply writes slots 3-5).</summary>
    public const int Property0 = 3;

    /// <summary>vmangos <c>MAX_ENCHANTMENT_SLOT</c>.</summary>
    public const int Count = 7;

    /// <summary>Fields per slot (id, duration, charges; vmangos <c>MAX_ENCHANTMENT_OFFSET</c>).</summary>
    public const int FieldsPerSlot = 3;

    public const int IdOffset = 0;

    public const int DurationOffset = 1;

    public const int ChargesOffset = 2;
}

/// <summary>
/// The enchantment fields of one <see cref="Item"/> (vmangos Item::GetEnchantmentId / SetEnchantment / SetEnchantmentDuration /
/// SetEnchantmentCharges / ClearEnchantment, Objects/Item.cpp:1028-1090), as static helpers so Item.cs stays untouched. Writing a field
/// queues the item's value update for its owner, which is what <c>SetState(ITEM_CHANGED)</c> adds to in the reference: the inventory snapshot is
/// taken from the fields.
/// </summary>
public static class ItemEnchantments
{
    private static int Field(int slot, int offset) => UpdateFields.ItemFieldEnchantment + (slot * EnchantSlots.FieldsPerSlot) + offset;

    public static uint Id(Item item, int slot) => item.GetUInt32(Field(slot, EnchantSlots.IdOffset));

    public static uint Duration(Item item, int slot) => item.GetUInt32(Field(slot, EnchantSlots.DurationOffset));

    public static uint Charges(Item item, int slot) => item.GetUInt32(Field(slot, EnchantSlots.ChargesOffset));

    /// <summary>
    /// vmangos <c>Item::SetEnchantment</c>. An unchanged triple writes nothing. A slot that is shown on inspect and a non-empty caster tell the owner
    /// first (the old enchantment fades, then the new one is logged). <paramref name="caster"/> empty skips the log (loading, GM edits).
    /// </summary>
    public static void Set(Item item, int slot, uint id, uint duration, uint charges, ObjectGuid caster = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (Id(item, slot) == id && Duration(item, slot) == duration && Charges(item, slot) == charges)
        {
            return;
        }

        if (slot < EnchantSlots.MaxInspected && !caster.IsEmpty && item.Inventory?.Player is { } owner)
        {
            uint old = Id(item, slot);
            if (old != 0)
            {
                EnchantPackets.SendLog(owner, default, item.Entry, old);
            }

            if (id != 0)
            {
                EnchantPackets.SendLog(owner, caster, item.Entry, id);
            }
        }

        item.SetUInt32(Field(slot, EnchantSlots.IdOffset), id);
        item.SetUInt32(Field(slot, EnchantSlots.DurationOffset), duration);
        item.SetUInt32(Field(slot, EnchantSlots.ChargesOffset), charges);
    }

    /// <summary>vmangos <c>Item::SetEnchantmentDuration</c>.</summary>
    public static void SetDuration(Item item, int slot, uint duration)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (Duration(item, slot) != duration)
        {
            item.SetUInt32(Field(slot, EnchantSlots.DurationOffset), duration);
        }
    }

    /// <summary>vmangos <c>Item::SetEnchantmentCharges</c>.</summary>
    public static void SetCharges(Item item, int slot, uint charges)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (Charges(item, slot) != charges)
        {
            item.SetUInt32(Field(slot, EnchantSlots.ChargesOffset), charges);
        }
    }

    /// <summary>
    /// vmangos <c>Item::ClearEnchantment</c>: nothing for an empty slot; <paramref name="sendToClient"/> tells the owner that an inspected slot's
    /// enchantment faded (a log with no caster), then the three fields are zeroed.
    /// </summary>
    public static void Clear(Item item, int slot, bool sendToClient = false)
    {
        ArgumentNullException.ThrowIfNull(item);
        uint id = Id(item, slot);
        if (id == 0)
        {
            return;
        }

        if (slot < EnchantSlots.MaxInspected && sendToClient && item.Inventory?.Player is { } owner)
        {
            EnchantPackets.SendLog(owner, default, item.Entry, id);
        }

        for (int offset = 0; offset < EnchantSlots.FieldsPerSlot; offset++)
        {
            item.SetUInt32(Field(slot, offset), 0);
        }

        if (item.LiveEnchantDuration is { } live && slot < live.Length)
        {
            live[slot] = null;
        }
    }
}

/// <summary>The two enchantment packets (vmangos Player::SendEnchantmentLog / WorldSession::SendItemEnchantTimeUpdate).</summary>
public static class EnchantPackets
{
    /// <summary>
    /// SMSG_ENCHANTMENTLOG (0x01D7, 1.12): u64 item owner, u64 caster (zero: the enchantment faded), u32 item entry, u32 enchant id
    /// (vmangos: spell id; the same value), u8 show affiliation (only read by the client when the caster is set). The owner/caster order
    /// is gtker wow_messages <c>smsg_enchantmentlog.wowm</c> and cmangos-classic <c>Unit::SendEnchantmentLog</c> (Unit.cpp:5988); vmangos
    /// <c>EnchantmentLog::AppendBodyTo</c> (Packets/Item.cpp:341) writes the caster first: the two sources disagree, the real-client run decides.
    /// </summary>
    public static byte[] EnchantmentLog(ObjectGuid owner, ObjectGuid caster, uint itemEntry, uint enchantId, bool showAffiliation)
    {
        var writer = new PacketWriter(25);
        writer.WriteUInt64(owner.Value);
        writer.WriteUInt64(caster.Value);
        writer.WriteUInt32(itemEntry);
        writer.WriteUInt32(enchantId);
        writer.WriteByte(showAffiliation ? (byte)1 : (byte)0);
        return writer.ToArray();
    }

    /// <summary>SMSG_ITEM_ENCHANT_TIME_UPDATE (0x01EB, build &gt; 1.10.2; gtker smsg_item_enchant_time_update.wowm): u64 item, u32 slot, u32 seconds left, u64 player.</summary>
    public static byte[] ItemEnchantTimeUpdate(ObjectGuid item, uint slot, uint seconds, ObjectGuid player)
    {
        var writer = new PacketWriter(24);
        writer.WriteUInt64(item.Value);
        writer.WriteUInt32(slot);
        writer.WriteUInt32(seconds);
        writer.WriteUInt64(player.Value);
        return writer.ToArray();
    }

    /// <summary>
    /// vmangos <c>Player::SendEnchantmentLog</c> (Player.cpp:11885-11909): the owner always hears it; a new enchantment (a caster) is also shown
    /// to everyone who sees the owner, with the affiliation flag set.
    /// </summary>
    public static void SendLog(Player owner, ObjectGuid caster, uint itemEntry, uint enchantId)
    {
        owner.Session.Send(WorldOpcode.SmsgEnchantmentlog, EnchantmentLog(owner.Guid, caster, itemEntry, enchantId, showAffiliation: false));
        if (!caster.IsEmpty)
        {
            SpellSystem.SendToSet(owner, WorldOpcode.SmsgEnchantmentlog, EnchantmentLog(owner.Guid, caster, itemEntry, enchantId, showAffiliation: true), includeSelf: false);
        }
    }

    /// <summary>One SMSG_ITEM_ENCHANT_TIME_UPDATE to the owner.</summary>
    public static void SendTimeUpdate(Player owner, Item item, int slot, uint durationMs)
        => owner.Session.Send(WorldOpcode.SmsgItemEnchantTimeUpdate, ItemEnchantTimeUpdate(item.Guid, (uint)slot, durationMs / 1000, owner.Guid));
}
