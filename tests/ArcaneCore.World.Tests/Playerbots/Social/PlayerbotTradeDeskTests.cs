using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Bots;
using ArcaneCore.World.Economy;
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
/// Lane T3 acceptance (design bot-capital-trade-design section 4, T3; docs/areas/playbots-social.md, Trading): the trade desk drives a bot's side of a fixed-price
/// deal through the bot's own client packets. Every test runs the real trade handlers on a real world with a real socket client for the player; the bot's packets are
/// recorded by <see cref="DispatchLog"/>. The item sells for 1000 copper (reference 1000, vendor floor 1000, quality 1), so every price below is exact:
/// a seller quotes 1000, a buyer bids 1000.
/// </summary>
public sealed class PlayerbotTradeDeskTests
{
    private const uint DealEntry = 996_001, OtherEntry = 996_002, Price = 1000, DiscountEntry = 996_003, DiscountEntryTwo = 996_004;
    private const string BotName = "Deskbot", SecondBotName = "Deskbottwo", CustomerName = "Customer", SecondCustomerName = "Customertwo";

    private static ItemTemplate Template(uint entry, string name)
        => new() { Entry = entry, Class = 15, SubClass = 0, Name = name, DisplayId = 1, Quality = 1, SellPrice = Price, Stackable = 20 };

    // Reference 1000 (a quarter of the 4000 buy price), vendor floor 500: a counter down to 900 is within the 10% tolerance and above the floor.
    private static ItemTemplate DiscountTemplate(uint entry, string name)
        => new() { Entry = entry, Class = 15, SubClass = 0, Name = name, DisplayId = 1, Quality = 1, SellPrice = 500, BuyPrice = 4000, Stackable = 20 };

    private static byte[] Gold(uint copper) => BitConverter.GetBytes(copper);

    private readonly record struct Trade(bool Exists, bool Opened, bool BotAccepted, bool CustomerAccepted, uint BotGold, uint CustomerGold, int BotItems, int CustomerItems);

    private sealed class Scene : IAsyncDisposable
    {
        private Scene(SocialWorld world, Guid bot, ObjectGuid botGuid)
        {
            World = world;
            Bot = bot;
            BotGuid = botGuid;
        }

        public SocialWorld World { get; }

        public Guid Bot { get; }

        public ObjectGuid BotGuid { get; }

        public WorldTestClient? Customer { get; private set; }

        public WorldTestClient? SecondCustomer { get; private set; }

        public ObjectGuid CustomerGuid { get; private set; }

        public static async Task<Scene> StartAsync(bool chat = false, bool frozen = true, bool generous = false)
        {
            var items = new ItemTestContent();
            items.Templates.Templates.AddRange([Template(DealEntry, "Deskitem Alpha"), Template(OtherEntry, "Deskitem Beta"), DiscountTemplate(DiscountEntry, "Deskitem Gamma"), DiscountTemplate(DiscountEntryTwo, "Deskitem Delta")]);
            SocialWorld world;
            using (items.Use())
            {
                world = await SocialWorld.StartAsync(
                    options =>
                    {
                        options.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone;
                        options.Chat.Enabled = chat;
                        options.Chat.PerPlayerCooldownSeconds = 0;
                        options.Social.Memory.AffinityPerChat = 0;
                        options.Social.Trading.Limits.MaxConcurrentTrades = 16;
                        options.Chat.Voice.Trade = false; // the bots must not post calls of their own over the one the test puts on the board
                        options.Chat.Voice.LookingForGroup = false;
                        options.Chat.Voice.QuestHelp = false;
                        options.Chat.Voice.RunCalls = false;
                        options.Chat.Voice.BattlegroundCalls = false;
                    },
                    frozen: frozen,
                    services: services => services.AddScoped<ArcaneCore.Kernel.Economy.IEconomyStore>(_ => new PlayerbotPartyTests.StubEconomyStore()));
            }

            if (generous)
            {
                // Character guids are handed out one after another, and a bot's generosity is a function of its guid: park idle filler bots until the next guid is a generous one,
                // so that the desk's bot is the kind that tips (the same selection the gratitude tests make by trying fourteen bots).
                await world.Host.OnWorldAsync(() =>
                {
                    world.Options.Social.Gratitude.TipChance = 1f;
                    world.Options.Social.Memory.MaxDailyAffinityGainPerPair = 0; // the daily cap would hold a pair under the tipping affinity
                    return true;
                });
                await world.AddBotAsync("Deskfilla");
                ObjectGuid last = await world.Host.PlayerStateAsync("Deskfilla", p => p.Guid);
                for (int i = 1; i < 20 && PlayerbotTraits.For(ObjectGuid.Player(last.Low + 1).Value).Generosity < PlayerbotGratitude.GenerousAt; i++)
                {
                    string filler = "Deskfill" + (char)('a' + i);
                    await world.AddBotAsync(filler);
                    last = await world.Host.PlayerStateAsync(filler, p => p.Guid);
                }
            }

            Guid bot = await world.AddBotAsync(BotName, money: 50);
            ObjectGuid botGuid = await world.Host.PlayerStateAsync(BotName, p => p.Guid);
            if (generous) Assert.True(PlayerbotTraits.For(botGuid.Value).Generosity >= PlayerbotGratitude.GenerousAt, "the desk bot is not a generous one");
            return new Scene(world, bot, botGuid);
        }

        /// <summary>The gratitude lane's pass at a hand-chosen world time (what the social feature's pass would run).</summary>
        public Task GratitudePassAsync(long nowMs)
            => World.Host.OnWorldAsync(() => SocialTestKit.Feature(World.Host).Gratitude.Run(SocialTestKit.BotPass(World.Host, nowMs), SocialTestKit.Feature(World.Host).Help));

        public PlayerbotGratitudeLane Gratitude => SocialTestKit.Feature(World.Host).Gratitude;

        public EconomyFeature Economy => World.Host.WorldServices.GetRequiredService<EconomyFeature>();

        public PlayerbotTradeDesk Desk => PartyTestHost.Feature(World.Host).TradeDesk;

        public PlayerbotSocialTradingOptions Trading => World.Options.Social.Trading;

        public async Task AddCustomerAsync(uint money = 5000)
        {
            Customer = await World.AddPlayerAsync(CustomerName);
            CustomerGuid = await World.Host.PlayerStateAsync(CustomerName, p => p.Guid);
            await PlaceBesideBotAsync(CustomerName, money);
        }

        public async Task AddSecondCustomerAsync()
        {
            SecondCustomer = await World.AddPlayerAsync(SecondCustomerName);
            await PlaceBesideBotAsync(SecondCustomerName, 5000);
        }

        public async Task<Guid> AddSecondBotAsync()
        {
            Guid id = await World.AddBotAsync(SecondBotName, money: 50);
            await World.Host.OnWorldAsync(() => HelpTestKit.Online(World, SecondBotName).Inventory.Load([]));
            return id;
        }

        private async Task PlaceBesideBotAsync(string player, uint money)
        {
            await World.Host.OnWorldAsync(() =>
            {
                Player bot = HelpTestKit.Online(World, BotName);
                PlayerbotMovementControl.Stop(PartyTestHost.Feature(World.Host).FindSession(Bot)!, bot);
                bot.Inventory.Load([]);
                Player other = HelpTestKit.Online(World, player);
                other.Inventory.Load([]);
                other.Money = money;
                other.Relocate(bot.X + 2f, bot.Y, bot.Z, 0f, World.Host.World.NowMs);
            });
            await World.Host.WaitForWorldAsync(() => HelpTestKit.Online(World, BotName).VisibleObjects.Contains(HelpTestKit.Online(World, player).Guid), player + " in sight");
        }

        /// <summary>Put <paramref name="count"/> of <paramref name="entry"/> in a player's bags and return the CMSG_SET_TRADE_ITEM body for trade slot 0.</summary>
        public Task<byte[]> GiveAsync(string player, uint entry, uint count = 1) => World.Host.OnWorldAsync(() =>
        {
            Player owner = HelpTestKit.Online(World, player);
            Assert.Equal(InventoryResult.Ok, owner.Inventory.AddItem(entry, count, out Item? item));
            return new byte[] { 0, item!.BagSlot, item.Slot };
        });

