using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Teleport;
using ArcaneCore.World.Features;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>Install a same-map teleport trigger (a sphere of <paramref name="radius"/> yards at <see cref="Entrance"/>, exit at <see cref="Exit"/>) in the world's tables.</summary>
    public static Task InstallTriggerAsync(SyntheticNavWorld world, uint id, byte requiredLevel, float radius = 3f)
        => world.Host.OnWorldAsync(() =>
        {
            WorldMaps maps = WorldMaps.Of(world.Host.World);
            maps.ReplaceAreaTriggerTables(maps.BuildAreaTriggerTables(InMemoryMapDataStore.Content with
            {
                AreaTriggers = [new AreaTriggerTemplate(id, 0, Entrance.X, Entrance.Y, Entrance.Z, radius, 0, 0, 0, 0, "Upper level portal")],
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

    /// <summary>
    /// A world whose registered <see cref="IAreaTriggerGate"/> refuses trigger <see cref="Trigger"/> for every player: a refusal the plan cannot
    /// foresee (<see cref="PlayerbotTravel.MeetsRequirements"/> reads only the row), so the leg is planned, walked and refused.
    /// </summary>
    private Task<SyntheticNavWorld> StartRefusingAsync()
        => SyntheticNavWorld.StartAsync(_fixture, [TravelIslands.Home(), TravelIslands.Far(), TravelIslands.Large()], new FloorLineOfSight(),
            configureServices: services => services.AddSingleton<IWorldFeature, RefusingGate>());

    private sealed class RefusingGate : IWorldFeature, IAreaTriggerGate
    {
        public void Attach(WorldRuntime world) { }

        public AreaTriggerVerdict Check(Player player, AreaTriggerTeleport teleport)
            => teleport.Id == Trigger ? AreaTriggerVerdict.Refuse("The way is shut.") : AreaTriggerVerdict.Allow;
    }

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
        await using SyntheticNavWorld world = await StartRefusingAsync();
        // The trigger row admits the bot (level 0); the registered gate refuses it, so the plan cannot foresee the refusal.
        await TravelIslands.InstallTriggerAsync(world, Trigger, requiredLevel: 0);
        await world.PlaceAsync(TravelIslands.Start);
        PlayerbotOptions options = TravelIslands.Options();
        var brain = new PlayerbotBrain(world.Session, options);
        var travel = new PlayerbotTravel(options);
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        try
        {
            await world.Host.OnWorldAsync(() =>
            {
                Assert.True(travel.TryBegin(world.Session, world.Player, 0, TravelIslands.Goal, out string? why), why);
                Assert.Equal(["Walk", "AreaTrigger", "Walk"], PlayerbotTravel.JourneyOf(world.Player)!.Planned.Legs.Select(l => l.Means.ToString()));
            });

            JourneyState? state = await RunAsync(world, brain, 300_000, (_, _) => { });
            TravelJourney journey = await world.Host.OnWorldAsync(() => PlayerbotTravel.JourneyOf(world.Player)!);
            output.WriteLine($"{journey.State} {journey.Failure}; {capture.Describe()}");
            Assert.Equal(JourneyState.Failed, state);
            Assert.False(string.IsNullOrEmpty(journey.Failure));
            Assert.Equal(1, capture.Count(PlayerbotAnomalies.Key(PlayerbotTravel.AnomalyArea, "leg-failed")));
            Assert.Contains("trigger 527: area trigger 527", capture.Describe());
            // The trigger is left out of the replan, which therefore found no other way.
            Assert.Contains(Trigger, journey.Excluded.Triggers);
            Assert.Contains("no route", journey.Failure);
            await world.Host.OnWorldAsync(() => Assert.True(world.Player.X < 200f, "the refused bot stayed on its island"));
        }
        finally
        {
            brain.Stop();
        }
    }

    /// <summary>
    /// A trigger volume wider than the walk's arrival radius is entered while the walk before the trigger leg is under way. The refusal it
    /// notes must still fail the trigger leg: the walk before the leg stops up to <see cref="PlayerbotTravel.HandOffYards"/> short of the
    /// trigger, so the volume (30 yards here, entered at 70 yards out) is entered by the trigger leg itself, whose start the refusal is
    /// not older than. Before, the walk went to within 10 yards, the refusal predated the leg and was discarded, and the journey stood
    /// in the volume until its deadline.
    /// </summary>
    [Fact]
    public async Task ARefusalByAVolumeEnteredOnTheWayIn_StillFailsTheLeg()
    {
        await using SyntheticNavWorld world = await StartRefusingAsync();
        await TravelIslands.InstallTriggerAsync(world, Trigger, requiredLevel: 0, radius: 30f);
        await world.PlaceAsync(TravelIslands.Start);
        PlayerbotOptions options = TravelIslands.Options();
        var brain = new PlayerbotBrain(world.Session, options);
        var travel = new PlayerbotTravel(options);
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        try
        {
            await world.Host.OnWorldAsync(() =>
            {
                Assert.True(travel.TryBegin(world.Session, world.Player, 0, TravelIslands.Goal, out string? why), why);
                Assert.Equal(["Walk", "AreaTrigger", "Walk"], PlayerbotTravel.JourneyOf(world.Player)!.Planned.Legs.Select(l => l.Means.ToString()));
            });

            JourneyState? state = await RunAsync(world, brain, 120_000, (_, _) => { });
            TravelJourney journey = await world.Host.OnWorldAsync(() => PlayerbotTravel.JourneyOf(world.Player)!);
            output.WriteLine($"{journey.State} {journey.Failure}; {capture.Describe()}");
            Assert.Equal(JourneyState.Failed, state);
            Assert.Equal(1, capture.Count(PlayerbotAnomalies.Key(PlayerbotTravel.AnomalyArea, "leg-failed")));
            // The leg failed on the trigger's own refusal (not as a side effect of a walk that stood in the volume).
            Assert.Contains("trigger 527: area trigger 527", capture.Describe());
            Assert.Contains(Trigger, journey.Excluded.Triggers);
        }
        finally
        {
            brain.Stop();
        }
    }

    /// <summary>A trigger whose level requirement the bot lacks is left out of the plan: no route, no leg walked into a refusal.</summary>
    [Fact]
    public async Task ATriggerWithALevelTheBotLacks_IsNotPlanned()
    {
        await using SyntheticNavWorld world = await StartAsync();
        await TravelIslands.InstallTriggerAsync(world, Trigger, requiredLevel: 60);
        await world.PlaceAsync(TravelIslands.Start);
        var travel = new PlayerbotTravel(TravelIslands.Options());
        await world.Host.OnWorldAsync(() =>
        {
            Assert.True(world.Player.Level < 60);
            Assert.False(travel.TryBegin(world.Session, world.Player, 0, TravelIslands.Goal, out string? why));
            Assert.NotNull(why);
            Assert.Null(PlayerbotTravel.JourneyOf(world.Player));
        });
    }

    /// <summary>With <c>Travel:AreaTriggers</c> off no trigger is planned.</summary>
    [Fact]
    public async Task WithTheAreaTriggersSwitchOff_NoTriggerIsPlanned()
    {
        await using SyntheticNavWorld world = await StartAsync();
        await TravelIslands.InstallTriggerAsync(world, Trigger, requiredLevel: 0);
        await world.PlaceAsync(TravelIslands.Start);
        PlayerbotOptions options = TravelIslands.Options();
        options.Travel.AreaTriggers = false;
        var travel = new PlayerbotTravel(options);
        await world.Host.OnWorldAsync(() =>
        {
            Assert.Empty(PlayerbotTravel.BuildFacts(world.Session, world.Player, options).TriggerPortals);
            Assert.False(travel.TryBegin(world.Session, world.Player, 0, TravelIslands.Goal, out _));
        });
    }
}
