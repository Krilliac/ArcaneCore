using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Tests.Items;
using ArcaneCore.World.Tests.Playerbots.Party;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// Follow-ups of the trade desk (#1700) and the town vendor spread (#1813), one test per point. The desk and fee tests run the real world with a real player on a socket
/// client; the town-goals tests pin the pure rules the controller's crowd handling and trip handling go through.
/// </summary>
public sealed class PlayerbotTradeDeskFollowupTests
{
    private const uint BuyEntry = 995_001, FillerEntry = 995_002, QuiverEntry = 995_003, ArrowEntry = 995_004, Price = 1000;
    private const string BotName = "Followbot", CustomerName = "Followcustomer";

    private static ItemTemplate Plain(uint entry, string name, uint stackable = 20)
        => new() { Entry = entry, Class = 15, SubClass = 0, Name = name, DisplayId = 1, Quality = 1, SellPrice = Price, Stackable = stackable };

    private static ItemTemplate[] Templates() =>
    [
        Plain(BuyEntry, "Followitem Alpha"),
        Plain(FillerEntry, "Followitem Filler", stackable: 1),
        new() { Entry = QuiverEntry, Class = 11, SubClass = 2, Name = "Followitem Quiver", DisplayId = 1, Quality = 1, InventoryType = 18, ContainerSlots = 4, BagFamily = 1 },
        new() { Entry = ArrowEntry, Class = 6, SubClass = 2, Name = "Followitem Arrow", DisplayId = 1, Quality = 1, InventoryType = 24, SellPrice = Price, Stackable = 20, BagFamily = 1 },
    ];

    private sealed class Scene : IAsyncDisposable
    {
        private Scene(SocialWorld world, Guid bot)
        {
            World = world;
            Bot = bot;
        }

        public SocialWorld World { get; }

        public Guid Bot { get; }

        public static async Task<Scene> StartAsync()
        {
            var items = new ItemTestContent();
            items.Templates.Templates.AddRange(Templates());
            SocialWorld world;
            using (items.Use())
            {
                world = await SocialWorld.StartAsync(
                    options =>
                    {
                        options.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone;
                        options.Chat.Enabled = false;
                        options.Chat.Voice.Trade = false;
                        options.Chat.Voice.LookingForGroup = false;
                        options.Chat.Voice.QuestHelp = false;
                        options.Chat.Voice.RunCalls = false;
                        options.Chat.Voice.BattlegroundCalls = false;
                    },
                    frozen: true,
                    services: services => services.AddScoped<ArcaneCore.Kernel.Economy.IEconomyStore>(_ => new PlayerbotPartyTests.StubEconomyStore()));
            }

            Guid bot = await world.AddBotAsync(BotName, money: 50_000);
            await world.AddPlayerAsync(CustomerName);
            await world.Host.OnWorldAsync(() =>
            {
                HelpTestKit.Online(world, BotName).Inventory.Load([]);
                HelpTestKit.Online(world, CustomerName).Inventory.Load([]);
                HelpTestKit.Online(world, CustomerName).Money = 5000;
                return true;
            });
            return new Scene(world, bot);
        }

        public PlayerbotTradeDesk Desk => PartyTestHost.Feature(World.Host).TradeDesk;

        public Task<PlayerbotDealQuote> QuoteBuyerAsync(uint entry) => World.Host.OnWorldAsync(() =>
            Desk.Quote(Bot, HelpTestKit.Online(World, BotName), HelpTestKit.Online(World, CustomerName), PlayerbotDealRole.Buyer, entry, 1));

        /// <summary>The partner's line, heard with the live bot (what the voice passes).</summary>
        public Task<PlayerbotDealHeard> HearAsync(string text) => World.Host.OnWorldAsync(() =>
            Desk.Hear(Bot, HelpTestKit.Online(World, CustomerName).Guid, text, HelpTestKit.Online(World, BotName)));

        public Task<PlayerbotDealState?> StateAsync() => World.Host.OnWorldAsync(() => Desk.StateOf(Bot, HelpTestKit.Online(World, CustomerName).Guid));

        public Task<int> FillBackpackAsync() => World.Host.OnWorldAsync(() =>
        {
            Player bot = HelpTestKit.Online(World, BotName);
            int n = 0;
            while (n < 200 && bot.Inventory.AddItem(FillerEntry, 1, out _) == InventoryResult.Ok) n++;
            return n;
        });