        public Task<uint> CountAsync(string player, uint entry) => World.Host.OnWorldAsync(() => HelpTestKit.Online(World, player).Inventory.GetItemCount(entry));

        public Task<uint> MoneyAsync(string player) => World.Host.PlayerStateAsync(player, p => p.Money);

        public Task SetMoneyAsync(string player, uint money) => World.Host.OnWorldAsync(() => { HelpTestKit.Online(World, player).Money = money; return true; });

        public Task<PlayerbotDealQuote> QuoteAsync(PlayerbotDealRole role, uint entry, uint count = 1, string bot = BotName, string partner = CustomerName)
            => World.Host.OnWorldAsync(() => Desk.Quote(World.Bots[bot], HelpTestKit.Online(World, bot), HelpTestKit.Online(World, partner), role, entry, count));

        public Task<PlayerbotDealHeard> HearAsync(string text, string bot = BotName, string partner = CustomerName)
            => World.Host.OnWorldAsync(() => Desk.Hear(World.Bots[bot], HelpTestKit.Online(World, partner).Guid, text));

        public Task<Trade> TradeAsync(string player = CustomerName) => World.Host.OnWorldAsync(() =>
        {
            Player other = HelpTestKit.Online(World, player), bot = HelpTestKit.Online(World, BotName);
            if (Economy.TradeOf(other) is not { } trade) return new Trade(false, false, false, false, 0, 0, 0, 0);
            TradeSide mine = trade.SideOf(bot), theirs = trade.SideOf(other);
            return new Trade(true, trade.Opened, mine.Accepted, theirs.Accepted, mine.Gold, theirs.Gold,
                mine.TradedItems.Count(g => !g.IsEmpty), theirs.TradedItems.Count(g => !g.IsEmpty));
        });

        /// <summary>The player opens a window with the bot; the bot's desk claims it (a quoted or agreed deal) and answers with its own BeginTrade.</summary>
        public async Task OpenWindowAsync(WorldTestClient client, string player = CustomerName)
        {
            var initiate = new PacketWriter(8);
            initiate.WriteUInt64(BotGuid.Value);
            await client.SendAsync(WorldOpcode.CmsgInitiateTrade, initiate.ToArray());
            await HelpTestKit.UntilAsync(() => TradeAsync(player).Result.Opened, "the bot to open the trade window");
        }

        /// <summary>An accept that follows a change by less than a second is bounced by the server (scam prevention), so the player waits it out.</summary>
        public async Task AcceptAsync(WorldTestClient client)
        {
            await Task.Delay(1100);
            await client.SendAsync(WorldOpcode.CmsgAcceptTrade, Gold(1));
        }

        /// <summary>Wait until <paramref name="stop"/> holds or <paramref name="milliseconds"/> pass; true when it held. Used to give the bot time to (wrongly) act.</summary>
        public static async Task<bool> WaitWhileAsync(Func<bool> stop, int milliseconds)
        {
            for (int waited = 0; waited < milliseconds; waited += 20)
            {
                if (stop()) return true;
                await Task.Delay(20);
            }

            return stop();
        }

        public int BotSent(WorldOpcode opcode) => World.Log.Count(BotName, opcode);

        public PlayerbotTradeRecord? Ledger(ObjectGuid other)
            => HelpTestKit.RecorderOf(World).Ledger!.TryGet(Bot, other.Value, out PlayerbotTradeRecord record) ? record : null;

        /// <summary>A whole sale, the player opening the window: quote, open, the bot puts the item, the player pays in full and accepts, the window completes.</summary>
        public async Task CompleteSaleAsync(uint entry)
        {
            PlayerbotDealQuote quote = await QuoteAsync(PlayerbotDealRole.Seller, entry);
            Assert.True(quote.Ok, quote.Refusal.ToString());
            await OpenWindowAsync(Customer!);
            await HelpTestKit.UntilAsync(() => TradeAsync().Result.BotItems == 1, "the bot to put the item in");
            await Customer!.SendAsync(WorldOpcode.CmsgSetTradeGold, Gold(Price));
            await AcceptAsync(Customer);
            await HelpTestKit.UntilAsync(() => !TradeAsync().Result.Exists, "the sale to complete");
            await HelpTestKit.UntilAsync(() => Ledger(CustomerGuid) is { FairDeals: > 0 }, "the bot to record the fair deal");
        }

        public async ValueTask DisposeAsync()
        {
            if (Customer is not null) await Customer.DisposeAsync();
            if (SecondCustomer is not null) await SecondCustomer.DisposeAsync();
            await World.DisposeAsync();
        }
    }

    // --- strangers ----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A stranger's trade request with no deal is not answered (the party AI's old rule still holds with the desk on). Control: once the bot has quoted that stranger,
    /// opening the window is answered at once, so the refusal was about the missing deal, not a dead bot. Broken: the desk would claim any BeginTrade.
    /// </summary>
    [Fact]
    public async Task TradeDesk_StrangerWindow_RefusedWithoutDeal()
    {
        await using Scene scene = await Scene.StartAsync();
        await scene.AddCustomerAsync();
        await scene.GiveAsync(BotName, DealEntry);
        var initiate = new PacketWriter(8);
        initiate.WriteUInt64(scene.BotGuid.Value);
        await scene.Customer!.SendAsync(WorldOpcode.CmsgInitiateTrade, initiate.ToArray());
        await HelpTestKit.UntilAsync(() => scene.TradeAsync().Result.Exists, "the invitation to reach the server");
        await Scene.WaitWhileAsync(() => scene.BotSent(WorldOpcode.CmsgBeginTrade) > 0, 2000);
        Assert.Equal(0, scene.BotSent(WorldOpcode.CmsgBeginTrade));
        Assert.False((await scene.TradeAsync()).Opened);
        Assert.Equal(0, scene.Desk.Count);

        // Control: withdraw the request, get a quote, ask again.
        await scene.Customer.SendAsync(WorldOpcode.CmsgCancelTrade, []);
        await HelpTestKit.UntilAsync(() => !scene.TradeAsync().Result.Exists, "the first request to be withdrawn");
        Assert.True((await scene.QuoteAsync(PlayerbotDealRole.Seller, DealEntry)).Ok);
        await scene.OpenWindowAsync(scene.Customer);
        Assert.Equal(1, scene.BotSent(WorldOpcode.CmsgBeginTrade));
    }

