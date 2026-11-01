using System.Numerics;
using ArcaneCore.World.Playerbots;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Travel;

/// <summary>
/// The wander fallback of a bot that is boxed in (post-#1651 findings): the home limits the mesh-point fallback keeps to
/// (<see cref="PlayerbotBrain.OutsideHomeLimits"/>: a pocket beyond the explore radius, a clipped home with no known zone), the back-off after
/// an empty run (<see cref="PlayerbotBrain.WanderMeshBackoffMs"/>) and the zone-border deflect angles (45, 90, 135 degrees).
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
}

/// <summary>
/// The Rut'theran landing of trigger 527 (8786, 969, 30) lies 3.9 yd outside the volume of the return trigger 542: walking from it to the dock
/// must not bounce the bot back up the tree. Needs the live terrain extraction; reported Skipped (with the reason) without it.
/// </summary>
public sealed class PlayerbotTravelFallbackRealTerrainTests : RealTerrainNavigationTestBase
{
    private const uint Kalimdor = 1;

    public PlayerbotTravelFallbackRealTerrainTests(ITestOutputHelper output) : base(output) { }

    [RealTerrainBotFact]
    public async Task FromTheRuttheranLanding_TheBotWalksToTheDock_WithoutBeingBouncedUpTheTree()
    {
        var landing = new Vector3(8786f, 969f, 30f);
        var dock = new Vector3(8600f, 800f, 0f);
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(landing, Kalimdor);
        float left = await terrain.WalkAsync(dock, 120_000, output);
        float fromLanding = await terrain.Host.OnWorldAsync(() => Vector2.Distance(new(terrain.Player.X, terrain.Player.Y), new(landing.X, landing.Y)));
        output.WriteLine($"left {left:F1} yd; {fromLanding:F1} yd from the landing");
        Assert.True(left <= 10f, $"stopped {left:F1} yards from the Rut'theran dock (a bounce through trigger 542 puts the bot on the tree, 1300 yards up)");
    }
}
