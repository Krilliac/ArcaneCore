using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// The trade-slot registry (<see cref="PlayerbotTradeOwners"/>, docs/areas/playbots-social.md, Trading): who owns a bot's one trade slot. Each test builds its own registry
/// on a hand-driven clock and real players and hand-built <see cref="TradeSession"/>s from a real world (no trade is registered with the economy feature, so nothing else
/// reads them). The expected values are the registry's own contract: a 2 s grace for a claim with no session yet, a 10 s grace for a bound claim whose session is gone.
/// </summary>
public sealed class PlayerbotTradeOwnersTests
{
    private const string BotName = "Ownbot", PartnerName = "Ownpartner", OtherName = "Ownother";

    private sealed class Clock
    {
        public long Ms = 1_000_000;

        public long Read() => Ms;
    }

    private sealed class Rig(SocialWorld world, PlayerbotTradeOwners owners, Clock clock) : IAsyncDisposable
    {
        public SocialWorld World { get; } = world;

        public PlayerbotTradeOwners Owners { get; } = owners;

        public Clock Clock { get; } = clock;

        public WorldTestClient? Partner { get; init; }

        public WorldTestClient? Other { get; init; }

        public static async Task<Rig> StartAsync()
        {
            SocialWorld world = await SocialWorld.StartAsync(frozen: true);
            await world.AddBotAsync(BotName);
            WorldTestClient partner = await world.AddPlayerAsync(PartnerName);
            WorldTestClient other = await world.AddPlayerAsync(OtherName);
            var clock = new Clock();
            return new Rig(world, new PlayerbotTradeOwners { Clock = clock.Read }, clock) { Partner = partner, Other = other };
        }

        public Task<T> OnWorld<T>(Func<Player, Player, Player, T> action) => World.Host.OnWorldAsync(
            () => action(HelpTestKit.Online(World, BotName), HelpTestKit.Online(World, PartnerName), HelpTestKit.Online(World, OtherName)));

        public async ValueTask DisposeAsync()
        {
            if (Partner is not null) await Partner.DisposeAsync();
            if (Other is not null) await Other.DisposeAsync();
            await World.DisposeAsync();
        }
    }

