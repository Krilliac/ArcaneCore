using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Templates;
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
            Assert.Equal([DungeonEntryScenario.EntranceTrigger], PlayerbotAreaTriggers.OnMap(maps, 0).Select(t => t.Id));
            Assert.Equal([DungeonEntryScenario.ExitTrigger], PlayerbotAreaTriggers.OnMap(maps, DungeonEntryScenario.Deadmines).Select(t => t.Id));
            Assert.Empty(PlayerbotAreaTriggers.OnMap(maps, 1));
        });
    }

    /// <summary>
    /// A bot following a route across trigger 78 reports it as it enters the box (no scripted CMSG_AREATRIGGER) and the ordinary
    /// handler teleports it into The Deadmines.
    /// </summary>
    [Fact]
    public async Task ARouteAcrossTheEntrance_ReportsTheTrigger_AndTheBotIsTeleportedIn()
    {
        await using DungeonBotHost host = await DungeonBotHost.StartAsync();
        AreaTriggerTemplate entrance = await host.OnWorldAsync(() => WorldMaps.Of(host.Host.World).FindAreaTrigger(DungeonEntryScenario.EntranceTrigger)!);
        await host.OnWorldAsync(() => host.Player.Level = 10);
        await host.TeleportAsync(0, entrance.X - 20f, entrance.Y, DeadminesTestContent.OutsideFloor);
        var options = new PlayerbotOptions { Enabled = true };
        PlayerbotRoute route = await host.OnWorldAsync(() =>
        {
            Assert.True(PlayerbotNavigation.TryPlan(host.Player, new Vector3(entrance.X + 20f, entrance.Y, DeadminesTestContent.OutsideFloor),
                options, out PlayerbotRoute? planned));
            return planned!;
        });

        bool inside = false;
        for (int think = 0; think < 40 && !inside; think++)
        {
            inside = await host.OnWorldAsync(() =>
            {
                if (PlayerbotMovementControl.Update(host.Session, host.Player)) return false;
                if (host.Player.MapId == DungeonEntryScenario.Deadmines) return true;
                host.Session.ManagedBudget = new ManagedActionBudget(1);
                PlayerbotNavigation.TryAdvance(host.Session, route, options, 500, host.Host.World.NowMs);
                return false;
            });
            await host.AdvanceAsync(500);
            await host.OnWorldAsync(() => PlayerbotMotion.Pump(host.Session, host.Player, host.Host.World.NowMs));
        }

        await host.AcknowledgeUntilAsync(p => p.MapId == DungeonEntryScenario.Deadmines, "teleported into The Deadmines");
    }
}
