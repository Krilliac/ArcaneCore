using System.Numerics;
using System.Reflection;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Bots;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Elevator;

/// <summary>
/// The progress clocks of three callers (the repair walk's stuck clock, the explore cell's best distance, the destination's not-closing
/// clock) against the walk to a lift's landing (<see cref="PlayerbotProgressAim"/>, #1482 / #1481): a bot on the canyon floor whose goal
/// lies on the plateau right across the cliff, 400 yards away, walks away from the goal for the whole approach, a thousand yards north
/// to the Great Lift. None of the three may take that for a goal it cannot reach, and none may keep a best distance measured to something
/// else after the aim changed (the lift leg, the fallback, the ride).
/// </summary>
public sealed class PlayerbotElevatorApproachClockTests
{
    private const uint Dizzywig = 3453;
    private const float Cliff = ElevatorTestWorld.Cliff;

    /// <summary>The canyon floor 100 yards from the cliff, 1000 yards south of the lift (y -1839).</summary>
    private static readonly Vector3 Floor = new(Cliff - 100f, -2839f, ElevatorTestWorld.BottomFloor);

    /// <summary>The goal: the plateau across the cliff, 400 yards due east of <see cref="Floor"/>; walking north to the lift walks away from it.</summary>
    private static readonly Vector3 Plateau = new(Cliff + 300f, -2839f, ElevatorTestWorld.TopFloor);

    private static PlayerbotOptions Options() => new() { Enabled = true, ThinkIntervalMs = 100, Population = { TramMinLevel = 0 }, Risk = { Enabled = false } };

    private static Vector3 PositionOf(Player player) => new(player.X, player.Y, player.Z);

    private static float FlatDistance(Vector3 a, Vector3 b) => Vector2.Distance(new(a.X, a.Y), new(b.X, b.Y));

