using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Maps.Collision.VMaps;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using Xunit.Abstractions;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// Creature heights against the server's own floor (<see cref="MapCollision.GetHeight"/>: terrain plus models) on the real extraction,
/// in the regions the float lane measured (docs/areas/collision-pathing.md, "Ground snap"). Offset = the unit's z minus the floor, so
/// a positive one floats. All of these are <see cref="RealTerrainFactAttribute"/>/<see cref="RealTerrainTheoryAttribute"/>: without
/// <c>ARCANECORE_TEST_TERRAIN_DIR</c> (maps, vmaps and mmaps of 1.12.1) they are reported as SKIPPED with that reason, never as passed.
/// </summary>
public sealed class GroundSnapRealTerrainTests(ITestOutputHelper output)
{
    private static string Root => Environment.GetEnvironmentVariable(RealTerrainFactAttribute.Variable)!;

    public sealed record Region(string Name, uint Map, float X, float Y, float ZHint, float Radius);

    public static readonly Region[] Regions =
    [
        new("Undercity", 0, 1630f, 240f, -62f, 90f),
        new("LordaeronRuins", 0, 1800f, 238f, 60f, 60f),
        new("Brill", 0, 2260f, 290f, float.NaN, 90f),
        new("TirisfalFields", 0, 2050f, 700f, float.NaN, 90f),
        new("Orgrimmar", 1, 1630f, -4410f, float.NaN, 90f),
        new("ElwynnForest", 0, -9700f, -400f, float.NaN, 90f),
        new("Ashenvale", 1, 2700f, -400f, float.NaN, 90f),
    ];

    public static TheoryData<string> AllRegions => new() { "Undercity", "LordaeronRuins", "Brill", "TirisfalFields", "Orgrimmar", "ElwynnForest", "Ashenvale" };

    /// <summary>LordaeronRuins is left out of the authored-leg gates: a second model surface 2 yd above the walkable one makes a z + 3 probe pick the wrong storey (float lane, probe artefact).</summary>
    public static TheoryData<string> LegRegions => new() { "Undercity", "Brill", "TirisfalFields", "Orgrimmar", "ElwynnForest", "Ashenvale" };

    private static Region RegionNamed(string name) => Regions.Single(r => r.Name == name);

