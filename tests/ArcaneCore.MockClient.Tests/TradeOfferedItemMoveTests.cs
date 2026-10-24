using System.Buffers.Binary;
using ArcaneCore.Game;
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
/// accepter saw. vmangos refuses Player::SplitItem on an item in the trade window (Player.cpp:10996-11006, silent return)
/// and every counting helper skips Item::IsInTrade (Player.cpp:8681-8734).
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

        await rig.Sender.SendAsync(WorldOpcode.CmsgSplitItem, [InventorySlots.Bag0, slot, InventorySlots.Bag0, free, 4], rig.Token);
        await SyncAsync(rig);

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

        await rig.Sender.SendAsync(WorldOpcode.CmsgDestroyitem, [InventorySlots.Bag0, slot, 3, 0, 0, 0], rig.Token);
        await SyncAsync(rig);

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
        await rig.Sender.SendAsync(WorldOpcode.CmsgSwapItem, [InventorySlots.Bag0, offeredSlot, InventorySlots.Bag0, fullSlot], rig.Token);
        await SyncAsync(rig);

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

    private static async Task<uint> ReceiverCountAsync(Rig rig)
        => await rig.Server.World.InvokeAsync(() =>
            rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.ReceiverGuid))!.Inventory.GetItemCount(StackEntry)).WaitAsync(rig.Token);

    private static async Task ReadStatusAsync(ScenarioConnection connection, TradeStatus status, CancellationToken token)
    {
        while (true)
        {
            byte[] payload = await connection.ReadUntilAsync(WorldOpcode.SmsgTradeStatus, token);
            if (BinaryPrimitives.ReadUInt32LittleEndian(payload) == (uint)status) return;
        }
    }
}