        public ValueTask DisposeAsync() => World.DisposeAsync();
    }

    /// <summary>
    /// A buyer quoted with room whose bags fill before the partner's "deal" is heard must refuse at the hearing (NoSpace), not agree and then drop the deal at its next tick;
    /// the deal stays quoted and the partner carries no blame, so the same line agrees once there is room. Broken: Hear agreed without the bag check (Agree got no bot).
    /// </summary>
    [Fact]
    public async Task Hear_BuyerWhoseBagsFilledAfterTheQuote_RefusesWithNoSpace_AndBlamesNobody()
    {
        await using Scene scene = await Scene.StartAsync();
        Assert.True((await scene.QuoteBuyerAsync(BuyEntry)).Ok);
        Assert.True(await scene.FillBackpackAsync() > 0);

        PlayerbotDealHeard heard = await scene.HearAsync("deal");
        Assert.Equal(PlayerbotDealHeardKind.Refused, heard.Kind);
        Assert.Equal(PlayerbotDealRefusal.NoSpace, heard.Refusal);
        Assert.Equal(PlayerbotDealState.Quoted, await scene.StateAsync());

        // No blame: no cooldown on the pair, so once the bags have room the very same line agrees.
        await scene.World.Host.OnWorldAsync(() => HelpTestKit.Online(scene.World, BotName).Inventory.DestroyItemCount(FillerEntry, 200));
        PlayerbotDealHeard again = await scene.HearAsync("deal");
        Assert.Equal(PlayerbotDealHeardKind.Agreed, again.Kind);
        Assert.Equal(PlayerbotDealState.Agreed, await scene.StateAsync());
    }

    /// <summary>The counter-offer path has the same hole: an acceptable counter by a buyer with no room is not agreed and the deal stays quoted.</summary>
    [Fact]
    public async Task Hear_BuyerWithFullBags_DoesNotAgreeAnAcceptableCounter()
    {
        await using Scene scene = await Scene.StartAsync();
        PlayerbotDealQuote quote = await scene.QuoteBuyerAsync(BuyEntry);
        Assert.True(quote.Ok);
        Assert.True(await scene.FillBackpackAsync() > 0);

        PlayerbotDealHeard heard = await scene.HearAsync(PlayerbotVoiceFacts.Price(quote.Price));
        Assert.NotEqual(PlayerbotDealHeardKind.CounterAgreed, heard.Kind);
        Assert.NotEqual(PlayerbotDealHeardKind.Agreed, heard.Kind);
        Assert.Equal(PlayerbotDealState.Quoted, await scene.StateAsync());
    }

    /// <summary>
    /// A completed fee window is the proof of payment, not a balance read: money that reached the bot between the settlement and the next Pay think (here more than the fee)
    /// must not make the leg call the fee unpaid and pay the mage again. Broken: the leg compared MoneyBefore with the new balance, found it higher, started over and traded again.
    /// </summary>
    [Fact]
    public async Task MageFee_CompletedWindow_IsPaid_EvenWhenMoneyArrivedBeforeTheNextThink()
    {
        await using Scene scene = await Scene.StartAsync();
        (bool paid, uint paidCopper, TravelLegStep step, MageLegStage stage) = await scene.World.Host.OnWorldAsync(() =>
        {
            Player bot = HelpTestKit.Online(scene.World, BotName);
            Player mage = HelpTestKit.Online(scene.World, CustomerName);
            var leg = new MageLeg(TravelMeans.MagePortal, 0, new TravelPlace(0, Vector3.Zero), 0) { Stage = MageLegStage.Paying, MoneyBefore = bot.Money };
            var request = new PortalRequest(bot.Guid.Value, BotName, bot.Team, 0, Vector3.Zero, 0, 0, Price, 0);
            var window = new TradeSession(bot, mage) { Completed = true };
            window.SideOf(bot).Gold = Price;
            leg.FeeSeen = true;
            leg.FeeTrade = window;
            bot.Money = bot.Money - Price + (5 * Price); // the fee left, and more arrived before this think
            WorldSession session = PartyTestHost.Feature(scene.World.Host).FindSession(scene.Bot)!;
            TravelLegStep result = PlayerbotMageService.Pay(session, bot, leg, request, mage, 100, out _);
            return (request.Paid, leg.PaidCopper, result, leg.Stage);
        });
        Assert.True(paid, "the completed fee window was not recognised as paid");
        Assert.Equal(Price, paidCopper);
        Assert.Equal(TravelLegStep.Traveling, step);
        Assert.Equal(MageLegStage.WaitPortal, stage);
    }

