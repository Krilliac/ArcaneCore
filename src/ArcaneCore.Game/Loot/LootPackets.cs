using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Loot;

/// <summary>
/// Loot packets for 1.12.1. Layouts follow the MaNGOS-Zero family (vmangos/cmangos-classic
/// LootView serialisation) and gtker/wow_messages; written from the documented field order.
/// </summary>
public static class LootPackets
{
    /// <summary>
    /// SMSG_LOOT_RESPONSE: u64 guid, u8 loot type, u32 gold, u8 item count, then per visible item
    /// u8 slot, u32 item id, u32 count, u32 display id, u32 random suffix (0 in 1.12),
    /// u32 random property id, u8 slot type.
    /// </summary>
    public static byte[] LootResponse(LootBag bag, Player viewer, Func<uint, uint>? displayIdOf = null)
    {
        ArgumentNullException.ThrowIfNull(bag);
        ArgumentNullException.ThrowIfNull(viewer);
        var writer = new PacketWriter(14 + (bag.Items.Count * 22));
        writer.WriteUInt64(bag.Source.Value);
        writer.WriteByte(LootTypes.ToWire(bag.Type)); // skinning/insignia go out as 2, fishing hole/fail as 3 (vmangos Player.cpp:7980-7995)
        bool money = bag.Owner.IsEmpty || bag.Owner == viewer.Guid;
        writer.WriteUInt32(money && bag.IsRecipient(viewer) ? bag.Gold : 0);
        int countAt = writer.Length;
        writer.WriteByte(0);
        byte count = 0;
        foreach (LootItem item in bag.Items)
        {
            if (bag.SlotFor(viewer, item) is not { } slotType)
            {
                continue;
            }

            writer.WriteByte(item.Slot);
            writer.WriteUInt32(item.ItemId);
            writer.WriteUInt32(item.Count);
            writer.WriteUInt32(displayIdOf?.Invoke(item.ItemId) ?? item.DisplayId);
            writer.WriteUInt32(0);
            writer.WriteUInt32(0);
            writer.WriteByte((byte)slotType);
            count++;
        }

        byte[] bytes = writer.ToArray();
        bytes[countAt] = count;
        return bytes;
    }

    /// <summary>SMSG_LOOT_RELEASE_RESPONSE: u64 guid, u8 1 (closes the window; also the refusal reply).</summary>
    public static byte[] ReleaseResponse(ObjectGuid source)
    {
        var writer = new PacketWriter(9);
        writer.WriteUInt64(source.Value);
        writer.WriteByte(1);
        return writer.ToArray();
    }

    /// <summary>SMSG_LOOT_REMOVED: u8 slot.</summary>
    public static byte[] Removed(byte slot) => [slot];

    /// <summary>SMSG_LOOT_MONEY_NOTIFY: u32 this player's share.</summary>
    public static byte[] MoneyNotify(uint amount)
    {
        var writer = new PacketWriter(4);
        writer.WriteUInt32(amount);
        return writer.ToArray();
    }
}