    private static WorldRuntime Load(uint mapId, float x, float y)
    {
        var vmaps = new VMapManager(Path.Combine(Root, "vmaps"));
        var nav = new NavMeshPathfinder(Path.Combine(Root, "mmaps"));
        WorldRuntime world = TestWorld.CreateRuntime();
        world.Options.Maps.DataDirectory = Root;
        WorldCollision.Of(world).Install(lineOfSight: vmaps, pathfinder: nav);
        (int tx, int ty) = TerrainTile.TileOf(x, y)!.Value;
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                vmaps.LoadTile(mapId, tx + dx, ty + dy);
                nav.LoadTile(mapId, tx + dx, ty + dy);
                world.GetMap(mapId).Terrain.GetTile(tx + dx, ty + dy); // the grids a player's neighbourhood loads: a drape never reads a terrain tile itself
            }
        }

        return world;
    }

    private static Vector3 Center(Map map, Region r)
    {
        float hint = float.IsNaN(r.ZHint) ? map.Terrain.GetHeight(r.X, r.Y, 0f) : r.ZHint;
        Assert.True(map.Collision.TryGetMeshFloor(r.X, r.Y, hint, 15f, 15f, out float z), $"{r.Name}: no mesh at the centre");
        return new Vector3(r.X, r.Y, z);
    }

    /// <summary>
    /// The server's floor under a point: <see cref="MapCollision.GetHeight"/> probed from z + <paramref name="probeUp"/>; NaN where it has none.
    /// The point is asked about 0.1 yd higher than it is: <c>GetHeight</c> prefers a model floor whenever the point is under the terrain
    /// (<c>z &lt; mapHeight</c>), so a unit a centimetre below the terrain on a concave stretch of a chord would be measured against a cellar
    /// or a cave 4 yd under it. A unit standing within a tenth of a yard under a surface still stands on it (a bigger shift would reach the second model surface 2.1 yd over LordaeronRuins' walkable floor).
    /// </summary>
    private static float Floor(Map map, float x, float y, float z, float probeUp)
    {
        float g = map.Collision.GetHeight(x, y, z + 0.1f + probeUp - 2f);
        return float.IsFinite(g) && g > TerrainTile.InvalidHeight ? g : float.NaN;
    }

    /// <summary>
    /// The independent oracle: the navigation mesh, which is built from the same terrain and models but is not the <c>GetHeight</c> probe the
    /// snap itself uses. The mesh is never under the floor and sits 0.2 to 0.3 yd over it, so a snapped point should be about
    /// mesh - <see cref="GroundHeight.MeshBias"/> + stand offset ("vs-mesh"); one more than a yard over the mesh ("over-mesh-1.0" = 1) stands on a roof,
    /// an upper storey or a prop top, never the walkable floor (terrain over a mesh that dips under it, a rock beside the path: up to 0.3 yd over measured).
    /// </summary>
    private static float AddOracle(Map map, Vector3 m, Dist d)
    {
        // Roof probe: any mesh surface within 3 yd either way; a point more than half a yard over one stands on something the mesh is not.
        if (map.Collision.TryGetMeshFloor(m.X, m.Y, m.Z, 3f, 3f, out float around))
        {
            d.Add("over-mesh-0.1", m.Z > around + 0.1f ? 1f : 0f);
            d.Add("over-mesh-0.5", m.Z > around + 0.5f ? 1f : 0f);
            d.Add("over-mesh-1.0", m.Z > around + 1.0f ? 1f : 0f);
        }

        // Offset from the floor the mesh implies: its surface under the point or at most a yard over it (a deck 2 yd over a creature standing
        // under a bridge is another storey, not its floor).
        if (!map.Collision.TryGetMeshFloor(m.X, m.Y, m.Z, 3f, 1f, out float mesh)) return float.NaN;
        float offset = m.Z - (mesh - GroundHeight.MeshBias + GroundHeight.StandOffset);
        d.Add("vs-mesh", offset);
        return offset;
    }

    private static string Source(Map map, float x, float y, float z, float floor)
    {
        if ((map.GetLiquidStatus(x, y, z, LiquidTypeFlags.AllLiquids, out _) & (LiquidStatus.InWater | LiquidStatus.UnderWater)) != 0)
        {
            return "liquid";
        }

        return MathF.Abs(map.Terrain.GetHeight(x, y, z) - floor) <= 0.02f ? "terrain" : "model";
    }

    private sealed class Dist
    {
        private readonly Dictionary<string, List<float>> _by = [];

        public void Add(string key, float v)
        {
            if (!float.IsFinite(v)) return;
            if (!_by.TryGetValue(key, out List<float>? l)) _by[key] = l = [];
            l.Add(v);
        }

        public float Sum(string key) => _by.TryGetValue(key, out List<float>? l) ? l.Sum() : 0f;

        public int Count(string key) => _by.TryGetValue(key, out List<float>? l) ? l.Count : 0;

        /// <summary>The q-quantile (0..1) of the key's samples, or NaN with none; <paramref name="absolute"/> takes |v|.</summary>
        public float Quantile(string key, double q, bool absolute = false)
        {
            if (!_by.TryGetValue(key, out List<float>? l) || l.Count == 0) return float.NaN;
            float[] v = absolute ? [.. l.Select(MathF.Abs)] : [.. l];
            Array.Sort(v);
            return v[Math.Clamp((int)Math.Round(q * (v.Length - 1)), 0, v.Length - 1)];
        }

        /// <summary>Every key's numbers on one line, for an assertion message: a failing gate says what was measured.</summary>
        public string Summary(string region) => string.Join(" || ", Lines(region).Select(l => string.Join(' ', l.Split(' ', StringSplitOptions.RemoveEmptyEntries))));

        public IEnumerable<string> Lines(string prefix)
        {
            foreach (string key in _by.Keys.Order(StringComparer.Ordinal))
            {
                yield return string.Create(CultureInfo.InvariantCulture,
                    $"{prefix,-16} {key,-24} n={Count(key),6} p05={Quantile(key, 0.05),6:F3} med={Quantile(key, 0.5),6:F3} p95={Quantile(key, 0.95),6:F3} |p95|={Quantile(key, 0.95, absolute: true),6:F3} sum={Sum(key),7:F0}");
            }
        }
    }

    /// <summary>A creature system over a real region with spawns on the navmesh, exactly on the ground.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly WorldRuntime _seed;

        public Harness(Region r, uint entry, int spawnCount, float spawnRadius, int seed, bool wander, uint inhabit = 1, bool pathJobs = false)
        {
            _seed = Load(r.Map, r.X, r.Y);
            Map seedMap = _seed.GetMap(r.Map);
            Center = GroundSnapRealTerrainTests.Center(seedMap, r);
            Random = new Random(seed);
            var spawns = new List<CreatureSpawn>();
            for (int i = 0; i < Math.Max(400, spawnCount * 4) && spawns.Count < spawnCount; i++)
            {
                if (!seedMap.Collision.MeshPoints!.TryGetRandomPointAround(r.Map, Center, MathF.Min(r.Radius, spawnRadius), Random, new PathOptions(), out Vector3 p)) continue;
                float g = Floor(seedMap, p.X, p.Y, p.Z, 2f);
                if (float.IsNaN(g) || MathF.Abs(p.Z - g) > 2f) continue;
                spawns.Add(wander
                    ? Spawn((uint)spawns.Count + 1, entry, p.X, p.Y, g, mapId: r.Map, movementType: 1, wander: 8f)
                    : Spawn((uint)spawns.Count + 1, entry, p.X, p.Y, g, mapId: r.Map));
            }

            Spawned = spawns.Count;
            CreatureTemplate template = Template(entry) with { InhabitType = (byte)inhabit };
            var content = new CreatureContent([template], spawns, [], [], [], CreatureAiContent.Empty);
            World = Load(r.Map, r.X, r.Y);
            Map = World.GetMap(r.Map, 0);
            System = new CreatureMapSystem(Map, content, null, random: new Random(1), aiServices: new CreatureAiServices());
            // FindPath in a loop is one update's worth of searches: the default 64 searches / 12 ms budget would defer the rest (an empty result),
            // so how many legs a run measured, and which, depended on the machine's load (the Undercity median and Ashenvale's leg count flaked).
            if (pathJobs)
            {
                Jobs = new NavJobSystem(threads: 2);
                System.PathJobs = Jobs;
            }
            else
            {
                System.PathBudget = CreaturePathBudget.Unlimited;
            }

            Map.AddUpdater(System);
            Player player = TestWorld.CreatePlayer(1, Center.X, Center.Y, new FakeSession(1));
            player.MapId = r.Map;
            player.Relocate(Center.X, Center.Y, Center.Z, 0, 0);
            Map.AddPlayer(player);
            World.RunTick(0);
        }

        public WorldRuntime World { get; }

        public Map Map { get; }

        public CreatureMapSystem System { get; }

        public Vector3 Center { get; }

        public Random Random { get; }

        public int Spawned { get; }

        public NavJobSystem? Jobs { get; }

        public void Dispose()
        {
            Jobs?.Dispose();
            World.Dispose();
            _seed.Dispose();
        }
    }

    /// <summary>
    /// Mesh bias (root cause 1): on Orgrimmar's flat ground the mesh is a constant +0.197 over the floor, and a wanderer kept it for good.
    /// Every sample of a wanderer standing still after a finished leg is within 0.06 of the floor plus the stand offset.
    /// </summary>
    [RealTerrainFact]
    public void OrgrimmarWanderer_EndsItsLegOnTheGround_NotOnTheMesh()
    {
        using var h = new Harness(RegionNamed("Orgrimmar"), 949301, 12, 60f, seed: 5, wander: true);
        var moved = new HashSet<ulong>();
        int checkedSamples = 0;
        float worst = 0f;
        for (int tick = 0; tick < 600; tick++)
        {
            h.World.RunTick(100);
            foreach (Creature cr in h.System.Creatures)
            {
                if (cr.IsMoving)
                {
                    moved.Add(cr.Guid.Value);
                    continue;
                }

                float g = Floor(h.Map, cr.X, cr.Y, cr.Z, 2f);
                if (!moved.Contains(cr.Guid.Value) || float.IsNaN(g) || Source(h.Map, cr.X, cr.Y, cr.Z, g) != "terrain") continue;
                checkedSamples++;
                worst = MathF.Max(worst, MathF.Abs(cr.Z - (g + GroundHeight.StandOffset)));
            }
        }

        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{checkedSamples} samples of {moved.Count} wanderers standing after a leg; worst distance from floor + stand offset {worst:F3} yd (the mesh sits +0.197 over the floor there)"));
        Assert.True(moved.Count >= 6, $"only {moved.Count} of {h.Spawned} wanderers moved");
        Assert.True(checkedSamples >= 500, $"only {checkedSamples} samples");
        Assert.True(worst <= 0.06f, $"a wanderer stands {worst:F3} yd off the floor + stand offset after a finished leg");
    }

    /// <summary>
    /// Chord (root cause 2): fixed authored legs that were the worst over Tirisfal and Elwynn rolling ground (float lane probe 6 shape: spawn
    /// on the ground, <see cref="CreatureMapSystem.FindPath"/> to a mesh point 15-60 yd away). The as-sent spline, sampled every 0.5 yd,
    /// stays within 0.2 yd of the floor; unsplit it was up to several yards off.
    /// </summary>
    [RealTerrainTheory]
    [InlineData("TirisfalFields", 2u, 2065.0254f, 738.7191f, 37.4774f, 2106.8591f, 771.98987f, 33.06575f)] // worst of 40 x 6 legs: p95 3.28 yd over the floor on the mesh path
    [InlineData("ElwynnForest", 9u, -9736.065f, -427.7577f, 46.56979f, -9791.165f, -420.2896f, 51.98166f)]  // worst of 40 x 6 legs: p95 4.88 yd, three corners
    public void AWorstCaseAuthoredLeg_FollowsTheGround(string regionName, uint spawnId, float sx, float sy, float sz, float dx, float dy, float dz)
    {
        Region r = RegionNamed(regionName);
        var start = new Vector3(sx, sy, sz);
        var dest = new Vector3(dx, dy, dz);
        const uint entry = 949302;
        using WorldRuntime world = Load(r.Map, sx, sy);
        Map map = world.GetMap(r.Map, 0);
        CreatureTemplate template = Template(entry) with { InhabitType = 1 };
        var content = new CreatureContent([template], [Spawn(spawnId, entry, sx, sy, sz, mapId: r.Map)], [], [], [], CreatureAiContent.Empty);
        var system = new CreatureMapSystem(map, content, null, random: new Random(1), aiServices: new CreatureAiServices());
        map.AddUpdater(system);
        Player player = TestWorld.CreatePlayer(1, sx, sy, new FakeSession(1));
        player.MapId = r.Map;
        player.Relocate(sx, sy, sz, 0, 0);
        map.AddPlayer(player);
        world.RunTick(0);
        Creature cr = Assert.Single(system.Creatures);

        IReadOnlyList<Vector3> pts = system.FindPath(cr, dest);

        Assert.NotEmpty(pts);
        float worst = 0f;
        var dump = new List<string> { $"start ({cr.X:F1},{cr.Y:F1},{cr.Z:F2})" };
        Vector3 prev = new(cr.X, cr.Y, cr.Z);
        foreach (Vector3 q in pts)
        {
            dump.Add(string.Create(CultureInfo.InvariantCulture, $"node ({q.X:F1},{q.Y:F1},{q.Z:F2}) floor {Floor(map, q.X, q.Y, q.Z, 2.5f):F2}"));
            int n = Math.Max(1, (int)MathF.Ceiling(Vector2.Distance(new(prev.X, prev.Y), new(q.X, q.Y)) / 0.5f));
            for (int j = 1; j <= n; j++)
            {
                Vector3 m = Vector3.Lerp(prev, q, j / (float)n);
                float g = Floor(map, m.X, m.Y, m.Z, 2.5f);
                if (!float.IsNaN(g) && MathF.Abs(m.Z - g) < 6f && Source(map, m.X, m.Y, m.Z, g) == "terrain")
                {
                    worst = MathF.Max(worst, MathF.Abs(m.Z - g));
                    if (MathF.Abs(m.Z - g) > 0.2f) dump.Add(string.Create(CultureInfo.InvariantCulture, $"off {m.Z - g:+0.00;-0.00} at ({m.X:F1},{m.Y:F1})"));
                }
            }

            prev = q;
        }

        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{regionName}: {pts.Count} points, worst offset from the floor {worst:F3} yd"));
        Assert.True(worst <= 0.2f, $"the sent spline is up to {worst:F2} yd off the floor: {string.Join("; ", dump)}");
    }

    /// <summary>
    /// Model top (root cause 4): beside an Elwynn M2 doodad (found by scanning the Goldshire woods for a point where <see cref="MapCollision.GetHeight"/>
    /// answers a model 1.7 yd over the terrain for a unit standing on the terrain) the raw floor is the prop; the walker query takes the terrain.
    /// </summary>
    [RealTerrainFact]
    public void BesideAnM2_TheWalkerStandsOnTheTerrain_NotOnTheProp()
    {
        const float x = -9780f, y = -403f;
        Region r = RegionNamed("ElwynnForest");
        using WorldRuntime world = Load(r.Map, x, y);
        MapCollision collision = world.GetMap(r.Map).Collision;
        float terrain = collision.Map.Terrain.GetHeight(x, y, 0f);
        float standing = terrain + GroundHeight.StandOffset;

        float raw = collision.GetHeight(x, y, standing);
        Assert.True(raw > terrain + 1.0f, $"the fixture changed: raw floor {raw:F2} is not a model over the terrain {terrain:F2}");
        Assert.False(collision.LineOfSight.TryGetAreaInfo(r.Map, x, y, standing, out _), "the fixture is inside a WMO group, not beside an M2");

        Assert.True(GroundHeight.TrySnap(collision, x, y, standing, new GroundQuery(), out float snapped, out GroundSource source));
        Assert.Equal(GroundSource.Terrain, source);
        Assert.Equal(standing, snapped, 0.01f);

        var anyModel = GroundHeight.Walker with { PreferTerrainOverProps = false };
        Assert.True(GroundHeight.TrySnap(collision, x, y, standing, anyModel, out float onProp, out GroundSource propSource));
        Assert.Equal(GroundSource.Model, propSource);
        Assert.Equal(raw + GroundHeight.StandOffset, onProp, 0.01f);
    }

    /// <summary>Wanderers on terrain stand on it (median within 0.06 of the floor, |offset| p95 under 0.15), on models within 0.5 (float lane probe 3 shape).</summary>
    [RealTerrainTheory]
    [MemberData(nameof(AllRegions))]
    public void WanderingCreatures_StandOnTheGround(string regionName)
    {
        Region r = RegionNamed(regionName);
        using var h = new Harness(r, 949301, 40, 60f, seed: 5, wander: true);
        var d = new Dist();
        for (int tick = 0; tick < 1200; tick++)
        {
            h.World.RunTick(100);
            foreach (Creature cr in h.System.Creatures)
            {
                float vsMesh = AddOracle(h.Map, new Vector3(cr.X, cr.Y, cr.Z), d);
                float g = Floor(h.Map, cr.X, cr.Y, cr.Z, 2f);
                if (float.IsNaN(g) || MathF.Abs(cr.Z - g) > 6f) continue;
                string src = Source(h.Map, cr.X, cr.Y, cr.Z, g);
                d.Add($"pos-{(cr.IsMoving ? "moving" : "idle")}-{src}", cr.Z - g);
                if (src == "model")
                {
                    d.Add("pos-model", cr.Z - g);
                    d.Add("vs-mesh-model", vsMesh);
                }
            }
        }

        foreach (string line in d.Lines(regionName)) output.WriteLine(line);
        Coverage c = Expect(regionName);
        AssertOffsets(d, regionName, c.PosTerrain, "pos-moving-terrain", "pos-idle-terrain");
        AssertModelOffsets(d, regionName, c.PosModel, c.RawModelGate, "pos-model");
        AssertOracle(d, regionName);
    }

    /// <summary>Authored legs of 15-60 yd, as sent: terrain |offset| p95 under 0.15 (p05 not under -0.15), models p95 under 0.5, at most 8 points per leg (median) and never over 32 (float lane probe 6 shape).</summary>
    [RealTerrainTheory]
    [MemberData(nameof(LegRegions))]
    public void AuthoredLegs_FollowTheGround_WithFewPoints(string regionName)
    {
        Region r = RegionNamed(regionName);
        using var h = new Harness(r, 949302, 40, 60f, seed: 9, wander: false);
        var d = new Dist();
        int legs = 0;
        foreach (Creature cr in h.System.Creatures.ToList())
        {
            for (int k = 0; k < 6; k++)
            {
                var s = new Vector3(cr.X, cr.Y, cr.Z);
                if (!h.Map.Collision.MeshPoints!.TryGetRandomPointAround(r.Map, s, 60f, h.Random, new PathOptions(), out Vector3 dest)
                    || Vector2.Distance(new(s.X, s.Y), new(dest.X, dest.Y)) < 15f) continue;
                IReadOnlyList<Vector3> pts = h.System.FindPath(cr, dest);
                if (pts.Count == 0) continue;
                legs++;
                d.Add("points-per-leg", pts.Count);
                Vector3 prev = s;
                foreach (Vector3 q in pts)
                {
                    int n = Math.Max(1, (int)MathF.Ceiling(Vector2.Distance(new(prev.X, prev.Y), new(q.X, q.Y)) / 0.5f));
                    for (int j = 1; j <= n; j++)
                    {
                        Vector3 m = Vector3.Lerp(prev, q, j / (float)n);
                        float vsMesh = AddOracle(h.Map, m, d);
                        float g = Floor(h.Map, m.X, m.Y, m.Z, 2.5f);
                        if (float.IsNaN(g) || MathF.Abs(m.Z - g) > 6f) continue;
                        string src = Source(h.Map, m.X, m.Y, m.Z, g);
                        d.Add($"leg-{src}", m.Z - g);
                        if (src == "model") d.Add("vs-mesh-model", vsMesh);
                    }

                    prev = q;
                }
            }
        }

        foreach (string line in d.Lines(regionName)) output.WriteLine(line);
        Assert.True(legs >= 20, $"only {legs} legs were measured");
        Coverage c = Expect(regionName);
        if (c.LegTerrain > 0)
        {
            Require(d, regionName, "leg-terrain", c.LegTerrain);
            Assert.True(d.Quantile("leg-terrain", 0.95) <= 0.15f, $"{regionName} terrain p95 {d.Quantile("leg-terrain", 0.95):F3} over 0.15: {d.Summary(regionName)}");
            Assert.True(d.Quantile("leg-terrain", 0.05) >= -0.15f, $"{regionName} terrain p05 {d.Quantile("leg-terrain", 0.05):F3} under -0.15: {d.Summary(regionName)}");
        }

        AssertModelOffsets(d, regionName, c.LegModel, c.RawModelGate, "leg-model");
        AssertOracle(d, regionName);

        // Undercity's WMO ramps and stairs hold the median at 8 only because model floors are drawn with the looser model tolerance (12 points at 0.11, 7 at 0.40).
        const float medianCap = 8f;
        Assert.True(d.Quantile("points-per-leg", 0.5) <= medianCap, $"{regionName} median {d.Quantile("points-per-leg", 0.5)} points per leg, cap {medianCap}: {d.Summary(regionName)}");
        Assert.True(d.Quantile("points-per-leg", 1.0) <= CreatureSplineSmoothing.MaxSplinePoints, $"{regionName} {d.Quantile("points-per-leg", 1.0)} points in a leg: {d.Summary(regionName)}");
    }

    /// <summary>
    /// How many samples a region must yield for each gate to mean anything, from what the fixed seeds give on the 1.12.1 extraction (about
    /// half of it, so a small change of the walk does not trip them, a gate that has stopped sampling does). A gate whose minimum is 0 is not
    /// expected in the region (Undercity has no open ground, Orgrimmar no models in reach). A region under its minimum FAILS: a gate that
    /// samples nothing must not read as a pass. <c>RawModelGate</c> false: the model subset is a few hundred samples of steps off ledges
    /// (Tirisfal p95 1.5 yd of 111 samples), gated by the mesh oracle only.
    /// </summary>
    private sealed record Coverage(int LegTerrain, int LegModel, int PosTerrain, int PosModel, bool RawModelGate = true);

    private static Coverage Expect(string region) => region switch
    {
        "Undercity" => new(0, 5000, 0, 10000),
        "LordaeronRuins" => new(0, 0, 0, 2000),
        "Brill" => new(5000, 300, 5000, 1000),
        "TirisfalFields" => new(5000, 50, 5000, 50, RawModelGate: false),
        "Orgrimmar" => new(5000, 0, 5000, 0),
        "ElwynnForest" => new(5000, 300, 5000, 200),
        "Ashenvale" => new(5000, 500, 5000, 1000),
        _ => throw new ArgumentOutOfRangeException(nameof(region), region, "no coverage expectation"),
    };

    private static void Require(Dist d, string region, string key, int min)
        => Assert.True(d.Count(key) >= min, $"{region} {key}: only n={d.Count(key)} samples, {min} expected (the gate would pass without measuring): {d.Summary(region)}");

    private static void AssertOffsets(Dist d, string region, int min, params string[] keys)
    {
        if (min == 0) return;
        foreach (string key in keys)
        {
            Require(d, region, key, min);
            Assert.True(d.Quantile(key, 0.95, absolute: true) <= 0.15f, $"{region} {key}: |offset| p95 {d.Quantile(key, 0.95, absolute: true):F3}: {d.Summary(region)}");
            Assert.True(d.Quantile(key, 0.5) is >= -0.06f and <= 0.06f, $"{region} {key}: median {d.Quantile(key, 0.5):F3} outside +-0.06: {d.Summary(region)}");
        }
    }

    /// <summary>
    /// Model floors against the raw floor probe: the floating side (positive offset) p95 under 0.5. A unit under a doodad's top is under the
    /// probe's floor by design (the walker query takes the terrain beside a prop), so the negative side of the raw probe is not gated; the
    /// oracle gates it (<see cref="AssertOracle"/>). Fewer than <paramref name="min"/> samples fails.
    /// </summary>
    private static void AssertModelOffsets(Dist d, string region, int min, bool rawGate, params string[] keys)
    {
        if (min == 0) return;
        foreach (string key in keys)
        {
            Require(d, region, key, min);
            if (rawGate)
            {
                Assert.True(d.Quantile(key, 0.95) <= 0.5f, $"{region} {key}: p95 {d.Quantile(key, 0.95):F3}: {d.Summary(region)}");
            }

            Require(d, region, "vs-mesh-model", min / 2);
            Assert.True(d.Quantile("vs-mesh-model", 0.95) <= 0.5f, $"{region} model samples stand {d.Quantile("vs-mesh-model", 0.95):F3} over mesh - bias (p95): {d.Summary(region)}");
            Assert.True(d.Quantile("vs-mesh-model", 0.05) >= -0.8f, $"{region} model samples stand {d.Quantile("vs-mesh-model", 0.05):F3} under mesh - bias (p05): {d.Summary(region)}");
        }
    }

    /// <summary>
    /// The independent oracle (the navigation mesh, not the probe the snap itself reads): a snapped point stands about mesh - bias + stand offset
    /// (the mesh is 0.2 to 0.6 yd over the floor: p05 -0.62 and p95 +0.31 measured), and a point more than half a yard over the mesh (a roof,
    /// an upper storey, or a model deck the mesh sags under) is rare: none in Brill, Tirisfal, Elwynn, Orgrimmar or LordaeronRuins, 0.96 percent
    /// of the Undercity wander samples and 0.05 percent of Ashenvale's. The Undercity ones are positions along a chord between two snapped points
    /// on different storeys of a ramp (the point is not a roof: a snap of it returns the floor a yard under it); the Ashenvale ones stand on a
    /// model deck and re-snap to the same height, the mesh being up to 1.2 yd under it. Counts are the same with and without the corner guard
    /// (<see cref="GroundHeight.MeshCeilingYards"/>), which gets its own synthetic test (a roof 2.3 yd over a floor).
    /// </summary>
    private static void AssertOracle(Dist d, string region)
    {
        Require(d, region, "vs-mesh", 1000);
        Assert.True(d.Quantile("vs-mesh", 0.95) <= 0.45f, $"{region} vs mesh - bias p95 {d.Quantile("vs-mesh", 0.95):F3} over 0.45: {d.Summary(region)}");
        Assert.True(d.Quantile("vs-mesh", 0.05) >= -0.8f, $"{region} vs mesh - bias p05 {d.Quantile("vs-mesh", 0.05):F3} under -0.8: {d.Summary(region)}");
        float allowed = region switch
        {
            "Undercity" => 0.015f,
            "Ashenvale" => 0.002f,
            _ => 0f,
        };
        int n = d.Count("over-mesh-0.5");
        float over = d.Sum("over-mesh-0.5");
        Assert.True(over <= n * allowed, $"{region}: {over} of {n} samples stand over half a yard over the mesh (allowed {allowed:P1}): {d.Summary(region)}");
    }

    /// <summary>
    /// Offsets (leg point minus floor, terrain only) along a leg as the client draws it, sampled every half yard, into <paramref name="d"/>.
    /// </summary>
    private static void MeasureLeg(Map map, Vector3 start, IReadOnlyList<Vector3> pts, Dist d)
    {
        Vector3 prev = start;
        foreach (Vector3 q in pts)
        {
            int n = Math.Max(1, (int)MathF.Ceiling(Vector2.Distance(new(prev.X, prev.Y), new(q.X, q.Y)) / 0.5f));
            for (int j = 1; j <= n; j++)
            {
                Vector3 m = Vector3.Lerp(prev, q, j / (float)n);
                float g = Floor(map, m.X, m.Y, m.Z, 2.5f);
                if (float.IsNaN(g) || MathF.Abs(m.Z - g) > 6f || Source(map, m.X, m.Y, m.Z, g) != "terrain") continue;
                d.Add("leg-terrain", m.Z - g);
            }

            prev = q;
        }
    }

    private void AssertDrapedLegs(Func<Harness, Creature, Vector3, IReadOnlyList<Vector3>> plan, string regionName)
    {
        Region r = RegionNamed(regionName);
        using var h = new Harness(r, 949302, 30, 60f, seed: 9, wander: false);
        var d = new Dist();
        int legs = 0;
        foreach (Creature cr in h.System.Creatures.ToList())
        {
            var s = new Vector3(cr.X, cr.Y, cr.Z);
            if (!h.Map.Collision.MeshPoints!.TryGetRandomPointAround(r.Map, s, 60f, h.Random, new PathOptions(), out Vector3 dest)
                || Vector2.Distance(new(s.X, s.Y), new(dest.X, dest.Y)) < 15f) continue;
            IReadOnlyList<Vector3> pts = plan(h, cr, dest);
            if (pts.Count == 0) continue;
            legs++;
            MeasureLeg(h.Map, s, pts, d);
        }

        string summary = d.Summary(regionName);
        output.WriteLine($"{legs} legs; {summary}");
        Assert.True(legs >= 15, $"only {legs} legs: {summary}");
        Assert.True(d.Count("leg-terrain") >= 300, $"only {d.Count("leg-terrain")} terrain samples: {summary}");
        Assert.True(d.Quantile("leg-terrain", 0.95, absolute: true) <= 0.15f, $"|offset| p95 {d.Quantile("leg-terrain", 0.95, absolute: true):F3} over 0.15: {summary}");
        Assert.True(d.Quantile("leg-terrain", 0.5) is >= -0.06f and <= 0.06f, $"median {d.Quantile("leg-terrain", 0.5):F3} outside +-0.06 (the mesh sits about +0.2 over the floor): {summary}");
    }

    /// <summary>A chase leg (<see cref="ICreaturePathQuery.FindPath"/>, what the melee chase generator asks) is sent on the ground, not on the mesh.</summary>
    [RealTerrainFact]
    public void ChaseLegs_AreDrapedOnTheGround()
        => AssertDrapedLegs((h, cr, dest) => ((ICreaturePathQuery)h.System).FindPath(cr, dest, out _), "Orgrimmar");

    /// <summary>A walk-home leg (<c>PlanPath(toSpawn: true)</c>, what the home generator asks) is sent on the ground, not on the mesh.</summary>
    [RealTerrainFact]
    public void HomeLegs_AreDrapedOnTheGround()
        => AssertDrapedLegs((h, cr, dest) => h.System.PlanPath(cr, dest, toSpawn: true).Corners, "Orgrimmar");

    /// <summary>
    /// A drape over a terrain tile that is not in memory reads nothing from disk (a synchronous tile read on the world thread was the 35 ms
    /// outlier of the cost run): the tile stays unloaded afterwards and the points keep their heights. With the tile loaded the same
    /// path is snapped.
    /// </summary>
    [RealTerrainFact]
    public void ADrapeOverAnUnloadedTerrainTile_ReadsNothing_AndKeepsTheHeights()
    {
        Region r = RegionNamed("ElwynnForest");
        using WorldRuntime world = Load(r.Map, r.X, r.Y);
        Map map = world.GetMap(r.Map);
        float fx = r.X + 2200f;
        float fy = r.Y;
        Assert.False(map.Terrain.TryGetLoadedHeight(fx, fy, out _), "the fixture changed: the far tile is already loaded");
        Assert.False(map.Terrain.TryGetLoadedHeight(fx + 40f, fy, out _), "the fixture changed: the far tile is already loaded");
        Vector3 start = new(fx, fy, 123f);
        Vector3[] corners = [new(fx + 20f, fy, 123f), new(fx + 40f, fy, 123f)];

        long t = Stopwatch.GetTimestamp();
        List<Vector3> draped = GroundHeight.DrapePath(map.Collision, start, corners, CreatureMapSystem.GroundSampleYards, CreatureMapSystem.GroundToleranceYards, modelTolerance: CreatureMapSystem.GroundModelToleranceYards);
        double ms = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"drape over an unloaded tile: {ms:F3} ms, {draped.Count} points"));

        Assert.False(map.Terrain.TryGetLoadedHeight(fx, fy, out _), "the drape read the terrain tile from disk");
        Assert.False(map.Terrain.TryGetLoadedHeight(fx + 40f, fy, out _), "the drape read the terrain tile from disk");
        Assert.Equal(corners.Length, draped.Count);
        Assert.All(draped, p => Assert.Equal(123f, p.Z));

        Assert.False(GroundHeight.TrySnap(map.Collision, fx, fy, 123f, new GroundQuery(), out float kept, out GroundSource source));
        Assert.Equal(GroundSource.None, source);
        Assert.Equal(123f, kept);
    }


    /// <summary>
    /// A shallow stretch of water in the region (0.6 to 2.5 yd over a terrain bed with nothing else under it): the bed and the surface.
    /// </summary>
    private static bool TryFindShallowWater(Map map, Region r, out Vector3 bed, out float level)
    {
        bed = default;
        level = 0f;
        for (float dx = -300f; dx <= 300f; dx += 4f)
        {
            for (float dy = -300f; dy <= 300f; dy += 4f)
            {
                float x = r.X + dx;
                float y = r.Y + dy;
                float ground = map.Terrain.GetHeight(x, y, 0f);
                if (ground <= TerrainTile.InvalidHeight) continue;
                if ((map.Terrain.GetLiquidStatus(x, y, ground, LiquidTypeFlags.AllLiquids, out LiquidData data) & (LiquidStatus.InWater | LiquidStatus.UnderWater)) == 0) continue;
                float depth = data.Level - ground;
                if (depth < 0.6f || depth > 2.5f) continue;
                if (MathF.Abs(map.Collision.GetHeight(x, y, data.Level) - ground) > 0.02f) continue; // a model in the water: not a clean bed
                bed = new Vector3(x, y, ground);
                level = data.Level;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The inhabit-type gates of <c>DrapeOnGround</c> on the real data, in a region with shallow water: a walker is put on the bed, a creature that
    /// also swims (InhabitType 3) on the water surface, a water-only creature (2) and a flier (4) keep the height they came with.
    /// </summary>
    [RealTerrainFact]
    public void ByInhabitType_AWalkerIsOnTheBed_ASwimmerOnTheSurface_AndWaterOnlyAndFliersKeepTheirHeight()
    {
        foreach (string name in new[] { "ElwynnForest", "Ashenvale", "TirisfalFields", "Brill" })
        {
            Region r = RegionNamed(name);
            Vector3 bed;
            float level;
            using (var probe = new Harness(r, 949304, 1, 30f, seed: 1, wander: false))
            {
                if (!TryFindShallowWater(probe.Map, r, out bed, out level)) continue;
            }

            var corner = new Vector3(bed.X, bed.Y, level - 0.4f);
            var start = new Vector3(bed.X - 3f, bed.Y, level - 0.4f);
            float? walker = null, swimmer = null, waterOnly = null, flier = null, flyingWalker = null;
            foreach (uint inhabit in new uint[] { 1, 3, 2, 4, 5 })
            {
                using var h = new Harness(r, 949304, 1, 30f, seed: 1, wander: false, inhabit: inhabit);
                Creature cr = h.System.Creatures.First();
                float z = h.System.DrapeOnGround(cr, start, [corner])[^1].Z;
                switch (inhabit)
                {
                    case 1: walker = z; break;
                    case 3: swimmer = z; break;
                    case 2: waterOnly = z; break;
                    case 4: flier = z; break;
                    default: flyingWalker = z; break;
                }
            }

            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{name}: water at ({bed.X:F1},{bed.Y:F1}) bed {bed.Z:F2} surface {level:F2}; walker {walker:F2} swimmer {swimmer:F2} water-only {waterOnly:F2} flier {flier:F2} flying walker {flyingWalker:F2}"));
            Assert.Equal(bed.Z + GroundHeight.StandOffset, walker!.Value, 0.03f);
            Assert.Equal(level, swimmer!.Value, 0.01f);
            Assert.Equal(corner.Z, waterOnly!.Value);
            Assert.Equal(corner.Z, flier!.Value);
            Assert.Equal(corner.Z, flyingWalker!.Value);
            return;
        }

        Assert.Fail("no shallow water in Elwynn, Ashenvale, Tirisfal or Brill: the fixture changed");
    }

    /// <summary>
    /// What the drape adds to a map update under a crowd, in this build configuration (printed, not asserted): 300 wanderers with the path-job
    /// pool on and the default per-update budget, 600 updates of 100 ms, once with the drape and once without it.
    /// </summary>
    [RealTerrainFact]
    public void CrowdUpdateCost_WithAndWithoutTheDrape_IsPrinted()
    {
        foreach (string name in new[] { "Brill", "Undercity" })
        {
            Region r = RegionNamed(name);
            foreach (bool snap in new[] { false, true })
            {
                using var h = new Harness(r, 949301, 300, 80f, seed: 5, wander: true, pathJobs: true);
                h.System.GroundSnapEnabled = snap;
                for (int i = 0; i < 100; i++) h.World.RunTick(100);
                long drapes = h.System.DrapedPaths;
                long drapeTicks = h.System.DrapeTicksTotal;
                long deferred = h.System.DeferredPathSearches;
                long applied = h.System.PathJobsApplied;
                var ms = new List<double>(600);
                for (int i = 0; i < 600; i++)
                {
                    long t = Stopwatch.GetTimestamp();
                    h.World.RunTick(100);
                    ms.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
                }

                ms.Sort();
                long n = h.System.DrapedPaths - drapes;
                double meanDrapeUs = n == 0 ? 0 : (h.System.DrapeTicksTotal - drapeTicks) * 1_000_000.0 / Stopwatch.Frequency / n;
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{name,-10} {h.Spawned} wanderers drape {(snap ? "on " : "off")}: update p50 {ms[ms.Count / 2]:F2} ms p99 {ms[(int)(0.99 * (ms.Count - 1))]:F2} ms max {ms[^1]:F2} ms; {n} drapes (mean {meanDrapeUs:F0} us), {h.System.PathJobsApplied - applied} job results applied, {h.System.DeferredPathSearches - deferred} deferred ({RuntimeConfiguration})"));
            }
        }
    }

    /// <summary>Cost of the drape per leg on the real data (printed, not asserted: the number depends on the build configuration and the machine).</summary>
    [RealTerrainFact]
    public void DrapeCostPerLeg_IsPrinted()
    {
        foreach (Region r in Regions.Where(x => x.Name is "Undercity" or "Brill" or "TirisfalFields" or "ElwynnForest"))
        {
            using WorldRuntime world = Load(r.Map, r.X, r.Y);
            Map map = world.GetMap(r.Map);
            Vector3 c = Center(map, r);
            var random = new Random(3);
            var legs = new List<(Vector3 Start, List<Vector3> Corners)>();
            var options = new PathOptions { Mover = PathMover.FromInhabit(1) };
            for (int i = 0; i < 600 && legs.Count < 200; i++)
            {
                if (!map.Collision.MeshPoints!.TryGetRandomPointAround(r.Map, c, r.Radius, random, new PathOptions(), out Vector3 a)
                    || !map.Collision.MeshPoints!.TryGetRandomPointAround(r.Map, a, 60f, random, new PathOptions(), out Vector3 b)
                    || Vector2.Distance(new(a.X, a.Y), new(b.X, b.Y)) < 15f) continue;
                PathResult path = map.Collision.FindPath(a, b, options);
                if (!path.HasPath || path.Points.Count < 2) continue;
                legs.Add((a, [.. path.Points.Skip(1)]));
            }

            Assert.True(legs.Count >= 50, $"{r.Name}: {legs.Count} legs");
            foreach ((Vector3 s, List<Vector3> corners) in legs.Take(50)) GroundHeight.DrapePath(map.Collision, s, corners, CreatureMapSystem.GroundSampleYards, CreatureMapSystem.GroundToleranceYards, modelTolerance: CreatureMapSystem.GroundModelToleranceYards); // warm
            var micros = new List<double>(legs.Count);
            int points = 0;
            foreach ((Vector3 s, List<Vector3> corners) in legs)
            {
                long t = Stopwatch.GetTimestamp();
                List<Vector3> draped = GroundHeight.DrapePath(map.Collision, s, corners, CreatureMapSystem.GroundSampleYards, CreatureMapSystem.GroundToleranceYards, modelTolerance: CreatureMapSystem.GroundModelToleranceYards);
                micros.Add(Stopwatch.GetElapsedTime(t).TotalMicroseconds);
                points += draped.Count;
            }

            micros.Sort();
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{r.Name,-16} legs={legs.Count} mean={micros.Average():F1} us median={micros[micros.Count / 2]:F1} us p95={micros[(int)(0.95 * (micros.Count - 1))]:F1} us p99={micros[(int)(0.99 * (micros.Count - 1))]:F1} us max={micros[^1]:F1} us, {points / (double)legs.Count:F1} points/leg ({RuntimeConfiguration})"));
        }
    }

    private static string RuntimeConfiguration
#if DEBUG
        => "Debug";
#else
        => "Release";
#endif
}
