using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Travel;

/// <summary>
/// The mage service (<see cref="PlayerbotMageService"/>, <see cref="PlayerbotPortalBoard"/>): a mage's own Teleport, and a portal asked for on
/// the board, opened by a mage bot and clicked by the requester, all through the real cast, gameobject, vendor and group opcodes.
/// </summary>
public sealed class MageServiceTests
{
    private sealed record Pair(MageTravelRig Rig, WorldSession MageSession, Player Mage, WorldSession RequesterSession, Player Requester) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Rig.DisposeAsync();
    }

    private static async Task<Pair> StartPairAsync(string mage, string requester, MageTravelFixture? fixture = null, bool knowsPortal = true, bool rune = true)
    {
        MageTravelRig rig = await MageTravelRig.StartAsync(fixture);
        WorldSession mageSession = await rig.EnterAsync(mage);
        WorldSession requesterSession = await rig.EnterAsync(requester);
        Player magePlayer = mageSession.Player!;
        await rig.OnWorld(() =>
        {
            if (knowsPortal) rig.Teach(magePlayer, MageTravelFixture.PortalSpell);
            if (rune) MageTravelRig.Give(magePlayer, MageTravelFixture.RuneOfPortals);
            magePlayer.Money = 1_000;
            return 0;
        });
        return new Pair(rig, mageSession, magePlayer, requesterSession, requesterSession.Player!);
    }

    /// <summary>Thinks of both bots (the mage first), each with a fresh budget, until the requester's leg answers other than Traveling.</summary>
    private static async Task<(TravelLegStep Requester, List<TravelLegStep> Mage)> ServeAsync(Pair pair, PlayerbotOptions options, int maxMs = 10_000)
    {
        var mageSteps = new List<TravelLegStep>();
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(maxMs);
        while (true)
        {
            TravelLegStep step = await pair.Rig.OnWorld(() =>
            {
                pair.MageSession.ManagedBudget = new ManagedActionBudget(4);
                mageSteps.Add(PlayerbotMageService.DriveMage(pair.MageSession, pair.Mage, options, 500));
                pair.RequesterSession.ManagedBudget = new ManagedActionBudget(4);
                return PlayerbotMageService.Drive(pair.RequesterSession, pair.Requester, options, 500, out _);
            });
            if (step != TravelLegStep.Traveling || DateTime.UtcNow > deadline) return (step, mageSteps);
            await Task.Delay(50);
        }
    }

    private static GroupManager Groups(MageTravelRig rig) => rig.Host.WorldServices.GetRequiredService<SocialFeature>().Context.Groups;

    [Fact]
    public async Task AMagesOwnTeleport_IsOffered_Cast_AndArrivesAtTheSpellsTargetPosition()
    {
        await using MageTravelRig rig = await MageTravelRig.StartAsync();
        WorldSession session = await rig.EnterAsync("Magetele");
        Player mage = session.Player!;
        using AnomalyCapture cap = await BotAnomalyCapture.OnWorldThreadAsync(rig.Host, mage);
        PlayerbotOptions options = MageTravelRig.Options();
        await rig.OnWorld(() =>
        {
            rig.Teach(mage, MageTravelFixture.TeleportSpell);
            MageTravelRig.Give(mage, MageTravelFixture.RuneOfTeleportation);
            OwnTeleport own = Assert.Single(PlayerbotMageService.OwnTeleports(session, mage, options));
            Assert.Equal(MageTravelFixture.TeleportSpell, own.SpellId);
            Assert.Equal(new TravelPlace(0, MageTravelFixture.Stormwind), own.Destination);
            Assert.True(PlayerbotMageService.TryBeginTeleport(session, mage, MageTravelFixture.TeleportSpell, options, out string? why), why);
            return 0;
        });

        Assert.Equal(TravelLegStep.Arrived, await rig.ThinkUntilAsync(session, () => PlayerbotMageService.Drive(session, mage, options, 500, out _)));
        await rig.OnWorld(() =>
        {
            Assert.True(MageTravelRig.DistanceTo(mage, MageTravelFixture.Stormwind) < 1f, $"the mage stands at {mage.X},{mage.Y},{mage.Z}");
            Assert.Equal(0u, mage.Inventory.GetItemCount(MageTravelFixture.RuneOfTeleportation)); // the server took the rune
            Assert.False(PlayerbotMageService.IsActive(mage));

            // Without a rune the Teleport is neither offered nor begun, and that refusal is the planner's, not an anomaly.
            Assert.Empty(PlayerbotMageService.OwnTeleports(session, mage, options));
            Assert.False(PlayerbotMageService.TryBeginTeleport(session, mage, MageTravelFixture.TeleportSpell, options, out string? why));
            Assert.Contains("no rune", why);
            Assert.False(PlayerbotMageService.TryBeginTeleport(session, mage, 3562, options, out why));
            Assert.Contains("does not know", why);
            return 0;
        });
        Assert.Empty(MageTravelRig.Codes(cap, PlayerbotMageService.Area));
    }

    [Fact]
    public async Task APortalRequest_IsMatched_Cast_Clicked_AndTheRequesterArrives_ThroughTheMagesParty()
    {
        await using Pair pair = await StartPairAsync("Mageport", "Travelport");
        using AnomalyCapture mageCap = await BotAnomalyCapture.OnWorldThreadAsync(pair.Rig.Host, pair.Mage);
        using AnomalyCapture requesterCap = await BotAnomalyCapture.OnWorldThreadAsync(pair.Rig.Host, pair.Requester);
        PlayerbotOptions options = MageTravelRig.Options();
        await pair.Rig.OnWorld(() =>
        {
            // An idle mage lists itself; the requester sees its offer, with the portal's real destination.
            Assert.Equal(TravelLegStep.Idle, PlayerbotMageService.DriveMage(pair.MageSession, pair.Mage, options, 500));
            PortalOffer offer = Assert.Single(PlayerbotPortalBoard.Offers(pair.RequesterSession, pair.Requester, options));
            Assert.Equal(pair.Mage.Guid.Value, offer.MageGuid);
            Assert.Equal(MageTravelFixture.PortalSpell, offer.PortalSpellId);
            Assert.Equal(new TravelPlace(0, MageTravelFixture.Stormwind), offer.Destination);
            Assert.Equal(0u, offer.FeeCopper);
            Assert.Empty(PlayerbotPortalBoard.Offers(pair.MageSession, pair.Mage, options)); // never its own
            Assert.True(PlayerbotMageService.TryBeginPortal(pair.RequesterSession, pair.Requester, offer, options, out string? why), why);
            return 0;
        });

        (TravelLegStep step, List<TravelLegStep> mageSteps) = await ServeAsync(pair, options);
        Assert.Equal(TravelLegStep.Arrived, step);
        Assert.Contains(TravelLegStep.Traveling, mageSteps);
        TravelLegStep mageEnd = await pair.Rig.ThinkUntilAsync(pair.MageSession, () => PlayerbotMageService.DriveMage(pair.MageSession, pair.Mage, options, 500));
        Assert.Equal(TravelLegStep.Arrived, mageEnd);
        await pair.Rig.OnWorld(() =>
        {
            Assert.True(MageTravelRig.DistanceTo(pair.Requester, MageTravelFixture.Stormwind) < 1f, $"the requester stands at {pair.Requester.X},{pair.Requester.Y}");
            Assert.True(MageTravelRig.DistanceTo(pair.Mage, MageTravelFixture.Stormwind) > 100f, "the mage went too");
            Assert.Equal(PortalRequestState.Done, PlayerbotPortalBoard.RequestOf(pair.RequesterSession, pair.Requester.Guid.Value)!.State);
            Assert.Equal(0u, pair.Mage.Inventory.GetItemCount(MageTravelFixture.RuneOfPortals)); // the cast took the rune
            Assert.Null(Groups(pair.Rig).GetGroup(pair.Requester.Guid)); // the party formed for the portal is gone again
            Assert.Null(Groups(pair.Rig).GetGroup(pair.Mage.Guid));
            Assert.False(PlayerbotMageService.IsActive(pair.Requester));
            Assert.False(PlayerbotMageService.IsServing(pair.Mage));
            return 0;
        });
        Assert.Empty(MageTravelRig.Codes(mageCap, PlayerbotMageService.Area));
        Assert.Empty(MageTravelRig.Codes(requesterCap, PlayerbotMageService.Area));
    }

    [Fact]
    public async Task ARequesterAwayFromTheMage_WalksToIt_PaysTheFeeByATrade_AndGoesThrough()
    {
        await using Pair pair = await StartPairAsync("Magefee", "Travelfee");
        using AnomalyCapture mageCap = await BotAnomalyCapture.OnWorldThreadAsync(pair.Rig.Host, pair.Mage);
        using AnomalyCapture requesterCap = await BotAnomalyCapture.OnWorldThreadAsync(pair.Rig.Host, pair.Requester);
        PlayerbotOptions options = MageTravelRig.Options(t => t.MageServiceFeeCopper = 100);
        await pair.Rig.OnWorld(() =>
        {
            WorldCollision.Of(pair.Rig.Host.World).Install(lineOfSight: new MageTravelFlatFloor(), pathfinder: new MageTravelOpenPathfinder());
            pair.Requester.Money = 500;
            pair.Requester.Relocate(pair.Mage.X - 25f, pair.Mage.Y, pair.Mage.Z, 0f, pair.Rig.Host.World.NowMs);
            Assert.Equal(TravelLegStep.Idle, PlayerbotMageService.DriveMage(pair.MageSession, pair.Mage, options, 500));
            PortalOffer offer = Assert.Single(PlayerbotPortalBoard.Offers(pair.RequesterSession, pair.Requester, options));
            Assert.Equal(100u, offer.FeeCopper);
            Assert.True(PlayerbotMageService.TryBeginPortal(pair.RequesterSession, pair.Requester, offer, options, out string? why), why);
            return 0;
        });

        (TravelLegStep step, _) = await ServeAsync(pair, options, maxMs: 20_000);
        Assert.Equal(TravelLegStep.Arrived, step);
        await pair.Rig.OnWorld(() =>
        {
            Assert.True(MageTravelRig.DistanceTo(pair.Requester, MageTravelFixture.Stormwind) < 1f);
            Assert.Equal(400u, pair.Requester.Money); // the fee changed hands through the trade
            Assert.Equal(1_100u, pair.Mage.Money);
            return 0;
        });
        Assert.Empty(MageTravelRig.Codes(mageCap, PlayerbotMageService.Area));
        Assert.Empty(MageTravelRig.Codes(requesterCap, PlayerbotMageService.Area));
    }

    [Fact]
    public async Task AMageThatDoesNotKnowThePortal_IsNotListed_AndARequestFindsNoMatch()
    {
        await using Pair pair = await StartPairAsync("Magenone", "Travelnone", knowsPortal: false);
        using AnomalyCapture mageCap = await BotAnomalyCapture.OnWorldThreadAsync(pair.Rig.Host, pair.Mage);
        PlayerbotOptions options = MageTravelRig.Options();
        await pair.Rig.OnWorld(() =>
        {
            Assert.Equal(TravelLegStep.Idle, PlayerbotMageService.DriveMage(pair.MageSession, pair.Mage, options, 500));
            Assert.Null(PlayerbotPortalBoard.ListingOf(pair.MageSession, pair.Mage.Guid.Value));
            Assert.Empty(PlayerbotPortalBoard.Offers(pair.RequesterSession, pair.Requester, options));
            Assert.True(PlayerbotMageService.TryBeginPortal(pair.RequesterSession, pair.Requester, MageTravelFixture.PortalSpell, options, out string? why), why);
            return 0;
        });

        (TravelLegStep step, _) = await ServeAsync(pair, options, maxMs: 600);
        Assert.Equal(TravelLegStep.Traveling, step); // still waiting for a match (it expires after PortalBoard.MatchWaitMs)
        await pair.Rig.OnWorld(() =>
        {
            PortalRequest request = PlayerbotPortalBoard.RequestOf(pair.RequesterSession, pair.Requester.Guid.Value)!;
            Assert.Equal(PortalRequestState.Open, request.State);
            Assert.Equal(0ul, request.MageGuid);
            Assert.False(PlayerbotMageService.IsServing(pair.Mage));
            return 0;
        });
        Assert.Empty(MageTravelRig.Codes(mageCap, PlayerbotMageService.Area));
    }

    [Fact]
    public async Task AMageWithoutTheRune_BuysItFromAVendorThatStocksIt_ThenOpensThePortal()
    {
        await using Pair pair = await StartPairAsync("Magebuy", "Travelbuy", new MageTravelFixture { Vendor = true }, rune: false);
        using AnomalyCapture mageCap = await BotAnomalyCapture.OnWorldThreadAsync(pair.Rig.Host, pair.Mage);
        using AnomalyCapture requesterCap = await BotAnomalyCapture.OnWorldThreadAsync(pair.Rig.Host, pair.Requester);
        await pair.Rig.Host.WaitForWorldAsync(() => pair.Mage.VisibleObjects.Any(g => g.Entry == MageTravelFixture.VendorEntry), "the vendor is in sight");
        PlayerbotOptions options = MageTravelRig.Options();
        await pair.Rig.OnWorld(() =>
        {
            Assert.Equal(TravelLegStep.Idle, PlayerbotMageService.DriveMage(pair.MageSession, pair.Mage, options, 500));
            PortalMage listing = PlayerbotPortalBoard.ListingOf(pair.MageSession, pair.Mage.Guid.Value)!;
            Assert.Equal(0, listing.ReadyMask); // cannot open it now: not offered to the planner...
            Assert.True(listing.CanBuyRune); // ...but matched, since it can buy the rune
            Assert.Empty(PlayerbotPortalBoard.Offers(pair.RequesterSession, pair.Requester, options));
            Assert.True(PlayerbotMageService.TryBeginPortal(pair.RequesterSession, pair.Requester, MageTravelFixture.PortalSpell, options, out string? why), why);
            return 0;
        });

        (TravelLegStep step, _) = await ServeAsync(pair, options);
        Assert.Equal(TravelLegStep.Arrived, step);
        await pair.Rig.OnWorld(() =>
        {
            Assert.True(MageTravelRig.DistanceTo(pair.Requester, MageTravelFixture.Stormwind) < 1f);
            Assert.Equal(980u, pair.Mage.Money); // the rune was bought (20 copper) and spent on the cast
            Assert.Equal(0u, pair.Mage.Inventory.GetItemCount(MageTravelFixture.RuneOfPortals));
            return 0;
        });
        Assert.Empty(MageTravelRig.Codes(mageCap, PlayerbotMageService.Area));
        Assert.Empty(MageTravelRig.Codes(requesterCap, PlayerbotMageService.Area));
    }

    [Fact]
    public async Task AMageWithoutTheRuneAndNoVendor_IsNotMatched_AndOneThatLostItAfterTheMatch_FailsOnce()
    {
        await using Pair pair = await StartPairAsync("Magenorune", "Travelnorune", rune: false);
        using AnomalyCapture mageCap = await BotAnomalyCapture.OnWorldThreadAsync(pair.Rig.Host, pair.Mage);
        using AnomalyCapture requesterCap = await BotAnomalyCapture.OnWorldThreadAsync(pair.Rig.Host, pair.Requester);
        PlayerbotOptions options = MageTravelRig.Options();
        await pair.Rig.OnWorld(() =>
        {
            Assert.Equal(TravelLegStep.Idle, PlayerbotMageService.DriveMage(pair.MageSession, pair.Mage, options, 500));
            PortalMage listing = PlayerbotPortalBoard.ListingOf(pair.MageSession, pair.Mage.Guid.Value)!;
            Assert.Equal(0, listing.ReadyMask);
            Assert.False(listing.CanBuyRune);
            Assert.True(PlayerbotMageService.TryBeginPortal(pair.RequesterSession, pair.Requester, MageTravelFixture.PortalSpell, options, out _));
            pair.RequesterSession.ManagedBudget = new ManagedActionBudget(4);
            Assert.Equal(TravelLegStep.Traveling, PlayerbotMageService.Drive(pair.RequesterSession, pair.Requester, options, 500, out _));
            Assert.Equal(PortalRequestState.Open, PlayerbotPortalBoard.RequestOf(pair.RequesterSession, pair.Requester.Guid.Value)!.State);

            // The mage gets a rune, is matched, and loses the rune before it serves: it has none and no vendor to buy one from.
            MageTravelRig.Give(pair.Mage, MageTravelFixture.RuneOfPortals);
            Assert.Equal(TravelLegStep.Idle, PlayerbotMageService.DriveMage(pair.MageSession, pair.Mage, options, 500));
            Assert.Equal(PortalRequestState.Matched, PlayerbotPortalBoard.Match(pair.RequesterSession, pair.Requester, options)!.State);
            pair.Mage.Inventory.DestroyItemCount(MageTravelFixture.RuneOfPortals, 1);
            pair.MageSession.ManagedBudget = new ManagedActionBudget(4);
            Assert.Equal(TravelLegStep.Failed, PlayerbotMageService.DriveMage(pair.MageSession, pair.Mage, options, 500));
            Assert.Equal(TravelLegStep.Idle, PlayerbotMageService.DriveMage(pair.MageSession, pair.Mage, options, 500));
            pair.RequesterSession.ManagedBudget = new ManagedActionBudget(4);
            Assert.Equal(TravelLegStep.Failed, PlayerbotMageService.Drive(pair.RequesterSession, pair.Requester, options, 500, out string? failure));
            Assert.Contains("the mage gave up", failure);
            Assert.Equal(TravelLegStep.Idle, PlayerbotMageService.Drive(pair.RequesterSession, pair.Requester, options, 500, out _));
            return 0;
        });
        Assert.Equal(["rune-purchase-failed"], MageTravelRig.Codes(mageCap, PlayerbotMageService.Area));
        Assert.Empty(MageTravelRig.Codes(requesterCap, PlayerbotMageService.Area));
    }

    [Fact]
    public async Task APortalCastTheServerRefuses_FailsTheServiceOnce_AndTheRequesterLegWithoutASecondReport()
    {
        await using Pair pair = await StartPairAsync("Magerefuse", "Travelrefuse", new MageTravelFixture { PortalNeedsMana = true });
        using AnomalyCapture mageCap = await BotAnomalyCapture.OnWorldThreadAsync(pair.Rig.Host, pair.Mage);
        using AnomalyCapture requesterCap = await BotAnomalyCapture.OnWorldThreadAsync(pair.Rig.Host, pair.Requester);
        PlayerbotOptions options = MageTravelRig.Options();
        await pair.Rig.OnWorld(() =>
        {
            PlayerbotMageService.DriveMage(pair.MageSession, pair.Mage, options, 500);
            PortalOffer offer = Assert.Single(PlayerbotPortalBoard.Offers(pair.RequesterSession, pair.Requester, options));
            Assert.True(PlayerbotMageService.TryBeginPortal(pair.RequesterSession, pair.Requester, offer, options, out _));
            return 0;
        });

        (TravelLegStep step, List<TravelLegStep> mageSteps) = await ServeAsync(pair, options);
        Assert.Equal(TravelLegStep.Failed, step);
        Assert.Equal(1, mageSteps.Count(s => s == TravelLegStep.Failed));
        await pair.Rig.OnWorld(() =>
        {
            Assert.Contains("refused", PlayerbotMageService.LastFailure(pair.Requester));
            Assert.Equal(1u, pair.Mage.Inventory.GetItemCount(MageTravelFixture.RuneOfPortals)); // nothing was cast
            Assert.Equal(PortalRequestState.Failed, PlayerbotPortalBoard.RequestOf(pair.RequesterSession, pair.Requester.Guid.Value)!.State);
            return 0;
        });
        Assert.Equal(["portal-cast-refused"], MageTravelRig.Codes(mageCap, PlayerbotMageService.Area));
        Assert.Empty(MageTravelRig.Codes(requesterCap, PlayerbotMageService.Area));
    }

    [Fact]
    public async Task WithTheMageServiceOff_NothingIsOffered_ListedOrBegun()
    {
        await using Pair pair = await StartPairAsync("Mageoff", "Traveloff");
        using AnomalyCapture mageCap = await BotAnomalyCapture.OnWorldThreadAsync(pair.Rig.Host, pair.Mage);
        PlayerbotOptions off = MageTravelRig.Options(t => t.MageService = false);
        await pair.Rig.OnWorld(() =>
        {
            pair.Rig.Teach(pair.Mage, MageTravelFixture.TeleportSpell);
            MageTravelRig.Give(pair.Mage, MageTravelFixture.RuneOfTeleportation);
            Assert.NotEmpty(PlayerbotMageService.OwnTeleports(pair.MageSession, pair.Mage, MageTravelRig.Options())); // on: offered

            Assert.Empty(PlayerbotMageService.OwnTeleports(pair.MageSession, pair.Mage, off));
            Assert.False(PlayerbotMageService.TryBeginTeleport(pair.MageSession, pair.Mage, MageTravelFixture.TeleportSpell, off, out string? why));
            Assert.Contains("off", why);
            Assert.Equal(TravelLegStep.Idle, PlayerbotMageService.DriveMage(pair.MageSession, pair.Mage, off, 500));
            Assert.Null(PlayerbotPortalBoard.ListingOf(pair.MageSession, pair.Mage.Guid.Value));
            Assert.Empty(PlayerbotPortalBoard.Offers(pair.RequesterSession, pair.Requester, off));
            Assert.False(PlayerbotMageService.TryBeginPortal(pair.RequesterSession, pair.Requester, MageTravelFixture.PortalSpell, off, out why));
            Assert.Contains("off", why);
            Assert.Null(PlayerbotPortalBoard.RequestOf(pair.RequesterSession, pair.Requester.Guid.Value));

            // Listed while on, then switched off: taken off the board, and no offer is left behind.
            PlayerbotMageService.DriveMage(pair.MageSession, pair.Mage, MageTravelRig.Options(), 500);
            Assert.NotEmpty(PlayerbotPortalBoard.Offers(pair.RequesterSession, pair.Requester, MageTravelRig.Options()));
            PlayerbotMageService.DriveMage(pair.MageSession, pair.Mage, off, 500);
            Assert.Empty(PlayerbotPortalBoard.Offers(pair.RequesterSession, pair.Requester, MageTravelRig.Options()));
            return 0;
        });
        Assert.Empty(MageTravelRig.Codes(mageCap, PlayerbotMageService.Area));
    }
}