    // --- the happy path -----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A WTS deal start to finish through the bot's voice: the bot has a Selling post on the board; the player whispers about the item, gets the quote (10s), whispers
    /// "deal", and the bot (in range) asks for the trade itself; the player opens the window; the bot puts exactly the one stack; the player puts the agreed gold and
    /// accepts; the window completes. The item and the gold move exactly once, the ledger holds one fair deal, the post is spent.
    /// </summary>
    [Fact]
    public async Task TradeDesk_WtsDeal_EndToEnd()
    {
        await using Scene scene = await Scene.StartAsync(chat: true, frozen: false);
        await scene.AddCustomerAsync();
        await scene.GiveAsync(BotName, DealEntry);
        PlayerbotChat chat = PartyTestHost.Feature(scene.World.Host).Chat!;
        await HelpTestKit.UntilAsync(() => chat.Responder is not null, "the voice to hook the chat service");
        await scene.World.Host.OnWorldAsync(() =>
        {
            chat.Voice.Post(scene.Bot, new PlayerbotVoicePost(PlayerbotVoicePostKind.Selling, chat.NowMs, "Deskitem Alpha", DealEntry, "10s", 1));
            return true;
        });

        await scene.Customer!.SendChatAsync(ChatType.Whisper, Language.Common, "how much for the deskitem?", BotName);
        ChatMessage quote = await PartyTestHost.ReadWhisperFromAsync(scene.Customer, scene.BotGuid.Value);
        Assert.Contains("10s", quote.Text);
        Assert.Equal(PlayerbotDealState.Quoted, scene.Desk.StateOf(scene.Bot, scene.CustomerGuid));
        Assert.Equal(Price, (uint)scene.Desk.PriceOf(scene.Bot, scene.CustomerGuid)!.Value);

        await scene.Customer.SendChatAsync(ChatType.Whisper, Language.Common, "deal", BotName);
        await HelpTestKit.UntilAsync(() => scene.BotSent(WorldOpcode.CmsgInitiateTrade) > 0, "the bot to ask for the trade");
        Assert.True(scene.Desk.StateOf(scene.Bot, scene.CustomerGuid) is PlayerbotDealState.Agreed or PlayerbotDealState.Window);
        await scene.Customer.SendAsync(WorldOpcode.CmsgBeginTrade, []);
        await HelpTestKit.UntilAsync(() => scene.TradeAsync().Result.Opened, "the window to open");
        await HelpTestKit.UntilAsync(() => scene.TradeAsync().Result.BotItems == 1, "the bot to put the item in");
        Assert.Equal(1, scene.BotSent(WorldOpcode.CmsgSetTradeItem));
        Assert.Equal(0u, (await scene.TradeAsync()).BotGold);

        await scene.Customer.SendAsync(WorldOpcode.CmsgSetTradeGold, Gold(Price));
        await scene.AcceptAsync(scene.Customer);
        await HelpTestKit.UntilAsync(() => !scene.TradeAsync().Result.Exists, "the window to complete");
        await HelpTestKit.UntilAsync(() => scene.Ledger(scene.CustomerGuid) is { FairDeals: > 0 }, "the fair deal to be recorded");

        Assert.Equal(0u, await scene.CountAsync(BotName, DealEntry));
        Assert.Equal(1u, await scene.CountAsync(CustomerName, DealEntry));
        Assert.Equal(50u + Price, await scene.MoneyAsync(BotName));
        Assert.Equal(5000u - Price, await scene.MoneyAsync(CustomerName));
        Assert.Equal(1, scene.Ledger(scene.CustomerGuid)!.FairDeals);
        Assert.Equal(0, scene.Ledger(scene.CustomerGuid)!.Scams);
        Assert.Equal(1, scene.BotSent(WorldOpcode.CmsgAcceptTrade));
        Assert.Equal(1, scene.BotSent(WorldOpcode.CmsgSetTradeItem));
        await HelpTestKit.UntilAsync(() => scene.World.Host.OnWorldAsync(() => chat.Voice.Find(scene.Bot, chat.NowMs, long.MaxValue) is null).Result, "the board post to be spent");
        Assert.Equal(0, scene.Desk.Count);
    }

    // --- scams --------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Short gold: the player puts price-1 and accepts; the bot never accepts and nothing moves. Control: the same window with the full price completes, so the refusal
    /// was the missing copper and not a stuck bot. Broken: the bot would accept any positive gold, or any accept of the player's.
    /// </summary>
    [Fact]
    public async Task TradeDesk_ShortGold_IsNeverAccepted_FullGoldCompletes()
    {
        await using Scene scene = await Scene.StartAsync();
        await scene.AddCustomerAsync();
        await scene.GiveAsync(BotName, DealEntry);
        Assert.True((await scene.QuoteAsync(PlayerbotDealRole.Seller, DealEntry)).Ok);
        await scene.OpenWindowAsync(scene.Customer!);
        await HelpTestKit.UntilAsync(() => scene.TradeAsync().Result.BotItems == 1, "the bot to put the item in");

        await scene.Customer!.SendAsync(WorldOpcode.CmsgSetTradeGold, Gold(Price - 1));
        await scene.AcceptAsync(scene.Customer);
        await HelpTestKit.UntilAsync(() => scene.TradeAsync().Result.CustomerAccepted, "the player's accept to reach the server");
        // The hold is 1.5 s: wait well past it.
        Assert.False(await Scene.WaitWhileAsync(() => scene.BotSent(WorldOpcode.CmsgAcceptTrade) > 0, 3000), "the bot accepted a window that is short of gold");
        Trade trade = await scene.TradeAsync();
        Assert.True(trade.Exists);
        Assert.False(trade.BotAccepted);
        Assert.Equal(1u, await scene.CountAsync(BotName, DealEntry));
        Assert.Equal(0u, await scene.CountAsync(CustomerName, DealEntry));
        Assert.Equal(5000u, await scene.MoneyAsync(CustomerName));
        Assert.Equal(50u, await scene.MoneyAsync(BotName));

        // Control: the full price; the player accepts again (a change cleared the accepts).
        await scene.Customer.SendAsync(WorldOpcode.CmsgSetTradeGold, Gold(Price));
        await scene.AcceptAsync(scene.Customer);
        await HelpTestKit.UntilAsync(() => !scene.TradeAsync().Result.Exists, "the paid window to complete");
        Assert.Equal(0u, await scene.CountAsync(BotName, DealEntry));
        Assert.Equal(1u, await scene.CountAsync(CustomerName, DealEntry));
        Assert.Equal(50u + Price, await scene.MoneyAsync(BotName));
        Assert.Equal(1, scene.BotSent(WorldOpcode.CmsgAcceptTrade));
    }

    /// <summary>
    /// The window changes after the bot accepted. The bot accepted a matching window; the player lowers its gold (the server clears both accepts); the bot does not
    /// accept the short window; the player restores it and the bot accepts again; the player lowers it a second time: the deal is cancelled and a TradeScam is recorded
    /// (the ledger holds the player as a scammer), and nothing moved. Broken: the bot would re-accept a changed window, never count the change, or complete anyway.
    /// </summary>
    [Fact]
    public async Task TradeDesk_WindowChangedAfterAccept_Rechecks()
    {
        await using Scene scene = await Scene.StartAsync();
        await scene.AddCustomerAsync();
        await scene.GiveAsync(BotName, DealEntry);
        Assert.True((await scene.QuoteAsync(PlayerbotDealRole.Seller, DealEntry)).Ok);
        await scene.OpenWindowAsync(scene.Customer!);
        await HelpTestKit.UntilAsync(() => scene.TradeAsync().Result.BotItems == 1, "the bot to put the item in");
        await scene.Customer!.SendAsync(WorldOpcode.CmsgSetTradeGold, Gold(Price));
        await HelpTestKit.UntilAsync(() => scene.BotSent(WorldOpcode.CmsgAcceptTrade) == 1, "the bot to accept the matching window");
        Assert.True((await scene.TradeAsync()).BotAccepted);

        await scene.Customer.SendAsync(WorldOpcode.CmsgSetTradeGold, Gold(Price - 1)); // the change after the bot's accept
        await HelpTestKit.UntilAsync(() => !scene.TradeAsync().Result.BotAccepted, "the server to clear the bot's accept");
        Assert.False(await Scene.WaitWhileAsync(() => scene.BotSent(WorldOpcode.CmsgAcceptTrade) > 1, 3000), "the bot re-accepted the short window");
        Assert.True((await scene.TradeAsync()).Exists);
        Assert.Equal(1, scene.BotSent(WorldOpcode.CmsgAcceptTrade));
        Assert.False(scene.Ledger(scene.CustomerGuid) is { Scams: > 0 }); // one change is not yet a scam

        await scene.Customer.SendAsync(WorldOpcode.CmsgSetTradeGold, Gold(Price)); // restored: the bot accepts the matching window again
        await HelpTestKit.UntilAsync(() => scene.BotSent(WorldOpcode.CmsgAcceptTrade) == 2, "the bot to accept the restored window");

        await scene.Customer.SendAsync(WorldOpcode.CmsgSetTradeGold, Gold(Price - 2)); // the second change after an accept
        await HelpTestKit.UntilAsync(() => !scene.TradeAsync().Result.Exists, "the deal to be cancelled");
        await HelpTestKit.UntilAsync(() => scene.Ledger(scene.CustomerGuid) is { Scams: > 0 }, "the scam to be recorded");
        Assert.True(HelpTestKit.RecorderOf(scene.World).Ledger!.IsScammer(scene.Bot, scene.CustomerGuid.Value));
        Assert.Equal(1, scene.Ledger(scene.CustomerGuid)!.Scams);
        Assert.Equal(0, scene.Ledger(scene.CustomerGuid)!.FairDeals);
        Assert.Equal(1u, await scene.CountAsync(BotName, DealEntry));
        Assert.Equal(0u, await scene.CountAsync(CustomerName, DealEntry));
        Assert.Equal(5000u, await scene.MoneyAsync(CustomerName));
        Assert.Equal(50u, await scene.MoneyAsync(BotName));
        Assert.Equal(0, scene.Desk.Count);
        Assert.Equal(PlayerbotDealRefusal.Scammer, (await scene.QuoteAsync(PlayerbotDealRole.Seller, DealEntry)).Refusal); // and the next quote is refused
    }

