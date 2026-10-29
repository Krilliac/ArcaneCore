using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Bots;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Tests.Items;
using ArcaneCore.World.Tests.Playerbots.Social;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Party;

/// <summary>
/// P4 S0 (docs/design: playerbot trading, section 10): the party AI's trade answers. The blind accept on SMSG_TRADE_STATUS 4 let a
/// partner talk a bot into a swap it never checked once anything sat on the bot's side, and any stranger's BeginTrade overwrote the
/// partner that the completed trade was credited to. Every test runs the real trade handlers on a real world with a real client for the
/// player; the bot's answers are its own client packets, recorded by <see cref="DispatchLog"/>.
/// </summary>
public sealed class PlayerbotPartyTradeTests
{
    private const uint GreyEntry = 995_001, CheapEntry = 995_002, WorthEntry = 995_003, RichEntry = 995_004, HalfEntry = 995_005, BotItemEntry = 995_006, BootsEntry = 995_007;

    // The vendor sell value at which a gift counts as a trade for the bot's memory: the GiftMinValueCopper default of the design (section 5).
    private const uint GiftMinValueCopper = 100;

    private const string BotName = "Tradebot", MasterName = "Trademaster", StrangerName = "Wanderer";

    private static ItemTemplate Template(uint entry, string name, uint sellPrice, uint stackable = 1)
        => new() { Entry = entry, Class = 15, SubClass = 0, Name = name, DisplayId = 1, Quality = 1, SellPrice = sellPrice, Stackable = stackable };

    private static byte[] Gold(uint copper) => BitConverter.GetBytes(copper);

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

        public WorldTestClient? Master { get; private set; }

        public WorldTestClient? Stranger { get; private set; }

        public ObjectGuid MasterGuid { get; private set; }

        public ObjectGuid StrangerGuid { get; private set; }

        public static async Task<Scene> StartAsync(bool chat = false)
        {
            var items = new ItemTestContent();
            items.Templates.Templates.AddRange(
            [
                Template(GreyEntry, "Tradetest Grey", 1),
                Template(CheapEntry, "Tradetest Cheap", GiftMinValueCopper - 1),
                Template(WorthEntry, "Tradetest Worth", GiftMinValueCopper),
                Template(RichEntry, "Tradetest Rich", 5_000),
                Template(HalfEntry, "Tradetest Half", GiftMinValueCopper / 2, stackable: 20),
                Template(BotItemEntry, "Tradetest Botitem", 500),
                PlayerbotEquipmentTests.Template(BootsEntry, InventoryType.Feet, 70),
            ]);
            SocialWorld world;
            using (items.Use())
            {
                world = await SocialWorld.StartAsync(
                    options =>
                    {
                        options.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone;
                        options.Chat.Enabled = chat;
                        options.Chat.PerPlayerCooldownSeconds = 0;
                        options.Social.Memory.MaxDailyAffinityGainPerPair = 0; // unlimited: the daily cap must not hide an affinity delta
                        options.Social.Memory.AffinityPerChat = 0; // the "trade me" whisper is a chat exchange; only the trade may move affinity here
                    },
                    frozen: true,
                    services: services => services.AddScoped<ArcaneCore.Kernel.Economy.IEconomyStore>(_ => new PlayerbotPartyTests.StubEconomyStore()));
            }

            Guid bot = await world.AddBotAsync(BotName, money: 50);
            ObjectGuid botGuid = await world.Host.PlayerStateAsync(BotName, p => p.Guid);
            return new Scene(world, bot, botGuid);
        }

        public EconomyFeature Economy => World.Host.WorldServices.GetRequiredService<EconomyFeature>();

        public async Task AddMasterAsync()
        {
            Master = await World.AddPlayerAsync(MasterName);
            MasterGuid = await World.Host.PlayerStateAsync(MasterName, p => p.Guid);
            await Master.SendAsync(WorldOpcode.CmsgGroupInvite, PartyTestHost.CString(BotName));
            await World.Host.WaitForWorldAsync(() => PartyTestHost.Feature(World.Host).IsPartyDriven(Bot), "the party AI drives the bot");
            await PlaceBesideBotAsync(MasterName);
        }

