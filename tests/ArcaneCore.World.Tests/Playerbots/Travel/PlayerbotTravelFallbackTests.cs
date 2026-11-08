using System.Numerics;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Travel;

/// <summary>
/// The wander fallback of a bot that is boxed in (post-#1651 findings): the home limits the mesh-point fallback keeps to
/// (<see cref="PlayerbotBrain.OutsideHomeLimits"/>: a pocket beyond the explore radius, a clipped home with no known zone), the back-off after
/// an empty run (<see cref="PlayerbotBrain.WanderMeshBackoffMs"/>), the zone-border deflect angles (45, 90, 135 degrees) and the landing guard
/// of a same-map portal beside where a bot arrived (<see cref="PlayerbotTriggerTracker.IsGuarded"/>).
/// </summary>
public sealed class PlayerbotTravelFallbackTests
{
    private const uint Map = 1, ZoneA = 40, ZoneB = 41;

    // Radius 1000: ExploreRadius is 800 (ExploreFraction 0.8).
    private static PlayerbotHomeArea Plain() => new(Map, Vector3.Zero, 1000f, "home");

    private static PlayerbotHomeArea Clipped(Func<float, float, float, uint> zoneAt)
        => new(Map, Vector3.Zero, 1000f, "home", zoneClipped: true, new PlayerbotZoneLookup(zoneAt));

    [Fact]
    public void ABotInsideTheExploreRadius_MaySampleInsideIt_NotBeyondIt()
    {
        PlayerbotHomeArea home = Plain();
        Assert.False(PlayerbotBrain.OutsideHomeLimits(home, 100f, 0f, new Vector3(700f, 0f, 0f)));
        Assert.True(PlayerbotBrain.OutsideHomeLimits(home, 100f, 0f, new Vector3(850f, 0f, 0f)));
    }

    [Fact]
    public void ABotBeyondTheExploreRadius_InAPocket_MayTakePointsNearerTheCentre_NotFartherOut()
    {
        PlayerbotHomeArea home = Plain();
        // The bot stands 900 yd out: points 880 yd out are beyond the radius but nearer the centre than the bot, so they are allowed.
        Assert.False(PlayerbotBrain.OutsideHomeLimits(home, 900f, 0f, new Vector3(880f, 0f, 0f)));
        Assert.False(PlayerbotBrain.OutsideHomeLimits(home, 900f, 0f, new Vector3(850f, 20f, 0f)));
        // Farther out than the bot (or level with it) stays refused, as does a point out of the circle's reach altogether.
        Assert.True(PlayerbotBrain.OutsideHomeLimits(home, 900f, 0f, new Vector3(930f, 0f, 0f)));
        Assert.True(PlayerbotBrain.OutsideHomeLimits(home, 900f, 0f, new Vector3(0f, 900f, 0f)));
    }

    [Fact]
    public void AClippedHome_KeepsItsZone_ForTheMeshPoints()
    {
        PlayerbotHomeArea home = Clipped((x, _, _) => x > 300f ? ZoneB : ZoneA);
        Assert.False(PlayerbotBrain.OutsideHomeLimits(home, 100f, 0f, new Vector3(200f, 0f, 0f)));
        Assert.True(PlayerbotBrain.OutsideHomeLimits(home, 100f, 0f, new Vector3(400f, 0f, 0f)));
    }

    [Fact]
    public void AClippedHomeWithNoKnownZone_RefusesNothingInsideTheCircle()
    {
        PlayerbotHomeArea home = Clipped((_, _, _) => 0u);
        Assert.True(home.ZoneUnknown);
        Assert.False(PlayerbotBrain.OutsideHomeLimits(home, 100f, 0f, new Vector3(200f, 0f, 0f)));
        Assert.False(PlayerbotBrain.OutsideHomeLimits(home, 100f, 0f, new Vector3(-400f, 300f, 0f)));
        Assert.True(PlayerbotBrain.OutsideHomeLimits(home, 100f, 0f, new Vector3(900f, 0f, 0f)), "the circle still limits it");
    }

