using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Ship;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Travel;

/// <summary>
/// Same-map teleport triggers as travel legs (the Rut'theran Village portal up to Teldrassil): which <c>areatrigger_teleport</c> rows
/// <c>PlayerbotTravel.BuildFacts</c> turns into <see cref="TriggerTeleport"/>s, a journey that walks into one and is carried to its exit, and a
/// refused one that fails the leg and is left out of the replan. Synthetic world and a flat floor: no real terrain.
/// </summary>
public sealed class TravelTeleportTriggerTests(ITestOutputHelper output)
{
    private const float Z = 83.53f;
    private const uint Good = 9001, CrossMap = 9002, TooHigh = 9003, NeedsItem = 9004, NeedsItem2 = 9005, NeedsQuest = 9006, NeedsCondition = 9007, Hop = 9008;

    private static readonly Vector3 Start = new(1000f, 0f, Z);
    private static readonly Vector3 Entrance = new(1030f, -80f, Z);
    private static readonly Vector3 Exit = new(1380f, 60f, Z);
    private static readonly Vector3 Goal = new(1400f, 60f, Z);

    private static AreaTriggerTemplate Volume(uint id, Vector3 at) => new(id, 1, at.X, at.Y, at.Z, 10f, 0, 0, 0, 0, "synthetic " + id);

    private static AreaTriggerTeleport Row(uint id, uint targetMap, Vector3 to, byte level = 0, uint item = 0, uint item2 = 0, uint quest = 0, uint condition = 0)
        => new(id, "synthetic " + id, "", level, targetMap, to.X, to.Y, to.Z, 0f, item, item2, quest, condition);

    private sealed class Maps(AreaTriggerTemplate[] volumes, AreaTriggerTeleport[] rows) : IMapDataStore
    {
        public Task<MapContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new MapContent(
            [
                new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
                new MapTemplate(1, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Kalimdor", ""),
            ],
            [],
            volumes,
            rows,
            []));
    }

    private static Task<ShipTestWorld> StartAsync(AreaTriggerTemplate[] volumes, AreaTriggerTeleport[] rows)
        => ShipTestWorld.StartAsync(configureServices: services => services.AddSingleton<IMapDataStore>(new Maps(volumes, rows)));

    private static PlayerbotOptions Options() => TravelJourneyShipTests.Options();

    [Fact]
    public async Task BuildFacts_KeepsASameMapTrigger_AndLeavesOutEveryOneTheBotCannotTake()
    {
        Vector3 far = new(2000f, 0f, Z);
        await using ShipTestWorld world = await StartAsync(
            [Volume(Good, Entrance), Volume(CrossMap, Entrance), Volume(TooHigh, Entrance), Volume(NeedsItem, Entrance), Volume(NeedsItem2, Entrance),
                Volume(NeedsQuest, Entrance), Volume(NeedsCondition, Entrance), Volume(Hop, Entrance)],
            [Row(Good, 1, Exit), Row(CrossMap, 0, Exit), Row(TooHigh, 1, Exit, level: 60), Row(NeedsItem, 1, Exit, item: 5), Row(NeedsItem2, 1, Exit, item2: 5),
                Row(NeedsQuest, 1, Exit, quest: 5), Row(NeedsCondition, 1, Exit, condition: 5), Row(Hop, 1, Entrance + new Vector3(2f, 0f, 0f))]);
        await world.PlaceAsync(1, far);
        PlayerbotOptions options = Options();
        await world.OnWorldAsync(() =>
        {
            Assert.True(world.Player.Level < 60);
            TravelFacts facts = PlayerbotTravel.BuildFacts(world.Session, world.Player, options);
            TriggerTeleport only = Assert.Single(facts.Triggers);
            Assert.Equal(Good, only.TriggerId);
            Assert.Equal(1u, only.Entrance.MapId);
            Assert.Equal(Entrance, only.Entrance.Position);
            Assert.Equal(Exit, only.Exit.Position);

            var excluded = new TravelExclusions();
            excluded.Triggers.Add(Good);
            Assert.Empty(PlayerbotTravel.BuildFacts(world.Session, world.Player, options, PlayerbotShipNetwork.Routes(world.Session.World), excluded).Triggers);
        });
    }