        public async Task AddStrangerAsync()
        {
            Stranger = await World.AddPlayerAsync(StrangerName);
            StrangerGuid = await World.Host.PlayerStateAsync(StrangerName, p => p.Guid);
            await PlaceBesideBotAsync(StrangerName);
        }

        private async Task PlaceBesideBotAsync(string player)
        {
            await World.Host.OnWorldAsync(() =>
            {
                Player bot = HelpTestKit.Online(World, BotName);
                PlayerbotMovementControl.Stop(PartyTestHost.Feature(World.Host).FindSession(Bot)!, bot);
                bot.Inventory.Load([]);
                Player other = HelpTestKit.Online(World, player);
                other.Inventory.Load([]);
                other.Relocate(bot.X + 2f, bot.Y, bot.Z, 0f, World.Host.World.NowMs);
            });
            await World.Host.WaitForWorldAsync(() => HelpTestKit.Online(World, BotName).VisibleObjects.Contains(HelpTestKit.Online(World, player).Guid), player + " in sight");
        }

        /// <summary>Put <paramref name="count"/> of <paramref name="entry"/> in a player's bags (world thread) and return where, as the CMSG_SET_TRADE_ITEM body for trade slot 0.</summary>
        public Task<byte[]> GiveAsync(string player, uint entry, uint count = 1) => World.Host.OnWorldAsync(() =>
        {
            Player owner = HelpTestKit.Online(World, player);
            Assert.Equal(InventoryResult.Ok, owner.Inventory.AddItem(entry, count, out Item? item));
            return new byte[] { 0, item!.BagSlot, item.Slot };
        });

        public Task<uint> CountAsync(string player, uint entry) => World.Host.OnWorldAsync(() => HelpTestKit.Online(World, player).Inventory.GetItemCount(entry));

        public Task<uint> MoneyAsync(string player) => World.Host.PlayerStateAsync(player, p => p.Money);

        public Task<TradeSession?> TradeAsync(string player) => World.Host.OnWorldAsync(() => Economy.TradeOf(HelpTestKit.Online(World, player)));

        public Task<(bool Exists, bool Opened, bool BotAccepted, bool PlayerAccepted, uint BotGold)> StateAsync(string player) => World.Host.OnWorldAsync(() =>
        {
            Player other = HelpTestKit.Online(World, player);
            return Economy.TradeOf(other) is { } trade
                ? (true, trade.Opened, trade.SideOf(HelpTestKit.Online(World, BotName)).Accepted, trade.SideOf(other).Accepted, trade.SideOf(HelpTestKit.Online(World, BotName)).Gold)
                : (false, false, false, false, 0u);
        });

        /// <summary>The player opens a window with the bot and the bot's party AI answers (BeginTrade).</summary>
        public async Task OpenWindowAsync(WorldTestClient client, string player)
        {
            var initiate = new PacketWriter(8);
            initiate.WriteUInt64(BotGuid.Value);
            await client.SendAsync(WorldOpcode.CmsgInitiateTrade, initiate.ToArray());
            await HelpTestKit.UntilAsync(() => StateAsync(player).Result.Opened, "the bot to open the trade window");
        }

        /// <summary>
        /// The player accepts once the server would not bounce the accept (scam prevention). The server measures from the change it HANDLED, in whole
        /// seconds of its own clock, so a fixed client-side delay raced a change still queued on the world thread: the change landed late, the accept
        /// fell in the same second, was bounced, and nobody re-accepted. Instead the client's earlier packets are drained through a barrier (all handled),
        /// then the accept waits until the trade is gone or the accepting side's last change is outside the window.
        /// </summary>
        public async Task AcceptAsync(WorldTestClient client)
        {
            string player = ReferenceEquals(client, Master) ? MasterName : ReferenceEquals(client, Stranger) ? StrangerName : throw new ArgumentException("not a scene client", nameof(client));
            await client.CollectThroughBarrierAsync();
            // EconomyFeature measures with its TimeProvider (TimeProvider.System unless the host registers one), as unix milliseconds.
            TimeProvider clock = World.Host.WorldServices.GetService<TimeProvider>() ?? TimeProvider.System;
            await HelpTestKit.UntilAsync(() => World.Host.OnWorldAsync(() =>
                Economy.TradeOf(HelpTestKit.Online(World, player)) is not { } trade
                || !TradeRules.ScamPrevented(trade.SideOf(HelpTestKit.Online(World, player)).LastModifiedMs, clock.GetUtcNow().ToUnixTimeMilliseconds(),
                    Economy.Options.TradeScamPreventionMs, Economy.Options.TradeScamPreventionWholeSeconds)).Result, "the server's scam-prevention window to pass");
            await client.SendAsync(WorldOpcode.CmsgAcceptTrade, Gold(1));
        }

