using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Dungeon;

/// <summary>
/// Client-like area triggers (<see cref="PlayerbotAreaTriggers"/>, <see cref="PlayerbotTriggerTracker"/>): the volume maths (a sphere,
/// a box turned by its orientation), one CMSG_AREATRIGGER per entry, re-armed after leaving, nothing for where a bot lands.
/// </summary>
public sealed class PlayerbotAreaTriggerTests
{
    private static readonly object Map = new();
    private static readonly AreaTriggerTemplate Sphere = new(1, 0, 100f, 100f, 10f, 5f, 0, 0, 0, 0, "sphere");

    // 2 yards along its orientation (x' axis), 10 across it, 6 high; turned a quarter turn: x' points along world +y.
    private static readonly AreaTriggerTemplate Box = new(2, 0, 200f, 200f, 10f, 0f, 2f, 10f, 6f, MathF.PI / 2, "box");

    [Theory]
    [InlineData(100f, 100f, 10f, true)]
    [InlineData(104.9f, 100f, 10f, true)]
    [InlineData(105.1f, 100f, 10f, false)]
    [InlineData(103f, 103f, 10f, true)]   // 4.24 yards
    [InlineData(104f, 104f, 10f, false)]  // 5.66 yards
    [InlineData(100f, 100f, 15.1f, false)] // a sphere, not a cylinder
    public void Sphere_ContainsPointsWithinItsRadius(float x, float y, float z, bool inside)
        => Assert.Equal(inside, PlayerbotTriggerTracker.Contains(Sphere, 0, new Vector3(x, y, z)));

    [Theory]
    // Turned a quarter turn, the 2-yard side runs along y and the 10-yard side along x.
    [InlineData(200f, 200.9f, 10f, true)]
    [InlineData(200f, 201.1f, 10f, false)]
    [InlineData(204.9f, 200f, 10f, true)]
    [InlineData(205.1f, 200f, 10f, false)]
    [InlineData(200f, 200f, 12.9f, true)]
    [InlineData(200f, 200f, 13.1f, false)]
    public void RotatedBox_ContainsPointsWithinItsTurnedSides(float x, float y, float z, bool inside)
        => Assert.Equal(inside, PlayerbotTriggerTracker.Contains(Box, 0, new Vector3(x, y, z)));

    [Fact]
    public void AnotherMap_OrANonFinitePosition_IsNeverInside()
    {
        Assert.False(PlayerbotTriggerTracker.Contains(Sphere, 1, new Vector3(100f, 100f, 10f)));
        Assert.False(PlayerbotTriggerTracker.Contains(Sphere, 0, new Vector3(float.NaN, 100f, 10f)));
    }

    [Fact]
    public void WalkingIn_FiresOnce_StayingInsideDoesNotFireAgain()
    {
        var tracker = new PlayerbotTriggerTracker();
        AreaTriggerTemplate[] triggers = [Sphere, Box];
        Assert.Empty(tracker.Observe(Map, 0, new Vector3(90f, 100f, 10f), triggers, fire: true)); // first position: outside
        Assert.False(tracker.WouldEnter(Map, 0, new Vector3(94f, 100f, 10f), triggers));
        Assert.True(tracker.WouldEnter(Map, 0, new Vector3(96f, 100f, 10f), triggers));
        Assert.Equal([1u], tracker.Observe(Map, 0, new Vector3(96f, 100f, 10f), triggers, fire: true));
        Assert.Empty(tracker.Observe(Map, 0, new Vector3(100f, 100f, 10f), triggers, fire: true));
        Assert.Empty(tracker.Observe(Map, 0, new Vector3(104f, 100f, 10f), triggers, fire: true));
        Assert.False(tracker.WouldEnter(Map, 0, new Vector3(104f, 100f, 10f), triggers));
    }

    [Fact]
    public void LeavingAndComingBack_FiresAgain()
    {
        var tracker = new PlayerbotTriggerTracker();
        AreaTriggerTemplate[] triggers = [Sphere];
        tracker.Observe(Map, 0, new Vector3(90f, 100f, 10f), triggers, fire: true);
        Assert.Equal([1u], tracker.Observe(Map, 0, new Vector3(100f, 100f, 10f), triggers, fire: true));
        Assert.Empty(tracker.Observe(Map, 0, new Vector3(110f, 100f, 10f), triggers, fire: true));
        Assert.Empty(tracker.Inside);
        Assert.Equal([1u], tracker.Observe(Map, 0, new Vector3(101f, 100f, 10f), triggers, fire: true));
    }