    /// <summary>
    /// A swapped item (buyer flow): the bot buys <c>DealEntry</c> for 1000; the player puts it in, the bot puts its gold in and accepts; the player then takes the item
    /// out and puts a different entry in. The bot takes its gold back and does not accept; when the player accepts the swapped window nothing completes and nothing of the
    /// bot's moves. Broken: the bot would pay for whatever sits in the slot.
    /// </summary>
    [Fact]
    public async Task TradeDesk_SwappedItem_BotTakesItsGoldBack_NothingMoves()
    {
        await using Scene scene = await Scene.StartAsync();
        await scene.AddCustomerAsync();
        await scene.SetMoneyAsync(BotName, 5000);
        byte[] agreedItem = await scene.GiveAsync(CustomerName, DealEntry);
        byte[] swappedItem = await scene.GiveAsync(CustomerName, OtherEntry);
        PlayerbotDealQuote quote = await scene.QuoteAsync(PlayerbotDealRole.Buyer, DealEntry);
        Assert.True(quote.Ok, quote.Refusal.ToString());
        Assert.Equal((long)Price, quote.Price);
        await scene.OpenWindowAsync(scene.Customer!);

        await scene.Customer!.SendAsync(WorldOpcode.CmsgSetTradeItem, agreedItem);
        await HelpTestKit.UntilAsync(() => scene.TradeAsync().Result.BotGold == Price, "the bot to put its gold in");
        await HelpTestKit.UntilAsync(() => scene.BotSent(WorldOpcode.CmsgAcceptTrade) == 1, "the bot to accept the agreed window");

        await scene.Customer.SendAsync(WorldOpcode.CmsgClearTradeItem, [0]);
        await scene.Customer.SendAsync(WorldOpcode.CmsgSetTradeItem, swappedItem);
        await HelpTestKit.UntilAsync(() => scene.TradeAsync().Result is { BotGold: 0, CustomerItems: 1 }, "the bot to take its gold back");
        await scene.AcceptAsync(scene.Customer);
        Assert.False(await Scene.WaitWhileAsync(() => scene.BotSent(WorldOpcode.CmsgAcceptTrade) > 1 || !scene.TradeAsync().Result.Exists, 3000), "the bot accepted, or the window completed, with the swapped item");
        Trade trade = await scene.TradeAsync();
        Assert.True(trade.Exists);
        Assert.False(trade.BotAccepted);
        Assert.Equal(0u, trade.BotGold);
        Assert.Equal(5000u, await scene.MoneyAsync(BotName));
        Assert.Equal(0u, await scene.CountAsync(BotName, DealEntry));
        Assert.Equal(0u, await scene.CountAsync(BotName, OtherEntry));
        Assert.Equal(1u, await scene.CountAsync(CustomerName, DealEntry));
        Assert.Equal(1u, await scene.CountAsync(CustomerName, OtherEntry));
        Assert.Equal(5000u, await scene.MoneyAsync(CustomerName));
    }

    // --- limits -------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>Deals:MaxDealCopper</c> is the largest deal, read live: a 1000 copper deal is refused at 999 and quoted at 1000 (the boundary). Below 1 the desk is off.
    /// Broken: the cap would be read once, off by one, or ignored.
    /// </summary>
    [Fact]
    public async Task TradeDesk_Limits_MaxDealCopper_IsTheBoundary_AndReadLive()
    {
        await using Scene scene = await Scene.StartAsync();
        await scene.AddCustomerAsync();
        await scene.GiveAsync(BotName, DealEntry);
        scene.Trading.Deals.MaxDealCopper = (int)Price - 1;
        Assert.Equal(PlayerbotDealRefusal.TooMuch, (await scene.QuoteAsync(PlayerbotDealRole.Seller, DealEntry)).Refusal);
        scene.Trading.Deals.MaxDealCopper = (int)Price;
        PlayerbotDealQuote quote = await scene.QuoteAsync(PlayerbotDealRole.Seller, DealEntry);
        Assert.True(quote.Ok);
        Assert.Equal((long)Price, quote.Price);
        scene.Trading.Deals.MaxDealCopper = 0;
        Assert.Equal(PlayerbotDealRefusal.Disabled, (await scene.QuoteAsync(PlayerbotDealRole.Seller, DealEntry)).Refusal);
        scene.Trading.Deals.MaxDealCopper = 500_000;
        scene.Trading.Deals.Enabled = false;
        Assert.Equal(PlayerbotDealRefusal.Disabled, (await scene.QuoteAsync(PlayerbotDealRole.Seller, DealEntry)).Refusal);
    }

    /// <summary>
    /// A partner whose trust is under <c>LowTrustPermille</c> gets a quarter of the cap (a new relation is 500 permille, so the line is moved to 501 here): with
    /// MaxDealCopper 3999 the cap is 999 and a 1000 deal is refused; at 4000 it is 1000 and quoted.
    /// </summary>
    [Fact]
    public async Task TradeDesk_Limits_LowTrustPartnerGetsAQuarterOfTheCap()
    {
        await using Scene scene = await Scene.StartAsync();
        await scene.AddCustomerAsync();
        await scene.GiveAsync(BotName, DealEntry);
        scene.Trading.LowTrustPermille = 501;
        scene.Trading.Deals.MaxDealCopper = 3999;
        Assert.Equal(PlayerbotDealRefusal.TooMuch, (await scene.QuoteAsync(PlayerbotDealRole.Seller, DealEntry)).Refusal);
        scene.Trading.Deals.MaxDealCopper = 4000;
        Assert.True((await scene.QuoteAsync(PlayerbotDealRole.Seller, DealEntry)).Ok);
        scene.Trading.LowTrustPermille = 500; // not below the line any more: the whole cap
        scene.Trading.Deals.MaxDealCopper = 1000;
        Assert.True((await scene.QuoteAsync(PlayerbotDealRole.Seller, DealEntry)).Ok);
    }

    /// <summary>
    /// The hourly trade limits count completed deals: after one sale <c>MaxTradesPerBotPerHour</c> 1 refuses the next quote, 2 allows it; then
    /// <c>MaxTradesPerAccountPerHour</c> 1 refuses it and 2 allows it. 0 allows none.
    /// </summary>
    [Fact]
    public async Task TradeDesk_Limits_MaxTradesPerBotAndAccountPerHour()
    {
        await using Scene scene = await Scene.StartAsync();
        await scene.AddCustomerAsync(money: 10_000);
        await scene.GiveAsync(BotName, DealEntry);
        await scene.GiveAsync(BotName, OtherEntry);
        scene.Trading.Limits.MaxTradesPerBotPerHour = 0;
        Assert.Equal(PlayerbotDealRefusal.BotLimit, (await scene.QuoteAsync(PlayerbotDealRole.Seller, OtherEntry)).Refusal);
        scene.Trading.Limits.MaxTradesPerBotPerHour = 6;
        await scene.CompleteSaleAsync(DealEntry);

        scene.Trading.Limits.MaxTradesPerBotPerHour = 1;
        Assert.Equal(PlayerbotDealRefusal.BotLimit, (await scene.QuoteAsync(PlayerbotDealRole.Seller, OtherEntry)).Refusal);
        scene.Trading.Limits.MaxTradesPerBotPerHour = 2;
        scene.Trading.Limits.MaxTradesPerAccountPerHour = 1;
        Assert.Equal(PlayerbotDealRefusal.AccountLimit, (await scene.QuoteAsync(PlayerbotDealRole.Seller, OtherEntry)).Refusal);
        scene.Trading.Limits.MaxTradesPerAccountPerHour = 0;
        Assert.Equal(PlayerbotDealRefusal.AccountLimit, (await scene.QuoteAsync(PlayerbotDealRole.Seller, OtherEntry)).Refusal);
        scene.Trading.Limits.MaxTradesPerAccountPerHour = 2;
        Assert.True((await scene.QuoteAsync(PlayerbotDealRole.Seller, OtherEntry)).Ok);
    }