    [Theory]
    [InlineData(0, 10_000u)]
    [InlineData(1, 10_000u)]
    [InlineData(2, 20_000u)]
    [InlineData(3, 40_000u)]
    [InlineData(4, 80_000u)]
    [InlineData(50, 80_000u)]
    [InlineData(-3, 10_000u)]
    public void AfterEmptyRuns_TheFallbackBacksOff_DoublingToACap(int empties, uint expectedMs)
        => Assert.Equal(expectedMs, PlayerbotBrain.WanderMeshBackoffMs(empties));

    [Theory]
    [InlineData(0, true, 45f)]
    [InlineData(0, false, -45f)]
    [InlineData(1, true, 90f)]
    [InlineData(2, true, 135f)]
    public void TheZoneBorderDeflect_TriesFortyFiveNinetyOneThirtyFiveDegrees(int refusals, bool preferPositive, float expectedDegrees)
    {
        // An unclipped home contains every candidate inside the explore radius, so the first candidate is the one returned.
        PlayerbotHomeArea home = Plain();
        Vector3 from = Vector3.Zero;
        Vector3 leg = PlayerbotBrain.DeflectZoneBorderLeg(home, from, baseAngle: 0f, preferPositive, refusals);
        float length = PlayerbotBrain.ExploreLegLength(refusals);
        float radians = expectedDegrees * (MathF.PI / 180f);
        Assert.Equal(length * MathF.Cos(radians), leg.X, 0.01f);
        Assert.Equal(length * MathF.Sin(radians), leg.Y, 0.01f);
    }

    // AreaTrigger.dbc / classic-db: 542 is Rut'theran's portal up (radius 10); trigger 527 in Darnassus lands the bot at (8785.79, 966.983, 30.1999).
    private static readonly AreaTriggerTemplate PortalUp = new(542, 1, 8799.41f, 969.787f, 30.2409f, 10f, 0, 0, 0, 0, "Teddrassil - Darnassus");
    private static readonly Vector3 PortalExit = new(8785.79f, 966.983f, 30.1999f);

    [Fact]
    public void TheDarnassusPortalExit_LiesOutsideTheReturnVolume_ByAboutFourYards()
    {
        float yardsFromCentre = Vector2.Distance(new(PortalExit.X, PortalExit.Y), new(PortalUp.X, PortalUp.Y));
        Assert.InRange(yardsFromCentre - PortalUp.Radius, 3.8f, 4.0f);
        Assert.False(PlayerbotTriggerTracker.Contains(PortalUp, 1, PortalExit));
    }

    [Fact]
    public void ABotLandingBesideAPortal_HoldsItBack_UntilItIsWalkedClearOfIt()
    {
        var tracker = new PlayerbotTriggerTracker();
        var map = new object();
        AreaTriggerTemplate[] near = [PortalUp];
        tracker.Observe(map, 1, PortalExit, near, fire: false);
        Assert.True(tracker.IsGuarded(542), "the landing is 3.9 yd outside the volume");

        // Walking into the volume still reports the entry: the Update filter holds the trigger back, not the tracker.
        IReadOnlyList<uint> entered = tracker.Observe(map, 1, new Vector3(PortalUp.X - 5f, PortalUp.Y, PortalUp.Z), near, fire: true);
        Assert.Equal(new uint[] { 542 }, entered);
        Assert.True(tracker.IsGuarded(542));

        // Still within the margin of the edge (10 + 8 yd of the centre): still held.
        tracker.Observe(map, 1, new Vector3(PortalUp.X - 16f, PortalUp.Y, PortalUp.Z), near, fire: true);
        Assert.True(tracker.IsGuarded(542));

        // Clear of it: released, so walking back in is a deliberate trip through the portal.
        tracker.Observe(map, 1, new Vector3(PortalUp.X - 19f, PortalUp.Y, PortalUp.Z), near, fire: true);
        Assert.False(tracker.IsGuarded(542));
    }

    [Fact]
    public void ABotInsideAVolumeWhereItLands_OrFarFromOne_GuardsNothing()
    {
        var inside = new PlayerbotTriggerTracker();
        inside.Observe(new object(), 1, new Vector3(PortalUp.X, PortalUp.Y, PortalUp.Z), new[] { PortalUp }, fire: false);
        Assert.False(inside.IsGuarded(542), "inside the volume it already counts as inside");

        var far = new PlayerbotTriggerTracker();
        far.Observe(new object(), 1, new Vector3(PortalUp.X - 60f, PortalUp.Y, PortalUp.Z), new[] { PortalUp }, fire: false);
        Assert.False(far.IsGuarded(542));
    }

