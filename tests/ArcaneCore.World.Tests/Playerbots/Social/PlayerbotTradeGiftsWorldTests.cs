using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Party;
using ArcaneCore.World.Tests.Items;
using ArcaneCore.World.Tests.Playerbots.Party;
using ArcaneCore.World.Tests.Playerbots.Travel;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// The party AI's side of the trade gifts lane, driven through a real world: how a trade request from someone the bot has no deal with is answered
/// (<c>Social:Trading:AnswerTrades</c>), and that answering strangers never takes the unopened trade away from the mage fee driver, which answers the very same
/// request on its own tick.
/// </summary>
public sealed class PlayerbotTradeGiftsWorldTests(ITestOutputHelper output)
{
    private static byte[] GuidPayload(ObjectGuid guid)
    {
        var writer = new PacketWriter(8);
        writer.WriteUInt64(guid.Value);
        return writer.ToArray();
    }

    private static async Task<(WorldTestHost Host, WorldTestClient Stranger, ObjectGuid BotGuid)> StartAsync(Action<PlayerbotOptions>? configure = null)
    {
        WorldTestHost host = PartyTestHost.Start(
            options =>
            {
                options.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone;
                configure?.Invoke(options);
            },
            services => services.AddScoped<ArcaneCore.Kernel.Economy.IEconomyStore>(_ => new PlayerbotPartyTests.StubEconomyStore()));
        await PartyTestHost.StartBotAsync(host, "Giftanswer");
        WorldTestClient stranger = await host.EnterWorldAsync("GIFTANSWERS", "Giftanswers");
        ObjectGuid botGuid = await host.PlayerStateAsync("Giftanswer", p => p.Guid);
        await host.OnWorldAsync(() =>
        {
            Player bot = host.World.FindOnlinePlayer("Giftanswer")!;
            Player other = host.World.FindOnlinePlayer("Giftanswers")!;
            bot.Relocate(other.X + 2f, other.Y, other.Z, 0f, host.World.NowMs);
            return true;
        });
        return (host, stranger, botGuid);
    }

    /// <summary>
    /// A stranger (not the master, not in the bot's group) who opens a trade to a bot with no deal is told "busy" and the unopened trade is closed. Broken: the request would stay
    /// pending for the stranger forever (the old rule), or the bot would open the window for anyone.
    /// </summary>
    [Fact]
    public async Task AStrangersTradeRequest_IsAnsweredBusy()
    {
        (WorldTestHost host, WorldTestClient stranger, ObjectGuid botGuid) = await StartAsync();
        await using WorldTestHost _ = host;
        await using WorldTestClient __ = stranger;
        await stranger.SendAsync(WorldOpcode.CmsgInitiateTrade, GuidPayload(botGuid));

        byte[] status = await stranger.ReadUntilAsync(WorldOpcode.SmsgTradeStatus);
        Assert.True(BitConverter.ToUInt32(status, 0) is (uint)TradeStatus.Busy or (uint)TradeStatus.Busy2, "status " + BitConverter.ToUInt32(status, 0));
        await host.OnWorldAsync(() =>
        {
            Assert.Null(host.WorldServices.GetRequiredService<EconomyFeature>().TradeOf(host.World.FindOnlinePlayer("Giftanswers")!));
            return true;
        });
    }

    /// <summary>
    /// With <c>AnswerTrades</c> off nothing is answered: the stranger's trade stays pending and unopened. Broken: the key would still be unread and the bot would say busy.
    /// </summary>
    [Fact]
    public async Task AnswerTradesOff_LeavesAStrangersRequestPending()
    {
        (WorldTestHost host, WorldTestClient stranger, ObjectGuid botGuid) = await StartAsync(options => options.Social.Trading.AnswerTrades = false);
        await using WorldTestHost _ = host;
        await using WorldTestClient __ = stranger;
        await stranger.SendAsync(WorldOpcode.CmsgInitiateTrade, GuidPayload(botGuid));
        await host.WaitForWorldAsync(() => host.WorldServices.GetRequiredService<EconomyFeature>().TradeOf(host.World.FindOnlinePlayer("Giftanswers")!) is not null, "the trade request to arrive");

        await Task.Delay(1500); // several think intervals of the bot's intake
        await host.OnWorldAsync(() =>
        {
            Assert.True(host.WorldServices.GetRequiredService<EconomyFeature>().TradeOf(host.World.FindOnlinePlayer("Giftanswers")!) is { Opened: false });
            return true;
        });
    }

