using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Items;

/// <summary>Item packet builders (payloads only; the opcode is added by the session).</summary>
public static class ItemPackets
{
    /// <summary>
    /// SMSG_INVENTORY_CHANGE_FAILURE: u8 result; for CANT_EQUIP_LEVEL_I a u32 required level;
    /// then (any failure) u64 item1, u64 item2, u8 bag slot. The level precedes the GUIDs in
    /// vmangos WorldPackets::Item::InventoryChangeFailure::AppendBodyTo and gtker 1.12 (the
    /// level-last layout is 2.4.3+). vmangos passes the bag slot; gtker's note "vmangos sets
    /// to 0" is outdated.
    /// </summary>
    public static byte[] InventoryChangeFailure(InventoryResult result, ObjectGuid item1, ObjectGuid item2, byte bagSlot = 0, uint requiredLevel = 0)
    {
        var writer = new PacketWriter(22);
        writer.WriteByte((byte)result);
        if (result != InventoryResult.Ok)
        {
            if (result == InventoryResult.CantEquipLevelI)
            {
                writer.WriteUInt32(requiredLevel);
            }

            writer.WriteUInt64(item1.Value);
            writer.WriteUInt64(item2.Value);
            writer.WriteByte(bagSlot);
        }

        return writer.ToArray();
    }

    /// <summary>
    /// SMSG_ITEM_PUSH_RESULT (build &gt; 1.10.2, vmangos Player::SendNewItem /
    /// ItemPushResult::AppendBodyTo; gtker smsg_item_push_result 1.12): u64 player, u32 received
    /// (0 looted, 1 from NPC), u32 created, u32 show in chat, u8 bag slot, u32 item slot
    /// (0xFFFFFFFF when added to an existing stack), u32 entry, u32 suffix factor,
    /// i32 random property, u32 count.
    /// </summary>
    public static byte[] ItemPushResult(ObjectGuid player, Item item, uint count, bool received, bool created, bool showInChat)
    {
        ArgumentNullException.ThrowIfNull(item);
        var writer = new PacketWriter(45);
        writer.WriteUInt64(player.Value);
        writer.WriteUInt32(received ? 1u : 0u);
        writer.WriteUInt32(created ? 1u : 0u);
        writer.WriteUInt32(showInChat ? 1u : 0u);
        writer.WriteByte(item.BagSlot);
        writer.WriteUInt32(item.Count == count ? item.Slot : 0xFFFFFFFFu);
        writer.WriteUInt32(item.Entry);
        writer.WriteUInt32(item.SuffixFactor);
        writer.WriteInt32(item.RandomPropertyId);
        writer.WriteUInt32(count);
        return writer.ToArray();
    }

    /// <summary>SMSG_ITEM_QUERY_SINGLE_RESPONSE for an unknown entry: entry | 0x80000000 (vmangos HandleItemQuerySingleOpcode).</summary>
    public static byte[] ItemQueryUnknown(uint entry)
    {
        var writer = new PacketWriter(4);
        writer.WriteUInt32(entry | 0x80000000u);
        return writer.ToArray();
    }