    [Fact]
    public void LandingInside_DoesNotFire_UntilTheBotLeavesAndWalksBackIn()
    {
        var tracker = new PlayerbotTriggerTracker();
        AreaTriggerTemplate[] triggers = [Sphere];
        Assert.Empty(tracker.Observe(Map, 0, new Vector3(100f, 100f, 10f), triggers, fire: true)); // a teleport landing
        Assert.Equal([1u], tracker.Inside);
        Assert.Empty(tracker.Observe(Map, 0, new Vector3(101f, 100f, 10f), triggers, fire: true));
        tracker.Forget(); // the next teleport
        Assert.False(tracker.WouldEnter(Map, 0, new Vector3(100f, 100f, 10f), triggers));
        Assert.Empty(tracker.Observe(Map, 0, new Vector3(100f, 100f, 10f), triggers, fire: true));
        tracker.Observe(Map, 0, new Vector3(120f, 100f, 10f), triggers, fire: true);
        Assert.Equal([1u], tracker.Observe(Map, 0, new Vector3(100f, 100f, 10f), triggers, fire: true));
    }

    [Fact]
    public void ANewMap_StartsSilent()
    {
        var tracker = new PlayerbotTriggerTracker();
        AreaTriggerTemplate[] triggers = [Sphere];
        tracker.Observe(Map, 0, new Vector3(90f, 100f, 10f), triggers, fire: true);
        Assert.Empty(tracker.Observe(new object(), 0, new Vector3(100f, 100f, 10f), triggers, fire: true));
    }

    [Fact]
    public void SeveralVolumesEnteredAtOnce_FireInIdOrder()
    {
        var tracker = new PlayerbotTriggerTracker();
        AreaTriggerTemplate overlap = new(3, 0, 100f, 100f, 10f, 2f, 0, 0, 0, 0, "inner");
        tracker.Observe(Map, 0, new Vector3(80f, 100f, 10f), [overlap, Sphere], fire: true);
        Assert.Equal([1u, 3u], tracker.Observe(Map, 0, new Vector3(100f, 100f, 10f), [overlap, Sphere], fire: true));
    }

    /// <summary>The per-map cache reads <c>WorldMaps.AreaTriggers</c> and holds only the map's own triggers.</summary>
    [Fact]
    public async Task OnMap_ListsEachMapsOwnTriggers()
    {
        await using DungeonBotHost host = await DungeonBotHost.StartAsync();
        await host.OnWorldAsync(() =>
        {
            WorldMaps maps = WorldMaps.Of(host.Host.World);
            Assert.Equal([DungeonEntryScenario.EntranceTrigger, DeadminesTestContent.TramTrigger, DeadminesTestContent.TavernTrigger],
                PlayerbotAreaTriggers.OnMap(maps, 0).Select(t => t.Id));
            Assert.Equal([DungeonEntryScenario.ExitTrigger], PlayerbotAreaTriggers.OnMap(maps, DungeonEntryScenario.Deadmines).Select(t => t.Id));
            Assert.Empty(PlayerbotAreaTriggers.OnMap(maps, 1));
        });
    }

    /// <summary>
    /// The grid (<see cref="PlayerbotTriggerIndex"/>) never hides a trigger: for points scattered around spheres and turned boxes of
    /// every size (and one larger than the grid allows), the triggers containing a point found through
    /// <see cref="PlayerbotTriggerIndex.Near"/> are exactly those a scan of the whole map finds.
    /// </summary>
    [Fact]
    public void TheGrid_FindsExactlyWhatAFullScanFinds()
    {
        var random = new Random(5875);
        var triggers = new List<AreaTriggerTemplate>();
        for (uint id = 1; id <= 200; id++)
        {
            float x = (random.NextSingle() * 2000f) - 1000f, y = (random.NextSingle() * 2000f) - 1000f;
            triggers.Add(id % 2 == 0
                ? new AreaTriggerTemplate(id, 0, x, y, 0f, 1f + (random.NextSingle() * 60f), 0, 0, 0, 0, "sphere")
                : new AreaTriggerTemplate(id, 0, x, y, 0f, 0f, 1f + (random.NextSingle() * 80f), 1f + (random.NextSingle() * 80f), 20f,
                    random.NextSingle() * 6.28f, "box"));
        }

        triggers.Add(new AreaTriggerTemplate(999, 0, 0f, 0f, 0f, 3000f, 0, 0, 0, 0, "huge")); // listed everywhere
        var index = new PlayerbotTriggerIndex(triggers);
        Assert.Equal(triggers.Count, index.All.Count);
        int hits = 0;
        for (int i = 0; i < 20_000; i++)
        {
            AreaTriggerTemplate near = triggers[random.Next(triggers.Count - 1)];
            var point = new Vector3(near.X + (random.NextSingle() * 140f) - 70f, near.Y + (random.NextSingle() * 140f) - 70f,
                (random.NextSingle() * 16f) - 8f);
            uint[] expected = [.. triggers.Where(t => AreaTriggerZone.Contains(t, 0, point.X, point.Y, point.Z)).Select(t => t.Id).Order()];
            uint[] found = [.. index.Near(point).Where(t => PlayerbotTriggerTracker.Contains(t, 0, point)).Select(t => t.Id).Order()];
            Assert.Equal(expected, found);
            if (expected.Length > 1) hits++;
        }

        Assert.True(hits > 1000, $"only {hits} points lay in a trigger besides the huge one");
        Assert.Single(index.Near(new Vector3(5000f, 5000f, 0f))); // far away only the huge trigger is a candidate
    }