    [Fact]
    public async Task AJourney_WalksIntoTheTrigger_IsCarriedToItsExit_AndWalksOnToTheGoal()
    {
        await using ShipTestWorld world = await StartAsync([Volume(Good, Entrance)], [Row(Good, 1, Exit)]);
        await world.PlaceAsync(1, Start);
        PlayerbotOptions options = Options();
        var brain = new PlayerbotBrain(world.Session, options);
        var travel = new PlayerbotTravel(options);
        try
        {
            TravelRoute route = await world.OnWorldAsync(() =>
            {
                Assert.True(travel.TryBegin(world.Session, world.Player, 1, Goal, out string? why), why);
                return PlayerbotTravel.JourneyOf(world.Player)!.Planned;
            });
            Assert.Equal([TravelMeans.Walk, TravelMeans.TeleportTrigger, TravelMeans.Walk], route.Legs.Select(l => l.Means));
            Assert.Equal(Good, route.Legs[1].TriggerId);

            Vector3 last = Start;
            float biggestJump = 0f;
            JourneyState? state = await TravelJourneyShipTests.RunAsync(world, brain, 120_000, p =>
            {
                var now = new Vector3(p.X, p.Y, p.Z);
                biggestJump = MathF.Max(biggestJump, Vector3.Distance(last, now));
                last = now;
            });
            output.WriteLine($"{state}; biggest step {biggestJump:F1}");
            Assert.Equal(JourneyState.Arrived, state);
            Assert.True(biggestJump > 200f, $"the bot was never carried to the exit (biggest step {biggestJump:F1} yd)");
            await world.OnWorldAsync(() =>
            {
                Assert.True(Vector3.Distance(new Vector3(world.Player.X, world.Player.Y, world.Player.Z), Goal) <= PlayerbotTravel.ArriveYards);
                Assert.Empty(PlayerbotTravel.JourneyOf(world.Player)!.Excluded.Triggers);
                Assert.False(PlayerbotTravel.JourneyOf(world.Player)!.Replanned);
                Assert.Equal(0, brain.TransitFailureCount);
            });
        }
        finally
        {
            brain.Stop();
        }
    }

    [Fact]
    public async Task ARefusedTrigger_FailsTheLeg_IsLeftOutOfTheReplan_AndTheBotWalksOn()
    {
        await using ShipTestWorld world = await StartAsync([Volume(Good, Entrance)], [Row(Good, 1, Exit)]);
        await world.PlaceAsync(1, Start);
        PlayerbotOptions options = Options();
        var brain = new PlayerbotBrain(world.Session, options);
        var travel = new PlayerbotTravel(options);
        try
        {
            await world.OnWorldAsync(() =>
            {
                Assert.True(travel.TryBegin(world.Session, world.Player, 1, Goal, out string? why), why);
                Assert.Contains(TravelMeans.TeleportTrigger, PlayerbotTravel.JourneyOf(world.Player)!.Planned.Legs.Select(l => l.Means));
            });

            Vector3 last = Start;
            float biggestJump = 0f;
            bool refused = false;
            JourneyState? state = await TravelJourneyShipTests.RunAsync(world, brain, 300_000, p =>
            {
                var now = new Vector3(p.X, p.Y, p.Z);
                biggestJump = MathF.Max(biggestJump, Vector3.Distance(last, now));
                last = now;
                if (refused || PlayerbotTravel.JourneyOf(p) is not { LegIndex: 1, Replanned: false }) return;
                refused = true;
                PlayerbotAreaTriggers.TrackerOf(p).Refusal = new PlayerbotAreaTriggers.TeleportRefusal(Good, "refused by the test", world.Host.World.NowMs);
            });
            TravelJourney journey = await world.OnWorldAsync(() => PlayerbotTravel.JourneyOf(world.Player)!);
            output.WriteLine($"{state}; replan {(journey.Replan is { } r ? PlayerbotTravel.Describe(r) : "none")}; biggest step {biggestJump:F1}");
            Assert.True(refused, "the bot never reached the trigger leg");
            Assert.Equal(JourneyState.Arrived, state);
            Assert.True(journey.Replanned);
            Assert.Contains(Good, journey.Excluded.Triggers);
            Assert.Equal([TravelMeans.Walk], journey.Replan!.Legs.Select(l => l.Means));
            Assert.True(biggestJump < 50f, $"the bot was carried to the exit all the same (biggest step {biggestJump:F1} yd)");
            await world.OnWorldAsync(() => Assert.True(
                Vector3.Distance(new Vector3(world.Player.X, world.Player.Y, world.Player.Z), Goal) <= PlayerbotTravel.ArriveYards));
        }
        finally
        {
            brain.Stop();
        }
    }
}
