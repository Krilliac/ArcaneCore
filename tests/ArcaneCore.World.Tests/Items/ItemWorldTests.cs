using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests.Items;

/// <summary>Items end to end over loopback: starting outfit, character list, login creates, inventory opcodes, item query, persistence.</summary>
public sealed class ItemWorldTests
{
    private const string Account = "ITEMS1";
    private const string Name = "Packrat";

    [Fact]
    public async Task NewCharacter_GetsTheStartingOutfit_AndTheCharacterListShowsIt()
    {
        ItemTestContent content = Content();
        await using WorldTestHost host = Start(content);
        byte[] key = await host.AddAccountAsync(Account);
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync(Account, key);
        await client.CreateCharacterAsync(Name);

        IReadOnlyList<InventoryItemData> stored = content.Items.Get(1);
        Assert.Equal(7, stored.Count);
        Assert.Contains(stored, r => r.Slot == InventorySlots.MainHand && r.Item.Entry == 25);
        Assert.Contains(stored, r => r.Slot == InventorySlots.ItemStart && r.Item.Entry == 117 && r.Item.Count == 4);

        await client.SendAsync(WorldOpcode.CmsgCharEnum, []);
        byte[] list = await client.ReadUntilAsync(WorldOpcode.SmsgCharEnum);
        (uint Display, byte Type)[] equipment = ReadEnumEquipment(list);
        Assert.Equal((1542u, (byte)21), equipment[InventorySlots.MainHand]);
        Assert.Equal((18730u, (byte)14), equipment[InventorySlots.OffHand]);
        Assert.Equal((9891u, (byte)4), equipment[InventorySlots.Body]);
        Assert.Equal((0u, (byte)0), equipment[InventorySlots.Head]);
    }