    [Fact]
    public void AGuardedTrigger_IsWanted_OnlyWhileAWalkRenewsIt()
    {
        var tracker = new PlayerbotTriggerTracker { WantedTrigger = 542, WantedUntilMs = 6_000 };
        Assert.True(tracker.IsWanted(542, 1_000));
        Assert.False(tracker.IsWanted(527, 1_000), "another trigger is not the errand");
        Assert.False(tracker.IsWanted(542, 6_000), "the want ran out");
        Assert.False(tracker.IsWanted(542, 7_000));
    }
}

/// <summary>
/// The Rut'theran landing of trigger 527 (8785.79, 966.983, 30.2) lies 3.9 yd outside the volume of the return trigger 542 (8799.41, 969.787,
/// radius 10): a bot that landed there and walked on past the portal used to be sent straight back up the tree to the Darnassus exit. The
/// world here has triggers 527 and 542 and their teleport rows, as in <see cref="PlayerbotRuttheranRecoveryTerrainTests"/> (classic-db
/// areatrigger_teleport), and the walk crosses the volume, where <see cref="PlayerbotMotion.Pump"/> reports the trigger as a client would.
/// Needs the live terrain extraction; reported Skipped (with the reason) without it.
/// </summary>
public sealed class PlayerbotTravelFallbackRealTerrainTests : RealTerrainNavigationTestBase
{
    private const uint Kalimdor = 1;
    private static readonly AreaTriggerTemplate PortalUp = new(542, 1, 8799.41f, 969.787f, 30.2409f, 10f, 0, 0, 0, 0, "Teddrassil - Darnassus");
    private static readonly AreaTriggerTemplate PortalDown = new(527, 1, 9947.48f, 2630.04f, 1318.6f, 10f, 0, 0, 0, 0, "Teddrassil - Ruth Theran");
    private static readonly Vector3 DarnassusExit = new(9946.25f, 2612.97f, 1316.49f);
    private static readonly Vector3 Landing = new(8785.79f, 966.983f, 30.1999f);

    // Past the portal's centre (8799.41, 969.787), so the walk from the landing crosses its volume.
    private static readonly Vector3 BeyondPortal = new(8808.5f, 971f, 30.2409f);

    public PlayerbotTravelFallbackRealTerrainTests(ITestOutputHelper output) : base(output) { }

    private static void PortalWorld(IServiceCollection services) => services.AddSingleton<IMapDataStore>(new PortalMaps());

    private sealed class PortalMaps : IMapDataStore
    {
        public Task<MapContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new MapContent(
            [
                new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
                new MapTemplate(1, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Kalimdor", ""),
            ],
            [],
            [PortalUp, PortalDown],
            [
                new AreaTriggerTeleport(542, "Teddrassil - Darnassus", "", 0, 1, DarnassusExit.X, DarnassusExit.Y, DarnassusExit.Z, 4.71239f),
                new AreaTriggerTeleport(527, "Teddrassil - Ruth Theran", "", 0, 1, Landing.X, Landing.Y, Landing.Z, 3.40339f),
            ],
            []));
    }

    [RealTerrainBotFact]
    public async Task FromTheDarnassusPortalExit_WalkingPastThePortalVolume_DoesNotBounceTheBotUpTheTree()
    {
        await using Terrain terrain = await Terrain.StartAsync(services: PortalWorld);
        await terrain.PlaceAsync(Landing, Kalimdor);
        float left = await terrain.WalkAsync(BeyondPortal, 60_000, output);
        Vector3 end = await terrain.Host.OnWorldAsync(() => new Vector3(terrain.Player.X, terrain.Player.Y, terrain.Player.Z));
        output.WriteLine($"left {left:F1} yd; the bot ends at {end}");
        Assert.True(end.Z < 500f && Vector3.Distance(end, DarnassusExit) > 100f,
            $"the bot was sent through trigger 542 to the Darnassus exit (ends at {end}) by a walk past the portal from its own landing");
        Assert.True(left <= 10f, $"stopped {left:F1} yards short of the point beyond the portal");
    }

