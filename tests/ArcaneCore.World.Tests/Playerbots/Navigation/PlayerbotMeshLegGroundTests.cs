using System.Numerics;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Tests.GridTerrain;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Navigation;

/// <summary>
/// The mesh-route ground check (<see cref="PlayerbotMotion.Validate(Map, PlayerbotRoute, int, int, Vector3, Vector3, out string?)"/>)
/// on legs whose straight chord runs far from every model floor while the mesh they were planned on has a polygon under the walk:
/// a steep tunnel descent and a cliff switchback (<c>bot-motion/start-blocked</c> and <c>route-blocked</c> with
/// <c>reason=NoGroundSupport moveMode=NavMesh</c>, issues #1485 and #1501), and a model floor below the terrain. The heights are the
/// issues' own; the collision is a fake (model floors, a mesh floor query, an unavailable mesh walk) so the cases run without client
/// data. A real cliff, with no polygon within the leg's heights, is still refused.
/// </summary>
public sealed class PlayerbotMeshLegGroundTests : IDisposable
{
    // The point the legs are placed at (#1485, map 1 inside the Timbermaw Hold tunnel); only the heights matter to the check.
    private const float X = 9070.5f, Y = 1869.1f;

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "arcanecore-meshleg-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true);
    }

    /// <summary>
    /// #1501: the first stretch of a 4-point route from 9061.7,1896.3,1339.6 towards 9066.4,1892.8,1316.6 (23 yd down over 6 yd flat).
    /// Half way the chord is 11.5 yd over the only model floor the probe finds (the storey below); the mesh's ramp is there. Before,
    /// NoGroundSupport.
    /// </summary>
    [Fact]
    public async Task NavMeshLeg_SteepDescent_TimbermawTunnelEntry_IsWalkable()
    {
        Vector3 from = new(X, Y, 1339.6f);
        Vector3 to = new(X + 4.7f, Y - 3.5f, 1316.6f);
        Vector3 half = Vector3.Lerp(from, to, 0.5f);
        (Vector3? walked, string? reason) = await ValidateAsync(from, to, half, modelFloor: 1300f, meshFloor: (_, _) => half.Z - 4.5f);
        Assert.True(walked is not null, $"refused: {reason}");
        Assert.Equal(half.Z - 4.5f + 0.05f, walked!.Value.Z, 0.01f);
    }

    /// <summary>
    /// #1485: a route ahead of 9070.5,1869.1,1312.6 inside a model below the terrain (the terrain 18 yd over it), no model floor in the
    /// under-map window, the mesh's floor 7 yd above the chord on the leg's way up. Before, NoGroundSupport (under the map).
    /// </summary>
    [Fact]
    public async Task NavMeshLeg_InsideWmoBelowTerrain_IsNotUnderMap()
    {
        Vector3 from = new(X, Y, 1312.6f);
        Vector3 to = new(X + 6f, Y, 1340f);
        Vector3 candidate = Vector3.Lerp(from, to, 0.1f);
        (Vector3? walked, string? reason) = await ValidateAsync(from, to, candidate, modelFloor: null, meshFloor: (x, _) => x < X + 0.1f ? 1312.6f : candidate.Z + 7f,
            terrainHeight: 1330.6f + 4f);
        Assert.True(walked is not null, $"refused: {reason}");
        Assert.Equal(candidate.Z + 7f + 0.05f, walked!.Value.Z, 0.01f);
    }

    /// <summary>
    /// #1501: a 2-point route on map 0 from -8165.9,-1449.7,195.6 towards -8154.9,-1446.9,135.9 (60 yd down over 11 yd flat): the
    /// chord 20 yd over the ledge the model probe finds, the mesh's switchback 6 yd under it. Before, NoGroundSupport.
    /// </summary>
    [Fact]
    public async Task NavMeshLeg_CliffChord_BurningSteppes_UsesMeshFloor()
    {
        Vector3 from = new(X, Y, 195.6f);
        Vector3 to = new(X + 11f, Y + 2.8f, 135.9f);
        Vector3 quarter = Vector3.Lerp(from, to, 0.25f);
        (Vector3? walked, string? reason) = await ValidateAsync(from, to, quarter, modelFloor: 160f, meshFloor: (_, _) => quarter.Z - 6f);
        Assert.True(walked is not null, $"refused: {reason}");
        Assert.Equal(quarter.Z - 6f + 0.05f, walked!.Value.Z, 0.01f);
    }

    /// <summary>The same steep leg over a real cliff: no mesh polygon under the walk. Still refused.</summary>
    [Fact]
    public async Task NavMeshLeg_OverARealCliff_WithNoMeshPolygon_IsStillRefused()
    {
        Vector3 from = new(X, Y, 1339.6f);
        Vector3 to = new(X + 4.7f, Y - 3.5f, 1316.6f);
        Vector3 half = Vector3.Lerp(from, to, 0.5f);
        (Vector3? walked, string? reason) = await ValidateAsync(from, to, half, modelFloor: 1300f, meshFloor: (_, _) => null);
        Assert.Null(walked);
        Assert.Equal("NoGroundSupport", reason);
    }

    /// <summary>A polygon at the floor of the drop, outside the leg's own heights (+-2 yd), is not the leg's floor: still refused.</summary>
    [Fact]
    public async Task NavMeshLeg_OverACliff_WithAPolygonOnlyAtTheBottom_IsStillRefused()
    {
        Vector3 from = new(X, Y, 1339.6f);
        Vector3 to = new(X + 4.7f, Y - 3.5f, 1316.6f);
        Vector3 half = Vector3.Lerp(from, to, 0.5f);
        (Vector3? walked, string? reason) = await ValidateAsync(from, to, half, modelFloor: 1300f, meshFloor: (_, _) => 1300f);
        Assert.Null(walked);
        Assert.Equal("NoGroundSupport", reason);
    }

    // ---- Review F1-F4 and the shared floor rule ----

    /// <summary>Blocks the sight line from the start's chest to any chest lower than <paramref name="minChestZ"/> (a ramp lip).</summary>
    private static Func<Vector3, Vector3, bool> SightAbove(float minChestZ) => (_, to) => to.Z >= minChestZ;

    /// <summary>
    /// F1: the leg's mesh floor is 8.1 yd under the chord and behind a lip, the chord itself in clear sight over the drop. Before, the
    /// line-of-sight fallback put the bot back on the chord (28 yd over the model floor: convicted as airwalk). Now refused.
    /// </summary>
    [Fact]
    public async Task F1_MeshFloorBehindLip_FallbackToChord_IsRefused()
    {
        Vector3 from = new(X, Y, 1339.6f);
        Vector3 to = new(X + 4.7f, Y - 3.5f, 1316.6f);
        Vector3 half = Vector3.Lerp(from, to, 0.5f);
        (Vector3? walked, string? reason) = await ValidateAsync(from, to, half, modelFloor: 1300f, meshFloor: (_, _) => half.Z - 8.1f,
            lineOfSight: SightAbove(half.Z + PlayerbotMoveCheck.ChestYards - 0.5f));
        Assert.Null(walked);
        Assert.Equal("NoGroundSupport", reason);
    }

    /// <summary>
    /// F2: inside a model below the terrain the mesh floor is 7 yd above the chord but behind the tunnel roof (no sight line, no mesh
    /// walk). Before, the bot was raised onto it with no check. Now refused.
    /// </summary>
    [Fact]
    public async Task F2_UnderMapRaise_WithoutSightOrMeshWalk_IsRefused()
    {
        Vector3 from = new(X, Y, 1312.6f);
        Vector3 to = new(X + 6f, Y, 1340f);
        Vector3 candidate = Vector3.Lerp(from, to, 0.1f);
        (Vector3? walked, string? reason) = await ValidateAsync(from, to, candidate, modelFloor: null,
            meshFloor: (x, _) => x < X + 0.1f ? 1312.6f : candidate.Z + 7f, terrainHeight: 1330.6f + 4f,
            lineOfSight: (_, t) => t.Z < candidate.Z + 7f);
        Assert.Null(walked);
        Assert.Equal("NoGroundSupport", reason);
    }

    /// <summary>
    /// F3: stacked floors under a steep leg's chord, a ramp 4.3 yd over it and the cliff floor 30.7 yd under it. The search is centred
    /// on the chord's height, so the ramp wins. Before, centred on the leg window's midpoint, the bot snapped 30 yd down.
    /// </summary>
    [Fact]
    public async Task F3_StackedFloors_NearestToTheChordWins()
    {
        Vector3 from = new(X, Y, 195.6f);
        Vector3 to = new(X + 11f, Y + 2.8f, 135.9f);
        Vector3 quarter = Vector3.Lerp(from, to, 0.25f);
        (Vector3? walked, string? reason, PlayerbotMotionAnomalies.GroundVerdict verdict) = await ValidateAsync(from, to, quarter,
            modelFloor: 160f, meshFloors: (_, _) => [150f, 185f], terrainHeight: null, lineOfSight: null, judge: true);
        Assert.True(walked is not null, $"refused: {reason}");
        Assert.Equal(185.05f, walked!.Value.Z, 0.01f);
        Assert.Equal(PlayerbotMotionAnomalies.GroundVerdict.Ok, verdict);
    }

    /// <summary>
    /// F4 and the real cliff: a leg that ends at the bottom of a drop whose only polygon is that bottom (inside the leg window, 44.7 yd
    /// under the chord, no mesh walk to it). Before, the bot dropped onto it. Now refused by the step limit.
    /// </summary>
    [Fact]
    public async Task F4_SinglePolygonAtTheBottomOfADrop_InsideTheLegWindow_IsRefused()
    {
        Vector3 from = new(X, Y, 195.6f);
        Vector3 to = new(X + 11f, Y + 2.8f, 135.9f);
        Vector3 quarter = Vector3.Lerp(from, to, 0.25f);
        (Vector3? walked, string? reason) = await ValidateAsync(from, to, quarter, modelFloor: 160f, meshFloor: (_, _) => 136f);
        Assert.Null(walked);
        Assert.Equal("NoGroundSupport", reason);
    }

    /// <summary>
    /// The shared floor rule: whatever the mover returns on a steep leg is not convicted by the airwalk judge, across sight-line and
    /// mesh-floor cases (F1's lip included, where it used to return the chord and the judge convicted it).
    /// </summary>
    [Theory]
    [InlineData(4.5f, false)]
    [InlineData(6f, false)]
    [InlineData(8.1f, false)]
    [InlineData(8.1f, true)]
    [InlineData(10.5f, true)]
    [InlineData(-3f, true)]
    public async Task SharedFloor_AnyPointTheMoverReturns_IsNotAirwalk(float underChord, bool lip)
    {
        Vector3 from = new(X, Y, 1339.6f);
        Vector3 to = new(X + 4.7f, Y - 3.5f, 1316.6f);
        Vector3 half = Vector3.Lerp(from, to, 0.5f);
        (Vector3? walked, string? reason, PlayerbotMotionAnomalies.GroundVerdict verdict) = await ValidateAsync(from, to, half,
            modelFloor: 1300f, meshFloors: (_, _) => [half.Z - underChord], terrainHeight: null,
            lineOfSight: lip ? SightAbove(half.Z + PlayerbotMoveCheck.ChestYards - 0.5f) : null, judge: true);
        Assert.True(walked is null || verdict == PlayerbotMotionAnomalies.GroundVerdict.Ok,
            $"returned {walked} ({reason}) but the judge says {verdict}");
    }

    /// <summary>A real airwalk is still convicted: 28 yd over the model floor with no mesh floor in the judge's window.</summary>
    [Theory]
    [InlineData(8.1f)]   // a polygon 8.1 yd down: under the judge's 6 yd window
    [InlineData(-3f)]    // a polygon 3 yd up: over its 2 yd cap (never higher)
    [InlineData(float.NaN)] // no polygon at all
    public async Task RealAirwalk_IsStillConvicted(float meshUnder)
    {
        Vector3 at = new(X, Y, 1328.1f);
        (_, _, PlayerbotMotionAnomalies.GroundVerdict verdict) = await ValidateAsync(at, at, at, modelFloor: 1300f,
            meshFloors: (_, _) => float.IsNaN(meshUnder) ? [] : [at.Z - meshUnder], terrainHeight: null, lineOfSight: null,
            judge: true, judgeAt: at);
        Assert.Equal(PlayerbotMotionAnomalies.GroundVerdict.Airwalk, verdict);
    }

    private async Task<(Vector3? Walked, string? Reason)> ValidateAsync(Vector3 from, Vector3 to, Vector3 candidate, float? modelFloor,
        Func<float, float, float?> meshFloor, float? terrainHeight = null, Func<Vector3, Vector3, bool>? lineOfSight = null)
    {
        (Vector3? walked, string? reason, _) = await ValidateAsync(from, to, candidate, modelFloor,
            (x, y) => meshFloor(x, y) is { } f ? [f] : [], terrainHeight, lineOfSight, judge: false);
        return (walked, reason);
    }

    private async Task<(Vector3? Walked, string? Reason, PlayerbotMotionAnomalies.GroundVerdict Verdict)> ValidateAsync(Vector3 from,
        Vector3 to, Vector3 candidate, float? modelFloor, Func<float, float, float[]> meshFloors, float? terrainHeight,
        Func<Vector3, Vector3, bool>? lineOfSight, bool judge, Vector3? judgeAt = null)
    {
        if (terrainHeight is { } terrain)
        {
            string maps = Directory.CreateDirectory(Path.Combine(_dataDir, "maps")).FullName;
            (int tx, int ty) = TerrainTile.TileOf(X, Y)!.Value;
            byte[] tile = new MapFileBuilder { GridHeight = terrain }.Build();
            foreach (uint mapId in new uint[] { 0, 1 }) File.WriteAllBytes(Path.Combine(maps, TerrainTile.FileName(mapId, tx, ty)), tile);
        }

        await using WorldTestHost host = WorldTestHost.Start(configure: options =>
        {
            if (terrainHeight is not null) options.Maps.DataDirectory = _dataDir;
        });
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            return await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new ModelFloor(modelFloor, lineOfSight), pathfinder: new MeshFloors(meshFloors));
                Map map = session.Player!.Map!;
                if (judgeAt is { } point)
                    return ((Vector3?)null, (string?)null, PlayerbotMotionAnomalies.JudgeFloor(map.Collision, point, out _, out _));
                var route = new PlayerbotRoute([from, to], Vector3.Distance(from, to), navigated: true) { MeshPoints = [from, to] };
                Vector3? walked = PlayerbotMotion.Validate(map, route, 1, 1, from, candidate, out string? reason);
                PlayerbotMotionAnomalies.GroundVerdict verdict = walked is { } w
                    ? PlayerbotMotionAnomalies.JudgeFloor(map.Collision, w, out _, out _)
                    : PlayerbotMotionAnomalies.GroundVerdict.Ok;
                return (walked, reason, verdict);
            });
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    /// <summary>One model floor at a fixed height everywhere (or none), and a clear line of sight.</summary>
    private sealed class ModelFloor(float? floor, Func<Vector3, Vector3, bool>? lineOfSight = null) : ILineOfSight
    {
        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => lineOfSight?.Invoke(from, to) ?? true;

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        {
            hit = to;
            return false;
        }

        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance)
            => floor is { } f && f <= z && f >= z - maxSearchDistance ? f : null;

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        {
            info = default;
            return false;
        }
    }

    /// <summary>
    /// A mesh whose walk along the surface is unavailable (no detail surface under the chord) and whose floor query answers per X/Y
    /// with the stacked surface nearest the query box's centre (z + (above - below) / 2), as Detour's nearest-polygon query ranks them.
    /// </summary>
    private sealed class MeshFloors(Func<float, float, float[]> surfacesAt) : IPathfinder, IMeshPointQuery, IMeshFloorQuery
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null) => new(PathType.Normal, [start, end]);

        public bool TryGetRandomPointAround(uint mapId, Vector3 center, float radius, Random random, PathOptions? options, out Vector3 point)
        {
            point = center;
            return false;
        }

        public MeshRaycastResult Raycast(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null) => MeshRaycastResult.Unavailable(end);

        public bool TryGetMeshFloor(uint mapId, float x, float y, float z, float below, float above, out float height)
        {
            height = 0f;
            float centre = z + (above - below) / 2f;
            bool found = false;
            foreach (float surface in surfacesAt(x, y))
            {
                if (surface < z - below || surface > z + above) continue;
                if (!found || MathF.Abs(surface - centre) < MathF.Abs(height - centre)) height = surface;
                found = true;
            }

            return found;
        }
    }
}