    /// <summary>
    /// <c>MaxConcurrentTrades</c> caps agreed deals world-wide: with 1, the second bot's agreement is refused (Busy) and, with 2, allowed. A bot holds one agreed deal at a
    /// time: its quote to another partner is refused (Busy) while it has one. 0 allows none.
    /// </summary>
    [Fact]
    public async Task TradeDesk_Limits_MaxConcurrentTrades_AndOneAgreedDealPerBot()
    {
        await using Scene scene = await Scene.StartAsync();
        await scene.AddCustomerAsync();
        await scene.AddSecondCustomerAsync();
        await scene.AddSecondBotAsync();
        await scene.GiveAsync(BotName, DealEntry);
        await scene.GiveAsync(SecondBotName, DealEntry);

        scene.Trading.Limits.MaxConcurrentTrades = 0;
        Assert.True((await scene.QuoteAsync(PlayerbotDealRole.Seller, DealEntry)).Ok);
        PlayerbotDealHeard none = await scene.HearAsync("deal");
        Assert.Equal(PlayerbotDealHeardKind.Refused, none.Kind);
        Assert.Equal(PlayerbotDealRefusal.Busy, none.Refusal);

        scene.Trading.Limits.MaxConcurrentTrades = 1;
        Assert.Equal(PlayerbotDealHeardKind.Agreed, (await scene.HearAsync("deal")).Kind);
        Assert.Equal(PlayerbotDealState.Agreed, scene.Desk.StateOf(scene.Bot, (await scene.World.Host.OnWorldAsync(() => HelpTestKit.Online(scene.World, CustomerName).Guid))));
        Assert.Equal(PlayerbotDealRefusal.Busy, (await scene.QuoteAsync(PlayerbotDealRole.Seller, DealEntry, partner: SecondCustomerName)).Refusal); // the same bot, another partner

        Assert.True((await scene.QuoteAsync(PlayerbotDealRole.Seller, DealEntry, bot: SecondBotName)).Ok);
        PlayerbotDealHeard second = await scene.HearAsync("deal", SecondBotName);
        Assert.Equal(PlayerbotDealHeardKind.Refused, second.Kind);
        Assert.Equal(PlayerbotDealRefusal.Busy, second.Refusal);

        scene.Trading.Limits.MaxConcurrentTrades = 2;
        Assert.Equal(PlayerbotDealHeardKind.Agreed, (await scene.HearAsync("deal", SecondBotName)).Kind);
    }

    // --- switching off, combat ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>Trading:Deals:Enabled</c> is read live: switched off while a desk window is open, the bot cancels it at its next drive and nothing moves. Broken: an open window
    /// would run on to completion after the switch.
    /// </summary>
    [Fact]
    public async Task TradeDesk_Disabled_CancelsOpenWindows()
    {
        await using Scene scene = await Scene.StartAsync();
        await scene.AddCustomerAsync();
        await scene.GiveAsync(BotName, DealEntry);
        Assert.True((await scene.QuoteAsync(PlayerbotDealRole.Seller, DealEntry)).Ok);
        await scene.OpenWindowAsync(scene.Customer!);
        await HelpTestKit.UntilAsync(() => scene.TradeAsync().Result.BotItems == 1, "the bot to put the item in");

        scene.Trading.Deals.Enabled = false;
        await HelpTestKit.UntilAsync(() => !scene.TradeAsync().Result.Exists, "the bot to cancel the window");
        Assert.Equal(1, scene.BotSent(WorldOpcode.CmsgCancelTrade));
        Assert.Equal(1u, await scene.CountAsync(BotName, DealEntry));
        Assert.Equal(0u, await scene.CountAsync(CustomerName, DealEntry));
        Assert.Equal(50u, await scene.MoneyAsync(BotName));
        Assert.Equal(5000u, await scene.MoneyAsync(CustomerName));
        Assert.Equal(0, scene.Desk.Count);
    }

    /// <summary>A bot that is pulled into combat while the window is open cancels the deal; nothing moves.</summary>
    [Fact]
    public async Task TradeDesk_CombatCancels()
    {
        await using Scene scene = await Scene.StartAsync();
        await scene.AddCustomerAsync();
        await scene.GiveAsync(BotName, DealEntry);
        Assert.True((await scene.QuoteAsync(PlayerbotDealRole.Seller, DealEntry)).Ok);
        await scene.OpenWindowAsync(scene.Customer!);
        await HelpTestKit.UntilAsync(() => scene.TradeAsync().Result.BotItems == 1, "the bot to put the item in");

        await scene.World.Host.OnWorldAsync(() =>
        {
            Player bot = HelpTestKit.Online(scene.World, BotName);
            bot.Map!.Combat.SetInCombatState(bot, 60_000);
            return true;
        });
        await HelpTestKit.UntilAsync(() => !scene.TradeAsync().Result.Exists && scene.Desk.Count == 0, "the deal to be cancelled");
        Assert.Equal(1u, await scene.CountAsync(BotName, DealEntry));
        Assert.Equal(0u, await scene.CountAsync(CustomerName, DealEntry));
        Assert.Equal(50u, await scene.MoneyAsync(BotName));
        Assert.Equal(5000u, await scene.MoneyAsync(CustomerName));
        Assert.Equal(0, scene.BotSent(WorldOpcode.CmsgAcceptTrade));
    }

    // --- daily outflow ------------------------------------------------------------------------------------------------------------

    private enum Cap { Bot, Account, Server }

    private static void SetCap(Scene scene, Cap cap, int value)
    {
        PlayerbotSocialTradingLimitsOptions limits = scene.Trading.Limits;
        limits.DailyOutflowCopperPerBot = cap == Cap.Bot ? value : 1_000_000;
        limits.DailyOutflowCopperPerAccount = cap == Cap.Account ? value : 1_000_000;
        limits.DailyOutflowCopperServer = cap == Cap.Server ? value : 1_000_000;
    }