    /// <summary>
    /// SMSG_ITEM_QUERY_SINGLE_RESPONSE, field order of vmangos WorldSession::HandleItemQuerySingleOpcode
    /// for build 5875 (cross-checked with gtker smsg_item_query_single_response 1.12):
    /// consumables report sub-class 0; names 2-4 are empty; the reputation rank is sent only when a
    /// reputation faction is set. A spell slot whose spell is unknown is written as
    /// 0, 0, 0, -1, 0, -1 (vmangos and cmangos-classic alike; gtker's capture has zeros — servers
    /// win). Spell.dbc is not loaded yet, so a set spell sends its item_template cooldowns as they
    /// are (vmangos falls back to Spell.dbc cooldowns only when both are negative).
    /// </summary>
    public static byte[] ItemQueryResponse(ItemTemplate t)
    {
        ArgumentNullException.ThrowIfNull(t);
        var w = new PacketWriter(512);
        w.WriteUInt32(t.Entry);
        w.WriteUInt32(t.Class);
        w.WriteUInt32((ItemClass)t.Class == ItemClass.Consumable ? 0 : t.SubClass);
        w.WriteCString(t.Name);
        w.WriteCString(string.Empty);
        w.WriteCString(string.Empty);
        w.WriteCString(string.Empty);
        w.WriteUInt32(t.DisplayId);
        w.WriteUInt32(t.Quality);
        w.WriteUInt32(t.Flags);
        w.WriteUInt32(t.BuyPrice);
        w.WriteUInt32(t.SellPrice);
        w.WriteUInt32(t.InventoryType);
        w.WriteUInt32(t.AllowableClass);
        w.WriteUInt32(t.AllowableRace);
        w.WriteUInt32(t.ItemLevel);
        w.WriteUInt32(t.RequiredLevel);
        w.WriteUInt32(t.RequiredSkill);
        w.WriteUInt32(t.RequiredSkillRank);
        w.WriteUInt32(t.RequiredSpell);
        w.WriteUInt32(t.RequiredHonorRank);
        w.WriteUInt32(t.RequiredCityRank);
        w.WriteUInt32(t.RequiredReputationFaction);
        w.WriteUInt32(t.RequiredReputationFaction > 0 ? t.RequiredReputationRank : 0);
        w.WriteUInt32(t.MaxCount);
        w.WriteUInt32(t.Stackable);
        w.WriteUInt32(t.ContainerSlots);
        for (int i = 0; i < ItemTemplate.MaxStats; i++)
        {
            ItemStat stat = i < t.Stats.Count ? t.Stats[i] : default;
            w.WriteUInt32(stat.Type);
            w.WriteInt32(stat.Value);
        }

        for (int i = 0; i < ItemTemplate.MaxDamages; i++)
        {
            ItemDamage damage = i < t.Damages.Count ? t.Damages[i] : default;
            w.WriteSingle(damage.Min);
            w.WriteSingle(damage.Max);
            w.WriteUInt32(damage.School);
        }

        w.WriteInt32(t.Armor);
        w.WriteInt32(t.HolyRes);
        w.WriteInt32(t.FireRes);
        w.WriteInt32(t.NatureRes);
        w.WriteInt32(t.FrostRes);
        w.WriteInt32(t.ShadowRes);
        w.WriteInt32(t.ArcaneRes);
        w.WriteUInt32(t.Delay);
        w.WriteUInt32(t.AmmoType);
        w.WriteSingle(t.RangedModRange);
        for (int i = 0; i < ItemTemplate.MaxSpells; i++)
        {
            ItemSpell spell = i < t.Spells.Count ? t.Spells[i] : default;
            if (spell.SpellId == 0)
            {
                w.WriteUInt32(0);
                w.WriteUInt32(0);
                w.WriteUInt32(0);
                w.WriteInt32(-1);
                w.WriteUInt32(0);
                w.WriteInt32(-1);
            }
            else
            {
                w.WriteUInt32(spell.SpellId);
                w.WriteUInt32(spell.Trigger);
                w.WriteInt32(spell.Charges);
                w.WriteInt32(spell.Cooldown);
                w.WriteUInt32(spell.Category);
                w.WriteInt32(spell.CategoryCooldown);
            }
        }

        w.WriteUInt32(t.Bonding);
        w.WriteCString(t.Description);
        w.WriteUInt32(t.PageText);
        w.WriteUInt32(t.PageLanguage);
        w.WriteUInt32(t.PageMaterial);
        w.WriteUInt32(t.StartQuest);
        w.WriteUInt32(t.LockId);
        w.WriteUInt32(t.Material);
        w.WriteUInt32(t.Sheath);
        w.WriteUInt32(t.RandomProperty);
        w.WriteUInt32(t.Block);
        w.WriteUInt32(t.SetId);
        w.WriteUInt32(t.MaxDurability);
        w.WriteUInt32(t.AreaBound);
        w.WriteUInt32(t.MapBound);
        w.WriteUInt32(t.BagFamily);
        return w.ToArray();
    }
}