    /// <summary>The bounding-sphere reject agrees with the exact test at the corners of a turned box.</summary>
    [Theory]
    [InlineData(200f, 200.9f, 10f, true)]
    [InlineData(204.9f, 200.9f, 12.9f, true)]  // a corner
    [InlineData(205.1f, 201.1f, 13.1f, false)]
    public void TheBoundingReject_KeepsTheCorners(float x, float y, float z, bool inside)
    {
        Assert.Equal(inside, PlayerbotTriggerTracker.Contains(Box, 0, new Vector3(x, y, z)));
        Assert.Equal(inside, AreaTriggerZone.Contains(Box, 0, x, y, z));
    }

    /// <summary>
    /// The per-tick check of a moving bot allocates nothing while it enters no trigger (it runs every world tick for every moving
    /// bot). Before, every check allocated a list and a set.
    /// </summary>
    [Fact]
    public void ObservingWithoutAnEntry_AllocatesNothing()
    {
        var tracker = new PlayerbotTriggerTracker();
        var index = new PlayerbotTriggerIndex([Sphere, Box]);
        var outside = new Vector3(90f, 100f, 10f);
        var inside = new Vector3(100f, 100f, 10f);
        tracker.Observe(Map, 0, outside, index.Near(outside), fire: true);
        Assert.Equal([1u], tracker.Observe(Map, 0, inside, index.Near(inside), fire: true)); // an entry may allocate
        for (int i = 0; i < 100; i++) Step(); // warm up
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++) Step();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);

        void Step()
        {
            // Staying inside: no entry.
            tracker.WouldEnter(Map, 0, inside, index.Near(inside));
            tracker.Observe(Map, 0, inside, index.Near(inside), fire: true);
        }
    }

    /// <summary>
    /// A bot whose controller opted in (<see cref="PlayerbotAreaTriggers.AllowTeleports"/>) and follows a route across trigger 78
    /// reports it as it enters the box (no scripted CMSG_AREATRIGGER) and the ordinary handler teleports it into The Deadmines.
    /// </summary>
    [Fact]
    public async Task ARouteAcrossTheEntrance_WithConsent_ReportsTheTrigger_AndTheBotIsTeleportedIn()
    {
        await using DungeonBotHost host = await DungeonBotHost.StartAsync();
        AreaTriggerTemplate entrance = await host.OnWorldAsync(() => WorldMaps.Of(host.Host.World).FindAreaTrigger(DungeonEntryScenario.EntranceTrigger)!);
        await host.OnWorldAsync(() =>
        {
            host.Player.Level = 10;
            PlayerbotAreaTriggers.AllowTeleports(host.Player, true);
        });

        Assert.True(await WalkAcrossAsync(host, entrance), "never left Eastern Kingdoms");
        await host.AcknowledgeUntilAsync(p => p.MapId == DungeonEntryScenario.Deadmines, "teleported into The Deadmines");
    }

    /// <summary>
    /// Without a controller's consent a living bot walks straight through teleport triggers that lead off AllowedMaps: the dungeon
    /// entrance (level 10 met) and a Deeprun-Tram-like trigger to map 369, a map that is neither listed nor a dungeon. Either would
    /// leave the bot where nothing brings it back and the login gate refuses it. Before, the bot was teleported by both.
    /// </summary>
    [Theory]
    [InlineData(DungeonEntryScenario.EntranceTrigger)]
    [InlineData(DeadminesTestContent.TramTrigger)]
    public async Task ARouteAcrossATeleportOffAllowedMaps_WithoutConsent_WalksStraightThrough(uint id)
    {
        await using DungeonBotHost host = await DungeonBotHost.StartAsync();
        AreaTriggerTemplate trigger = await host.OnWorldAsync(() => WorldMaps.Of(host.Host.World).FindAreaTrigger(id)!);
        await host.OnWorldAsync(() => host.Player.Level = 10);

        Assert.False(await WalkAcrossAsync(host, trigger), "the bot was teleported away");
        await host.OnWorldAsync(() =>
        {
            Assert.Equal(0u, host.Player.MapId);
            Assert.Null(host.Teleports.StageOf(host.Player));
            Assert.True(host.Player.X > trigger.X + 15f, $"stopped at x {host.Player.X}, the trigger is at {trigger.X}");
        });
    }

    /// <summary>
    /// What the motion reports as the bot steps into a volume: a trigger without a teleport (a tavern, quest exploration) always;
    /// the tram trigger not; the entrance only after the controller opted in.
    /// </summary>
    [Fact]
    public async Task Update_ReportsTriggersWithoutATeleport_AndOnlyTheTeleportsTheBotMayTake()
    {
        await using DungeonBotHost host = await DungeonBotHost.StartAsync();
        var options = new PlayerbotOptions { Enabled = true };
        (AreaTriggerTemplate tavern, AreaTriggerTemplate tram, AreaTriggerTemplate entrance) = await host.OnWorldAsync(() =>
        {
            WorldMaps maps = WorldMaps.Of(host.Host.World);
            return (maps.FindAreaTrigger(DeadminesTestContent.TavernTrigger)!, maps.FindAreaTrigger(DeadminesTestContent.TramTrigger)!,
                maps.FindAreaTrigger(DungeonEntryScenario.EntranceTrigger)!);
        });
        await host.OnWorldAsync(() => host.Player.Level = 10);

        Assert.Equal([DeadminesTestContent.TavernTrigger], await StepInAsync(host, options, tavern));
        Assert.Empty(await StepInAsync(host, options, tram));
        Assert.Empty(await StepInAsync(host, options, entrance));
        await host.OnWorldAsync(() => PlayerbotAreaTriggers.AllowTeleports(host.Player, true));
        Assert.Equal([DungeonEntryScenario.EntranceTrigger], await StepInAsync(host, options, entrance));
        await host.AcknowledgeUntilAsync(p => p.MapId == DungeonEntryScenario.Deadmines, "teleported into The Deadmines");
    }

    // A route started 15 yards west of the trigger, and the bot now stands at its centre (placed there, so the server's own position
    // check agrees): the triggers the motion sends for that step.
    private static async Task<IReadOnlyList<uint>> StepInAsync(DungeonBotHost host, PlayerbotOptions options, AreaTriggerTemplate trigger)
    {
        await host.TeleportAsync(0, trigger.X, trigger.Y, DeadminesTestContent.OutsideFloor);
        return await host.OnWorldAsync(() =>
        {
            Map map = host.Player.Map!;
            PlayerbotAreaTriggers.Reset(host.Player);
            PlayerbotAreaTriggers.Begin(host.Session, host.Player, map, new Vector3(trigger.X - 15f, trigger.Y, DeadminesTestContent.OutsideFloor), options);
            return PlayerbotAreaTriggers.Update(host.Session, host.Player, map, new Vector3(host.Player.X, host.Player.Y, host.Player.Z));
        });
    }

    // Walk with the brain's navigation from 20 yards west of the trigger to 20 yards east of it; true when the bot left map 0.
    private static async Task<bool> WalkAcrossAsync(DungeonBotHost host, AreaTriggerTemplate trigger)
    {
        await host.TeleportAsync(0, trigger.X - 20f, trigger.Y, DeadminesTestContent.OutsideFloor);
        var options = new PlayerbotOptions { Enabled = true };
        var end = new Vector3(trigger.X + 20f, trigger.Y, DeadminesTestContent.OutsideFloor);
        PlayerbotRoute route = await host.OnWorldAsync(() =>
        {
            Assert.True(PlayerbotNavigation.TryPlan(host.Player, end, options, out PlayerbotRoute? planned));
            return planned!;
        });

        for (int think = 0; think < 40; think++)
        {
            bool done = await host.OnWorldAsync(() =>
            {
                if (PlayerbotMovementControl.Update(host.Session, host.Player)) return false;
                if (host.Player.MapId != 0) return true;
                if (Vector2.Distance(new(host.Player.X, host.Player.Y), new(end.X, end.Y)) <= 1f) return true;
                host.Session.ManagedBudget = new ManagedActionBudget(1);
                if (route.Complete)
                {
                    Assert.True(PlayerbotNavigation.TryPlan(host.Player, end, options, out PlayerbotRoute? again));
                    route = again!;
                }

                PlayerbotNavigation.TryAdvance(host.Session, route, options, 500, host.Host.World.NowMs);
                return false;
            });
            if (done) break;
            await host.AdvanceAsync(500);
            await host.OnWorldAsync(() => PlayerbotMotion.Pump(host.Session, host.Player, host.Host.World.NowMs));
        }

        return await host.OnWorldAsync(() => host.Player.MapId != 0 || !host.Player.IsInWorld || host.Teleports.StageOf(host.Player) is not null);
    }
}