    /// <summary>
    /// The daily outflow caps count the discount a deal gives (<see cref="PlayerbotDealRules.Outflow"/>: reference 1000 less the 900 a counter-offer agreed is 100 copper),
    /// each of the three caps on its own. A discount of 100 fits a cap of 100 (the cap is exceeded only above it) and is refused (Outflow) at 99; a deal with no discount
    /// (a counter above the ask agrees at the ask) is agreed even at cap 0 (the check is skipped when nothing is given away); a completed discounted sale adds its discount
    /// to the bot's bucket, so with a cap of 150 the next discounted deal is refused for lack of headroom and at 200 it is agreed. Broken: the discount would not be counted
    /// (every cap ignored), the cap would be off by one, a 0 cap would stop undiscounted deals, or a completed sale would not use up the headroom.
    /// </summary>
    [Fact]
    public async Task TradeDesk_Limits_DailyOutflowCountsTheDiscount()
    {
        foreach (Cap cap in new[] { Cap.Bot, Cap.Account, Cap.Server })
        {
            await using Scene scene = await Scene.StartAsync();
            await scene.AddCustomerAsync();
            await scene.AddSecondCustomerAsync();
            await scene.GiveAsync(BotName, DiscountEntry);
            SetCap(scene, cap, 99);
            PlayerbotDealQuote quote = await scene.QuoteAsync(PlayerbotDealRole.Seller, DiscountEntry);
            Assert.True(quote.Ok, quote.Refusal.ToString());
            Assert.Equal(1000L, quote.Price);
            PlayerbotDealHeard refused = await scene.HearAsync("900c");
            Assert.True(refused.Kind == PlayerbotDealHeardKind.Refused && refused.Refusal == PlayerbotDealRefusal.Outflow, $"{cap}: {refused.Kind} {refused.Refusal}");
            Assert.Equal(PlayerbotDealState.Quoted, scene.Desk.StateOf(scene.Bot, scene.CustomerGuid));

            SetCap(scene, cap, 100); // exactly the discount: it fits (another partner and account, so no earlier use of the counter or the account bucket)
            Assert.True((await scene.QuoteAsync(PlayerbotDealRole.Seller, DiscountEntry, partner: SecondCustomerName)).Ok);
            PlayerbotDealHeard agreed = await scene.HearAsync("900c", partner: SecondCustomerName);
            Assert.True(agreed.Kind == PlayerbotDealHeardKind.CounterAgreed, $"{cap}: {agreed.Kind} {agreed.Refusal}");
            Assert.Equal(900L, agreed.Price);
        }

        // No discount: caps of 0 do not refuse a deal that gives nothing away (pinned: the check runs only when the outflow is above 0).
        await using (Scene scene = await Scene.StartAsync())
        {
            await scene.AddCustomerAsync();
            await scene.GiveAsync(BotName, DiscountEntry);
            scene.Trading.Limits.DailyOutflowCopperPerBot = 0;
            scene.Trading.Limits.DailyOutflowCopperPerAccount = 0;
            scene.Trading.Limits.DailyOutflowCopperServer = 0;
            Assert.True((await scene.QuoteAsync(PlayerbotDealRole.Seller, DiscountEntry)).Ok);
            PlayerbotDealHeard above = await scene.HearAsync("1500c"); // a counter above the ask agrees at the ask: no discount
            Assert.Equal(PlayerbotDealHeardKind.CounterAgreed, above.Kind);
            Assert.Equal(1000L, above.Price);
        }

        await using (Scene scene = await Scene.StartAsync())
        {
            await scene.AddCustomerAsync();
            await scene.AddSecondCustomerAsync();
            await scene.GiveAsync(BotName, DiscountEntry);
            await scene.GiveAsync(BotName, DiscountEntryTwo);
            SetCap(scene, Cap.Bot, 150);
            Assert.True((await scene.QuoteAsync(PlayerbotDealRole.Seller, DiscountEntry)).Ok);
            Assert.Equal(PlayerbotDealHeardKind.CounterAgreed, (await scene.HearAsync("900c")).Kind);
            await HelpTestKit.UntilAsync(() => scene.BotSent(WorldOpcode.CmsgInitiateTrade) > 0, "the bot to ask for the trade");
            await scene.Customer!.SendAsync(WorldOpcode.CmsgBeginTrade, []);
            await HelpTestKit.UntilAsync(() => scene.TradeAsync().Result.Opened, "the window to open");
            await HelpTestKit.UntilAsync(() => scene.TradeAsync().Result.BotItems == 1, "the bot to put the item in");
            await scene.Customer.SendAsync(WorldOpcode.CmsgSetTradeGold, Gold(900));
            await scene.AcceptAsync(scene.Customer);
            await HelpTestKit.UntilAsync(() => !scene.TradeAsync().Result.Exists, "the discounted sale to complete");
            await HelpTestKit.UntilAsync(() => scene.Ledger(scene.CustomerGuid) is { FairDeals: > 0 }, "the sale to be recorded");
            Assert.Equal(50u + 900u, await scene.MoneyAsync(BotName));

            // 100 of the 150 is used: another 100 does not fit, at 200 it does.
            Assert.True((await scene.QuoteAsync(PlayerbotDealRole.Seller, DiscountEntryTwo)).Ok);
            PlayerbotDealHeard over = await scene.HearAsync("900c");
            Assert.True(over.Kind == PlayerbotDealHeardKind.Refused && over.Refusal == PlayerbotDealRefusal.Outflow, $"{over.Kind} {over.Refusal}");
            SetCap(scene, Cap.Bot, 200);
            Assert.True((await scene.QuoteAsync(PlayerbotDealRole.Seller, DiscountEntryTwo, partner: SecondCustomerName)).Ok);
            Assert.Equal(PlayerbotDealHeardKind.CounterAgreed, (await scene.HearAsync("900c", partner: SecondCustomerName)).Kind);
        }
    }

    // --- other drivers beside the desk ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A pending gratitude tip from the bot to the deal partner must not touch the desk's window. The bot is a generous one that the partner just helped (its thanks roll a
    /// tip) and it holds an agreed buyer deal with the same partner (price 1000; its tip would be a different, smaller sum). The gratitude pass runs while the deal waits for
    /// its window and on every step of the window: the tip is dropped, the bot's only gold on the table is the price (exactly one SetTradeGold packet in all), it accepts
    /// only the matching window, the sale completes at the price, and the desk sale is not counted as a paid tip. Broken: the tip lane would put its own gold in the desk
    /// window and accept it, the desk and the tip would fight over the bot's gold, or the sale would be credited as a tip.
    /// </summary>
    [Fact]
    public async Task TradeDesk_PendingTipForTheSamePartner_DoesNotTouchTheDeskWindow()
    {
        await using Scene scene = await Scene.StartAsync(generous: true);
        await scene.AddCustomerAsync();
        await scene.SetMoneyAsync(BotName, 50_000);
        byte[] item = await scene.GiveAsync(CustomerName, DealEntry);
        PlayerbotDealQuote quote = await scene.QuoteAsync(PlayerbotDealRole.Buyer, DealEntry);
        Assert.True(quote.Ok, quote.Refusal.ToString());
        Assert.Equal(PlayerbotDealHeardKind.Agreed, (await scene.HearAsync("deal")).Kind);
        await HelpTestKit.UntilAsync(() => scene.BotSent(WorldOpcode.CmsgInitiateTrade) > 0, "the bot to ask for the trade");

        // The customer has helped the bot: a thanks is queued and, at a world time where the roll says tip, the lane starts a tip.
        int level = await scene.World.Host.PlayerStateAsync(BotName, p => (int)p.Level);
        uint tipCopper = PlayerbotGratitude.TipCopper(50_000, level, scene.World.Options.Social.Gratitude.MaxTipCopper);
        Assert.True(tipCopper > 0 && tipCopper != Price, $"the tip ({tipCopper}) must differ from the price");
        float generosity = PlayerbotTraits.For(scene.BotGuid.Value).Generosity;
        await scene.World.Host.OnWorldAsync(() =>
        {
            PlayerbotSocialRecorder recorder = HelpTestKit.RecorderOf(scene.World);
            for (int i = 0; i < 60 && !(recorder.Memory.TryGet(scene.Bot, scene.CustomerGuid.Value, out PlayerbotRelation r) && r.Affinity >= PlayerbotGratitude.FavourAffinity); i++)
                recorder.NoteById(scene.Bot, scene.BotGuid, scene.CustomerGuid, PlayerbotSocialEvent.GroupSucceeded);
            recorder.NoteHelp(HelpTestKit.Online(scene.World, CustomerName), HelpTestKit.Online(scene.World, BotName));
        });
        long now = HelpTestKit.NowWhere(t => PlayerbotGratitude.FavourOf(scene.BotGuid.Value, scene.CustomerGuid.Value, (uint)(t / 1000), generosity, 100, 1f, true, true, true) == PlayerbotFavour.Tip);
        await scene.GratitudePassAsync(now);
        Assert.Equal(1, scene.Gratitude.TipsStarted); // control: the thanks did roll a tip
        Assert.Equal(0, scene.Gratitude.TipsPending); // and the lane dropped it at once: the partner has a desk deal with the bot

        await scene.Customer!.SendAsync(WorldOpcode.CmsgBeginTrade, []);
        await HelpTestKit.UntilAsync(() => scene.TradeAsync().Result.Opened, "the window to open");
        await scene.Customer.SendAsync(WorldOpcode.CmsgSetTradeItem, item);
        var botGold = new HashSet<uint>();
        bool accepted = false;
        for (int i = 0; i < 60 && !accepted; i++)
        {
            await scene.GratitudePassAsync(now += 1000);
            Trade trade = await scene.TradeAsync();
            botGold.Add(trade.BotGold);
            accepted = trade.BotAccepted;
            await Task.Delay(100);
        }

        Assert.True(accepted, "the bot never accepted the matching window");
        Assert.Equal(Price, (await scene.TradeAsync()).BotGold);
        await scene.AcceptAsync(scene.Customer);
        for (int i = 0; i < 100 && (await scene.TradeAsync()).Exists; i++)
        {
            await scene.GratitudePassAsync(now += 1000);
            botGold.Add((await scene.TradeAsync()).BotGold);
            await Task.Delay(100);
        }

        Assert.False((await scene.TradeAsync()).Exists, "the window did not complete");
        await HelpTestKit.UntilAsync(() => scene.Ledger(scene.CustomerGuid) is { FairDeals: > 0 }, "the sale to be recorded");
        await scene.GratitudePassAsync(now += 1000);
        Assert.True(botGold.IsSubsetOf(new HashSet<uint> { 0, Price }), "the bot's gold was " + string.Join(",", botGold));
        Assert.Equal(1, scene.BotSent(WorldOpcode.CmsgSetTradeGold)); // only the desk put gold in
        Assert.Equal(1, scene.BotSent(WorldOpcode.CmsgAcceptTrade));
        Assert.Equal(50_000u - Price, await scene.MoneyAsync(BotName));
        Assert.Equal(5000u + Price, await scene.MoneyAsync(CustomerName));
        Assert.Equal(1u, await scene.CountAsync(BotName, DealEntry));
        Assert.Equal(0u, await scene.CountAsync(CustomerName, DealEntry));
        Assert.Equal(0, scene.Gratitude.TipsPaid);
        Assert.Equal(0, scene.Gratitude.TipsPending);
        Assert.False(await scene.World.Host.OnWorldAsync(() => HelpTestKit.RecorderOf(scene.World).TryTakeTradeDone(scene.Bot)), "the desk sale was left as a completed trade for the tip lane to count");
    }

