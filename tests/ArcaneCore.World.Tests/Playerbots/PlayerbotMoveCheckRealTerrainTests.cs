using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.World.Playerbots;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotMoveCheckRealTerrainTests : RealTerrainNavigationTestBase
{
    public PlayerbotMoveCheckRealTerrainTests(ITestOutputHelper output) : base(output) { }

    // Dun Morogh, just west of Kharanos: a doodad (M2) wall across x about -5639 at y -502, found by scanning the real vmaps for
    // segments that the default line of sight (doodads ignored) calls clear and the doodad-aware one blocks.
    private static readonly Vector3 FenceWest = new(-5646f, -502f, 396.7f), FenceEast = new(-5630f, -502f, 396.7f);

    /// <summary>
    /// A straight stepped leg across a real doodad fence is refused (cut before it), though the default line of sight, which ignores
    /// doodads, calls it clear: bots walked through such fences, trees and rocks.
    /// </summary>
    [RealTerrainBotFact]
    public async Task ADoodadFenceNearKharanos_BlocksAStraightLeg_TheDefaultSightLetsThrough()
    {
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(FenceWest);
        (bool Default, bool Aware, bool Whole, Vector3 Reach, bool Route) r = await terrain.Host.OnWorldAsync(() =>
        {
            var map = terrain.Player.Map!;
            MapCollision collision = map.Collision;
            Vector3 a = FenceWest with { Z = collision.GetHeight(FenceWest.X, FenceWest.Y, FenceWest.Z) };
            Vector3 b = FenceEast with { Z = collision.GetHeight(FenceEast.X, FenceEast.Y, FenceEast.Z) };
            bool defaultSight = collision.IsInLineOfSight(a.X, a.Y, a.Z + PlayerbotMoveCheck.ChestYards, b.X, b.Y, b.Z + PlayerbotMoveCheck.ChestYards);
            bool aware = PlayerbotMoveCheck.LineClear(collision, a, b);
            bool whole = PlayerbotMoveCheck.TryClip(map, a, b, out Vector3 reach) && reach == b;
            bool route = PlayerbotMoveCheck.TerrainRoute(map, a, b, new PlayerbotOptions { MaxRouteYards = 60, MaxPathPoints = 64 }, out _);
            return (defaultSight, aware, whole, reach, route);
        });
        output.WriteLine($"fence leg: default sight {r.Default}, doodad-aware {r.Aware}, whole {r.Whole}, reach {r.Reach}, route {r.Route}");
        Assert.True(r.Default, "the premise: the default line of sight ignores the fence");
        Assert.False(r.Aware);
        Assert.False(r.Whole);
        Assert.False(r.Route, "a stepped route was planned across the fence");
        Assert.True(r.Reach.X < -5636f, $"the leg reaches {r.Reach}");
    }
}
