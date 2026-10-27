using System.Buffers.Binary;
using System.Collections.Concurrent;
using ArcaneCore.Game;
using ArcaneCore.Game.AntiCheat;
using ArcaneCore.Game.Npc;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.World.AntiCheat;
using Microsoft.Extensions.DependencyInjection;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.MockClient.Tests.EconomyMailSendParityTests;

namespace ArcaneCore.MockClient.Tests;

/// <summary>
/// Two real players trade. The first to accept saw a stack of a given size in the window; a later split, partial destroy
/// or merge of that stack changes its count without any TradeNegotiated, so the partner would settle for less than the
/// accepter saw. vmangos refuses Player::SplitItem on an item in the trade window (Player.cpp:10996-11006, silent return).
/// The rest is a deliberate deviation: vmangos sets Item::IsInTrade only while a trade completes, so it allows swapping,
/// destroying, auto-equipping and auto-storing an offered item (and selling part of it); here those are refused with
/// ItemLocked (SellResult.CantSellItem for a vendor sale) and scored against the anticheat when the offered item is the
/// source. A destination that holds an offered item is refused but not scored, because vmangos never checks the
/// destination and the client may let such a drop through.
/// </summary>
public sealed class TradeOfferedItemMoveTests
{
    private const uint StackEntry = 992801;