    /// <summary>
    /// A buyer puts exactly the agreed price and accepts only with exactly that gold. The test sets the bot's gold itself to a wrong sum right after the desk placed the
    /// price (below it, then above it; both inside the window's 1.5 s hold, so the bot has not accepted yet): the bot corrects it to exactly the price, never has an accept
    /// standing with any other gold, and the sale completes at the price. Broken: a gold below the price (still "at most the price") would be accepted and the bot would
    /// pay less than it agreed or the partner would be shorted by a changed amount, and a gold above the price would never be corrected.
    /// </summary>
    [Fact]
    public async Task TradeDesk_Buyer_PutsExactlyThePrice()
    {
        await using Scene scene = await Scene.StartAsync();
        await scene.AddCustomerAsync();
        await scene.SetMoneyAsync(BotName, 5000);
        byte[] item = await scene.GiveAsync(CustomerName, DealEntry);
        Assert.True((await scene.QuoteAsync(PlayerbotDealRole.Buyer, DealEntry)).Ok);
        await scene.OpenWindowAsync(scene.Customer!);
        await scene.Customer!.SendAsync(WorldOpcode.CmsgSetTradeItem, item);
        await HelpTestKit.UntilAsync(() => scene.TradeAsync().Result.BotGold == Price, "the bot to put the price in");

        Assert.True(await scene.World.SendAsync(BotName, WorldOpcode.CmsgSetTradeGold, Gold(Price - 600)));
        await HelpTestKit.UntilAsync(() => scene.TradeAsync().Result.BotGold == Price, "the bot to correct a gold below the price");
        Assert.True(await scene.World.SendAsync(BotName, WorldOpcode.CmsgSetTradeGold, Gold(Price + 500)));
        await HelpTestKit.UntilAsync(() => scene.TradeAsync().Result.BotGold == Price, "the bot to correct a gold above the price");

        bool accepted = false;
        for (int waited = 0; waited < 8000 && !accepted; waited += 25)
        {
            Trade trade = await scene.TradeAsync();
            Assert.True(!trade.BotAccepted || trade.BotGold == Price, $"the bot has an accept standing with gold {trade.BotGold}");
            accepted = trade.BotAccepted;
            if (!accepted) await Task.Delay(25);
        }

        Assert.True(accepted, "the bot never accepted the corrected window");
        Assert.Equal(Price, (await scene.TradeAsync()).BotGold);
        await scene.AcceptAsync(scene.Customer);
        await HelpTestKit.UntilAsync(() => !scene.TradeAsync().Result.Exists, "the sale to complete");
        Assert.Equal(1, scene.BotSent(WorldOpcode.CmsgAcceptTrade));
        Assert.Equal(5000u - Price, await scene.MoneyAsync(BotName));
        Assert.Equal(5000u + Price, await scene.MoneyAsync(CustomerName));
        Assert.Equal(1u, await scene.CountAsync(BotName, DealEntry));
        Assert.Equal(0u, await scene.CountAsync(CustomerName, DealEntry));
    }

    // --- guards on the other trade drivers ------------------------------------------------------------------------------------------

    /// <summary>
    /// The mage-fee leg's <c>Pay</c> run while a desk window is open (the bot's seller deal with the customer; the mage is somebody else) must not initiate, put gold in,
    /// accept or cancel anything, before and after its PayWaitMs runs out (it then fails like an unpaid fee). Control: once the desk's deal is gone, the same call does
    /// start the fee trade with the mage. Broken: the old Pay saw a trade that did not involve the mage and cancelled it, or put the fee into the desk's window.
    /// </summary>
    [Fact]
    public async Task TradeDesk_FeeLegPay_NeverTouchesADeskWindow()
    {
        await using Scene scene = await Scene.StartAsync();
        await scene.AddCustomerAsync();
        await scene.AddSecondCustomerAsync(); // plays the mage
        await scene.GiveAsync(BotName, DealEntry);
        Assert.True((await scene.QuoteAsync(PlayerbotDealRole.Seller, DealEntry)).Ok);
        await scene.OpenWindowAsync(scene.Customer!);
        await HelpTestKit.UntilAsync(() => scene.TradeAsync().Result.BotItems == 1, "the bot to put the item in");
        Assert.Equal(PlayerbotDealState.Window, scene.Desk.StateOf(scene.Bot, scene.CustomerGuid));
        int cancels = scene.BotSent(WorldOpcode.CmsgCancelTrade), golds = scene.BotSent(WorldOpcode.CmsgSetTradeGold);
        int accepts = scene.BotSent(WorldOpcode.CmsgAcceptTrade), initiates = scene.BotSent(WorldOpcode.CmsgInitiateTrade);

        (TravelLegStep Step, string? Failure) Pay(uint now, uint stageMs) => scene.World.Host.OnWorldAsync(() =>
        {
            Player bot = HelpTestKit.Online(scene.World, BotName);
            WorldSession session = PartyTestHost.Feature(scene.World.Host).FindSession(scene.Bot)!;
            var leg = new MageLeg(TravelMeans.MagePortal, 0, new TravelPlace(0, System.Numerics.Vector3.Zero), stageMs) { Stage = MageLegStage.Paying, MoneyBefore = bot.Money };
            var request = new PortalRequest(bot.Guid.Value, BotName, bot.Team, 0, System.Numerics.Vector3.Zero, 0, 0, 100, stageMs);
            TravelLegStep step = PlayerbotMageService.Pay(session, bot, leg, request, HelpTestKit.Online(scene.World, SecondCustomerName), now, out string? failure);
            return (step, failure);
        }).Result;

        Assert.Equal(TravelLegStep.Traveling, Pay(2000, 1000).Step);
        (TravelLegStep late, string? failure) = Pay(1000 + PlayerbotMageService.PayWaitMs + 1, 1000);
        Assert.Equal(TravelLegStep.Failed, late);
        Assert.NotNull(failure);
        await Task.Delay(300);
        Assert.True((await scene.TradeAsync()).Exists, "the fee leg cancelled the desk's window");
        Assert.Equal(PlayerbotDealState.Window, scene.Desk.StateOf(scene.Bot, scene.CustomerGuid));
        Assert.Equal(cancels, scene.BotSent(WorldOpcode.CmsgCancelTrade));
        Assert.Equal(golds, scene.BotSent(WorldOpcode.CmsgSetTradeGold));
        Assert.Equal(accepts, scene.BotSent(WorldOpcode.CmsgAcceptTrade));
        Assert.Equal(initiates, scene.BotSent(WorldOpcode.CmsgInitiateTrade));

        // Control: with the desk's deal gone the same call starts the fee trade with the mage.
        await scene.Customer!.SendAsync(WorldOpcode.CmsgCancelTrade, []);
        await HelpTestKit.UntilAsync(() => scene.Desk.Count == 0 && !scene.TradeAsync().Result.Exists, "the desk deal to end");
        Assert.Equal(TravelLegStep.Traveling, Pay(2000, 1000).Step);
        await HelpTestKit.UntilAsync(() => scene.BotSent(WorldOpcode.CmsgInitiateTrade) > initiates, "the fee trade to be started");
    }

