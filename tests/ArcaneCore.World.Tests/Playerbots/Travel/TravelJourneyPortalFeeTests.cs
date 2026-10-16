using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Travel;

/// <summary>
/// A portal fee is paid once per journey (<see cref="TravelJourney.PaidPortal"/>, <see cref="MageLeg.PrepaidMage"/>): a requester attacked
/// after it paid the mage drops its portal leg (the brain fights), and the journey begins the leg again with that mage, already paid, so
/// the fee is not traded a second time; the journey counts it once (<see cref="TravelJourney.SpentCopper"/>). Two managed bots
/// (<see cref="MageTravelRig"/>): the requester with its running brain, the mage driven by its service alone
/// (<see cref="PlayerbotMageService.DriveMage"/>), so it stays at its spot instead of exploring away between two requests.
/// </summary>
public sealed class TravelJourneyPortalFeeTests(ITestOutputHelper output)
{
    private const uint Fee = 100;

    [Fact]
    public async Task APortalLegBegunAgainAfterAFight_DoesNotPayTheFeeTwice()
    {
        await using MageTravelRig rig = await MageTravelRig.StartAsync(new MageTravelFixture { Vendor = true });
        WorldSession mageSession = await rig.EnterAsync("Feemage");
        WorldSession requesterSession = await rig.EnterAsync("Feerider");
        Player mage = mageSession.Player!, requester = requesterSession.Player!;
        var options = new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 100, Risk = { Enabled = false } };
        options.Travel.MageServiceFeeCopper = Fee;
        var requesterBrain = new PlayerbotBrain(requesterSession, options);
        var travel = new PlayerbotTravel(options);
        try
        {
            await rig.OnWorld(() =>
            {
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

            TravelRoute route = await rig.OnWorld(() =>
            {
                Assert.True(travel.TryBegin(requesterSession, requester, 0, MageTravelFixture.Stormwind, out string? why), why);
                return PlayerbotTravel.JourneyOf(requester)!.Planned;
            });
            output.WriteLine(PlayerbotTravel.Describe(route));
            Assert.Contains(TravelMeans.MagePortal, route.Legs.Select(l => l.Means));

            // Both brains think until the fee changed hands; at that think the requester is attacked (before it went through).
            bool attacked = false;
            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            while (!attacked && DateTime.UtcNow < deadline)
            {
                attacked = await rig.OnWorld(() =>
                {
                    mageSession.ManagedBudget = new ManagedActionBudget(4);
                    PlayerbotMageService.DriveMage(mageSession, mage, options, 100);
                    requesterSession.ManagedBudget = new ManagedActionBudget(4);
                    requesterBrain.Update(100);
                    if (requester.Money != 500 - Fee || PlayerbotMageService.LegOf(requester) is not { Stage: < MageLegStage.Teleporting }) return false;
                    requester.Map!.Combat.SetInCombatState(requester, 60_000);
                    return true;
                });
                await Task.Delay(50);
            }

            Assert.True(attacked, "the fee was never paid before the requester went through");
            for (int i = 0; i < 10; i++)
            {
                await rig.OnWorld(() =>
                {
                    mageSession.ManagedBudget = new ManagedActionBudget(4);
                    PlayerbotMageService.DriveMage(mageSession, mage, options, 100);
                    requesterSession.ManagedBudget = new ManagedActionBudget(4);
                    requesterBrain.Update(100);
                    return 0;
                });
                await Task.Delay(50);
            }

            (bool legActive, uint spent) = await rig.OnWorld(() => (PlayerbotMageService.IsActive(requester), PlayerbotTravel.JourneyOf(requester)!.SpentCopper));
            Assert.False(legActive, "the portal leg went on in the fight");
            Assert.Equal(Fee, spent);

            // The fight over: the journey asks the paid mage again and goes through without a second trade.
            await rig.OnWorld(() =>
            {
                MapCombat.ClearInCombat(requester);
                return 0;
            });
            JourneyState? state = JourneyState.Active;
            deadline = DateTime.UtcNow.AddSeconds(60);
            while (state == JourneyState.Active && DateTime.UtcNow < deadline)
            {
                state = await rig.OnWorld(() =>
                {
                    mageSession.ManagedBudget = new ManagedActionBudget(4);
                    PlayerbotMageService.DriveMage(mageSession, mage, options, 100);
                    requesterSession.ManagedBudget = new ManagedActionBudget(4);
                    requesterBrain.Update(100);
                    return PlayerbotTravel.JourneyOf(requester)?.State;
                });
                await Task.Delay(50);
            }

            string trace = await rig.OnWorld(() =>
                $"requester at {requester.X:F1},{requester.Y:F1} money {requester.Money}; journey {PlayerbotTravel.JourneyOf(requester)?.State} "
                + $"{PlayerbotTravel.JourneyOf(requester)?.Failure} spent {PlayerbotTravel.JourneyOf(requester)?.SpentCopper}; "
                + $"mage leg {PlayerbotMageService.LegOf(requester)?.Stage} {PlayerbotMageService.LegOf(requester)?.Failure}");
            output.WriteLine(trace);
            Assert.Equal(JourneyState.Arrived, state);
            await rig.OnWorld(() =>
            {
                Assert.True(MageTravelRig.DistanceTo(requester, MageTravelFixture.Stormwind) <= PlayerbotTravel.ArriveYards, trace);
                Assert.Equal(500u - Fee, requester.Money); // one fee, not two
                Assert.Equal(Fee, PlayerbotTravel.JourneyOf(requester)!.SpentCopper);
                return 0;
            });
        }
        finally
        {
            requesterBrain.Stop();
        }
    }

    /// <summary>
    /// The requester paid mage A, a fight dropped the leg, and A is gone from the board when the journey asks for the paid portal again:
    /// mage B takes the request. B was not paid, and the fee is not traded a second time to B (the journey checked no spend for a prepaid
    /// portal): the leg quits, the journey goes on without that portal, the requester's money falls by one fee only, B's not at all, and the
    /// journey never spends past <see cref="PlayerbotTravelOptions.MaxTravelSpendCopper"/> (150: room for one fee of 100, not two).
    /// </summary>
    [Fact]
    public async Task APaidPortalLegTakenAgainByAnotherMage_DoesNotTradeTheFeeASecondTime()
    {
        await using MageTravelRig rig = await MageTravelRig.StartAsync(new MageTravelFixture { Vendor = true });
        WorldSession mageASession = await rig.EnterAsync("Feemagea");
        WorldSession mageBSession = await rig.EnterAsync("Feemageb");
        WorldSession requesterSession = await rig.EnterAsync("Feeridertwo");
        Player mageA = mageASession.Player!, mageB = mageBSession.Player!, requester = requesterSession.Player!;
        var options = new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 100, Risk = { Enabled = false } };
        options.Travel.MageServiceFeeCopper = Fee;
        options.Travel.MaxTravelSpendCopper = 150;
        var requesterBrain = new PlayerbotBrain(requesterSession, options);
        var travel = new PlayerbotTravel(options);

        void DriveMage(WorldSession session, Player mage)
        {
            session.ManagedBudget = new ManagedActionBudget(4);
            PlayerbotMageService.DriveMage(session, mage, options, 100);
        }

        void ThinkRequester()
        {
            requesterSession.ManagedBudget = new ManagedActionBudget(4);
            requesterBrain.Update(100);
        }

        try
        {
            await rig.OnWorld(() =>
            {
                WorldCollision.Of(rig.Host.World).Install(lineOfSight: new MageTravelFlatFloor(), pathfinder: new MageTravelOpenPathfinder());
                foreach (Player mage in new[] { mageA, mageB })
                {
                    rig.Teach(mage, MageTravelFixture.PortalSpell);
                    MageTravelRig.Give(mage, MageTravelFixture.RuneOfPortals, 2);
                    mage.Money = 1_000;
                }

                mageB.Relocate(mageA.X, mageA.Y + 12f, mageA.Z, 0f, rig.Host.World.NowMs);
                requester.Money = 500;
                requester.Relocate(mageA.X - 25f, mageA.Y, mageA.Z, 0f, rig.Host.World.NowMs);
                return 0;
            });

            // Only A serves yet: the journey plans A's portal.
            for (int i = 0; i < 40 && await rig.OnWorld(() => PlayerbotPortalBoard.Offers(requesterSession, requester, options).Count) == 0; i++)
            {
                await rig.OnWorld(() =>
                {
                    DriveMage(mageASession, mageA);
                    return 0;
                });
                await Task.Delay(50);
            }

            TravelRoute route = await rig.OnWorld(() =>
            {
                Assert.True(travel.TryBegin(requesterSession, requester, 0, MageTravelFixture.Stormwind, out string? why), why);
                return PlayerbotTravel.JourneyOf(requester)!.Planned;
            });
            output.WriteLine(PlayerbotTravel.Describe(route));
            TravelLeg portalLeg = Assert.Single(route.Legs, l => l.Means == TravelMeans.MagePortal);
            Assert.Equal(mageA.Guid.Value, portalLeg.MageGuid);

            // A is paid; at that think the requester is attacked (before it went through).
            bool attacked = false;
            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            while (!attacked && DateTime.UtcNow < deadline)
            {
                attacked = await rig.OnWorld(() =>
                {
                    DriveMage(mageASession, mageA);
                    ThinkRequester();
                    if (requester.Money != 500 - Fee || PlayerbotMageService.LegOf(requester) is not { Stage: < MageLegStage.Teleporting }) return false;
                    requester.Map!.Combat.SetInCombatState(requester, 60_000);
                    return true;
                });
                await Task.Delay(50);
            }

            Assert.True(attacked, "the fee was never paid to mage A before the requester went through");
            for (int i = 0; i < 10; i++)
            {
                await rig.OnWorld(() =>
                {
                    DriveMage(mageASession, mageA);
                    ThinkRequester();
                    return 0;
                });
                await Task.Delay(50);
            }

            await rig.OnWorld(() =>
            {
                Assert.False(PlayerbotMageService.IsActive(requester), "the portal leg went on in the fight");
                TravelJourney journey = PlayerbotTravel.JourneyOf(requester)!;
                Assert.Equal(Fee, journey.SpentCopper);
                Assert.Equal<(ulong, uint)?>((mageA.Guid.Value, MageTravelFixture.PortalSpell), journey.PaidPortal);
                Assert.Equal(1_000u + Fee, mageA.Money);

                // A leaves the service (taken over), and B, which was never offered, begins serving.
                PlayerbotMageService.CancelMage(mageASession, mageA);
                Assert.Null(PlayerbotPortalBoard.ListingOf(requesterSession, mageA.Guid.Value));
                return 0;
            });
            for (int i = 0; i < 40 && await rig.OnWorld(() => PlayerbotPortalBoard.ListingOf(mageBSession, mageB.Guid.Value)) is null; i++)
            {
                await rig.OnWorld(() =>
                {
                    DriveMage(mageBSession, mageB);
                    return 0;
                });
                await Task.Delay(50);
            }

            Assert.NotNull(await rig.OnWorld(() => PlayerbotPortalBoard.ListingOf(mageBSession, mageB.Guid.Value)));

            // The fight over: the journey asks for the paid portal again; A is gone, B takes the request. Driven until the leg B took ended
            // (it quits at once; before the fix it traded the fee to B and went through), then a while longer: no late trade either.
            await rig.OnWorld(() =>
            {
                MapCombat.ClearInCombat(requester);
                return 0;
            });
            bool matchedB = false, legEnded = false;
            deadline = DateTime.UtcNow.AddSeconds(60);
            for (int settle = 40; settle > 0 && DateTime.UtcNow < deadline; settle -= legEnded ? 1 : 0)
            {
                (bool byB, bool active, JourneyState? journeyState) = await rig.OnWorld(() =>
                {
                    DriveMage(mageBSession, mageB);
                    ThinkRequester();
                    return (PlayerbotPortalBoard.RequestOf(requesterSession, requester.Guid.Value) is { } request && request.MageGuid == mageB.Guid.Value,
                        PlayerbotMageService.IsActive(requester), PlayerbotTravel.JourneyOf(requester)?.State);
                });
                matchedB |= byB;
                legEnded |= matchedB && !active || journeyState != JourneyState.Active;
                await Task.Delay(50);
            }

            string trace = await rig.OnWorld(() =>
                $"requester at {requester.X:F1},{requester.Y:F1} money {requester.Money}; mage B money {mageB.Money}; journey {PlayerbotTravel.JourneyOf(requester)?.State} "
                + $"{PlayerbotTravel.JourneyOf(requester)?.Failure} spent {PlayerbotTravel.JourneyOf(requester)?.SpentCopper} replanned {PlayerbotTravel.JourneyOf(requester)?.Replanned}; "
                + $"mage leg {PlayerbotMageService.LegOf(requester)?.Stage} {PlayerbotMageService.LegOf(requester)?.Failure}");
            output.WriteLine(trace);
            Assert.True(matchedB, "mage B never took the request, so this test proves nothing: " + trace);
            Assert.True(legEnded, "the portal leg B took never ended: " + trace);
            await rig.OnWorld(() =>
            {
                Assert.Equal(500u - Fee, requester.Money);  // one fee, to A
                Assert.Equal(1_000u, mageB.Money);           // nothing traded to B
                uint spent = PlayerbotTravel.JourneyOf(requester)!.SpentCopper;
                Assert.Equal(Fee, spent);
                Assert.True(spent <= options.Travel.MaxTravelSpendCopper, trace);
                return 0;
            });
        }
        finally
        {
            requesterBrain.Stop();
        }
    }
}
