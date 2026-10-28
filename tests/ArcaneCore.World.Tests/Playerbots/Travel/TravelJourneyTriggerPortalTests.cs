using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.GridTerrain;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Travel;

/// <summary>
/// Synthetic stand-in for Teldrassil's upper level on map 0: two flat mesh islands no walk joins (the bot's <see cref="Home"/> and the
/// <see cref="Far"/> one the goal is on), each over the 500 polygons below which the planner reads a piece of mesh as a fragment, and a
/// <see cref="Large"/> third one more than 16 times their size (the map's largest component). Each tile sits at its true Detour
/// coordinates (<c>HaveTileAt</c>: tile x from world Y, tile y from world X, 533.33 yards a tile) and its true terrain file.
/// </summary>
internal static class TravelIslands
{
    /// <summary>World X 0-115, Y 0-115; terrain file (31, 31).</summary>
    public static (int X, int Y, CellTile Tile) Home() => (31, 31, new CellTile(0, 0, 0, 0, 23, 23, Size: 5));

    /// <summary>World X 560-675, Y 0-115; terrain file (30, 31).</summary>
    public static (int X, int Y, CellTile Tile) Far() => (30, 31, new CellTile(0, 1, 560, 0, 23, 23, Size: 5));

    /// <summary>World X 0-465, Y 540-1005 (8649 polygons); terrain file (31, 30).</summary>
    public static (int X, int Y, CellTile Tile) Large() => (31, 30, new CellTile(1, 0, 0, 540, 93, 93, Size: 5));

    public static readonly Vector3 Start = new(20f, 60f, 0f);
    public static readonly Vector3 Entrance = new(100f, 60f, 0f);
    public static readonly Vector3 Exit = new(600f, 60f, 0f);
    public static readonly Vector3 Goal = new(650f, 60f, 0f);

    public static PlayerbotOptions Options() => new() { Enabled = true, ThinkIntervalMs = 100, Risk = { Enabled = false } };

    /// <summary>Install a same-map teleport trigger (a 3-yard sphere at <see cref="Entrance"/>, exit at <see cref="Exit"/>) in the world's tables.</summary>
    public static Task InstallTriggerAsync(SyntheticNavWorld world, uint id, byte requiredLevel)
        => world.Host.OnWorldAsync(() =>
        {
            WorldMaps maps = WorldMaps.Of(world.Host.World);
            maps.ReplaceAreaTriggerTables(maps.BuildAreaTriggerTables(InMemoryMapDataStore.Content with
            {
                AreaTriggers = [new AreaTriggerTemplate(id, 0, Entrance.X, Entrance.Y, Entrance.Z, 3f, 0, 0, 0, 0, "Upper level portal")],
                AreaTriggerTeleports = [new AreaTriggerTeleport(id, "Upper level - lower landing", "", requiredLevel, 0, Exit.X, Exit.Y, Exit.Z, 0f)],
            }));
            return true;
        });
}

/// <summary>
/// End to end on a mesh the walk cannot cross (<see cref="TravelIslands"/>): a journey whose goal lies on another island of the same map
/// goes over the same-map teleport trigger, as a bot on Teldrassil's upper level must (trigger 527 to Rut'theran); the real
/// <c>TeleportHandlers</c> move the bot. When the trigger refuses the bot, the leg fails once, the trigger is left out of the replan, and
/// the journey ends with one <c>leg-failed</c> report.
/// </summary>
public sealed class TravelJourneyTriggerPortalTests(ITestOutputHelper output) : IDisposable
{
    private const uint Trigger = 527;
    private readonly NavMeshFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private Task<SyntheticNavWorld> StartAsync()
        => SyntheticNavWorld.StartAsync(_fixture, [TravelIslands.Home(), TravelIslands.Far(), TravelIslands.Large()], new FloorLineOfSight());

    /// <summary>Brain updates every 50 ms tick (a think every 100 ms) and the motion pumped after each, until the journey ends or <paramref name="maxMs"/> passed.</summary>
    private static async Task<JourneyState?> RunAsync(SyntheticNavWorld world, PlayerbotBrain brain, uint maxMs, Action<Player, TravelJourney?> observe)
    {
        for (uint elapsed = 0; elapsed < maxMs; elapsed += 50)
        {
            JourneyState? state = await world.Host.OnWorldAsync(() =>
            {
                world.Session.ManagedBudget = new ManagedActionBudget(4);
                brain.Update(50);
                TravelJourney? journey = PlayerbotTravel.JourneyOf(world.Player);
                observe(world.Player, journey);
                return journey?.State;
            });
            if (state is not JourneyState.Active) return state;
            await world.Host.World.AdvanceClockAsync(50);
            await world.Host.OnWorldAsync(() => PlayerbotMotion.Pump(world.Session, world.Player, world.Host.World.NowMs));
        }

        return await world.Host.OnWorldAsync(() => PlayerbotTravel.JourneyOf(world.Player)?.State);
    }