    /// <summary>
    /// Starts a gratitude tip from the generous desk bot to the customer (the same setup as the pending-tip test) and returns the world time of the pass that started it.
    /// </summary>
    private static async Task<long> StartTipAsync(Scene scene)
    {
        int level = await scene.World.Host.PlayerStateAsync(BotName, p => (int)p.Level);
        Assert.True(PlayerbotGratitude.TipCopper(50_000, level, scene.World.Options.Social.Gratitude.MaxTipCopper) > 0);
        float generosity = PlayerbotTraits.For(scene.BotGuid.Value).Generosity;
        await scene.World.Host.OnWorldAsync(() =>
        {
            PlayerbotSocialRecorder recorder = HelpTestKit.RecorderOf(scene.World);
            for (int i = 0; i < 60 && !(recorder.Memory.TryGet(scene.Bot, scene.CustomerGuid.Value, out PlayerbotRelation r) && r.Affinity >= PlayerbotGratitude.FavourAffinity); i++)
                recorder.NoteById(scene.Bot, scene.BotGuid, scene.CustomerGuid, PlayerbotSocialEvent.GroupSucceeded);
            recorder.NoteHelp(HelpTestKit.Online(scene.World, CustomerName), HelpTestKit.Online(scene.World, BotName));
        });
        long now = HelpTestKit.NowWhere(t => PlayerbotGratitude.FavourOf(scene.BotGuid.Value, scene.CustomerGuid.Value, (uint)(t / 1000), generosity, 100, 1f, true, true, true) == PlayerbotFavour.Tip);
        await scene.GratitudePassAsync(now);
        Assert.Equal(1, scene.Gratitude.TipsStarted);
        Assert.Equal(1, scene.Gratitude.TipsPending);
        await HelpTestKit.UntilAsync(() => scene.TradeAsync().Result.Exists, "the tip's trade request to reach the server");
        return now;
    }

    /// <summary>
    /// A tip already under way (its trade request is out) when the bot agrees a deal with the same customer: the next gratitude pass drops the tip and sends nothing to the
    /// trade (no cancel, no gold, no accept); the trade stays. Broken: the tip step would carry on (still pending) or cancel the window the desk is about to use.
    /// </summary>
    [Fact]
    public async Task TradeDesk_TipStep_DropsWithoutCancelling_WhenTheBotHoldsADeal()
    {
        await using Scene scene = await Scene.StartAsync(generous: true);
        await scene.AddCustomerAsync();
        await scene.SetMoneyAsync(BotName, 50_000);
        await scene.GiveAsync(BotName, DealEntry);
        long now = await StartTipAsync(scene);
        Assert.True((await scene.QuoteAsync(PlayerbotDealRole.Seller, DealEntry)).Ok);
        Assert.Equal(PlayerbotDealHeardKind.Agreed, (await scene.HearAsync("deal")).Kind);

        await scene.GratitudePassAsync(now + 1000);
        Assert.Equal(0, scene.Gratitude.TipsPending);
        await Task.Delay(300);
        Assert.Equal(0, scene.BotSent(WorldOpcode.CmsgCancelTrade));
        Assert.Equal(0, scene.BotSent(WorldOpcode.CmsgSetTradeGold));
        Assert.True((await scene.TradeAsync()).Exists, "the tip step cancelled the trade");
    }

    /// <summary>
    /// The cancel paths of a tip (the lane switched off, the bot missing from a pass, trading off) share one helper: with the bot holding a desk deal it must not send
    /// CMSG_CANCEL_TRADE. Here a tip's request is out, a deal is agreed with the same customer, and every tip is cancelled at once. Broken: the trade is cancelled.
    /// </summary>
    [Fact]
    public async Task TradeDesk_TipCancel_NeverCancelsATradeOfABotHoldingADeal()
    {
        await using Scene scene = await Scene.StartAsync(generous: true);
        await scene.AddCustomerAsync();
        await scene.SetMoneyAsync(BotName, 50_000);
        await scene.GiveAsync(BotName, DealEntry);
        await StartTipAsync(scene);
        Assert.True((await scene.QuoteAsync(PlayerbotDealRole.Seller, DealEntry)).Ok);
        Assert.Equal(PlayerbotDealHeardKind.Agreed, (await scene.HearAsync("deal")).Kind);

        await scene.World.Host.OnWorldAsync(() =>
        {
            scene.Gratitude.CancelAllTips();
            return true;
        });
        await Task.Delay(300);
        Assert.Equal(0, scene.BotSent(WorldOpcode.CmsgCancelTrade));
        Assert.True((await scene.TradeAsync()).Exists, "CancelAllTips cancelled the trade");
    }

    /// <summary>
    /// A window is the desk's only when the desk opened or claimed it. An agreed deal with a customer 30 yards away (the bot waits for the customer to come near) does not
    /// own a window with that customer: the party AI's gift rules keep any such window (the master's own "trade me"). Once the customer is in reach the bot starts the
    /// trade itself and the window is the desk's. Broken: an agreed deal alone would own the window and the gift rules would be shut out.
    /// </summary>
    [Fact]
    public async Task TradeDesk_AgreedDealOwnsNoWindowUntilTheDeskOpensOne()
    {
        await using Scene scene = await Scene.StartAsync();
        await scene.AddCustomerAsync();
        await scene.GiveAsync(BotName, DealEntry);
        await scene.World.Host.OnWorldAsync(() =>
        {
            Player bot = HelpTestKit.Online(scene.World, BotName), customer = HelpTestKit.Online(scene.World, CustomerName);
            customer.Relocate(bot.X + 30f, bot.Y, bot.Z, 0f, scene.World.Host.World.NowMs);
            return true;
        });
        Assert.True((await scene.QuoteAsync(PlayerbotDealRole.Seller, DealEntry)).Ok);
        Assert.Equal(PlayerbotDealHeardKind.Agreed, (await scene.HearAsync("deal")).Kind);
        await Task.Delay(500);
        Assert.Equal(PlayerbotDealState.Agreed, scene.Desk.StateOf(scene.Bot, scene.CustomerGuid));
        Assert.False(scene.Desk.Owns(scene.Bot, scene.CustomerGuid), "an agreed deal the desk has not opened a window for owns no window");
        Assert.True(scene.Desk.HoldsGuid(scene.BotGuid), "the other drivers still keep off a bot that holds a deal");

        await scene.World.Host.OnWorldAsync(() =>
        {
            Player bot = HelpTestKit.Online(scene.World, BotName), customer = HelpTestKit.Online(scene.World, CustomerName);
            customer.Relocate(bot.X + 2f, bot.Y, bot.Z, 0f, scene.World.Host.World.NowMs);
            return true;
        });
        await HelpTestKit.UntilAsync(() => scene.BotSent(WorldOpcode.CmsgInitiateTrade) > 0, "the bot to ask for the trade");
        Assert.True(scene.Desk.Owns(scene.Bot, scene.CustomerGuid), "the window the desk started is the desk's");
    }
}
