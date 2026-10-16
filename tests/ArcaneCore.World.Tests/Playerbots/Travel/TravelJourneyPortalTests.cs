using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Travel;

/// <summary>
/// End to end: two managed bots with running brains (<see cref="MageTravelRig"/>). A mage bot lists itself on the portal board from its own
/// brain; the requester is given a capital through <see cref="IPlayerbotTravel.TryBegin"/>, the planner takes the mage's portal, and with
/// nothing but the two brains' thinks the mage casts it, the requester goes through and arrives where the portal leads.
/// </summary>
public sealed class TravelJourneyPortalTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ARequestersJourney_IsPlannedThroughTheMagesPortal_AndArrivesAtThePortalsDestination()
    {
        await using MageTravelRig rig = await MageTravelRig.StartAsync();
        WorldSession mageSession = await rig.EnterAsync("Journeymage");
        WorldSession requesterSession = await rig.EnterAsync("Journeyrider");
        Player mage = mageSession.Player!, requester = requesterSession.Player!;
        var options = new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 100, Risk = { Enabled = false } };
        var mageBrain = new PlayerbotBrain(mageSession, options);
        var requesterBrain = new PlayerbotBrain(requesterSession, options);
        var travel = new PlayerbotTravel(options);
        using AnomalyCapture mageCap = BotAnomalyCapture.For(mage);
        using AnomalyCapture requesterCap = BotAnomalyCapture.For(requester);
        try
        {
            await rig.OnWorld(() =>
            {
                WorldCollision.Of(rig.Host.World).Install(lineOfSight: new MageTravelFlatFloor(), pathfinder: new MageTravelOpenPathfinder());
                rig.Teach(mage, MageTravelFixture.PortalSpell);
                MageTravelRig.Give(mage, MageTravelFixture.RuneOfPortals);
                return 0;
            });

            // The mage's own brain lists it (PlayerbotMageService.DriveMage at its think).
            for (int i = 0; i < 40 && await rig.OnWorld(() => PlayerbotPortalBoard.Offers(requesterSession, requester, options).Count) == 0; i++)
            {
                await rig.OnWorld(() =>
                {
                    mageSession.ManagedBudget = new ManagedActionBudget(4);
                    mageBrain.Update(100);
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
            Assert.Equal(mage.Guid.Value, route.Legs.Single(l => l.Means == TravelMeans.MagePortal).MageGuid);

            JourneyState? state = JourneyState.Active;
            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            while (state == JourneyState.Active && DateTime.UtcNow < deadline)
            {
                state = await rig.OnWorld(() =>
                {
                    mageSession.ManagedBudget = new ManagedActionBudget(4);
                    mageBrain.Update(100);
                    requesterSession.ManagedBudget = new ManagedActionBudget(4);
                    requesterBrain.Update(100);
                    return PlayerbotTravel.JourneyOf(requester)?.State;
                });
                await Task.Delay(50);
            }

            string trace = await rig.OnWorld(() =>
                $"requester at {requester.X:F1},{requester.Y:F1}; journey {PlayerbotTravel.JourneyOf(requester)?.State} {PlayerbotTravel.JourneyOf(requester)?.Failure}; "
                + $"mage leg {PlayerbotMageService.LegOf(requester)?.Stage} {PlayerbotMageService.LegOf(requester)?.Failure}");
            output.WriteLine(trace);
            Assert.Equal(JourneyState.Arrived, state);
            await rig.OnWorld(() =>
            {
                Assert.True(MageTravelRig.DistanceTo(requester, MageTravelFixture.Stormwind) <= PlayerbotTravel.ArriveYards, trace);
                Assert.Equal(PortalRequestState.Done, PlayerbotPortalBoard.RequestOf(requesterSession, requester.Guid.Value)!.State);
                Assert.Equal(0u, mage.Inventory.GetItemCount(MageTravelFixture.RuneOfPortals)); // the mage really cast it
                Assert.Equal(0, requesterBrain.TransitFailureCount);
                return 0;
            });
            Assert.Empty(MageTravelRig.Codes(requesterCap, PlayerbotTravel.AnomalyArea));
            Assert.Empty(MageTravelRig.Codes(requesterCap, PlayerbotMageService.Area));
            Assert.Empty(MageTravelRig.Codes(mageCap, PlayerbotMageService.Area));
        }
        finally
        {
            mageBrain.Stop();
            requesterBrain.Stop();
        }
    }
}
