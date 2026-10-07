using System.Text;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.MockClient.Tests.EconomyMailSendParityTests;

namespace ArcaneCore.MockClient.Tests;

/// <summary>
/// The trade window against vmangos (D:\refs\vmangos\src\game\Handlers\TradeHandler.cpp:240-520 accept,
/// :675-746 set gold/item, D:\refs\vmangos\src\game\Objects\TradeData.cpp:57-140). The notification texts are the
/// classic-db mangos_string rows 801-803. Only SQLite is exercised; no store changed.
/// Two clients act through different sessions, so each step waits for the packet its predecessor causes before the
/// other client acts (the world applies packets of different sessions in arrival order).
/// </summary>
public sealed class EconomyTradeParityTests
{
    private const string NotEnoughGold = "You do not have enough gold";
    private const string NoSlots = "You do not have enough free slots";
    private const string PartnerNoSlots = "Your partner does not have enough free bag slots";

    [Fact]
    public async Task Setting_the_same_gold_twice_only_clears_the_partner_and_refreshes_once()
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true);
        (Recorder a, Recorder b) = await OpenTradeAsync(rig);
        await a.SendAsync(WorldOpcode.CmsgSetTradeGold, ScenarioWire.UInt32(10));
        await a.SendAsync(WorldOpcode.CmsgSetTradeGold, ScenarioWire.UInt32(10));
        await a.SendAsync(WorldOpcode.CmsgCancelTrade, []);

        // First set: partner BACK_TO_TRADE (handler), owner BACK_TO_TRADE, partner BACK_TO_TRADE (SetAccepted), trader view.
        // Second set (unchanged): partner BACK_TO_TRADE only.
        Assert.Equal(["S7", "S7", "EXT", "S7"], await b.UntilCanceledAsync());
        Assert.Equal(["S7"], await a.UntilCanceledAsync());
    }

    [Fact]
    public async Task An_accept_in_the_same_second_as_a_change_is_bounced_and_a_later_one_goes_through()
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true);
        (Recorder a, Recorder b) = await OpenTradeAsync(rig);
        await a.SendAsync(WorldOpcode.CmsgSetTradeGold, ScenarioWire.UInt32(10));
        await b.WaitForAsync(seen => seen.Contains("EXT"));          // the change is applied everywhere
        await b.SendAsync(WorldOpcode.CmsgAcceptTrade, []);          // same second as the change: bounced to B itself
        await b.WaitForAsync(seen => seen.Count(s => s == "S7") == 3);
        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        await b.SendAsync(WorldOpcode.CmsgAcceptTrade, []);          // allowed: A is told TRADE_ACCEPT
        await a.WaitForAsync(seen => seen.Contains("S4"));
        await a.SendAsync(WorldOpcode.CmsgCancelTrade, []);

        // A saw exactly one TRADE_ACCEPT: the bounced accept never reached it.
        Assert.Equal(["S7", "S4"], await a.UntilCanceledAsync());
    }

    [Fact]
    public async Task Not_enough_gold_notifies_the_short_player_and_sends_the_partner_back_to_trade()
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true);
        (Recorder a, Recorder b) = await OpenTradeAsync(rig);
        await a.SendAsync(WorldOpcode.CmsgSetTradeGold, ScenarioWire.UInt32(100));
        await b.WaitForAsync(seen => seen.Contains("EXT"));
        await rig.Server.World.InvokeAsync(() =>
        {
            rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!.Money = 10;
            return true;
        }).WaitAsync(rig.Token);
        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        await a.SendAsync(WorldOpcode.CmsgAcceptTrade, []);
        await a.WaitForAsync(seen => seen.Contains($"N:{NotEnoughGold}"));
        await a.SendAsync(WorldOpcode.CmsgCancelTrade, []);

        Assert.Equal(["S7", $"N:{NotEnoughGold}"], await a.UntilCanceledAsync());
        List<string> partner = await b.UntilCanceledAsync();
        Assert.Equal(["S7", "S7", "EXT", "S7"], partner); // BACK_TO_TRADE for the failed accept; the window was open to cancel
    }

    [Fact]
    public async Task Missing_bag_space_keeps_the_window_open_and_notifies_both_players()
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true);
        (Recorder a, Recorder b) = await OpenTradeAsync(rig);
        (byte bag, byte slot) = await rig.Server.World.InvokeAsync(() =>
        {
            Player sender = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!;
            Assert.Equal(InventoryResult.Ok, sender.Inventory.AddItem(SyntheticArcaneServer.FixedRewardItem, 1, out Item? offered));
            Player receiver = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.ReceiverGuid))!;
            InventoryResult filled = InventoryResult.Ok;
            while (filled == InventoryResult.Ok)
            {
                filled = receiver.Inventory.AddItem(SyntheticArcaneServer.FixedRewardItem, 20, out _);
            }

            return (offered!.BagSlot, offered.Slot);
        }).WaitAsync(rig.Token);
        await a.SendAsync(WorldOpcode.CmsgSetTradeItem, [0, bag, slot]);
        await b.WaitForAsync(seen => seen.Contains("EXT"));
        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        await a.SendAsync(WorldOpcode.CmsgAcceptTrade, []);
        await b.WaitForAsync(seen => seen.Contains("S4"));           // A's accept reached B
        await b.SendAsync(WorldOpcode.CmsgAcceptTrade, []);
        await b.WaitForAsync(seen => seen.Contains($"N:{NoSlots}"));
        await a.SendAsync(WorldOpcode.CmsgCancelTrade, []);          // only possible while the trade is still open

        List<string> receiver = await b.UntilCanceledAsync();
        List<string> sender = await a.UntilCanceledAsync();
        Assert.Contains($"N:{NoSlots}", receiver);
        Assert.Contains($"N:{PartnerNoSlots}", sender);
        Assert.DoesNotContain("S12", receiver.Concat(sender)); // no CLOSE_WINDOW
        Assert.Equal("S7", receiver[^1]);
        Assert.Equal("S7", sender[^1]);
    }

    [Fact]
    public async Task A_traded_stack_merges_into_the_receivers_stack_when_no_slot_is_free()
    {
        // vmangos trade stores the received items with CanStoreItems/StoreItem (TradeHandler.cpp), which fill room in existing
        // stacks: full bags with stack room take the stack, and the traded instance ends inside the receiver's stack.
        const uint Arrow = 992801;
        const uint Filler = 992802;
        await using Rig rig = await Rig.StartAsync(loginReceiver: true);
        (Recorder a, Recorder b) = await OpenTradeAsync(rig);
        (byte bag, byte slot, uint offeredGuid, uint stackGuid) = await rig.Server.World.InvokeAsync(() =>
        {
            Player sender = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!;
            Player receiver = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.ReceiverGuid))!;
            IEnumerable<ArcaneCore.Kernel.Items.ItemTemplate> known = sender.Inventory.Templates is ItemTemplateStore store ? store.All : [];
            var templates = new ItemTemplateStore([.. known,
                new ArcaneCore.Kernel.Items.ItemTemplate { Entry = Arrow, Class = 0, Stackable = 20 },
                new ArcaneCore.Kernel.Items.ItemTemplate { Entry = Filler, Class = 0, Stackable = 1 }]);
            sender.Inventory.Templates = templates;
            receiver.Inventory.Templates = templates;
            Assert.Equal(InventoryResult.Ok, sender.Inventory.AddItem(Arrow, 5, out Item? offered));
            Assert.Equal(InventoryResult.Ok, receiver.Inventory.AddItem(Arrow, 10, out Item? stack));
            while (receiver.Inventory.AddItem(Filler, 1, out _) == InventoryResult.Ok)
            {
            }

            return (offered!.BagSlot, offered.Slot, offered.Guid.Low, stack!.Guid.Low);
        }).WaitAsync(rig.Token);
        await a.SendAsync(WorldOpcode.CmsgSetTradeItem, [0, bag, slot]);
        await b.WaitForAsync(seen => seen.Contains("EXT"));
        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        await a.SendAsync(WorldOpcode.CmsgAcceptTrade, []);
        await b.WaitForAsync(seen => seen.Contains("S4"));
        await b.SendAsync(WorldOpcode.CmsgAcceptTrade, []);
        await a.WaitForAsync(seen => seen.Contains("S8"));            // TRADE_COMPLETE after the commit

        await rig.Server.World.InvokeAsync(() =>
        {
            Player sender = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!;
            Player receiver = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.ReceiverGuid))!;
            Assert.Equal(15u, receiver.Inventory.GetItemByGuid(ObjectGuid.Item(stackGuid))!.Count);
            Assert.Null(receiver.Inventory.GetItemByGuid(ObjectGuid.Item(offeredGuid)));
            Assert.Null(sender.Inventory.GetItemByGuid(ObjectGuid.Item(offeredGuid)));
            Assert.Equal(0u, sender.Inventory.GetItemCount(Arrow));
            return true;
        }).WaitAsync(rig.Token);

        await using AsyncServiceScope scope = rig.Server.Services.CreateAsyncScope();
        CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        IReadOnlyList<ArcaneCore.Kernel.Items.InventoryItemData> stored = await new EfItemStore(db).GetInventoryAsync((int)rig.ReceiverGuid, rig.Token);
        Assert.Equal(15u, stored.Single(row => row.Item.Guid == stackGuid).Item.Count);
        Assert.DoesNotContain(stored, row => row.Item.Guid == offeredGuid);
        Assert.DoesNotContain(await new EfItemStore(db).GetInventoryAsync((int)rig.SenderGuid, rig.Token), row => row.Item.Guid == offeredGuid);
        Assert.False(await db.Set<ItemInstanceRow>().AnyAsync(row => row.Guid == offeredGuid, rig.Token));
    }

    private static async Task<(Recorder Sender, Recorder Receiver)> OpenTradeAsync(Rig rig)
    {
        var a = new Recorder(rig.Sender, rig.Token);
        var b = new Recorder(rig.Receiver!, rig.Token);
        await a.SendAsync(WorldOpcode.CmsgInitiateTrade, ScenarioWire.Guid(rig.ReceiverGuid));
        await b.WaitForAsync(seen => seen.Contains("S1"));
        await b.SendAsync(WorldOpcode.CmsgBeginTrade, []);
        await b.WaitForAsync(seen => seen.Contains("S2"));
        await a.WaitForAsync(seen => seen.Contains("S2"));
        a.Forget();
        b.Forget();
        return (a, b);
    }

    /// <summary>The trade packets one client receives, in order: "S&lt;status&gt;", "EXT", "N:&lt;text&gt;".</summary>
    private sealed class Recorder(ScenarioConnection connection, CancellationToken token)
    {
        private readonly List<string> _seen = [];

        public Task SendAsync(WorldOpcode opcode, byte[] payload) => connection.SendAsync(opcode, payload, token);

        public void Forget() => _seen.Clear();

        public async Task WaitForAsync(Func<List<string>, bool> done)
        {
            while (!done(_seen))
            {
                await ReadOneAsync();
            }
        }

        /// <summary>Everything received before the TRADE_CANCELED that ends the scenario (the cancel itself is consumed).</summary>
        public async Task<List<string>> UntilCanceledAsync()
        {
            while (true)
            {
                if (await ReadOneAsync() == "S3")
                {
                    _seen.RemoveAt(_seen.Count - 1);
                    return [.. _seen];
                }
            }
        }

        private async Task<string?> ReadOneAsync()
        {
            WorldFrame frame = await connection.ReadAsync(token);
            string? entry = (WorldOpcode)frame.Opcode switch
            {
                WorldOpcode.SmsgTradeStatus => "S" + new PacketReader(frame.Payload).ReadUInt32(),
                WorldOpcode.SmsgTradeStatusExtended => "EXT",
                WorldOpcode.SmsgNotification => "N:" + Encoding.UTF8.GetString(frame.Payload).TrimEnd('\0'),
                _ => null,
            };
            if (entry is not null)
            {
                _seen.Add(entry);
            }

            return entry;
        }
    }
}