    /// <summary>A fee window that completed with another gold amount than the fee on the bot's side is not the fee: the leg starts over, not paid.</summary>
    [Fact]
    public async Task MageFee_CompletedWindowWithADifferentGold_IsNotPaid()
    {
        await using Scene scene = await Scene.StartAsync();
        (bool paid, bool seen) = await scene.World.Host.OnWorldAsync(() =>
        {
            Player bot = HelpTestKit.Online(scene.World, BotName);
            Player mage = HelpTestKit.Online(scene.World, CustomerName);
            var leg = new MageLeg(TravelMeans.MagePortal, 0, new TravelPlace(0, Vector3.Zero), 0) { Stage = MageLegStage.Paying, MoneyBefore = bot.Money };
            var request = new PortalRequest(bot.Guid.Value, BotName, bot.Team, 0, Vector3.Zero, 0, 0, Price, 0);
            var window = new TradeSession(bot, mage) { Completed = true };
            window.SideOf(bot).Gold = Price - 1;
            leg.FeeSeen = true;
            leg.FeeTrade = window;
            WorldSession session = PartyTestHost.Feature(scene.World.Host).FindSession(scene.Bot)!;
            PlayerbotMageService.Pay(session, bot, leg, request, mage, 100, out _);
            return (request.Paid, leg.FeeSeen);
        });
        Assert.False(paid);
        Assert.False(seen);
    }

    /// <summary>
    /// The quiver and soul bag probe: a buyer whose only free room is a quiver slot takes arrows (the bag check is family-aware, the same CanStoreNewItem the server's staging
    /// uses) and refuses an item the quiver cannot hold. Expected to hold on main too: it pins that the desk's check agrees with the server's family rules.
    /// </summary>
    [Fact]
    public async Task Quote_BuyerWithOnlyAQuiverSlotFree_TakesArrowsAndRefusesOtherGoods()
    {
        await using Scene scene = await Scene.StartAsync();
        await scene.World.Host.OnWorldAsync(() =>
        {
            Player bot = HelpTestKit.Online(scene.World, BotName);
            Assert.Equal(InventoryResult.Ok, bot.Inventory.AddItem(QuiverEntry, 1, out Item? quiver));
            Assert.True(bot.Inventory.AutoEquipItem(quiver!.BagSlot, quiver.Slot));
            return true;
        });
        Assert.True(await scene.FillBackpackAsync() > 0);
        await scene.World.Host.OnWorldAsync(() =>
        {
            Player bot = HelpTestKit.Online(scene.World, BotName);
            Assert.NotEqual(InventoryResult.Ok, bot.Inventory.CanStoreNewItem(FillerEntry, 1, [], out _));
            Assert.Equal(InventoryResult.Ok, bot.Inventory.CanStoreNewItem(ArrowEntry, 1, [], out _));
            return true;
        });

        Assert.Equal(PlayerbotDealRefusal.NoSpace, (await scene.QuoteBuyerAsync(BuyEntry)).Refusal);
        Assert.True((await scene.QuoteBuyerAsync(ArrowEntry)).Ok);
    }

    // --- town goals ---------------------------------------------------------------------------------------------------------------

    /// <summary>The sell-trip exemption: the NPC of the service a trip was set out for is never left out for doing nothing; any other NPC (or no trip) is.</summary>
    [Fact]
    public void TownGoals_TheNpcOfTheWantedTrip_IsNeverLeftOut()
    {
        Assert.False(PlayerbotTownGoals.LeavesNpcOut(NpcFlags.Vendor, NpcFlags.Vendor | NpcFlags.Repair));
        Assert.False(PlayerbotTownGoals.LeavesNpcOut(NpcFlags.Repair, NpcFlags.Repair));
        Assert.True(PlayerbotTownGoals.LeavesNpcOut(NpcFlags.Vendor, NpcFlags.Trainer));
        Assert.True(PlayerbotTownGoals.LeavesNpcOut(NpcFlags.None, NpcFlags.Vendor));
    }