        /// <summary>The bot's capture queue is handed a trade status, as the server would send it to the bot.</summary>
        public Task InjectStatusAsync(uint status, ObjectGuid? trader = null) => World.Host.OnWorldAsync(() =>
        {
            var packet = new PacketWriter(12);
            packet.WriteUInt32(status);
            if (trader is { } guid) packet.WriteUInt64(guid.Value);
            PartyTestHost.Feature(World.Host).FindSession(Bot)!.Send(WorldOpcode.SmsgTradeStatus, packet.ToArray());
        });

        /// <summary>The bot's own accounting of a finished trade: set by the party AI on status 8 (the tip lane reads it). Taking it also proves the status was handled.</summary>
        public Task WaitForBotToSeeCompletionAsync() => HelpTestKit.UntilAsync(
            () => World.Host.OnWorldAsync(() => HelpTestKit.RecorderOf(World).TryTakeTradeDone(Bot)).Result, "the bot to see the trade complete");

        public Task<int> AffinityAsync(ObjectGuid other) => World.Host.OnWorldAsync(() =>
            HelpTestKit.RecorderOf(World).Memory.TryGet(Bot, other.Value, out PlayerbotRelation relation) ? (int)relation.Affinity : 0);

        public int AcceptsSent => World.Log.Count(BotName, WorldOpcode.CmsgAcceptTrade);