    [Fact]
    public async Task SplitOfOfferedStack_AfterFirstAccept_LeavesTheSeenCountToTheRecipient()
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true);
        (byte slot, Item offered) = await StartOfferAsync(rig, [10]);
        byte free = await FreeSlotAsync(rig);
        await OpenAndOfferAsync(rig, slot);
        await AcceptAsync(rig);

        await RefusedAsync(rig, WorldOpcode.CmsgSplitItem, [InventorySlots.Bag0, slot, InventorySlots.Bag0, free, 4], scored: true);

        await AssertSenderAsync(rig, offered.Guid, expectedCount: 10, expectedTotal: 10);
        await CompleteAsync(rig);
        Assert.Equal(10u, await ReceiverCountAsync(rig));
    }

    [Fact]
    public async Task SplitOntoOfferedStack_IsRefusedWithoutScoring()
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true);
        (byte offeredSlot, Item offered) = await StartOfferAsync(rig, [5, 20], offerLast: true);
        byte otherSlot = await OtherStackSlotAsync(rig, offered.Guid);
        await OpenAndOfferAsync(rig, offeredSlot);
        await AcceptAsync(rig);

        await RefusedAsync(rig, WorldOpcode.CmsgSplitItem, [InventorySlots.Bag0, otherSlot, InventorySlots.Bag0, offeredSlot, 6], scored: false);

        await AssertSenderAsync(rig, offered.Guid, expectedCount: 5, expectedTotal: 25);
        await CompleteAsync(rig);
        Assert.Equal(5u, await ReceiverCountAsync(rig));
    }

    [Fact]
    public async Task SwapOfOfferedItemItself_AfterFirstAccept_IsRefused()
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true);
        (byte slot, Item offered) = await StartOfferAsync(rig, [10]);
        byte free = await FreeSlotAsync(rig);
        await OpenAndOfferAsync(rig, slot);
        await AcceptAsync(rig);

        // CMSG_SWAP_ITEM: dst bag, dst slot, src bag, src slot. The offered item is the one dragged.
        await RefusedAsync(rig, WorldOpcode.CmsgSwapItem, [InventorySlots.Bag0, free, InventorySlots.Bag0, slot], scored: true);

        await AssertOfferedStaysAsync(rig, offered.Guid, slot);
        await CompleteAsync(rig);
        Assert.Equal(10u, await ReceiverCountAsync(rig));
    }

    [Fact]
    public async Task SwapInvItem_OfOfferedItem_AfterFirstAccept_IsRefused()
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true);
        (byte slot, Item offered) = await StartOfferAsync(rig, [10]);
        byte free = await FreeSlotAsync(rig);
        await OpenAndOfferAsync(rig, slot);
        await AcceptAsync(rig);

        await RefusedAsync(rig, WorldOpcode.CmsgSwapInvItem, [slot, free], scored: true);

        await AssertOfferedStaysAsync(rig, offered.Guid, slot);
        await CompleteAsync(rig);
        Assert.Equal(10u, await ReceiverCountAsync(rig));
    }

    [Fact]
    public async Task SwapInvItem_OntoOfferedItem_IsRefusedWithoutScoring()
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true);
        (byte offeredSlot, Item offered) = await StartOfferAsync(rig, [5, 20], offerLast: true);
        byte otherSlot = await OtherStackSlotAsync(rig, offered.Guid);
        await OpenAndOfferAsync(rig, offeredSlot);
        await AcceptAsync(rig);

        await RefusedAsync(rig, WorldOpcode.CmsgSwapInvItem, [otherSlot, offeredSlot], scored: false);

        await AssertSenderAsync(rig, offered.Guid, expectedCount: 5, expectedTotal: 25);
        await CompleteAsync(rig);
        Assert.Equal(5u, await ReceiverCountAsync(rig));
    }

    [Fact]
    public async Task AutoEquipOfOfferedItem_AfterFirstAccept_IsRefused()
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true);
        (byte slot, Item offered) = await StartOfferAsync(rig, [10]);
        await OpenAndOfferAsync(rig, slot);
        await AcceptAsync(rig);

        await RefusedAsync(rig, WorldOpcode.CmsgAutoequipItem, [InventorySlots.Bag0, slot], scored: true);

        await AssertOfferedStaysAsync(rig, offered.Guid, slot);
        await CompleteAsync(rig);
        Assert.Equal(10u, await ReceiverCountAsync(rig));
    }

    [Fact]
    public async Task AutoStoreBagItemOfOfferedItem_AfterFirstAccept_IsRefused()
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true);
        (byte slot, Item offered) = await StartOfferAsync(rig, [10]);
        await OpenAndOfferAsync(rig, slot);
        await AcceptAsync(rig);

        await RefusedAsync(rig, WorldOpcode.CmsgAutostoreBagItem, [InventorySlots.Bag0, slot, InventorySlots.Bag0], scored: true);

        await AssertOfferedStaysAsync(rig, offered.Guid, slot);
        await CompleteAsync(rig);
        Assert.Equal(10u, await ReceiverCountAsync(rig));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(10)]
    public async Task VendorSaleOfOfferedStack_AfterFirstAccept_IsRefusedAndLeavesTheSeenCount(byte count)
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true);
        (byte slot, Item offered) = await StartOfferAsync(rig, [10]);
        await OpenAndOfferAsync(rig, slot);
        await AcceptAsync(rig);

        // The guard runs before the vendor lookup, so the vendor guid is irrelevant; without it the reply would be CantFindVendor (3).
        byte[] payload = new byte[17];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, SyntheticArcaneServer.NpcGuid);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(8), offered.Guid.Value);
        payload[16] = count;
        await RefusedAsync(rig, WorldOpcode.CmsgSellItem, payload, scored: true, WorldOpcode.SmsgSellItem, resultIndex: 16, expectedResult: (byte)SellResult.CantSellItem);

        await AssertSenderAsync(rig, offered.Guid, expectedCount: 10, expectedTotal: 10);
        await CompleteAsync(rig);
        Assert.Equal(10u, await ReceiverCountAsync(rig));
    }

    [Fact]
    public async Task PartialDestroyOfOfferedStack_AfterFirstAccept_LeavesTheSeenCountToTheRecipient()
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true);
        (byte slot, Item offered) = await StartOfferAsync(rig, [10]);
        await OpenAndOfferAsync(rig, slot);
        await AcceptAsync(rig);

        await RefusedAsync(rig, WorldOpcode.CmsgDestroyitem, [InventorySlots.Bag0, slot, 3, 0, 0, 0], scored: true);

        await AssertSenderAsync(rig, offered.Guid, expectedCount: 10, expectedTotal: 10);
        await CompleteAsync(rig);
        Assert.Equal(10u, await ReceiverCountAsync(rig));
    }

    [Fact]
    public async Task MergeIntoOfferedStack_AfterFirstAccept_LeavesTheSeenCountToTheRecipient()
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true);
        // 5 then 20 of a 20-stack: the first stack is topped up to 20 and the remaining 5 start a second stack, which is offered.
        (byte offeredSlot, Item offered) = await StartOfferAsync(rig, [5, 20], offerLast: true);
        byte fullSlot = await OtherStackSlotAsync(rig, offered.Guid);
        await OpenAndOfferAsync(rig, offeredSlot);
        await AcceptAsync(rig);

        // CMSG_SWAP_ITEM: dst bag, dst slot, src bag, src slot. The full stack is dragged onto the offered one.
        await RefusedAsync(rig, WorldOpcode.CmsgSwapItem, [InventorySlots.Bag0, offeredSlot, InventorySlots.Bag0, fullSlot], scored: false);

        await AssertSenderAsync(rig, offered.Guid, expectedCount: 5, expectedTotal: 25);
        await CompleteAsync(rig);
        Assert.Equal(5u, await ReceiverCountAsync(rig));
    }

    [Fact]
    public async Task SplitOfAnotherStack_WhileSomethingIsOffered_StillWorks()
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true);
        (byte offeredSlot, Item offered) = await StartOfferAsync(rig, [5, 20], offerLast: true);
        byte otherSlot = await OtherStackSlotAsync(rig, offered.Guid);
        byte free = await FreeSlotAsync(rig);
        await OpenAndOfferAsync(rig, offeredSlot);
        await AcceptAsync(rig);

        await rig.Sender.SendAsync(WorldOpcode.CmsgSplitItem, [InventorySlots.Bag0, otherSlot, InventorySlots.Bag0, free, 6], rig.Token);
        await SyncAsync(rig);

        await rig.Server.World.InvokeAsync(() =>
        {
            Player sender = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!;
            Assert.Equal(14u, sender.Inventory.GetItem(InventorySlots.Bag0, otherSlot)!.Count);
            Assert.Equal(6u, sender.Inventory.GetItem(InventorySlots.Bag0, free)!.Count);
            Assert.Equal(5u, sender.Inventory.GetItemByGuid(offered.Guid)!.Count);
            return true;
        }).WaitAsync(rig.Token);
        await CompleteAsync(rig);
        Assert.Equal(5u, await ReceiverCountAsync(rig));
    }

    [Fact]
    public async Task AutoStoreBagItemOfAnotherStack_CannotTopUpTheOfferedStack()
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true);
        (byte offeredSlot, Item offered, byte sourceSlot) = await StartTopUpAsync(rig);
        await OpenAndOfferAsync(rig, offeredSlot);
        await AcceptAsync(rig);

        // The 5-stack is not offered, but the first stack with room is: storing it would raise the offered count from 17 to 20.
        await RefusedAsync(rig, WorldOpcode.CmsgAutostoreBagItem, [InventorySlots.Bag0, sourceSlot, InventorySlots.Bag0], scored: false);

        await AssertSenderAsync(rig, offered.Guid, expectedCount: 17, expectedTotal: 25);
        await CompleteAsync(rig);
        Assert.Equal(17u, await ReceiverCountAsync(rig));
    }

    [Fact]
    public async Task SplitToAutomaticSlot_CannotTopUpTheOfferedStack()
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true);
        (byte offeredSlot, Item offered, byte sourceSlot) = await StartTopUpAsync(rig);
        await OpenAndOfferAsync(rig, offeredSlot);
        await AcceptAsync(rig);

        await RefusedAsync(rig, WorldOpcode.CmsgSplitItem, [InventorySlots.Bag0, sourceSlot, InventorySlots.Bag0, InventorySlots.NullSlot, 2], scored: false);

        await AssertSenderAsync(rig, offered.Guid, expectedCount: 17, expectedTotal: 25);
        await CompleteAsync(rig);
        Assert.Equal(17u, await ReceiverCountAsync(rig));
    }

    [Fact]
    public async Task WrapWithOfferedPaper_AfterFirstAccept_IsRefusedAndKeepsThePaperCount()
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true);
        (Item paper, Item target) = await StartWrapAsync(rig, wrapFirst: false);
        await OpenAndOfferAsync(rig, paper.Slot);
        await AcceptAsync(rig);

        await RefusedAsync(rig, WorldOpcode.CmsgWrapItem, [InventorySlots.Bag0, paper.Slot, InventorySlots.Bag0, target.Slot], scored: false);

        await AssertEntryAndCountAsync(rig, target.Guid, PlainEntry, 1);
        await AssertEntryAndCountAsync(rig, paper.Guid, PaperEntry, 3);
        await CompleteAsync(rig);
        Assert.Equal(3u, await ReceiverCountAsync(rig, PaperEntry));
    }

    [Fact]
    public async Task WrapOfOfferedItem_AfterFirstAccept_IsRefusedAndKeepsTheEntry()
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true);
        (Item paper, Item target) = await StartWrapAsync(rig, wrapFirst: false);
        await OpenAndOfferAsync(rig, target.Slot);
        await AcceptAsync(rig);

        await RefusedAsync(rig, WorldOpcode.CmsgWrapItem, [InventorySlots.Bag0, paper.Slot, InventorySlots.Bag0, target.Slot], scored: false);

        await AssertEntryAndCountAsync(rig, target.Guid, PlainEntry, 1);
        await AssertEntryAndCountAsync(rig, paper.Guid, PaperEntry, 3);
        await CompleteAsync(rig);
        Assert.Equal(1u, await ReceiverCountAsync(rig, PlainEntry));
    }

    [Fact]
    public async Task OpenOfOfferedGift_AfterFirstAccept_IsRefusedAndStaysWrapped()
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true);
        (Item paper, Item gift) = await StartWrapAsync(rig, wrapFirst: true);
        await OpenAndOfferAsync(rig, gift.Slot);
        await AcceptAsync(rig);

        await RefusedAsync(rig, WorldOpcode.CmsgOpenItem, [InventorySlots.Bag0, gift.Slot], scored: false);

        await AssertEntryAndCountAsync(rig, gift.Guid, GiftEntry, 1);
        await CompleteAsync(rig);
        Assert.Equal(1u, await ReceiverCountAsync(rig, GiftEntry));
    }

    [Fact]
    public async Task AutoStoreBankItemOfABankStack_CannotTopUpTheOfferedStack()
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true);
        (byte offeredSlot, Item offered, byte sourceSlot) = await StartTopUpAsync(rig);
        byte bankSlot = await MoveToBankAsync(rig, sourceSlot);
        await OpenAndOfferAsync(rig, offeredSlot);
        await AcceptAsync(rig);

        // The bank 5-stack is not offered, but CanStoreItem(NullBag, NullSlot) picks the offered 17-stack first: 17 -> 20.
        await RefusedAsync(rig, WorldOpcode.CmsgAutostoreBankItem, [InventorySlots.Bag0, bankSlot], scored: false);

        await AssertSenderAsync(rig, offered.Guid, expectedCount: 17, expectedTotal: 20); // the 5-stack stays in the bank, which GetItemCount does not count
        await CompleteAsync(rig);
        Assert.Equal(17u, await ReceiverCountAsync(rig));
    }

    [Fact]
    public async Task AutoBankItemOfOfferedItem_AfterFirstAccept_IsRefused()
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true);
        (byte slot, Item offered) = await StartOfferAsync(rig, [10]);
        await OpenBankAsync(rig);
        await OpenAndOfferAsync(rig, slot);
        await AcceptAsync(rig);

        await RefusedAsync(rig, WorldOpcode.CmsgAutobankItem, [InventorySlots.Bag0, slot], scored: true);

        await AssertOfferedStaysAsync(rig, offered.Guid, slot);
        await CompleteAsync(rig);
        Assert.Equal(10u, await ReceiverCountAsync(rig));
    }

    [Fact]
    public async Task AutoStoreBankItemOfOfferedItem_AfterFirstAccept_IsRefused()
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true);
        (byte slot, Item offered) = await StartOfferAsync(rig, [10]);
        await OpenBankAsync(rig);
        await OpenAndOfferAsync(rig, slot);
        await AcceptAsync(rig);

        await RefusedAsync(rig, WorldOpcode.CmsgAutostoreBankItem, [InventorySlots.Bag0, slot], scored: true);

        await AssertOfferedStaysAsync(rig, offered.Guid, slot);
        await CompleteAsync(rig);
        Assert.Equal(10u, await ReceiverCountAsync(rig));
    }

    [Fact]
    public async Task AutoEquipItemSlotOfOfferedItem_AfterFirstAccept_IsRefused()
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true);
        (byte slot, Item offered) = await StartOfferAsync(rig, [10]);
        await OpenAndOfferAsync(rig, slot);
        await AcceptAsync(rig);

        // CMSG_AUTOEQUIP_ITEM_SLOT: u64 item guid, u8 equipment slot (4 = chest).
        byte[] payload = new byte[9];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, offered.Guid.Value);
        payload[8] = 4;
        await RefusedAsync(rig, WorldOpcode.CmsgAutoequipItemSlot, payload, scored: true);

        await AssertOfferedStaysAsync(rig, offered.Guid, slot);
        await CompleteAsync(rig);
        Assert.Equal(10u, await ReceiverCountAsync(rig));
    }

    private static async Task OpenBankAsync(Rig rig)
        => await rig.Server.World.InvokeAsync(() =>
        {
            rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!.Inventory.CanUseBank = static () => true;
            return true;
        }).WaitAsync(rig.Token);

    /// <summary>Open the bank and move the stack at the backpack slot into the first bank slot; returns that bank slot.</summary>
    private static async Task<byte> MoveToBankAsync(Rig rig, byte slot)
    {
        await OpenBankAsync(rig);
        return await rig.Server.World.InvokeAsync(() =>
        {
            Player sender = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!;
            sender.Inventory.SwapItem(InventorySlots.Bag0, slot, InventorySlots.Bag0, InventorySlots.BankItemStart);
            Assert.NotNull(sender.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.BankItemStart));
            return InventorySlots.BankItemStart;
        }).WaitAsync(rig.Token);
    }

    /// <summary>
    /// Stacks A (17, room for 3, offered later), B (5) and C (3): B is not offered and would top A up. Built straight on the
    /// world thread before the trade opens.
    /// </summary>
    private static async Task<(byte OfferedSlot, Item Offered, byte SourceSlot)> StartTopUpAsync(Rig rig)
    {
        (byte slot, Item offered) = await StartOfferAsync(rig, [20, 5]);
        byte free = await FreeSlotAsync(rig);
        byte source = await rig.Server.World.InvokeAsync(() =>
        {
            Player sender = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!;
            sender.Inventory.SplitItem(InventorySlots.Bag0, slot, InventorySlots.Bag0, free, 3);
            Assert.Equal(17u, offered.Count);
            return Stacks(sender).Single(i => i.Count == 5).Slot;
        }).WaitAsync(rig.Token);
        return (slot, offered, source);
    }

    private const uint PaperEntry = 992811;
    private const uint PlainEntry = 992812;
    private const uint GiftEntry = 992813;

    /// <summary>A stack of 3 wrapping paper and one plain item; with <paramref name="wrapFirst"/> the item is already wrapped (the returned item is then the gift).</summary>
    private static async Task<(Item Paper, Item Target)> StartWrapAsync(Rig rig, bool wrapFirst)
        => await rig.Server.World.InvokeAsync(() =>
        {
            Player sender = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!;
            Player receiver = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.ReceiverGuid))!;
            var templates = new ItemTemplateStore(
            [
                new ItemTemplate { Entry = PaperEntry, Class = 0, Stackable = 20, Flags = (uint)ItemTemplateFlags.Wrapper, WrappedGift = GiftEntry },
                new ItemTemplate { Entry = PlainEntry, Class = 0, Stackable = 1 },
                new ItemTemplate { Entry = GiftEntry, Class = 0, Stackable = 1 },
            ], []);
            sender.Inventory.Templates = templates;
            receiver.Inventory.Templates = templates;
            Assert.Equal(InventoryResult.Ok, sender.Inventory.AddItem(PaperEntry, 3, out _));
            Assert.Equal(InventoryResult.Ok, sender.Inventory.AddItem(PlainEntry, 1, out _));
            Item paper = FindByEntry(sender, PaperEntry);
            Item target = FindByEntry(sender, PlainEntry);
            if (wrapFirst)
            {
                Assert.Equal(InventoryResult.Ok, sender.Inventory.WrapItem(paper.BagSlot, paper.Slot, target.BagSlot, target.Slot));
            }

            return (paper, target);
        }).WaitAsync(rig.Token);

    private static Item FindByEntry(Player player, uint entry)
    {
        for (byte slot = InventorySlots.ItemStart; slot < InventorySlots.ItemEnd; slot++)
        {
            if (player.Inventory.GetItem(InventorySlots.Bag0, slot) is { } item && item.Entry == entry)
            {
                return item;
            }
        }

        throw new InvalidOperationException($"no item {entry}");
    }

    private static async Task AssertEntryAndCountAsync(Rig rig, ObjectGuid guid, uint entry, uint count)
        => await rig.Server.World.InvokeAsync(() =>
        {
            Item item = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!.Inventory.GetItemByGuid(guid)!;
            Assert.Equal(entry, item.Entry);
            Assert.Equal(count, item.Count);
            return true;
        }).WaitAsync(rig.Token);

    /// <summary>Give the sender stacks of <see cref="StackEntry"/> (one AddItem per count) and return the offered stack and its slot.</summary>
    private static async Task<(byte Slot, Item Offered)> StartOfferAsync(Rig rig, uint[] adds, bool offerLast = false)
        => await rig.Server.World.InvokeAsync(() =>
        {
            Player sender = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!;
            Player receiver = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.ReceiverGuid))!;
            var templates = new ItemTemplateStore([new ItemTemplate { Entry = StackEntry, Class = 0, Stackable = 20 }], []);
            sender.Inventory.Templates = templates;
            receiver.Inventory.Templates = templates;
            foreach (uint count in adds)
            {
                Assert.Equal(InventoryResult.Ok, sender.Inventory.AddItem(StackEntry, count, out _));
            }

            List<Item> stacks = Stacks(sender);
            Item pick = offerLast ? stacks[^1] : stacks[0];
            Assert.Equal((uint)adds.Sum(c => (int)c), (uint)stacks.Sum(i => (int)i.Count));
            return (pick.Slot, pick);
        }).WaitAsync(rig.Token);

    private static List<Item> Stacks(Player player)
    {
        var stacks = new List<Item>();
        for (byte slot = InventorySlots.ItemStart; slot < InventorySlots.ItemEnd; slot++)
        {
            if (player.Inventory.GetItem(InventorySlots.Bag0, slot) is { } item && item.Entry == StackEntry)
            {
                stacks.Add(item);
            }
        }

        return stacks;
    }

    private static async Task<byte> FreeSlotAsync(Rig rig)
        => await rig.Server.World.InvokeAsync(() =>
        {
            Player sender = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!;
            for (byte slot = InventorySlots.ItemStart; slot < InventorySlots.ItemEnd; slot++)
            {
                if (sender.Inventory.GetItem(InventorySlots.Bag0, slot) is null)
                {
                    return slot;
                }
            }

            throw new InvalidOperationException("no free backpack slot");
        }).WaitAsync(rig.Token);

    private static async Task<byte> OtherStackSlotAsync(Rig rig, ObjectGuid offered)
        => await rig.Server.World.InvokeAsync(() =>
            Stacks(rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!).Single(i => i.Guid != offered).Slot).WaitAsync(rig.Token);

    private static async Task OpenAndOfferAsync(Rig rig, byte slot)
    {
        await rig.Sender.SendAsync(WorldOpcode.CmsgInitiateTrade, ScenarioWire.Guid(rig.ReceiverGuid), rig.Token);
        await ReadStatusAsync(rig.Receiver!, TradeStatus.BeginTrade, rig.Token);
        await rig.Receiver!.SendAsync(WorldOpcode.CmsgBeginTrade, [], rig.Token);
        await ReadStatusAsync(rig.Sender, TradeStatus.OpenWindow, rig.Token);
        await ReadStatusAsync(rig.Receiver!, TradeStatus.OpenWindow, rig.Token);
        await rig.Sender.SendAsync(WorldOpcode.CmsgSetTradeItem, [0, InventorySlots.Bag0, slot], rig.Token);
        await rig.Receiver!.ReadUntilAsync(WorldOpcode.SmsgTradeStatusExtended, rig.Token);
    }

    /// <summary>The sender accepts first (after the anti-scam delay); the recipient is told.</summary>
    private static async Task AcceptAsync(Rig rig)
    {
        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        await rig.Sender.SendAsync(WorldOpcode.CmsgAcceptTrade, [], rig.Token);
        await ReadStatusAsync(rig.Receiver!, TradeStatus.TradeAccept, rig.Token);
    }

    /// <summary>
    /// The inventory opcodes are queued to the world thread by the session, in order. A second accept right after the first is
    /// bounced by the scam-prevention window without touching the acceptance, so its BACK_TO_TRADE proves the earlier opcode was
    /// queued; a world-thread round trip then proves it ran.
    /// </summary>
    private static async Task SyncAsync(Rig rig)
    {
        await rig.Sender.SendAsync(WorldOpcode.CmsgAcceptTrade, [], rig.Token);
        await ReadStatusAsync(rig.Sender, TradeStatus.BackToTrade, rig.Token);
        await rig.Server.World.InvokeAsync(() => true).WaitAsync(rig.Token);
    }

    /// <summary>
    /// Send an inventory opcode the server must refuse: the sender is told <paramref name="expectedResult"/> (ItemLocked by
    /// default) in <paramref name="reply"/>, and the anticheat records one Item finding of weight 30 when <paramref name="scored"/>
    /// and none otherwise. The reply is read before <see cref="SyncAsync"/>, which would skip past it.
    /// </summary>
    private static async Task RefusedAsync(Rig rig, WorldOpcode opcode, byte[] payload, bool scored,
        WorldOpcode reply = WorldOpcode.SmsgInventoryChangeFailure, int resultIndex = 0, byte expectedResult = (byte)InventoryResult.ItemLocked)
    {
        AntiCheatFeature anticheat = rig.Server.Services.GetRequiredService<AntiCheatFeature>();
        var findings = new ConcurrentQueue<AntiCheatFinding>();
        void Record(Player _, AntiCheatFinding finding, float score, AntiCheatAction action) => findings.Enqueue(finding);
        anticheat.FindingRecorded += Record;
        try
        {
            await rig.Sender.SendAsync(opcode, payload, rig.Token);
            byte[] answer = await rig.Sender.ReadUntilAsync(reply, rig.Token);
            Assert.Equal(expectedResult, answer[resultIndex]);
            await SyncAsync(rig);
        }
        finally
        {
            anticheat.FindingRecorded -= Record;
        }

        if (scored)
        {
            AntiCheatFinding recorded = Assert.Single(findings);
            Assert.Equal(AntiCheatViolation.Item, recorded.Type);
            Assert.Equal(30f, recorded.Weight);
        }
        else
        {
            Assert.Empty(findings);
        }
    }

    private static async Task AssertOfferedStaysAsync(Rig rig, ObjectGuid offered, byte slot)
        => await rig.Server.World.InvokeAsync(() =>
        {
            Player sender = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!;
            Assert.Equal(offered, sender.Inventory.GetItem(InventorySlots.Bag0, slot)?.Guid);
            return true;
        }).WaitAsync(rig.Token);

    private static async Task AssertSenderAsync(Rig rig, ObjectGuid offered, uint expectedCount, uint expectedTotal)
        => await rig.Server.World.InvokeAsync(() =>
        {
            Player sender = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!;
            Assert.Equal(expectedCount, sender.Inventory.GetItemByGuid(offered)!.Count);
            Assert.Equal(expectedTotal, sender.Inventory.GetItemCount(StackEntry));
            return true;
        }).WaitAsync(rig.Token);

    private static async Task CompleteAsync(Rig rig)
    {
        await rig.Receiver!.SendAsync(WorldOpcode.CmsgAcceptTrade, [], rig.Token);
        await ReadStatusAsync(rig.Receiver!, TradeStatus.TradeComplete, rig.Token);
    }

    private static async Task<uint> ReceiverCountAsync(Rig rig, uint entry = StackEntry)
        => await rig.Server.World.InvokeAsync(() =>
            rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.ReceiverGuid))!.Inventory.GetItemCount(entry)).WaitAsync(rig.Token);

    private static async Task ReadStatusAsync(ScenarioConnection connection, TradeStatus status, CancellationToken token)
    {
        while (true)
        {
            byte[] payload = await connection.ReadUntilAsync(WorldOpcode.SmsgTradeStatus, token);
            if (BinaryPrimitives.ReadUInt32LittleEndian(payload) == (uint)status) return;
        }
    }
}