    /// <summary>
    /// Repair (B2): the repair NPC stands on the plateau across the cliff; the trip to it walks to the lift first. The stuck clock measures
    /// that walk to the lift's landing: the spawn is not set aside nor the trip rested while the bot walks away from the NPC.
    /// </summary>
    [Fact]
    public async Task ARepairNpcAwayFromTheLift_IsNotSetAside_WhileTheBotWalksToTheLift()
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate
        {
            Entry = 9212, Name = "boots", Class = (uint)ItemClass.Armor, SubClass = ItemSubClasses.ArmorMisc,
            InventoryType = (uint)InventoryType.Feet, Armor = 10, MaxDurability = 20, ItemLevel = 10, Quality = 1, Stackable = 1,
            AllowableClass = uint.MaxValue, AllowableRace = uint.MaxValue,
        });
        using IDisposable itemScope = items.Use();
        var content = new CreatureContent(
            [new CreatureTemplate { Entry = 951, Name = "armorer", Faction = 900011, NpcFlags = (uint)NpcFlags.Repair, DisplayIds = [49] }],
            [new CreatureSpawn { Guid = 95101, Entry = 951, MapId = ElevatorTestData.Kalimdor, X = Plateau.X, Y = Plateau.Y, Z = Plateau.Z }], [], [], []);
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync(creatures: content, configure: services =>
        {
            services.AddSingleton(new RepairCostTable([(10u, Enumerable.Repeat(1u, RepairCostTable.MultiplierCount).ToArray())], [(4u, 1f)]));
            services.AddSingleton(new FactionTemplateCatalog([new FactionTemplateRecord(1, 1, 0, 1, 0, 0), new FactionTemplateRecord(900011, 0, 0, 8, 0, 0)]));
        });
        await world.OnWorldAsync(() => WorldCollision.Of(world.Host.World).Install(pathfinder: new ElevatorTestWorld.DensePathfinder()));
        await world.PlaceAsync(Floor);
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            player.Level = 10;
            PlayerbotEquipmentTests.EquipFromBags(player, 9212);
            player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Feet)!.Durability = 0;
            player.Money = 100;
        });
        PlayerbotOptions options = Options();
        var goals = new PlayerbotTownGoals(world.Session, options);
        FieldInfo spawnField = typeof(PlayerbotTownGoals).GetField("_repairSpawn", BindingFlags.Instance | BindingFlags.NonPublic)!;

        ElevatorTrip? trip = null;
        float start = -1f, farthest = 0f;
        for (int step = 0; step < 6_000 && trip is null; step++)
        {
            (trip, uint walkingTo, bool candidate, float fromNpc) = await world.TickAsync(() =>
            {
                goals.BeginThink();
                world.Session.ManagedBudget = new ManagedActionBudget(1);
                goals.Update(world.Player, 100);
                return (PlayerbotElevator.TripOf(world.Player), (uint)spawnField.GetValue(goals)!, goals.HasCandidate(world.Player),
                    Vector3.Distance(PositionOf(world.Player), Plateau));
            });
            if (start < 0f) start = fromNpc;
            farthest = MathF.Max(farthest, fromNpc);
            if (step == 0 || trip is not null) continue;
            Assert.True(walkingTo == 95101, $"step {step}: the spawn was set aside {fromNpc:F0} yd from the NPC");
            Assert.True(candidate, $"step {step}: the repair trip rested {fromNpc:F0} yd from the NPC");
        }

        Assert.NotNull(trip);
        Assert.Equal(ElevatorLevel.Low, trip.From);
        Assert.Equal(951u, goals.TargetEntry);
        Assert.True(farthest > start + 200f, $"the bot never walked away from the NPC (farthest {farthest:F0}, start {start:F0}): the check proves nothing");
    }

    private sealed record ExploreRun(ElevatorTestWorld World, PlayerbotBrain Brain, MethodInfo Explore, System.Collections.IDictionary Blocked, FieldInfo Candidate, FieldInfo RouteField);

    private static async Task<ElevatorTestWorld> ExploreWorldAsync()
    {
        var content = new CreatureContent(
            [new CreatureTemplate { Entry = 99010, Name = "right", MinLevel = 8, MaxLevel = 8, Faction = 14 }],
            [new CreatureSpawn { Guid = 1, Entry = 99010, MapId = ElevatorTestData.Kalimdor, X = Plateau.X, Y = Plateau.Y, Z = Plateau.Z }], [], [], []);
        ElevatorTestWorld world = await ElevatorTestWorld.StartAsync(creatures: content, configure: services =>
            services.AddSingleton(new FactionTemplateCatalog([new FactionTemplateRecord(1, 1, 0, 1, 0, 0), new FactionTemplateRecord(14, 0, 0, 8, 0, 1)])));
        await world.OnWorldAsync(() => WorldCollision.Of(world.Host.World).Install(pathfinder: new ElevatorTestWorld.DensePathfinder()));
        await world.PlaceAsync(Floor);
        await world.OnWorldAsync(() => world.Player.Level = 8);
        return world;
    }

    private static ExploreRun Explorer(ElevatorTestWorld world)
    {
        var brain = new PlayerbotBrain(world.Session, Options());
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        return new ExploreRun(world, brain, typeof(PlayerbotBrain).GetMethod("Explore", flags)!,
            (System.Collections.IDictionary)typeof(PlayerbotBrain).GetField("_exploreBlockedCells", flags)!.GetValue(brain)!,
            typeof(PlayerbotBrain).GetField("_exploreCandidate", flags)!, typeof(PlayerbotBrain).GetField("_route", flags)!);
    }

    private static uint CandidateGuid(ExploreRun run)
        => run.Candidate.GetValue(run.Brain) is PlayerbotExploreAtlas.Candidate candidate ? candidate.Spawn.Guid : 0u;

    /// <summary>
    /// Explore (B1, the ride): the cell is on the plateau across the cliff. The lift legs walk away from it, the ride lifts the bot onto the
    /// plateau, 900 yards from the cell; the first plateau leg that completes is judged against a best distance measured to the cell
    /// (not to the lift's landing the earlier legs closed on), and the cell is not blocked.
    /// </summary>
    [Fact]
    public async Task AnExploreCellAwayFromTheLift_IsNotBlocked_AcrossTheApproachAndTheRide()
    {
        await using ElevatorTestWorld world = await ExploreWorldAsync();
        ExploreRun run = Explorer(world);
        PlayerbotOptions options = Options();
        bool sawLiftRoute = false, rode = false;
        float start = -1f, farthest = 0f;
        for (int step = 0; step < 10_000; step++)
        {
            (ElevatorStage? stage, int blocked, uint guid, bool toLift, Vector3 at) = await world.TickAsync(() =>
            {
                Player player = world.Player;
                if (PlayerbotElevator.TripOf(player) is { Stage: not (ElevatorStage.Done or ElevatorStage.Failed) })
                    PlayerbotElevator.Drive(world.Session, player, options, 100, out _);
                else
                {
                    world.Session.ManagedBudget = new ManagedActionBudget(4);
                    run.Explore.Invoke(run.Brain, [player, 100u]);
                }

                return (PlayerbotElevator.TripOf(player)?.Stage, run.Blocked.Count, CandidateGuid(run),
                    run.RouteField.GetValue(run.Brain) is PlayerbotRoute { ToLiftLanding: true }, PositionOf(player));
            });
            float fromCell = FlatDistance(at, Plateau);
            if (start < 0f) start = fromCell;
            farthest = MathF.Max(farthest, fromCell);
            sawLiftRoute |= toLift;
            rode |= stage == ElevatorStage.Done;
            Assert.True(blocked == 0, $"step {step}: the cell was blocked {fromCell:F0} yd from it, trip {stage}");
            if (step >= 1) Assert.True(guid == 1u, $"step {step}: the explore candidate is {guid}, not the fixture's cell");
            if (rode && at.X >= Cliff && fromCell < 700f) break;
        }

        Assert.True(sawLiftRoute, "no leg walked to the lift's landing: the check proves nothing");
        Assert.True(rode, "the bot never rode the lift");
        Assert.True(farthest > start + 200f, $"the bot never walked away from the cell (farthest {farthest:F0}, start {start:F0})");
        Assert.Empty(run.Blocked);
    }

    /// <summary>
    /// Explore (B1, the fallback): the lift is held off after three lift legs; the walk to the cell goes on from the bot's place, 380
    /// yards north of its start. That leg is judged against a best distance measured to the cell on the spot, not the start's.
    /// </summary>
    [Fact]
    public async Task AnExploreCellAwayFromTheLift_IsNotBlocked_WhenTheApproachFallsBackToTheCell()
    {
        await using ElevatorTestWorld world = await ExploreWorldAsync();
        ElevatorLift lift = await world.GreatLiftAsync();
        ExploreRun run = Explorer(world);
        PlayerbotOptions options = Options();
        bool failed = false, sawFallback = false;
        float start = -1f, farthest = 0f;
        ElevatorTrip? trip = null;
        for (int step = 0; step < 10_000 && trip is null; step++)
        {
            (trip, int blocked, bool toLift, bool hasRoute, Vector3 at) = await world.TickAsync(() =>
            {
                Player player = world.Player;
                world.Session.ManagedBudget = new ManagedActionBudget(4);
                run.Explore.Invoke(run.Brain, [player, 100u]);
                bool lifting = run.RouteField.GetValue(run.Brain) is PlayerbotRoute { ToLiftLanding: true };
                bool any = run.RouteField.GetValue(run.Brain) is not null;
                if (!failed && lifting && player.Y >= Floor.Y + 300f)
                {
                    failed = true;
                    PlayerbotTransitBackoff.Fail(player, TransitKind.Lift, lift.Key, world.Host.World.NowMs, options);
                }
                else if (failed && !sawFallback && any && !lifting)
                {
                    sawFallback = true;
                    PlayerbotTransitBackoff.Clear(player, TransitKind.Lift, lift.Key);
                }

                return (PlayerbotElevator.TripOf(player), run.Blocked.Count, lifting, any, PositionOf(player));
            });
            float fromCell = FlatDistance(at, Plateau);
            if (start < 0f) start = fromCell;
            farthest = MathF.Max(farthest, fromCell);
            Assert.True(blocked == 0, $"step {step}: the cell was blocked {fromCell:F0} yd from it (lift leg {toLift}, route {hasRoute}, fallback {sawFallback})");
        }

        Assert.True(failed, "the lift was never held off: the check proves nothing");
        Assert.True(sawFallback, "no fallback leg to the cell was planned: the check proves nothing");
        Assert.NotNull(trip);
        Assert.True(farthest > start + 200f, $"the bot never walked away from the cell (farthest {farthest:F0}, start {start:F0})");
    }

    /// <summary>A destinations object whose entries hold Dizzywig's spawn (14419) on the plateau 1000 yards south-east of <see cref="Floor"/>, and the bot on the floor.</summary>
    private static async Task<(ElevatorTestWorld World, PlayerbotWorldDestinations Destinations, Func<Vector3, PlayerbotRoute?, uint, Task<bool>> Clock)> ClockWorldAsync()
    {
        Vector3 goal = new(Cliff + 50f, -3839f, ElevatorTestWorld.TopFloor);
        var content = new CreatureContent(
            [new CreatureTemplate { Entry = Dizzywig, Name = "Wharfmaster Dizzywig", DisplayIds = [49] }],
            [new CreatureSpawn { Guid = 14419, Entry = Dizzywig, MapId = ElevatorTestData.Kalimdor, X = goal.X, Y = goal.Y, Z = goal.Z }], [], [], []);
        ElevatorTestWorld world = await ElevatorTestWorld.StartAsync(creatures: content);
        await world.OnWorldAsync(() => WorldCollision.Of(world.Host.World).Install(pathfinder: new ElevatorTestWorld.DensePathfinder()));
        await world.PlaceAsync(Floor);
        await world.OnWorldAsync(() => world.Player.Level = 10);
        var destinations = new PlayerbotWorldDestinations(world.Session, Options());
        await world.TickAsync(() => destinations.Update(world.Player, Dizzywig, 100)); // loads the entries
        object entries = typeof(PlayerbotWorldDestinations).GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(destinations)!;
        string held = string.Join(",", ((System.Collections.IEnumerable)entries).Cast<object>().Select(e =>
        {
            object spawn = e.GetType().GetProperty("Spawn")!.GetValue(e)!;
            return spawn.GetType().GetProperty("Guid")!.GetValue(spawn)!.ToString();
        }));
        Assert.True(held.Split(',').Contains("14419"), $"the destination holds the spawns [{held}], not 14419: the check proves nothing");
        MethodInfo notClosing = typeof(PlayerbotWorldDestinations).GetMethod("NotClosing", BindingFlags.Instance | BindingFlags.NonPublic)!;
        uint now = world.Host.World.NowMs + 120_000u; // past the 60 s gap: the first look starts the clock afresh
        async Task<bool> Clock(Vector3 at, PlayerbotRoute? route, uint elapsedMs)
        {
            await world.PlaceAsync(at);
            now += elapsedMs;
            return await world.OnWorldAsync(() => (bool)notClosing.Invoke(destinations, [world.Player, 14419u, elapsedMs, now, route])!);
        }

        return (world, destinations, Clock);
    }

    private static PlayerbotRoute LiftLeg(Vector3 landing)
        => new([Floor, Floor with { Y = Floor.Y + 100f }], 1f, [true]) { ToLiftLanding = true, LiftLanding = landing };

    /// <summary>
    /// Destinations (I3): the not-closing clock of a bot whose lift legs and goal legs alternate (a pending or refused approach between
    /// them). Each leg closes on its own aim, so no leg stops the clock by itself; the bound on the changes of aim does: past it the
    /// clock measures to the spawn, the way to the lift is no progress, and the clock runs out. Without the bound every flip started the
    /// clock afresh and it never ran out. The clock is called as the destination does, one look a second, the bot placed 10 yards on per look.
    /// </summary>
    [Fact]
    public async Task ADestinationThatAlternatesLiftAndGoalLegs_RunsOutAfterTheBoundedChanges()
    {
        (ElevatorTestWorld world, _, Func<Vector3, PlayerbotRoute?, uint, Task<bool>> clock) = await ClockWorldAsync();
        await using ElevatorTestWorld owned = world;
        PlayerbotRoute lift = LiftLeg(new Vector3(Cliff - 100f, -1900f, ElevatorTestWorld.BottomFloor));
        const int legLooks = 8;
        int calls = 0, timedOutAt = -1;
        for (int leg = 0; leg < 20 && timedOutAt < 0; leg++)
        {
            bool towardsLift = leg % 2 == 0;
            for (int look = 1; look <= legLooks && timedOutAt < 0; look++)
            {
                float north = towardsLift ? look * 10f : (legLooks - look) * 10f;
                calls++;
                if (await clock(Floor with { Y = Floor.Y + north }, towardsLift ? lift : null, 1000u)) timedOutAt = calls;
            }
        }

        Assert.True(timedOutAt > 0, "the clock never ran out for a bot that alternates between the lift and the goal");
        // Six legs are six changes of aim (the lift, the goal, ... ): each leg closes on its own aim and the clock stays quiet.
        Assert.True(timedOutAt > PlayerbotProgressAim.MaxChanges * legLooks, $"the clock ran out at look {timedOutAt}, inside the legs that move the aim");
        Assert.True(timedOutAt <= (PlayerbotProgressAim.MaxChanges + 3) * legLooks, $"the clock ran out only at look {timedOutAt}");
    }

    /// <summary>
    /// A change of aim is no progress: a bot that made no headway for 15 seconds towards the spawn, and then is handed a walk to a lift's
    /// landing it does not make headway on either, runs out after the 5 seconds left, not after 20 more.
    /// </summary>
    [Fact]
    public async Task AChangeOfAim_KeepsTheTimeAlreadyRun()
    {
        (ElevatorTestWorld world, _, Func<Vector3, PlayerbotRoute?, uint, Task<bool>> clock) = await ClockWorldAsync();
        await using ElevatorTestWorld owned = world;
        PlayerbotRoute lift = LiftLeg(new Vector3(Cliff - 100f, -1900f, ElevatorTestWorld.BottomFloor));
        Assert.False(await clock(Floor, null, 1000u)); // the clock starts
        for (int look = 1; look <= 15; look++) Assert.False(await clock(Floor, null, 1000u), $"look {look}");
        int lookOfTimeout = 0;
        for (int look = 16; look <= 40 && lookOfTimeout == 0; look++)
            if (await clock(Floor, lift, 1000u)) lookOfTimeout = look;
        Assert.Equal(20, lookOfTimeout);
    }

    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>The repair world of <see cref="ARepairNpcAwayFromTheLift_IsNotSetAside_WhileTheBotWalksToTheLift"/>: boots worn out, the repairer on the plateau, the bot on the canyon floor.</summary>
    private sealed class RepairRun(ElevatorTestWorld world, PlayerbotTownGoals goals, IDisposable items) : IAsyncDisposable
    {
        public ElevatorTestWorld World { get; } = world;

        public PlayerbotTownGoals Goals { get; } = goals;

        public async ValueTask DisposeAsync()
        {
            await World.DisposeAsync();
            items.Dispose();
        }

        public T Field<T>(string name) => (T)typeof(PlayerbotTownGoals).GetField(name, Private)!.GetValue(Goals)!;

        public void SetField(string name, object? value) => typeof(PlayerbotTownGoals).GetField(name, Private)!.SetValue(Goals, value);

        public Task<int> StepAsync() => World.TickAsync(() =>
        {
            Goals.BeginThink();
            World.Session.ManagedBudget = new ManagedActionBudget(1);
            Goals.Update(World.Player, 100);
            return 0;
        });
    }

    private static async Task<RepairRun> RepairRunAsync()
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate
        {
            Entry = 9212, Name = "boots", Class = (uint)ItemClass.Armor, SubClass = ItemSubClasses.ArmorMisc,
            InventoryType = (uint)InventoryType.Feet, Armor = 10, MaxDurability = 20, ItemLevel = 10, Quality = 1, Stackable = 1,
            AllowableClass = uint.MaxValue, AllowableRace = uint.MaxValue,
        });
        IDisposable itemScope = items.Use();
        var content = new CreatureContent(
            [new CreatureTemplate { Entry = 951, Name = "armorer", Faction = 900011, NpcFlags = (uint)NpcFlags.Repair, DisplayIds = [49] }],
            [new CreatureSpawn { Guid = 95101, Entry = 951, MapId = ElevatorTestData.Kalimdor, X = Plateau.X, Y = Plateau.Y, Z = Plateau.Z }], [], [], []);
        ElevatorTestWorld world = await ElevatorTestWorld.StartAsync(creatures: content, configure: services =>
        {
            services.AddSingleton(new RepairCostTable([(10u, Enumerable.Repeat(1u, RepairCostTable.MultiplierCount).ToArray())], [(4u, 1f)]));
            services.AddSingleton(new FactionTemplateCatalog([new FactionTemplateRecord(1, 1, 0, 1, 0, 0), new FactionTemplateRecord(900011, 0, 0, 8, 0, 0)]));
        });
        await world.OnWorldAsync(() => WorldCollision.Of(world.Host.World).Install(pathfinder: new ElevatorTestWorld.DensePathfinder()));
        await world.PlaceAsync(Floor);
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            player.Level = 10;
            PlayerbotEquipmentTests.EquipFromBags(player, 9212);
            player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Feet)!.Durability = 0;
            player.Money = 100;
        });
        return new RepairRun(world, new PlayerbotTownGoals(world.Session, Options()), itemScope);
    }

    /// <summary>Thinks until the repair walk holds a walk to a lift's landing (the aim is then the landing, one change of aim).</summary>
    private static async Task HoldLiftWalkAsync(RepairRun run)
    {
        bool held = false;
        for (int step = 0; step < 6_000 && !held; step++)
        {
            await run.StepAsync();
            held = await run.World.OnWorldAsync(() => run.Field<PlayerbotRoute?>("_repairRoute") is { ToLiftLanding: true }
                && PlayerbotElevator.TripOf(run.World.Player) is null);
        }

        Assert.True(held, "the repair walk never held a walk to the lift: the check proves nothing");
        PlayerbotProgressAim aim = await run.World.OnWorldAsync(() => run.Field<PlayerbotProgressAim>("_repairAim"));
        Assert.NotNull(aim.Landing);
    }

    /// <summary>
    /// A think with no route (between legs, a pending tile) is no change of aim: Retarget(null) would drop a held landing and count a change, so
    /// a few brief pauses on the way to the lift used up the bound on the changes of aim and the walk was measured to the NPC again.
    /// </summary>
    [Fact]
    public async Task ABriefPauseWithNoRoute_IsNoChangeOfAim_ForTheRepairWalk()
    {
        await using RepairRun run = await RepairRunAsync();
        await HoldLiftWalkAsync(run);
        int before = (await run.World.OnWorldAsync(() => run.Field<PlayerbotProgressAim>("_repairAim"))).Changes;
        for (int pause = 0; pause < PlayerbotProgressAim.MaxChanges; pause++)
        {
            await run.World.OnWorldAsync(() => { run.SetField("_repairRoute", null); return 0; });
            await run.StepAsync(); // the think that finds no route
            await run.StepAsync(); // the think that holds the replanned one
        }

        PlayerbotProgressAim after = await run.World.OnWorldAsync(() => run.Field<PlayerbotProgressAim>("_repairAim"));
        Assert.True(after.Changes <= before + 1, $"{PlayerbotProgressAim.MaxChanges} pauses made {after.Changes - before} changes of aim");
    }

    /// <summary>
    /// A change of aim is no progress for the repair clock either: a walk that made no headway for 3 seconds keeps those seconds when it is handed a
    /// walk to a lift's landing, as the destinations' clock does (<see cref="AChangeOfAim_KeepsTheTimeAlreadyRun"/>).
    /// </summary>
    [Fact]
    public async Task AChangeOfAim_KeepsTheRepairClockTimeAlreadyRun()
    {
        await using RepairRun run = await RepairRunAsync();
        await HoldLiftWalkAsync(run);
        await run.World.OnWorldAsync(() =>
        {
            run.SetField("_repairStuckMs", 3_000u);
            run.SetField("_repairAim", default(PlayerbotProgressAim)); // the next think meets the held lift walk as a new aim
            return 0;
        });
        await run.StepAsync();

        uint stuck = await run.World.OnWorldAsync(() => run.Field<uint>("_repairStuckMs"));
        Assert.True(stuck >= 3_000u, $"the change of aim started the clock over: {stuck} ms");
        Assert.True(await run.World.OnWorldAsync(() => run.Field<PlayerbotProgressAim>("_repairAim").Changes) >= 1, "no change of aim was seen: the check proves nothing");
    }
}