    [Fact]
    public async Task AJourneyToAnotherIsland_WalksIntoTheTrigger_IsTeleported_AndWalksOnFromTheExit()
    {
        await using SyntheticNavWorld world = await StartAsync();
        await TravelIslands.InstallTriggerAsync(world, Trigger, requiredLevel: 0);
        await world.PlaceAsync(TravelIslands.Start);
        PlayerbotOptions options = TravelIslands.Options();
        var brain = new PlayerbotBrain(world.Session, options);
        var travel = new PlayerbotTravel(options);
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        try
        {
            TravelRoute route = await world.Host.OnWorldAsync(() =>
            {
                Assert.True(travel.TryBegin(world.Session, world.Player, 0, TravelIslands.Goal, out string? why), why);
                return PlayerbotTravel.JourneyOf(world.Player)!.Planned;
            });
            output.WriteLine(PlayerbotTravel.Describe(route));
            // By name: the means of a trigger leg is TravelMeans.AreaTrigger.
            Assert.Equal(["Walk", "AreaTrigger", "Walk"], route.Legs.Select(l => l.Means.ToString()));

            var legsSeen = new List<int>();
            var jumps = new List<float>();
            float lastX = TravelIslands.Start.X;
            JourneyState? state = await RunAsync(world, brain, 300_000, (p, j) =>
            {
                if (j is not null && (legsSeen.Count == 0 || legsSeen[^1] != j.LegIndex)) legsSeen.Add(j.LegIndex);
                if (MathF.Abs(p.X - lastX) > 200f) jumps.Add(p.X - lastX);
                lastX = p.X;
            });

            await world.Host.OnWorldAsync(() => output.WriteLine($"bot at {world.Player.X:F1},{world.Player.Y:F1}; journey {PlayerbotTravel.JourneyOf(world.Player)?.State} {PlayerbotTravel.JourneyOf(world.Player)?.Failure}"));
            Assert.Equal(JourneyState.Arrived, state);
            Assert.Equal([0, 1, 2], legsSeen.Where(l => l <= 2)); // the legs in order; none repeated
            Assert.Single(jumps); // exactly one teleport, from the entrance side to the exit side
            Assert.InRange(jumps[0], 400f, 520f);
            await world.Host.OnWorldAsync(() =>
            {
                Assert.True(Vector3.Distance(new Vector3(world.Player.X, world.Player.Y, world.Player.Z), TravelIslands.Goal) <= PlayerbotTravel.ArriveYards);
                Assert.Equal(0u, world.Player.MapId);
            });
            Assert.Equal(0, capture.Count(PlayerbotAnomalies.Key(PlayerbotTravel.AnomalyArea, "leg-failed")));
        }
        finally
        {
            brain.Stop();
        }
    }

    [Fact]
    public async Task ATriggerThatRefusesTheBot_FailsTheLegOnce_AndTheReplanHasNoRoute()
    {
        await using SyntheticNavWorld world = await StartAsync();
        await TravelIslands.InstallTriggerAsync(world, Trigger, requiredLevel: 60);
        await world.PlaceAsync(TravelIslands.Start);
        PlayerbotOptions options = TravelIslands.Options();
        var brain = new PlayerbotBrain(world.Session, options);
        var travel = new PlayerbotTravel(options);
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        try
        {
            await world.Host.OnWorldAsync(() =>
            {
                Assert.True(world.Player.Level < 60, "the trigger must refuse this bot");
                Assert.True(travel.TryBegin(world.Session, world.Player, 0, TravelIslands.Goal, out string? why), why);
                Assert.Equal(["Walk", "AreaTrigger", "Walk"], PlayerbotTravel.JourneyOf(world.Player)!.Planned.Legs.Select(l => l.Means.ToString()));
            });

            JourneyState? state = await RunAsync(world, brain, 300_000, (_, _) => { });
            TravelJourney journey = await world.Host.OnWorldAsync(() => PlayerbotTravel.JourneyOf(world.Player)!);
            output.WriteLine($"{journey.State} {journey.Failure}; {capture.Describe()}");
            Assert.Equal(JourneyState.Failed, state);
            Assert.False(string.IsNullOrEmpty(journey.Failure));
            Assert.Equal(1, capture.Count(PlayerbotAnomalies.Key(PlayerbotTravel.AnomalyArea, "leg-failed")));
            await world.Host.OnWorldAsync(() => Assert.True(world.Player.X < 200f, "the refused bot stayed on its island"));
        }
        finally
        {
            brain.Stop();
        }
    }
}