    [Fact]
    public async Task Login_SendsTheItemsBeforeTheSelfCreate()
    {
        ItemTestContent content = Content();
        await using WorldTestHost host = Start(content);
        await using WorldTestClient client = await host.EnterWorldAsync(Account, Name);

        byte[] update = client.LoginPacket(WorldOpcode.SmsgUpdateObject);
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(update)); // 7 items + the player
        Assert.Equal((byte)ObjectUpdateType.CreateObject, update[5]);
        ulong first = ReadPackedGuid(update, 6);
        Assert.Equal(0x4000_0000u, (uint)(first >> 32)); // HIGHGUID_ITEM
        Assert.Equal(7, await host.PlayerStateAsync(Name, p => p.Inventory.AllItems.Count()));
    }

    [Fact]
    public async Task InventoryOpcodes_SwapEquipDestroy_AndThePlayerKeepsThemAfterRelog()
    {
        ItemTestContent content = Content();
        await using WorldTestHost host = Start(content);
        byte[] key = await host.AddAccountAsync(Account);
        await using (WorldTestClient client = await host.ConnectAsync())
        {
            await client.AuthenticateAsync(Account, key);
            await client.CreateCharacterAsync(Name);
            await client.LoginAsync(1);

            // CMSG_SWAP_INV_ITEM: jerky 23 → 30.
            await client.SendAsync(WorldOpcode.CmsgSwapInvItem, [InventorySlots.ItemStart, 30]);
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(Name)!.Inventory.GetItem(InventorySlots.Bag0, 30)?.Entry == 117, "jerky moved");

            // CMSG_SWAP_ITEM: sword off to slot 25, CMSG_AUTOEQUIP_ITEM: back on.
            await client.SendAsync(WorldOpcode.CmsgSwapItem, [InventorySlots.Bag0, 25, InventorySlots.Bag0, InventorySlots.MainHand]);
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(Name)!.Inventory.GetItem(InventorySlots.Bag0, 25)?.Entry == 25, "sword unequipped");
            await client.SendAsync(WorldOpcode.CmsgAutoequipItem, [InventorySlots.Bag0, 25]);
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(Name)!.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.MainHand)?.Entry == 25, "sword equipped");

            // A shirt does not go into the main hand.
            await client.SendAsync(WorldOpcode.CmsgSwapInvItem, [InventorySlots.Body, InventorySlots.MainHand]);
            byte[] failure = await client.ReadUntilAsync(WorldOpcode.SmsgInventoryChangeFailure);
            Assert.Equal((byte)InventoryResult.ItemCantBeEquipped, failure[0]);

            // CMSG_SPLIT_ITEM 2 of the jerky, CMSG_DESTROYITEM the hearthstone.
            await client.SendAsync(WorldOpcode.CmsgSplitItem, [InventorySlots.Bag0, 30, InventorySlots.Bag0, 31, 2]);
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(Name)!.Inventory.GetItem(InventorySlots.Bag0, 31)?.Count == 2, "jerky split");
            ulong stone = await host.PlayerStateAsync(Name, p => p.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.ItemStart + 1)!.Guid.Value);
            await client.SendAsync(WorldOpcode.CmsgDestroyitem, [InventorySlots.Bag0, InventorySlots.ItemStart + 1, 0, 0, 0, 0]);
            byte[] destroyed = await client.ReadUntilAsync(WorldOpcode.SmsgDestroyObject);
            Assert.Equal(stone, BinaryPrimitives.ReadUInt64LittleEndian(destroyed));
        }

        // Disconnect saves; the next login loads what was saved.
        await WorldTestHost.WaitForAsync(() => host.World.OnlinePlayerCount == 0, "player saved and removed");
        await WorldTestHost.WaitForAsync(() => host.SaveQueue.Pending == 0, "the save");
        IReadOnlyList<InventoryItemData> saved = content.Items.Get(1);
        Assert.Equal(7, saved.Count);
        Assert.Contains(saved, r => r.Slot == 30 && r.Item.Entry == 117 && r.Item.Count == 2);
        Assert.Contains(saved, r => r.Slot == 31 && r.Item.Entry == 117 && r.Item.Count == 2);
        Assert.DoesNotContain(saved, r => r.Item.Entry == 6948);

        await using (WorldTestClient client = await host.ConnectAsync())
        {
            await client.AuthenticateAsync(Account, key);
            await client.LoginAsync(1);
            Assert.Equal(4u, await host.PlayerStateAsync(Name, p => p.Inventory.GetItemCount(117)));
        }
    }

    [Fact]
    public async Task ItemQuery_AnswersFromTheContent()
    {
        ItemTestContent content = Content();
        await using WorldTestHost host = Start(content);
        await using WorldTestClient client = await host.EnterWorldAsync(Account, Name);

        await client.SendAsync(WorldOpcode.CmsgItemQuerySingle, [25, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        byte[] known = await client.ReadUntilAsync(WorldOpcode.SmsgItemQuerySingleResponse);
        Assert.Equal(25u, BinaryPrimitives.ReadUInt32LittleEndian(known));
        Assert.Equal("Worn Shortsword", System.Text.Encoding.UTF8.GetString(known, 12, "Worn Shortsword".Length));

        await client.SendAsync(WorldOpcode.CmsgItemQuerySingle, [0x3F, 0x42, 0x0F, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        byte[] unknown = await client.ReadUntilAsync(WorldOpcode.SmsgItemQuerySingleResponse);
        Assert.Equal(999999u | 0x80000000u, BinaryPrimitives.ReadUInt32LittleEndian(unknown));
    }

    private static WorldTestHost Start(ItemTestContent content)
    {
        using (content.Use())
        {
            return WorldTestHost.Start();
        }
    }

    /// <summary>The vmangos human warrior outfit (playercreateinfo_item race 1, class 1).</summary>
    private static ItemTestContent Content()
    {
        var content = new ItemTestContent();
        content.Templates.Templates.AddRange(
        [
            new ItemTemplate { Entry = 25, Class = 2, SubClass = 7, Name = "Worn Shortsword", DisplayId = 1542, Quality = 1, InventoryType = 21, Delay = 1900, MaxDurability = 20, Damages = [new ItemDamage(1, 3, 0)] },
            new ItemTemplate { Entry = 38, Class = 4, SubClass = 0, Name = "Recruit's Shirt", DisplayId = 9891, Quality = 1, InventoryType = 4 },
            new ItemTemplate { Entry = 39, Class = 4, SubClass = 1, Name = "Recruit's Pants", DisplayId = 9892, Quality = 1, InventoryType = 7, Armor = 2, MaxDurability = 25 },
            new ItemTemplate { Entry = 40, Class = 4, SubClass = 1, Name = "Recruit's Boots", DisplayId = 10141, Quality = 1, InventoryType = 8, Armor = 1, MaxDurability = 16 },
            new ItemTemplate { Entry = 117, Class = 0, SubClass = 0, Name = "Tough Jerky", DisplayId = 2473, Quality = 1, Stackable = 20 },
            new ItemTemplate { Entry = 2362, Class = 4, SubClass = 6, Name = "Worn Wooden Shield", DisplayId = 18730, InventoryType = 14, Armor = 5, MaxDurability = 20 },
            new ItemTemplate { Entry = 6948, Class = 15, SubClass = 0, Name = "Hearthstone", DisplayId = 6418, Quality = 1, Bonding = 1 },
        ]);
        content.Templates.StartingItems.AddRange(
        [
            new StartingItem(1, 1, 25, 1), new StartingItem(1, 1, 38, 1), new StartingItem(1, 1, 39, 1), new StartingItem(1, 1, 40, 1),
            new StartingItem(1, 1, 117, 4), new StartingItem(1, 1, 2362, 1), new StartingItem(1, 1, 6948, 1),
        ]);
        return content;
    }

    /// <summary>SMSG_CHAR_ENUM with one character: the 20 (display id, inventory type) pairs at the end.</summary>
    private static (uint, byte)[] ReadEnumEquipment(byte[] payload)
    {
        Assert.Equal(1, payload[0]);
        int start = payload.Length - (20 * 5);
        return Enumerable.Range(0, 20)
            .Select(i => (BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(start + (i * 5))), payload[start + (i * 5) + 4]))
            .ToArray();
    }

    private static ulong ReadPackedGuid(byte[] data, int offset)
    {
        byte mask = data[offset++];
        ulong guid = 0;
        for (int i = 0; i < 8; i++)
        {
            if ((mask & (1 << i)) != 0)
            {
                guid |= (ulong)data[offset++] << (i * 8);
            }
        }

        return guid;
    }
}
