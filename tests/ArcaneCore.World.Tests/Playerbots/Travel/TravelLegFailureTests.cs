using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Ship;
using ArcaneCore.World.Tests.Transports;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Travel;

/// <summary>
/// A failed leg defers only that leg (the brain's <c>GiveUpTransitLeg</c>: no quest set aside, no stall, no dead-end pin) and the journey
/// replans once from where the bot stands, without what failed: a portal whose cast the server refused is replaced by the walk, and a dock
/// no walk reaches leaves no route, so the journey fails, reported once.
/// </summary>
public sealed class TravelLegFailureTests(ITestOutputHelper output)
{
    private const uint QuestId = 900, EnderEntry = 6;

    private static void AssertQuestKept(PlayerbotBrain brain, uint now, Player player)
    {
        Assert.Equal(0, brain.StallCount);
        Assert.False(brain.Suspensions.IsQuestSuspended(QuestId, now));
        Assert.False(brain.Suspensions.IsEntrySuspended(EnderEntry, now));
        Assert.Equal(0, brain.Suspensions.QuestSetAsideCount(QuestId));
        Assert.Equal(0, brain.StallWatch.PinStallCount(player.MapId, player.X, player.Y));
    }

    /// <summary>Every plan fails and ends at the bot's feet.</summary>
    private sealed class Refusing : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null) => new(PathType.Incomplete, [start, start]);
    }

    [Fact]
    public async Task AFailedPortalLeg_IsReplannedOnceOnFoot_WithoutThatMage_AndTheJourneyArrives_TheQuestKept()
    {
        await using MageTravelRig rig = await MageTravelRig.StartAsync(new MageTravelFixture { PortalNeedsMana = true });
        WorldSession mageSession = await rig.EnterAsync("Failmage");
        WorldSession requesterSession = await rig.EnterAsync("Failrider");
        Player mage = mageSession.Player!, requester = requesterSession.Player!;
        var options = new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 100, Risk = { Enabled = false } };
        var mageBrain = new PlayerbotBrain(mageSession, options);
        var brain = new PlayerbotBrain(requesterSession, options);
        var travel = new PlayerbotTravel(options);
        using AnomalyCapture mageCap = BotAnomalyCapture.For(mage);
        using AnomalyCapture requesterCap = BotAnomalyCapture.For(requester);
        try
        {
            await rig.OnWorld(() =>
            {
                WorldCollision.Of(rig.Host.World).Install(lineOfSight: new MageTravelFlatFloor(), pathfinder: new MageTravelOpenPathfinder());
                // Both 150 yards from "Stormwind": the walk (28 s) is slower than the portal (25 s), and short enough for the test.
                requester.Relocate(MageTravelFixture.Stormwind.X - 150f, MageTravelFixture.Stormwind.Y, MageTravelFixture.Z, 0f, rig.Host.World.NowMs);
                mage.Relocate(MageTravelFixture.Stormwind.X - 150f, MageTravelFixture.Stormwind.Y + 2f, MageTravelFixture.Z, 0f, rig.Host.World.NowMs);
                rig.Teach(mage, MageTravelFixture.PortalSpell);
                MageTravelRig.Give(mage, MageTravelFixture.RuneOfPortals);
                mageSession.ManagedBudget = new ManagedActionBudget(4);
                PlayerbotMageService.DriveMage(mageSession, mage, options, 500); // listed
                brain.HoldGoalForTests(PlayerbotGoalKind.Quest, EnderEntry, QuestId);
                Assert.True(travel.TryBegin(requesterSession, requester, 0, MageTravelFixture.Stormwind, out string? why), why);
                Assert.Contains(TravelMeans.MagePortal, PlayerbotTravel.JourneyOf(requester)!.Planned.Legs.Select(l => l.Means));
                return 0;
            });

            JourneyState? state = JourneyState.Active;
            DateTime deadline = DateTime.UtcNow.AddSeconds(60);
            while (state == JourneyState.Active && DateTime.UtcNow < deadline)
            {
                state = await rig.OnWorld(() =>
                {
                    mageSession.ManagedBudget = new ManagedActionBudget(4);
                    mageBrain.Update(100);
                    requesterSession.ManagedBudget = new ManagedActionBudget(4);
                    brain.Update(100);
                    return PlayerbotTravel.JourneyOf(requester)?.State;
                });
                await Task.Delay(50);
            }

            TravelJourney journey = await rig.OnWorld(() => PlayerbotTravel.JourneyOf(requester)!);
            output.WriteLine($"{journey.State} {journey.Failure}; replan {(journey.Replan is { } r ? PlayerbotTravel.Describe(r) : "none")}; last transit failure {brain.LastTransitFailure}");
            Assert.Equal(JourneyState.Arrived, state);
            Assert.True(journey.Replanned);
            Assert.Equal([TravelMeans.Walk], journey.Replan!.Legs.Select(l => l.Means)); // that mage is left out of the replan
            await rig.OnWorld(() =>
            {
                Assert.True(MageTravelRig.DistanceTo(requester, MageTravelFixture.Stormwind) <= PlayerbotTravel.ArriveYards);
                Assert.Equal(1, brain.TransitFailureCount); // the portal leg only
                Assert.StartsWith("mage:", brain.LastTransitFailure);
                AssertQuestKept(brain, rig.Host.World.NowMs, requester);
                return 0;
            });
            Assert.Equal(["portal-cast-refused"], MageTravelRig.Codes(mageCap, PlayerbotMageService.Area)); // the mage's own report, once
            Assert.Empty(MageTravelRig.Codes(requesterCap, PlayerbotMageService.Area));
            Assert.Empty(MageTravelRig.Codes(requesterCap, PlayerbotTravel.AnomalyArea)); // the journey arrived: nothing to report
        }
        finally
        {
            mageBrain.Stop();
            brain.Stop();
        }
    }

    [Fact]
    public async Task ADockNoWalkReaches_FailsTheShipLeg_TheReplanFindsNoRoute_AndTheJourneyFailsOnce_TheQuestKept()
    {
        await using ShipTestWorld world = await ShipTestWorld.StartAsync();
        await world.PlaceAsync(0, ShipTestWorld.Start + new Vector3(-100f, 0f, 0f));
        await world.OnWorldAsync(() => WorldCollision.Of(world.Host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new Refusing()));
        PlayerbotOptions options = TravelJourneyShipTests.Options();
        var brain = new PlayerbotBrain(world.Session, options);
        var travel = new PlayerbotTravel(options);
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        try
        {
            await world.OnWorldAsync(() =>
            {
                brain.HoldGoalForTests(PlayerbotGoalKind.Quest, EnderEntry, QuestId);
                Assert.True(travel.TryBegin(world.Session, world.Player, 1, TravelJourneyShipTests.Goal, out string? why), why);
            });

            JourneyState? state = await TravelJourneyShipTests.RunAsync(world, brain, 300_000);
            TravelJourney journey = await world.OnWorldAsync(() => PlayerbotTravel.JourneyOf(world.Player)!);
            output.WriteLine($"{journey.State} {journey.Failure}; last transit failure {brain.LastTransitFailure} ({brain.TransitFailureCount}); {capture.Describe()}");
            Assert.Equal(JourneyState.Failed, state);
            Assert.True(journey.Replanned);
            Assert.Null(journey.Replan); // the boat was the only way across, and its dock is left out (and backed off)
            Assert.Contains("no route", journey.Failure);
            await world.OnWorldAsync(() =>
            {
                Assert.True(PlayerbotShip.IsBackedOff(world.Player, 20808, fromA: true, world.Host.World.NowMs));
                Assert.Equal(2, brain.TransitFailureCount); // the ship leg, then the journey: legs only
                Assert.StartsWith("travel:", brain.LastTransitFailure);
                AssertQuestKept(brain, world.Host.World.NowMs, world.Player);
                Assert.Equal(TravelLegStep.Idle, PlayerbotTravel.Drive(world.Session, world.Player, options, 100, out _)); // answered once
            });
            Assert.Equal(1, capture.Count(PlayerbotAnomalies.Key(PlayerbotShip.AnomalyArea, "dock-unreachable")));
            Assert.Equal(1, capture.Count(PlayerbotAnomalies.Key(PlayerbotTravel.AnomalyArea, "leg-failed")));
            Assert.Equal(0, capture.Count(PlayerbotAnomalies.Key(PlayerbotTravel.AnomalyArea, "overdue")));
        }
        finally
        {
            brain.Stop();
        }
    }

    /// <summary>
    /// Bot QA #1456 class 1: a journey walk that failed 600 yards from the dock handed off to the ship driver, whose weaker walk failed too: a
    /// <c>dock-unreachable</c> of a dock the bot never neared, backing off a healthy dock so the journey left the ship out. Far from the dock the
    /// walk leg's own failure is the journey's, and the dock is untouched.
    /// </summary>
    [Fact]
    public async Task AWalkThatFails600YardsFromTheDock_IsTheWalkLegsFailure_NotADockUnreachable_AndTheDockIsNotBackedOff()
    {
        await using ShipTestWorld world = await ShipTestWorld.StartAsync();
        await world.PlaceAsync(0, ShipTestWorld.Start + new Vector3(-540f, 0f, 0f));
        await world.OnWorldAsync(() => WorldCollision.Of(world.Host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new Refusing()));
        PlayerbotOptions options = TravelJourneyShipTests.Options();
        var brain = new PlayerbotBrain(world.Session, options);
        var travel = new PlayerbotTravel(options);
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        try
        {
            float fromDock = await world.OnWorldAsync(() =>
            {
                Assert.True(travel.TryBegin(world.Session, world.Player, 1, TravelJourneyShipTests.Goal, out string? why), why);
                Assert.Equal([TravelMeans.Walk, TravelMeans.Boat], PlayerbotTravel.JourneyOf(world.Player)!.Planned.Legs.Take(2).Select(l => l.Means));
                return Vector2.Distance(new(world.Player.X, world.Player.Y), new(ShipTestWorld.PortOnMap0.Position.X, ShipTestWorld.PortOnMap0.Position.Y));
            });
            Assert.True(fromDock > 550f, $"{fromDock:F0} yd from the dock");

            JourneyState? state = await TravelJourneyShipTests.RunAsync(world, brain, 300_000);
            TravelJourney journey = await world.OnWorldAsync(() => PlayerbotTravel.JourneyOf(world.Player)!);
            output.WriteLine($"{state} {journey.Failure}; {capture.Describe()}");
            Assert.Equal(JourneyState.Failed, state);
            Assert.Contains("Walk leg: walk:", journey.Failure);
            Assert.Equal(0, capture.Count(PlayerbotAnomalies.Key(PlayerbotShip.AnomalyArea, "dock-unreachable")));
            Assert.Equal(1, capture.Count(PlayerbotAnomalies.Key(PlayerbotTravel.AnomalyArea, "leg-failed")));
            Assert.False(await world.OnWorldAsync(() => PlayerbotShip.IsBackedOff(world.Player, 20808, fromA: true, world.Host.World.NowMs)));
        }
        finally
        {
            brain.Stop();
        }
    }
}