        public async ValueTask DisposeAsync()
        {
            if (Master is not null) await Master.DisposeAsync();
            if (Stranger is not null) await Stranger.DisposeAsync();
            await World.DisposeAsync();
        }
    }

    private static int AffinityPerTrade(Scene scene) => scene.World.Options.Social.Memory.AffinityPerTrade;

    /// <summary>
    /// The scam the blind accept allowed: the master offers gold, the bot puts something of its own on its side, the master takes its gold back and
    /// accepts. The bot must not accept a window in which it gives something. Control: once the bot has taken its own offer back and the master
    /// accepts again, the (empty) window completes, which also proves the bot was reading the earlier accept and decided against it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABotThatGivesSomething_DoesNotAcceptTheMastersReAccept_AndAcceptsOnceItsSideIsEmpty(bool item)
    {
        await using Scene scene = await Scene.StartAsync();
        await scene.AddMasterAsync();
        byte[] botOffer = item ? await scene.GiveAsync(BotName, BotItemEntry) : [];
        await scene.OpenWindowAsync(scene.Master!, MasterName);

        await scene.Master!.SendAsync(WorldOpcode.CmsgSetTradeGold, Gold(10));
        Assert.True(item
            ? await scene.World.SendAsync(BotName, WorldOpcode.CmsgSetTradeItem, botOffer)
            : await scene.World.SendAsync(BotName, WorldOpcode.CmsgSetTradeGold, Gold(5)));
        await scene.Master.SendAsync(WorldOpcode.CmsgSetTradeGold, Gold(0)); // the master takes its gold back, then accepts
        await scene.AcceptAsync(scene.Master);
        await HelpTestKit.UntilAsync(() => scene.StateAsync(MasterName).Result is { Exists: false } or { PlayerAccepted: true }, "the master's accept to reach the server");

        // Give the bot's intake ample time to act on the accept it was sent; it returns at once if the bot (wrongly) accepts.
        for (int waited = 0; waited < 1500 && scene.AcceptsSent == 0; waited += 20) await Task.Delay(20);
        Assert.Equal(0, scene.AcceptsSent);
        (bool exists, _, bool botAccepted, _, _) = await scene.StateAsync(MasterName);
        Assert.True(exists, "the window is still open");
        Assert.False(botAccepted);
        Assert.Equal(50u, await scene.MoneyAsync(BotName));
        Assert.Equal(item ? 1u : 0u, await scene.CountAsync(BotName, BotItemEntry));

        // Control: the bot takes its offer back (the server clears both accepts); the master accepts again; now the bot's side is empty.
        Assert.True(item
            ? await scene.World.SendAsync(BotName, WorldOpcode.CmsgClearTradeItem, [0])
            : await scene.World.SendAsync(BotName, WorldOpcode.CmsgSetTradeGold, Gold(0)));
        await scene.AcceptAsync(scene.Master);
        await HelpTestKit.UntilAsync(() => scene.StateAsync(MasterName).Result is { Exists: false }, "the window with an empty bot side to complete");
        Assert.Equal(1, scene.AcceptsSent);
        Assert.Equal(50u, await scene.MoneyAsync(BotName));
    }

    /// <summary>A gift from the master with the bot's side empty still completes, and the bot ends up holding it.</summary>
    [Fact]
    public async Task AMastersGift_WithTheBotsSideEmpty_Completes_AndTheBotHoldsTheItem()
    {
        await using Scene scene = await Scene.StartAsync();
        await scene.AddMasterAsync();
        byte[] offer = await scene.GiveAsync(MasterName, RichEntry);
        await scene.OpenWindowAsync(scene.Master!, MasterName);
        await scene.Master!.SendAsync(WorldOpcode.CmsgSetTradeItem, offer);
        await scene.AcceptAsync(scene.Master);
        await HelpTestKit.UntilAsync(() => scene.StateAsync(MasterName).Result is { Exists: false }, "the gift to complete");
        await scene.WaitForBotToSeeCompletionAsync();

        Assert.Equal(1, scene.AcceptsSent);
        Assert.Equal(1u, await scene.CountAsync(BotName, RichEntry));
        Assert.Equal(0u, await scene.CountAsync(MasterName, RichEntry));
    }

    /// <summary>A gift of gear that is an upgrade is worn: the status 4 and 8 paths still run the equipment pass after the trade-safety change.</summary>
    [Fact]
    public async Task AMastersGiftOfAnUpgrade_IsWorn()
    {
        await using Scene scene = await Scene.StartAsync();
        await scene.AddMasterAsync();
        byte[] offer = await scene.GiveAsync(MasterName, BootsEntry);
        await scene.OpenWindowAsync(scene.Master!, MasterName);
        await scene.Master!.SendAsync(WorldOpcode.CmsgSetTradeItem, offer);
        await scene.AcceptAsync(scene.Master);
        await HelpTestKit.UntilAsync(() => scene.StateAsync(MasterName).Result is { Exists: false }, "the gift to complete");
        await scene.WaitForBotToSeeCompletionAsync();

        await HelpTestKit.UntilAsync(() => scene.World.Host.OnWorldAsync(() =>
            HelpTestKit.Online(scene.World, BotName).Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Feet)?.Entry == BootsEntry).Result, "the bot to wear the gift");
        Assert.Equal(1u, await scene.CountAsync(BotName, BootsEntry));
    }

    /// <summary>
    /// Affinity from the party AI's trade note (<c>TradeCompleted</c>, +AffinityPerTrade): a worthless gift gave the junk-gift farm its affinity.
    /// Now only a gift worth at least <c>GiftMinValueCopper</c> of vendor value counts, summed over the stack. The boundary is the design's default (100).
    /// </summary>
    [Theory]
    [InlineData(GreyEntry, 1u, false)]
    [InlineData(CheapEntry, 1u, false)]
    [InlineData(WorthEntry, 1u, true)]
    [InlineData(RichEntry, 1u, true)]
    [InlineData(HalfEntry, 1u, false)]
    [InlineData(HalfEntry, 2u, true)]
    public async Task AGroupmatesGift_CountsAsATrade_OnlyAtOrAboveTheMinimumVendorValue(uint entry, uint count, bool counts)
    {
        await using Scene scene = await Scene.StartAsync();
        await scene.AddMasterAsync();
        byte[] offer = await scene.GiveAsync(MasterName, entry, count);
        int before = await scene.AffinityAsync(scene.MasterGuid);
        await scene.OpenWindowAsync(scene.Master!, MasterName);
        await scene.Master!.SendAsync(WorldOpcode.CmsgSetTradeItem, offer);
        await scene.AcceptAsync(scene.Master);
        await HelpTestKit.UntilAsync(() => scene.StateAsync(MasterName).Result is { Exists: false }, "the gift to complete");
        await scene.WaitForBotToSeeCompletionAsync();

        Assert.Equal(count, await scene.CountAsync(BotName, entry)); // the gift itself is taken either way
        Assert.Equal(before + (counts ? AffinityPerTrade(scene) : 0), await scene.AffinityAsync(scene.MasterGuid));
    }

    /// <summary>
    /// A window cancelled after the bot refused to accept it (its own side was not empty, so the window was counted) is forgotten: a later
    /// completed window the party AI did not open (the mage fee, a tip) must not credit the earlier partner.
    /// </summary>
    [Fact]
    public async Task ACancelledWindow_IsForgotten_ALaterCompletionGivesItsPartnerNoAffinity()
    {
        await using Scene scene = await Scene.StartAsync();
        await scene.AddMasterAsync();
        int before = await scene.AffinityAsync(scene.MasterGuid);
        await scene.OpenWindowAsync(scene.Master!, MasterName);
        Assert.True(await scene.World.SendAsync(BotName, WorldOpcode.CmsgSetTradeGold, Gold(5)));
        await scene.AcceptAsync(scene.Master!);
        await HelpTestKit.UntilAsync(() => scene.StateAsync(MasterName).Result is { Exists: false } or { PlayerAccepted: true }, "the master's accept to reach the server");
        await Task.Delay(500); // the bot reads the accept and refuses (it gives gold), which marks the window as counted
        await scene.Master!.SendAsync(WorldOpcode.CmsgCancelTrade, []);
        await HelpTestKit.UntilAsync(() => scene.StateAsync(MasterName).Result is { Exists: false }, "the window to be cancelled");

        await scene.InjectStatusAsync(8); // a completion of a window the party AI never opened
        await scene.WaitForBotToSeeCompletionAsync();

        Assert.Equal(0, scene.AcceptsSent);
        Assert.Equal(before, await scene.AffinityAsync(scene.MasterGuid));
    }

    /// <summary>
    /// A bot-initiated window ("trade me") the partner bounces with CMSG_BUSY_TRADE is forgotten like any other close: a later completion the party AI
    /// did not open (a tip where the bot gives gold) must not credit that player.
    /// </summary>
    [Fact]
    public async Task ABotInitiatedWindow_AnsweredWithBusy_IsForgotten_ALaterCompletionGivesNoAffinity()
    {
        await using Scene scene = await Scene.StartAsync(chat: true);
        await scene.AddStrangerAsync();
        await HelpTestKit.StandAsync(scene.World, BotName, (StrangerName, 2f));

        await scene.Stranger!.SendChatAsync(ChatType.Whisper, Language.Common, "trade me", BotName);
        await HelpTestKit.UntilAsync(() => scene.World.Log.Count(BotName, WorldOpcode.CmsgInitiateTrade) > 0, "the bot to initiate the trade", seconds: 60);
        await scene.Stranger.SendAsync(WorldOpcode.CmsgBusyTrade, []);
        // The busy answer arrives over the stranger's socket and is handled on a later world tick, while the bot's packets below are handled
        // at once on the world thread. If the busy answer is still in flight, the bot's second CMSG_INITIATE_TRADE is ignored (the first
        // invitation still stands), the busy answer then closes the first one, and the stranger's CMSG_BEGIN_TRADE finds no window to open.
        // Wait until the server has closed the first invitation.
        await HelpTestKit.UntilAsync(() => scene.StateAsync(StrangerName).Result is { Exists: false }, "the busy answer to close the first invitation");
        await scene.World.Host.OnWorldAsync(() => HelpTestKit.RecorderOf(scene.World).NoteById(scene.Bot, scene.BotGuid, scene.StrangerGuid, PlayerbotSocialEvent.ChatExchange));
        int before = await scene.AffinityAsync(scene.StrangerGuid);

        // Window two is not the party AI's: the bot opens it itself, as a tip would, and puts gold on its side. The stale partner would make the
        // party AI count it on the stranger's accept and credit it on completion.
        var initiate = new PacketWriter(8);
        initiate.WriteUInt64(scene.StrangerGuid.Value);
        Assert.True(await scene.World.SendAsync(BotName, WorldOpcode.CmsgInitiateTrade, initiate.ToArray()));
        await scene.Stranger.SendAsync(WorldOpcode.CmsgBeginTrade, []);
        await HelpTestKit.UntilAsync(() => scene.StateAsync(StrangerName).Result.Opened, "the second window to open");
        Assert.True(await scene.World.SendAsync(BotName, WorldOpcode.CmsgSetTradeGold, Gold(5)));
        await scene.AcceptAsync(scene.Stranger);
        await HelpTestKit.UntilAsync(() => scene.StateAsync(StrangerName).Result is { Exists: false } or { PlayerAccepted: true }, "the stranger's accept to reach the server");
        Assert.True(await scene.World.SendAsync(BotName, WorldOpcode.CmsgAcceptTrade, Gold(1)));
        await HelpTestKit.UntilAsync(() => scene.StateAsync(StrangerName).Result is { Exists: false }, "the tip window to complete");
        await scene.WaitForBotToSeeCompletionAsync();

        Assert.Equal(before, await scene.AffinityAsync(scene.StrangerGuid));
    }

    /// <summary>
    /// The same close, with the bot's intake held (a solo bot drains its capture queue once per ThinkIntervalMs): the bot's own second
    /// CMSG_INITIATE_TRADE opens window two before it reads the busy status 0, so status 0 (and 2 and 4 of window two) are all read while window two is
    /// open. The bot must still forget the first window's partner: its unbound claim and gift partner may never adopt window two.
    /// </summary>
    [Fact]
    public async Task ABusyAnswer_ReadWhileALaterWindowIsOpen_StillForgetsThePartner_NoAffinity()
    {
        await using Scene scene = await Scene.StartAsync(chat: true);
        await scene.AddStrangerAsync();
        await HelpTestKit.StandAsync(scene.World, BotName, (StrangerName, 2f));
        PlayerbotOptions options = PartyTestHost.Feature(scene.World.Host).BotOptions;
        int think = options.ThinkIntervalMs;

        await scene.Stranger!.SendChatAsync(ChatType.Whisper, Language.Common, "trade me", BotName);
        await HelpTestKit.UntilAsync(() => scene.World.Log.Count(BotName, WorldOpcode.CmsgInitiateTrade) > 0, "the bot to initiate the trade", seconds: 60);
        options.ThinkIntervalMs = 60_000; // hold the bot's intake: nothing below is read until it is released
        await scene.Stranger.SendAsync(WorldOpcode.CmsgBusyTrade, []);
        await HelpTestKit.UntilAsync(() => scene.StateAsync(StrangerName).Result is { Exists: false }, "the busy answer to close the first invitation");
        await scene.World.Host.OnWorldAsync(() => HelpTestKit.RecorderOf(scene.World).NoteById(scene.Bot, scene.BotGuid, scene.StrangerGuid, PlayerbotSocialEvent.ChatExchange));
        int before = await scene.AffinityAsync(scene.StrangerGuid);

        var initiate = new PacketWriter(8);
        initiate.WriteUInt64(scene.StrangerGuid.Value);
        Assert.True(await scene.World.SendAsync(BotName, WorldOpcode.CmsgInitiateTrade, initiate.ToArray()));
        await scene.Stranger.SendAsync(WorldOpcode.CmsgBeginTrade, []);
        await HelpTestKit.UntilAsync(() => scene.StateAsync(StrangerName).Result.Opened, "the second window to open");
        Assert.True(await scene.World.SendAsync(BotName, WorldOpcode.CmsgSetTradeGold, Gold(5)));
        await scene.AcceptAsync(scene.Stranger);
        await HelpTestKit.UntilAsync(() => scene.StateAsync(StrangerName).Result is { Exists: false } or { PlayerAccepted: true }, "the stranger's accept to reach the server");

        options.ThinkIntervalMs = think; // release the intake: statuses 0, 2 and 4 are read in one drain, with window two open
        await Task.Delay(1_000);
        Assert.True(await scene.World.SendAsync(BotName, WorldOpcode.CmsgAcceptTrade, Gold(1)));
        await HelpTestKit.UntilAsync(() => scene.StateAsync(StrangerName).Result is { Exists: false }, "the tip window to complete");
        await scene.WaitForBotToSeeCompletionAsync();

        Assert.Equal(before, await scene.AffinityAsync(scene.StrangerGuid));
    }

    /// <summary>A trade that gives the bot nothing (an empty window) is not a trade for its memory.</summary>
    [Fact]
    public async Task AnEmptyWindow_GivesNoAffinity()
    {
        await using Scene scene = await Scene.StartAsync();
        await scene.AddMasterAsync();
        int before = await scene.AffinityAsync(scene.MasterGuid);
        await scene.OpenWindowAsync(scene.Master!, MasterName);
        await scene.AcceptAsync(scene.Master!);
        await HelpTestKit.UntilAsync(() => scene.StateAsync(MasterName).Result is { Exists: false }, "the empty window to complete");
        await scene.WaitForBotToSeeCompletionAsync();

        Assert.Equal(before, await scene.AffinityAsync(scene.MasterGuid));
    }

    /// <summary>
    /// A stranger's BeginTrade (the bot does not open it) must not take over the partner the master's open window is credited to: the completed
    /// trade is the master's, and so is its affinity; the stranger earns nothing.
    /// </summary>
    [Fact]
    public async Task AStrangersBeginTrade_DuringTheMastersWindow_DoesNotMoveTheCredit()
    {
        await using Scene scene = await Scene.StartAsync();
        await scene.AddMasterAsync();
        await scene.AddStrangerAsync();
        byte[] offer = await scene.GiveAsync(MasterName, RichEntry);
        int masterBefore = await scene.AffinityAsync(scene.MasterGuid), strangerBefore = await scene.AffinityAsync(scene.StrangerGuid);
        await scene.OpenWindowAsync(scene.Master!, MasterName);
        await scene.InjectStatusAsync(1, scene.StrangerGuid);
        await scene.Master!.SendAsync(WorldOpcode.CmsgSetTradeItem, offer);
        await scene.AcceptAsync(scene.Master);
        await HelpTestKit.UntilAsync(() => scene.StateAsync(MasterName).Result is { Exists: false }, "the gift to complete");
        await scene.WaitForBotToSeeCompletionAsync();

        Assert.Equal(masterBefore + AffinityPerTrade(scene), await scene.AffinityAsync(scene.MasterGuid));
        Assert.Equal(strangerBefore, await scene.AffinityAsync(scene.StrangerGuid));
    }

    /// <summary>
    /// A TradeAccept status naming a stranger (no window the bot opened, nothing of the bot's own on offer to check) is not accepted. The master's
    /// real gift afterwards is: exactly one accept in all, which also proves the bot had handled the stranger's statuses first (one capture queue, in order).
    /// </summary>
    [Fact]
    public async Task AStrangersTradeAccept_IsNotAccepted_OnlyTheMastersWindowIs()
    {
        await using Scene scene = await Scene.StartAsync();
        await scene.AddMasterAsync();
        await scene.AddStrangerAsync();
        byte[] offer = await scene.GiveAsync(MasterName, RichEntry);
        await scene.InjectStatusAsync(1, scene.StrangerGuid);
        await scene.InjectStatusAsync(4);
        await scene.OpenWindowAsync(scene.Master!, MasterName);
        await scene.Master!.SendAsync(WorldOpcode.CmsgSetTradeItem, offer);
        await scene.AcceptAsync(scene.Master);
        await HelpTestKit.UntilAsync(() => scene.StateAsync(MasterName).Result is { Exists: false }, "the gift to complete");
        await scene.WaitForBotToSeeCompletionAsync();

        Assert.Equal(1, scene.AcceptsSent);
        Assert.Equal(1u, await scene.CountAsync(BotName, RichEntry));
    }

    /// <summary>A stranger's grey item through the bot-initiated "trade me" window is accepted but earns no affinity.</summary>
    [Fact]
    public async Task TradeMe_FromAnAllowedStranger_WithAGreyItem_GivesNoAffinity()
    {
        await using Scene scene = await Scene.StartAsync(chat: true);
        await scene.AddStrangerAsync();
        byte[] offer = await scene.GiveAsync(StrangerName, GreyEntry);
        await HelpTestKit.StandAsync(scene.World, BotName, (StrangerName, 2f));

        await scene.Stranger!.SendChatAsync(ChatType.Whisper, Language.Common, "trade me", BotName);
        await HelpTestKit.UntilAsync(() => scene.World.Log.Count(BotName, WorldOpcode.CmsgInitiateTrade) > 0, "the bot to initiate the trade", seconds: 60);
        await scene.Stranger.SendAsync(WorldOpcode.CmsgBeginTrade, []);
        await HelpTestKit.UntilAsync(() => scene.StateAsync(StrangerName).Result.Opened, "the window to open");
        await scene.Stranger.SendAsync(WorldOpcode.CmsgSetTradeItem, offer);
        await scene.World.Host.OnWorldAsync(() => HelpTestKit.RecorderOf(scene.World).NoteById(scene.Bot, scene.BotGuid, scene.StrangerGuid, PlayerbotSocialEvent.ChatExchange));
        int before = await scene.AffinityAsync(scene.StrangerGuid);
        await scene.AcceptAsync(scene.Stranger);
        await HelpTestKit.UntilAsync(() => scene.StateAsync(StrangerName).Result is { Exists: false }, "the gift to complete");
        await scene.WaitForBotToSeeCompletionAsync();

        Assert.Equal(1u, await scene.CountAsync(BotName, GreyEntry));
        Assert.Equal(before, await scene.AffinityAsync(scene.StrangerGuid));
    }

    /// <summary>
    /// The chat flow ("trade me" from a player the bot's invite policy lets trade, who is not in its group): the bot initiates the window itself and
    /// must still accept the player's gift, and the gift counts as a trade for that player. Guards the S0 rule "trusted or the bot-initiated partner".
    /// </summary>
    [Fact]
    public async Task TradeMe_FromAnAllowedStranger_BotInitiatesAndAcceptsTheirGift()
    {
        await using Scene scene = await Scene.StartAsync(chat: true);
        await scene.AddStrangerAsync();
        byte[] offer = await scene.GiveAsync(StrangerName, RichEntry);
        await HelpTestKit.StandAsync(scene.World, BotName, (StrangerName, 2f));

        await scene.Stranger!.SendChatAsync(ChatType.Whisper, Language.Common, "trade me", BotName);
        await HelpTestKit.UntilAsync(() => scene.World.Log.Count(BotName, WorldOpcode.CmsgInitiateTrade) > 0, "the bot to initiate the trade", seconds: 60);
        await scene.Stranger.SendAsync(WorldOpcode.CmsgBeginTrade, []);
        await HelpTestKit.UntilAsync(() => scene.StateAsync(StrangerName).Result.Opened, "the window to open");
        await scene.Stranger.SendAsync(WorldOpcode.CmsgSetTradeItem, offer);
        // A relation made by the trade itself does not start from zero the way a known one does: the baseline is a relation made by a chat exchange (AffinityPerChat is 0 here).
        await scene.World.Host.OnWorldAsync(() => HelpTestKit.RecorderOf(scene.World).NoteById(scene.Bot, scene.BotGuid, scene.StrangerGuid, PlayerbotSocialEvent.ChatExchange));
        int before = await scene.AffinityAsync(scene.StrangerGuid);
        await scene.AcceptAsync(scene.Stranger);
        await HelpTestKit.UntilAsync(() => scene.StateAsync(StrangerName).Result is { Exists: false }, "the gift to complete");
        await scene.WaitForBotToSeeCompletionAsync();

        Assert.Equal(1, scene.AcceptsSent);
        Assert.Equal(1u, await scene.CountAsync(BotName, RichEntry));
        Assert.Equal(before + AffinityPerTrade(scene), await scene.AffinityAsync(scene.StrangerGuid));
    }
}