    /// <summary>
    /// The group gift fallback between two managed bots, the whole award path: the giving bot (its offer unclaimed, a groupmate bot awarded the item) opens the window, puts the
    /// item in, and accepts first once the partner's side is empty; the receiving bot sees that accept and accepts; the window completes (status 8) and the item moves. Broken: the
    /// giver waited for the other side to accept and the receiver for the giver, so neither accepted and the window timed out (TradeMaxMs) with the item still in the giver's bags.
    /// </summary>
    [Fact]
    public async Task ABotAwardedAGift_ByAGroupmateBot_CompletesTheTrade_AndTheItemMoves()
    {
        const uint GiftEntry = 996_501;
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = GiftEntry, Class = 15, SubClass = 0, Name = "Giftgiven Trinket", DisplayId = 1, Quality = 2, SellPrice = 150, Stackable = 1 });
        WorldTestHost host;
        using (items.Use())
            host = PartyTestHost.Start(options => options.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone,
                services => services.AddScoped<ArcaneCore.Kernel.Economy.IEconomyStore>(_ => new PlayerbotPartyTests.StubEconomyStore()));
        await using WorldTestHost hostScope = host;
        WorldSession giverSession = await PartyTestHost.EnterManagedAsync(host, "GIFTGIVER", "Giftgiver");
        WorldSession receiverSession = await PartyTestHost.EnterManagedAsync(host, "GIFTRECVR", "Giftreceiver");
        try
        {
            PlayerbotOptions options = PartyTestHost.PlayerbotOptionsOf(host);
            var giverAi = new PlayerbotPartyAI(giverSession, options);
            var receiverAi = new PlayerbotPartyAI(receiverSession, options);
            Player giver = giverSession.Player!, receiver = receiverSession.Player!;
            ObjectGuid itemGuid = await host.OnWorldAsync(() =>
            {
                ArcaneCore.Game.Groups.GroupManager groups = host.WorldServices.GetRequiredService<ArcaneCore.World.Social.SocialFeature>().Context.Groups;
                groups.Invite(giver, receiver.Name);
                groups.Accept(receiver);
                Assert.True(groups.AreInSameGroup(giver.Guid, receiver.Guid));
                receiver.Inventory.Load([]);
                giver.Inventory.Load([]);
                receiver.Relocate(giver.X + 2f, giver.Y, giver.Z, 0f, host.World.NowMs);
                Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, giver.Inventory.AddItem(GiftEntry, 1, out ArcaneCore.Game.Entities.Item? gift));
                return gift!.Guid;
            });
            await host.WaitForWorldAsync(() => giver.VisibleObjects.Contains(receiver.Guid) && receiver.VisibleObjects.Contains(giver.Guid), "the bots in sight of each other");
            await host.OnWorldAsync(() =>
            {
                // The state the unclaimed-offer branch of the tick reaches: the item was offered, no claim came, the groupmate bot is awarded it.
                long now = (long)host.World.Uptime.TotalMilliseconds;
                giverAi.GroupGifts.Begin(itemGuid, now);
                Assert.True(giverAi.GroupGifts.Award(receiver.Guid, now));
                return true;
            });

            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(20) && await host.OnWorldAsync(() => receiver.Inventory.GetItemCount(GiftEntry) == 0))
            {
                await host.OnWorldAsync(() =>
                {
                    giverAi.Intake(giver);
                    receiverAi.Intake(receiver);
                    return true;
                });
                await Task.Delay(50);
            }

            output.WriteLine($"gift trade took {clock.ElapsedMilliseconds} ms");
            await host.OnWorldAsync(() =>
            {
                Assert.Equal(1u, receiver.Inventory.GetItemCount(GiftEntry));
                Assert.Equal(0u, giver.Inventory.GetItemCount(GiftEntry));
                return true;
            });
        }
        finally
        {
            giverSession.Kick();
            await giverSession.ManagedClosed;
            receiverSession.Kick();
            await receiverSession.ManagedClosed;
        }
    }

    /// <summary>
    /// A mage serving a portal request has the requester open a trade to it for the fee; the requester is a stranger to the mage. The party AI drains the mage's packets first on every
    /// tick, and must leave that unopened trade for the mage service, which answers it (CMSG_BEGIN_TRADE) and takes the fee. The whole journey runs: the fee is paid once and the
    /// requester arrives. Broken: the party AI answers the stranger's request "busy" first, the trade is closed, and the fee is never paid.
    /// </summary>
    [Fact]
    public async Task AMageFeeTrade_FromAnUntrustedRequester_StillOpens_BesideThePartyAI()
    {
        const uint Fee = 100;
        await using MageTravelRig rig = await MageTravelRig.StartAsync(new MageTravelFixture { Vendor = true });
        WorldSession mageSession = await rig.EnterAsync("Busymage");
        WorldSession requesterSession = await rig.EnterAsync("Busyrider");
        Player mage = mageSession.Player!, requester = requesterSession.Player!;
        var options = new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 100, Risk = { Enabled = false } };
        options.Travel.MageServiceFeeCopper = Fee;
        ManagedPlayerbotFeature feature = requesterSession.Services.GetRequiredService<ManagedPlayerbotFeature>();
        var requesterBrain = new PlayerbotBrain(requesterSession, options);
        var mageParty = new PlayerbotPartyAI(mageSession, options);
        var travel = new PlayerbotTravel(options);
        try
        {
            await rig.OnWorld(() =>
            {
                feature.BotOptions.Enabled = true;
                WorldCollision.Of(rig.Host.World).Install(lineOfSight: new MageTravelFlatFloor(), pathfinder: new MageTravelOpenPathfinder());
                rig.Teach(mage, MageTravelFixture.PortalSpell);
                MageTravelRig.Give(mage, MageTravelFixture.RuneOfPortals, 2);
                mage.Money = 1_000;
                requester.Money = 500;
                requester.Relocate(mage.X - 25f, mage.Y, mage.Z, 0f, rig.Host.World.NowMs);
                return 0;
            });

            for (int i = 0; i < 40 && await rig.OnWorld(() => PlayerbotPortalBoard.Offers(requesterSession, requester, options).Count) == 0; i++)
            {
                await rig.OnWorld(() =>
                {
                    mageSession.ManagedBudget = new ManagedActionBudget(4);
                    PlayerbotMageService.DriveMage(mageSession, mage, options, 100);
                    return 0;
                });
                await Task.Delay(50);
            }

            await rig.OnWorld(() =>
            {
                Assert.True(travel.TryBegin(requesterSession, requester, 0, MageTravelFixture.Stormwind, out string? why), why);
                return 0;
            });

            JourneyState? state = JourneyState.Active;
            DateTime deadline = DateTime.UtcNow.AddSeconds(60);
            while (state == JourneyState.Active && DateTime.UtcNow < deadline)
            {
                state = await rig.OnWorld(() =>
                {
                    // The party AI's intake comes first: the requester's BeginTrade status reaches it before the mage service looks at the trade.
                    mageSession.ManagedBudget = new ManagedActionBudget(4);
                    mageParty.Intake(mage);
                    mageSession.ManagedBudget = new ManagedActionBudget(4);
                    PlayerbotMageService.DriveMage(mageSession, mage, options, 100);
                    requesterSession.ManagedBudget = new ManagedActionBudget(4);
                    requesterBrain.Update(100);
                    return PlayerbotTravel.JourneyOf(requester)?.State;
                });
                await Task.Delay(50);
            }

            string trace = await rig.OnWorld(() =>
                $"journey {PlayerbotTravel.JourneyOf(requester)?.State} {PlayerbotTravel.JourneyOf(requester)?.Failure}; money {requester.Money}; "
                + $"mage leg {PlayerbotMageService.LegOf(requester)?.Stage} {PlayerbotMageService.LegOf(requester)?.Failure}");
            output.WriteLine(trace);
            Assert.Equal(JourneyState.Arrived, state);
            await rig.OnWorld(() =>
            {
                Assert.Equal(500u - Fee, requester.Money); // the fee was paid once
                return 0;
            });
        }
        finally
        {
            requesterBrain.Stop();
        }
    }
}