    /// <summary>A trainer that is useful is logged under a reason (before, the nothing-to-do line read "none" for every trainer).</summary>
    [Fact]
    public void TownGoals_TrainerUsefulReasons_AreNamed()
    {
        Assert.False(string.IsNullOrWhiteSpace(PlayerbotTownGoals.TrainerOfferReason));
        Assert.False(string.IsNullOrWhiteSpace(PlayerbotTownGoals.RidingTrainerReason));
        Assert.NotEqual("none", PlayerbotTownGoals.TrainerOfferReason);
        Assert.NotEqual(PlayerbotTownGoals.TrainerOfferReason, PlayerbotTownGoals.RidingTrainerReason);
    }

    /// <summary>The crowd gate is per NPC: firing at one NPC does not make the rule due again at it when the bot returns from another one inside the minute.</summary>
    [Fact]
    public void TownGoals_CrowdGate_RemembersEachNpc()
    {
        var memo = new PlayerbotTownGoals.CrowdSpotMemo();
        ObjectGuid a = ObjectGuid.Item(1), b = ObjectGuid.Item(2);
        uint now = 1000;
        Assert.True(memo.Due(a, now));
        memo.Mark(a, now);
        Assert.False(memo.Due(a, now + 10));
        Assert.True(memo.Due(b, now + 20));
        memo.Mark(b, now + 20);
        Assert.False(memo.Due(a, now + 30), "marking the second NPC forgot the first");
        Assert.False(memo.Due(b, now + 30));
        Assert.True(memo.Due(a, now + PlayerbotTownGoals.CrowdSpotMs + 1));
    }

    /// <summary>The memo is bounded: past its capacity of live entries it is cleared instead of growing.</summary>
    [Fact]
    public void TownGoals_CrowdGate_IsBounded()
    {
        var memo = new PlayerbotTownGoals.CrowdSpotMemo();
        uint overflow = (uint)PlayerbotTownGoals.CrowdSpotCapacity + 2;
        for (uint i = 0; i <= overflow; i++) memo.Mark(ObjectGuid.Item(100 + i), 5000);
        Assert.True(memo.Due(ObjectGuid.Item(100), 5001), "the oldest entry survived a full memo");
        Assert.False(memo.Due(ObjectGuid.Item(100 + overflow), 5001));
    }

    /// <summary>Only a bot crowds the arriving bot, and only on its own floor: a real player on the spot, or a bot a floor above, does not.</summary>
    [Fact]
    public void TownGoals_Crowding_CountsBotsOnTheSameFloorOnly()
    {
        Assert.True(PlayerbotTownGoals.IsCrowding(0.5f, 0.5f, 0.2f, otherIsBot: true, PlayerbotTownGoals.CrowdedYards));
        Assert.False(PlayerbotTownGoals.IsCrowding(0.5f, 0.5f, 0.2f, otherIsBot: false, PlayerbotTownGoals.CrowdedYards));
        Assert.False(PlayerbotTownGoals.IsCrowding(0.5f, 0.5f, 6f, otherIsBot: true, PlayerbotTownGoals.CrowdedYards));
        Assert.False(PlayerbotTownGoals.IsCrowding(0.5f, 0.5f, -6f, otherIsBot: true, PlayerbotTownGoals.CrowdedYards));
        Assert.False(PlayerbotTownGoals.IsCrowding(2f, 0f, 0f, otherIsBot: true, PlayerbotTownGoals.CrowdedYards));
    }

    /// <summary>A crowd plan still waiting for navigation tiles does not arm the 60 s gate; a decided one does.</summary>
    [Fact]
    public void TownGoals_CrowdGate_IsNotBurnedByAPendingPlan()
    {
        Assert.False(PlayerbotTownGoals.ArmsCrowdGate(PlanOutcome.Pending));
        Assert.True(PlayerbotTownGoals.ArmsCrowdGate(PlanOutcome.Route));
        Assert.True(PlayerbotTownGoals.ArmsCrowdGate(PlanOutcome.Refused));
    }
}