    // The Rut'theran boat port: the one TaxiPathNode.dbc stop of the ship path of Moonspray (gameobject 176244, type 15, data0 = taxi path 293)
    // in the Rut'theran area, on map 1. Its keyframe lies at sea level (z 0), so the walk's goal is the dry dock floor
    // PlayerbotShip.ResolveDockTarget finds in the rings up to DockSearchRadiiYards beside it.
    private static readonly Vector3 RuttheranPort = new(8533.65f, 1025.05f, 0f);

    // ResolveDockTarget answers the port itself while a tile is still being read; it is asked again until it names a dock.
    private const int DockResolveAttempts = 200;

    /// <summary>
    /// Row 15/225: on the real mesh, a dry dock floor is found beside the Rut'theran port, and a bot that landed from the Darnassus portal
    /// (trigger 527) walks to it on the ground below the tree and is not sent up trigger 542 (the landing is 3.9 yd outside its volume).
    /// The walk itself runs through PlanToward and TryAdvance, not through PlayerbotTravel; the landing guard is covered by
    /// <see cref="FromTheDarnassusPortalExit_WalkingPastThePortalVolume_DoesNotBounceTheBotUpTheTree"/> and the tracker unit tests.
    /// </summary>
    [RealTerrainBotFact]
    public async Task FromTheDarnassusPortalExit_TheBotWalksToTheRuttheranDock_WithoutTakingThePortalUp()
    {
        await using Terrain terrain = await Terrain.StartAsync(services: PortalWorld);
        await terrain.PlaceAsync(Landing, Kalimdor);
        Vector3 dock = RuttheranPort;
        for (int i = 0; i < DockResolveAttempts && dock == RuttheranPort; i++)
        {
            dock = await terrain.Host.OnWorldAsync(() => PlayerbotShip.ResolveDockTarget(terrain.Player.Map!, RuttheranPort,
                new Vector3(terrain.Player.X, terrain.Player.Y, terrain.Player.Z)));
            if (dock != RuttheranPort) break;
            await Task.Delay(20);
            await terrain.Host.World.AdvanceClockAsync(100);
        }

        output.WriteLine($"port {RuttheranPort}, dock target {dock}");
        float portToDock = Vector2.Distance(new(RuttheranPort.X, RuttheranPort.Y), new(dock.X, dock.Y));
        Assert.True(dock != RuttheranPort, $"no dry dock floor was found beside the Rut'theran port {RuttheranPort}");
        Assert.True(portToDock <= 38f, $"the dock {dock} lies {portToDock:F1} yd from the port, beyond the 38 yd search");
        float left = await terrain.WalkTowardAsync(dock, 5f, 120_000, output);
        Vector3 end = await terrain.Host.OnWorldAsync(() => new Vector3(terrain.Player.X, terrain.Player.Y, terrain.Player.Z));
        output.WriteLine($"left {left:F1} yd; the bot ends at {end}");
        Assert.True(end.Z < 500f && Vector3.Distance(end, DarnassusExit) > 100f, $"the bot was sent up trigger 542 on its way to the dock (ends at {end})");
        Assert.True(left <= 5f, $"stopped {left:F1} yards from the Rut'theran dock {dock}");
    }

    [RealTerrainBotFact]
    public async Task WalkingIntoThePortalOnPurpose_StillTakesIt()
    {
        await using Terrain terrain = await Terrain.StartAsync(services: PortalWorld);
        await terrain.PlaceAsync(Landing, Kalimdor);
        // A short walk first reads the navigation tiles, so the want below does not run out while the plan waits for them.
        await terrain.WalkAsync(new Vector3(Landing.X - 3f, Landing.Y, Landing.Z), 20_000, output);
        await terrain.Host.OnWorldAsync(() => PlayerbotAreaTriggers.Want(terrain.Player, 542, terrain.Host.World.NowMs));
        await terrain.WalkAsync(BeyondPortal, 60_000, output);
        await terrain.WaitAsync(() => terrain.Player.MapId == Kalimdor && terrain.Player.Z > 1000f, "the climb through trigger 542");
        Vector3 end = await terrain.Host.OnWorldAsync(() => new Vector3(terrain.Player.X, terrain.Player.Y, terrain.Player.Z));
        Assert.True(Vector3.Distance(end, DarnassusExit) < 30f, $"a walk that asked for trigger 542 ended at {end}, not the Darnassus exit");
    }
}