    /// <summary>
    /// Claiming: a free slot is claimed; the same owner and partner may claim again; another owner, or the same owner for another partner, is refused with the first claim
    /// untouched. Broken: a second driver would take the slot over an owner.
    /// </summary>
    [Fact]
    public async Task TryClaim_OnAFreeSlot_Succeeds_AndAnotherOwnerOrPartnerIsRefused()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnWorld((bot, partner, other) =>
        {
            Assert.Equal(PlayerbotTradeOwner.None, rig.Owners.Of(bot, null).Owner);
            Assert.True(rig.Owners.TryClaim(bot, PlayerbotTradeOwner.Tip, partner.Guid, null));
            Assert.True(rig.Owners.TryClaim(bot, PlayerbotTradeOwner.Tip, partner.Guid, null)); // the same claim again
            Assert.False(rig.Owners.TryClaim(bot, PlayerbotTradeOwner.Desk, partner.Guid, null)); // another owner
            Assert.False(rig.Owners.TryClaim(bot, PlayerbotTradeOwner.Tip, other.Guid, null)); // the same owner, another partner
            PlayerbotTradeClaim claim = rig.Owners.Of(bot, null);
            Assert.Equal(PlayerbotTradeOwner.Tip, claim.Owner);
            Assert.Equal(partner.Guid, claim.Partner);
            Assert.Null(claim.Session);
            Assert.True(rig.Owners.Is(bot, PlayerbotTradeOwner.Tip, partner.Guid, null));
            Assert.False(rig.Owners.Is(bot, PlayerbotTradeOwner.Desk, partner.Guid, null));
            Assert.False(rig.Owners.Is(bot, PlayerbotTradeOwner.Tip, other.Guid, null));
            return true;
        });
    }

    /// <summary>
    /// A claim made with a live trade is a begin: the trade must be the partner's own unopened request to the bot. A trade the bot started, an opened one, or another partner's
    /// is refused; the right one is claimed and the claim binds to that session. Broken: a driver could claim and answer a window that is not a request to it.
    /// </summary>
    [Fact]
    public async Task TryClaim_WithALiveTrade_NeedsThePartnersOwnUnopenedRequest_AndBindsToIt()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnWorld((bot, partner, other) =>
        {
            var botsOwn = new TradeSession(bot, partner); // the bot is the initiator
            var opened = new TradeSession(partner, bot) { Opened = true };
            var othersRequest = new TradeSession(other, bot);
            var request = new TradeSession(partner, bot);
            Assert.False(rig.Owners.TryClaim(bot, PlayerbotTradeOwner.Desk, partner.Guid, botsOwn));
            Assert.False(rig.Owners.TryClaim(bot, PlayerbotTradeOwner.Desk, partner.Guid, opened));
            Assert.False(rig.Owners.TryClaim(bot, PlayerbotTradeOwner.Desk, partner.Guid, othersRequest));
            Assert.Equal(PlayerbotTradeOwner.None, rig.Owners.Of(bot, null).Owner);

            Assert.True(rig.Owners.TryClaim(bot, PlayerbotTradeOwner.Desk, partner.Guid, request));
            PlayerbotTradeClaim claim = rig.Owners.Of(bot, request);
            Assert.Equal(PlayerbotTradeOwner.Desk, claim.Owner);
            Assert.Same(request, claim.Session);
            Assert.True(rig.Owners.Is(bot, PlayerbotTradeOwner.Desk, partner.Guid, request));
            Assert.False(rig.Owners.Is(bot, PlayerbotTradeOwner.Desk, partner.Guid, new TradeSession(partner, bot))); // another session of the same pair is not the claim's
            return true;
        });
    }

    /// <summary>
    /// A stale claim does not block: one with no session after 2 s (exactly 2000 ms still holds), and a bound one whose session has been gone for more than 10 s. Broken: a
    /// bounced initiate would leave the slot owned for ever and starve every other driver.
    /// </summary>
    [Fact]
    public async Task TryClaim_OverAStaleClaim_Succeeds_AtTheGraceBoundaries()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnWorld((bot, partner, _) =>
        {
            Assert.True(rig.Owners.TryClaim(bot, PlayerbotTradeOwner.Desk, partner.Guid, null));
            rig.Clock.Ms += PlayerbotTradeOwners.PendingGraceMs;
            Assert.False(rig.Owners.TryClaim(bot, PlayerbotTradeOwner.Tip, partner.Guid, null)); // 2000 ms: still the desk's
            rig.Clock.Ms += 1;
            Assert.True(rig.Owners.TryClaim(bot, PlayerbotTradeOwner.Tip, partner.Guid, null)); // 2001 ms: stale
            Assert.Equal(PlayerbotTradeOwner.Tip, rig.Owners.Of(bot, null).Owner);

            // A bound claim: seen with a live trade, then gone.
            rig.Owners.Release(bot);
            var request = new TradeSession(partner, bot);
            Assert.True(rig.Owners.TryClaim(bot, PlayerbotTradeOwner.Gift, partner.Guid, request));
            Assert.Equal(PlayerbotTradeOwner.Gift, rig.Owners.Of(bot, null).Owner); // gone: stamped now
            rig.Clock.Ms += PlayerbotTradeOwners.GoneGraceMs;
            Assert.False(rig.Owners.TryClaim(bot, PlayerbotTradeOwner.Desk, partner.Guid, null)); // 10000 ms gone: still held
            rig.Clock.Ms += 1;
            Assert.True(rig.Owners.TryClaim(bot, PlayerbotTradeOwner.Desk, partner.Guid, null));
            return true;
        });
    }

    /// <summary>
    /// Healing on read, unbound claims: held to 2000 ms and gone at 2001 ms with no live trade; bound to a live trade with the claimed partner; cleared by a live trade with
    /// anybody else. Broken: a claim for one partner would keep covering a trade with another.
    /// </summary>
    [Fact]
    public async Task Of_HealsAnUnboundClaim_ByTimeAndByTheLiveTrade()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnWorld((bot, partner, other) =>
        {
            Assert.True(rig.Owners.TryClaim(bot, PlayerbotTradeOwner.Fee, partner.Guid, null));
            rig.Clock.Ms += PlayerbotTradeOwners.PendingGraceMs;
            Assert.Equal(PlayerbotTradeOwner.Fee, rig.Owners.Of(bot, null).Owner);
            rig.Clock.Ms += 1;
            Assert.Equal(PlayerbotTradeOwner.None, rig.Owners.Of(bot, null).Owner);
            Assert.Equal(0, rig.Owners.Count);

            Assert.True(rig.Owners.TryClaim(bot, PlayerbotTradeOwner.Fee, partner.Guid, null));
            var bots = new TradeSession(bot, partner); // the claimed partner's trade (the initiate arrived): binds
            PlayerbotTradeClaim bound = rig.Owners.Of(bot, bots);
            Assert.Equal(PlayerbotTradeOwner.Fee, bound.Owner);
            Assert.Same(bots, bound.Session);

            rig.Owners.Release(bot);
            Assert.True(rig.Owners.TryClaim(bot, PlayerbotTradeOwner.Fee, partner.Guid, null));
            Assert.Equal(PlayerbotTradeOwner.None, rig.Owners.Of(bot, new TradeSession(bot, other)).Owner); // a trade with somebody else: not the claim's
            Assert.Equal(0, rig.Owners.Count);
            return true;
        });
    }

    /// <summary>
    /// Healing on read, bound claims: the session gone for 10000 ms still holds, at 10001 ms it clears; coming back resets the count; a different session clears the claim.
    /// Broken: a claim whose terminal status never came would hold the slot for ever, or a returning session would be read as gone.
    /// </summary>
    [Fact]
    public async Task Of_HealsABoundClaim_WhenItsSessionIsGone_OrReplaced()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnWorld((bot, partner, _) =>
        {
            var session = new TradeSession(bot, partner);
            Assert.True(rig.Owners.TryClaim(bot, PlayerbotTradeOwner.Tip, partner.Guid, null));
            Assert.Equal(PlayerbotTradeOwner.Tip, rig.Owners.Of(bot, session).Owner); // bound
            Assert.Equal(PlayerbotTradeOwner.Tip, rig.Owners.Of(bot, null).Owner); // gone: stamped
            rig.Clock.Ms += 6_000;
            Assert.Equal(PlayerbotTradeOwner.Tip, rig.Owners.Of(bot, session).Owner); // it came back: the count resets
            Assert.Equal(PlayerbotTradeOwner.Tip, rig.Owners.Of(bot, null).Owner);
            rig.Clock.Ms += 6_000;
            Assert.Equal(PlayerbotTradeOwner.Tip, rig.Owners.Of(bot, null).Owner); // 6000 ms since it went again
            rig.Clock.Ms += PlayerbotTradeOwners.GoneGraceMs - 6_000;
            Assert.Equal(PlayerbotTradeOwner.Tip, rig.Owners.Of(bot, null).Owner); // exactly 10000 ms
            rig.Clock.Ms += 1;
            Assert.Equal(PlayerbotTradeOwner.None, rig.Owners.Of(bot, null).Owner);

            Assert.True(rig.Owners.TryClaim(bot, PlayerbotTradeOwner.Tip, partner.Guid, null));
            Assert.Equal(PlayerbotTradeOwner.Tip, rig.Owners.Of(bot, session).Owner);
            Assert.Equal(PlayerbotTradeOwner.None, rig.Owners.Of(bot, new TradeSession(bot, partner)).Owner); // another session of the pair: the claim was for the first
            return true;
        });
    }

    /// <summary>
    /// The release variants: by player and owner, by guid and owner (the desk has no player) only when that owner holds the slot; by player for any owner; Forget for a bot
    /// that left. Broken: a driver's release would drop another driver's claim.
    /// </summary>
    [Fact]
    public async Task Release_OnlyTheNamedOwner_ExceptTheAnyOwnerFormAndForget()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnWorld((bot, partner, _) =>
        {
            Assert.True(rig.Owners.TryClaim(bot, PlayerbotTradeOwner.Desk, partner.Guid, null));
            rig.Owners.Release(bot, PlayerbotTradeOwner.Tip);
            rig.Owners.Release(bot.Guid, PlayerbotTradeOwner.Fee);
            Assert.Equal(PlayerbotTradeOwner.Desk, rig.Owners.Of(bot, null).Owner); // not the desk's call
            rig.Owners.Release(bot, PlayerbotTradeOwner.Desk);
            Assert.Equal(PlayerbotTradeOwner.None, rig.Owners.Of(bot, null).Owner);

            Assert.True(rig.Owners.TryClaim(bot, PlayerbotTradeOwner.Fee, partner.Guid, null));
            rig.Owners.Release(bot.Guid, PlayerbotTradeOwner.Desk);
            Assert.Equal(PlayerbotTradeOwner.Fee, rig.Owners.Of(bot, null).Owner);
            rig.Owners.Release(bot.Guid, PlayerbotTradeOwner.Fee);
            Assert.Equal(0, rig.Owners.Count);

            Assert.True(rig.Owners.TryClaim(bot, PlayerbotTradeOwner.Gift, partner.Guid, null));
            rig.Owners.Release(bot); // any owner
            Assert.Equal(0, rig.Owners.Count);

            Assert.True(rig.Owners.TryClaim(bot, PlayerbotTradeOwner.Gift, partner.Guid, null));
            rig.Owners.Forget(bot.Guid);
            Assert.Equal(0, rig.Owners.Count);
            rig.Owners.Release(bot); // releasing nothing is harmless
            rig.Owners.Forget(bot.Guid);
            return true;
        });
    }

    // --- no registry --------------------------------------------------------------------------------------------------------------

    private const uint WantedEntry = 996_301;

    /// <summary>
    /// The null-registry semantics: a desk built without a registry (a host without the playerbot feature) is the sole owner. A quoted deal's begin is claimed, an agreed
    /// deal's live trade with its partner is the desk's window, and a trade with somebody else is not. Broken: a desk without a registry would claim nothing, or own every window.
    /// </summary>
    [Fact]
    public async Task ADeskWithoutARegistry_IsTheSoleOwner()
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = WantedEntry, Class = 15, SubClass = 0, Name = "Ownerthing", DisplayId = 1, Quality = 1, SellPrice = 1000, Stackable = 20 });
        SocialWorld world;
        using (items.Use()) world = await SocialWorld.StartAsync(frozen: true);
        await using SocialWorld ownedWorld = world;
        Guid botId = await world.AddBotAsync(BotName);
        await using WorldTestClient partnerClient = await world.AddPlayerAsync(PartnerName);
        await using WorldTestClient otherClient = await world.AddPlayerAsync(OtherName);
        await world.Host.OnWorldAsync(() =>
        {
            Player bot = HelpTestKit.Online(world, BotName), partner = HelpTestKit.Online(world, PartnerName), other = HelpTestKit.Online(world, OtherName);
            Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, bot.Inventory.AddItem(WantedEntry, 1, out _));
            var desk = new PlayerbotTradeDesk(world.Options, HelpTestKit.RecorderOf(world), PlayerbotReferenceDealPrices.Instance, static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null);
            Assert.True(desk.Quote(botId, bot, partner, PlayerbotDealRole.Seller, WantedEntry, 1).Ok);
            var request = new TradeSession(partner, bot);
            Assert.True(desk.Claims(botId, partner.Guid, bot, request)); // a quoted deal: opening the window agrees it, with nobody to share the slot with
            Assert.Equal(PlayerbotDealState.Agreed, desk.StateOf(botId, partner.Guid));
            Assert.True(desk.Claims(botId, partner.Guid, bot, request)); // and an agreed one stays claimable
            Assert.True(desk.Owns(botId, bot, request));
            Assert.False(desk.Owns(botId, bot, new TradeSession(other, bot))); // a trade with somebody the desk has no deal with
            return true;
        });
    }

    /// <summary>
    /// A terminal status releases only the claim whose window it closed. A trade still open means the status is a stale one of an earlier trade (the new trade's claim,
    /// bound or not, stays); with no trade open, a bound claim is released and an unbound (fresh, no session yet) claim stays. Broken: a stale cancel status of the old tip
    /// trade wiped the trade desk's new claim and stalled the deal.
    /// </summary>
    [Fact]
    public async Task ReleaseClosed_NeverClearsAClaimMadeAfterTheClose()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnWorld((bot, partner, _) =>
        {
            // A stale status while the new trade is open: the bound claim stays.
            var newer = new TradeSession(bot, partner);
            Assert.True(rig.Owners.TryClaim(bot, PlayerbotTradeOwner.Desk, partner.Guid, null));
            Assert.Equal(PlayerbotTradeOwner.Desk, rig.Owners.Of(bot, newer).Owner); // binds to the newer session
            rig.Owners.ReleaseClosed(bot, newer);
            Assert.Equal(PlayerbotTradeOwner.Desk, rig.Owners.Of(bot, newer).Owner);

            // A stale status after a fresh claim, before its initiate made a session: the unbound claim stays.
            rig.Owners.Release(bot);
            Assert.True(rig.Owners.TryClaim(bot, PlayerbotTradeOwner.Desk, partner.Guid, null));
            rig.Owners.ReleaseClosed(bot, null);
            Assert.Equal(PlayerbotTradeOwner.Desk, rig.Owners.Of(bot, null).Owner);

            // The claim's own window closed (bound, no trade open): released, whoever holds it.
            Assert.Equal(PlayerbotTradeOwner.Desk, rig.Owners.Of(bot, newer).Owner);
            rig.Owners.ReleaseClosed(bot, null);
            Assert.Equal(PlayerbotTradeOwner.None, rig.Owners.Of(bot, null).Owner);
            Assert.Equal(0, rig.Owners.Count);
            rig.Owners.ReleaseClosed(bot, null); // nothing held: harmless
            return true;
        });
    }
}
