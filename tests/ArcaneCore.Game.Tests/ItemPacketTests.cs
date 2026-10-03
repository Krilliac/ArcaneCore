using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests;

/// <summary>Item packet layouts against gtker/wow_messages 1.12 test vectors and vmangos.</summary>
public sealed class ItemPacketTests
{
    /// <summary>
    /// gtker smsg_item_query_single_response test (item 7230, Smite's Mighty Hammer), payload
    /// after the size and opcode. The five spell blocks are patched from gtker's zeros to the
    /// vmangos/cmangos empty-spell form (0, 0, 0, -1, 0, -1): servers win (docs/areas/items.md).
    /// </summary>
    private const string SmitesMightyHammerHex =
        "3E1C00000200000005000000536D6974652773204D69676874792048616D6D6572000000009A4C000003000000000000009B3C00001F0C000011000000DF050000FF01000017000000120000000000000000000000000000000000000000000000000000000000000000000000010000000000000000000000000000000100000000000000040000000B000000030000000400000007000000000000000500000000000000060000000000000000000000000000000000000000000000000000000000000000005C420000A6420000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000AC0D0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001000000000000000000000000000000000000000000000000020000000100000000000000000000000000000050000000000000000000000000000000";

    [Fact]
    public void ItemQueryResponse_MatchesGtkerVector_WithServerSpellDefaults()
    {
        var template = new ItemTemplate
        {
            Entry = 7230,
            Class = 2,
            SubClass = 5,
            Name = "Smite's Mighty Hammer",
            DisplayId = 19610,
            Quality = 3,
            Flags = 0,
            BuyPrice = 15515,
            SellPrice = 3103,
            InventoryType = 17,
            AllowableClass = 1503,
            AllowableRace = 511,
            ItemLevel = 23,
            RequiredLevel = 18,
            Stackable = 1,
            Stats = [new ItemStat(0, 0), new ItemStat(1, 0), new ItemStat(4, 11), new ItemStat(3, 4), new ItemStat(7, 0), new ItemStat(5, 0), new ItemStat(6, 0)],
            Damages = [new ItemDamage(55, 83, 0)],
            Delay = 3500,
            Bonding = 1,
            Material = 2,
            Sheath = 1,
            MaxDurability = 80,
        }.Normalized();

        byte[] expected = Convert.FromHexString(SmitesMightyHammerHex);
        Assert.Equal(478, expected.Length);
        PatchEmptySpells(expected);

        Assert.Equal(expected, ItemPackets.ItemQueryResponse(template));
    }

    [Fact]
    public void ItemQueryResponse_UnknownEntry_SetsHighBit()
        => Assert.Equal([0x39, 0x30, 0x00, 0x80], ItemPackets.ItemQueryUnknown(12345));

    [Fact]
    public void ItemQueryResponse_ConsumableSubclassIsZero_AndReputationRankNeedsAFaction()
    {
        var food = new ItemTemplate { Entry = 1, Class = 0, SubClass = 5, Name = "x", RequiredReputationRank = 4 }.Normalized();
        byte[] body = ItemPackets.ItemQueryResponse(food);
        Assert.Equal(0u, BitConverter.ToUInt32(body, 8));
        int repRank = 12 + 2 + 3 + (15 * 4) + 4; // entry/class/subclass, name "x\0", 3 empty names, 15 u32 fields, faction
        Assert.Equal(0u, BitConverter.ToUInt32(body, repRank));
    }

    /// <summary>gtker smsg_item_push_result 1.12 test: guid 4, looted, created, shown, bag 0xFF, slot 24, item 12640, count 1.</summary>
    [Fact]
    public void ItemPushResult_MatchesGtkerVector()
    {
        var template = new ItemTemplate { Entry = 0x3160 }.Normalized();
        var item = new Item(1, template, ObjectGuid.Player(4)) { Slot = 0x18 };
        byte[] body = ItemPackets.ItemPushResult(ObjectGuid.Player(4), item, 1, received: false, created: true, showInChat: true);
        Assert.Equal(Convert.FromHexString("0400000000000000" + "00000000" + "01000000" + "01000000" + "FF" + "18000000" + "60310000" + "00000000" + "00000000" + "01000000"), body);
    }

    [Fact]
    public void ItemPushResult_MergedIntoAStack_ReportsNoSlot()
    {
        var item = new Item(1, new ItemTemplate { Entry = 117, Stackable = 20 }.Normalized(), ObjectGuid.Player(4)) { Slot = 23, Count = 5 };
        byte[] body = ItemPackets.ItemPushResult(ObjectGuid.Player(4), item, 2, received: true, created: false, showInChat: true);
        Assert.Equal(0xFFFFFFFFu, BitConverter.ToUInt32(body, 21));
        Assert.Equal(2u, BitConverter.ToUInt32(body, 37));
    }

    [Fact]
    public void InventoryChangeFailure_Layouts()
    {
        Assert.Equal([0], ItemPackets.InventoryChangeFailure(InventoryResult.Ok, ObjectGuid.Item(1), ObjectGuid.Empty));

        byte[] full = ItemPackets.InventoryChangeFailure(InventoryResult.InventoryFull, ObjectGuid.Item(7), ObjectGuid.Item(8), 19);
        Assert.Equal(Convert.FromHexString("32" + "0700000000000040" + "0800000000000040" + "13"), full);

        byte[] level = ItemPackets.InventoryChangeFailure(InventoryResult.CantEquipLevelI, ObjectGuid.Item(7), ObjectGuid.Empty, 0, 40);
        Assert.Equal(Convert.FromHexString("01" + "28000000" + "0700000000000040" + "0000000000000000" + "00"), level);
    }

    /// <summary>Overwrite the five spell blocks (6 x u32 each, after the 20 header u32s, 10 stats, 5 damages, 7 resistances, delay, ammo type and range) with the empty-spell form.</summary>
    private static void PatchEmptySpells(byte[] body)
    {
        int offset = 4 + 4 + 4 + "Smite's Mighty Hammer".Length + 1 + 3 + (20 * 4) + (10 * 8) + (5 * 12) + (7 * 4) + 12;
        for (int spell = 0; spell < 5; spell++)
        {
            int at = offset + (spell * 24);
            BitConverter.TryWriteBytes(body.AsSpan(at + 12), -1);
            BitConverter.TryWriteBytes(body.AsSpan(at + 20), -1);
        }
    }
}
