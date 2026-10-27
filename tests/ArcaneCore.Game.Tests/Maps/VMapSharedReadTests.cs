using System.Numerics;
using System.Reflection;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Maps.Collision.VMaps;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Tests.Collision;
using ArcaneCore.Game.Tests.GridTerrain;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.TestSupport;
using Xunit;
using Xunit.Abstractions;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.Maps;

/// <summary>
/// The ground drape outside the serial gate (<see cref="GroundHeight.TryDrapePathShared"/>, <see cref="VMapManager"/> shared reads): it must
/// give exactly the gated drape's points whenever it answers, give up (null, with the reason) whenever a tile or navmesh lookup would be
/// needed, and stay whole while another thread loads and unloads the tiles it reads. Synthetic ground: two flat terrain tiles (ground at z 20)
/// with stepped box platforms on both as the model floors, plus the real extraction when <c>ARCANECORE_TEST_TERRAIN_DIR</c> is set.
/// </summary>
public sealed class VMapSharedReadTests(ITestOutputHelper output) : IDisposable
{
    private const float Ground = 20f;
    private const float Surface = 21.5f;
    private const float XA = 30f;
    private const float YA = -30f;   // terrain tile (31, 32)
    private const float XB = 30f;
    private const float YB = -600f;  // terrain tile (31, 33)

    private readonly string _terrain = Path.Combine(Path.GetTempPath(), "arcanecore-sharedread-" + Guid.NewGuid().ToString("N"));
    private readonly VMapFixture _models = new();

    public void Dispose()
    {
        _models.Dispose();
        try
        {
            Directory.Delete(_terrain, recursive: true);
        }
        catch (IOException)
        {
            // a temp directory a test left behind is not worth a failure
        }
    }

    private void WriteTerrain(bool liquid)
    {
        Directory.CreateDirectory(Path.Combine(_terrain, "maps"));
        foreach (int ty in new[] { 32, 33 })
        {
            var builder = new MapFileBuilder { GridHeight = Ground };
            if (liquid)
            {
                builder.HasLiquid = true;
                builder.LiquidGlobalFlags = (byte)LiquidTypeFlags.Water;
                builder.LiquidGlobalEntry = 1;
                builder.LiquidLevel = Surface;
            }

            File.WriteAllBytes(Path.Combine(_terrain, "maps", TerrainTile.FileName(0, 31, ty)), builder.Build());
        }
    }

    /// <summary>Stepped box platforms along the leg at <paramref name="y"/>: model floors at 0.3 .. 1.9 yd over the ground.</summary>
    private void PlacePlatforms(string prefix, float y)
    {
        float[] heights = [0.3f, 0.7f, 1.1f, 1.5f, 1.9f];
        for (int i = 0; i < heights.Length; i++)
        {
            _models.Place(prefix + i, VMapFixture.Box(new Vector3(-2, -2, 0), new Vector3(2, 2, heights[i])), new Vector3(XA - 12f + (6f * i), y, Ground));
        }
    }

    private sealed class Scene : IDisposable
    {
        public required WorldRuntime World { get; init; }

        public required VMapManager Vmaps { get; init; }

        public Map Map => World.GetMap(0);

        public MapCollision Collision => Map.Collision;

        public void Dispose() => World.Dispose();
    }

    private Scene Build(bool liquid = false, bool loadModelTiles = true, bool loadTerrain = true)
    {
        WriteTerrain(liquid);
        PlacePlatforms("a", YA);
        PlacePlatforms("b", YB);
        _models.Write(0);
        var vmaps = new VMapManager(_models.Directory);
        WorldRuntime world = TestWorld.CreateRuntime();
        world.Options.Maps.DataDirectory = _terrain;
        WorldCollision.Of(world).Install(lineOfSight: vmaps);
        Map map = world.GetMap(0);
        if (loadModelTiles)
        {
            Assert.True(vmaps.LoadTile(0, 31, 32));
            Assert.True(vmaps.LoadTile(0, 31, 33));
        }

        if (loadTerrain)
        {
            map.Terrain.GetTile(31, 32);
            map.Terrain.GetTile(31, 33);
        }

        return new Scene { World = world, Vmaps = vmaps };
    }

    private static Vector3[] LegA => [new(XA + 18f, YA, Ground + 0.05f)];

    private static Vector3 StartA => new(XA - 18f, YA, Ground + 0.05f);

    private static Vector3[] LegB => [new(XB + 18f, YB, Ground + 0.05f)];

    private static Vector3 StartB => new(XB - 18f, YB, Ground + 0.05f);

    private static GroundQuery Models(LiquidMode liquid = LiquidMode.Bottom) => new(2f, 4f, liquid, PreferTerrainOverProps: false, AllowMeshFallback: true, LoadTiles: false);

    private static List<Vector3> Gated(MapCollision c, Vector3 start, Vector3[] corners, GroundQuery query)
        => GroundHeight.DrapePath(c, start, corners, step: 1f, tolerance: 0.11f, query: query, modelTolerance: 0.4f);

    private static List<Vector3>? Shared(MapCollision c, Vector3 start, Vector3[] corners, GroundQuery query, out SharedDrapeAbort abort)
        => GroundHeight.TryDrapePathShared(c, start, corners, out abort, step: 1f, tolerance: 0.11f, query: query, modelTolerance: 0.4f);

    private static bool Same(IReadOnlyList<Vector3>? a, IReadOnlyList<Vector3> b)
    {
        if (a is null || a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (BitConverter.SingleToInt32Bits(a[i].X) != BitConverter.SingleToInt32Bits(b[i].X)
                || BitConverter.SingleToInt32Bits(a[i].Y) != BitConverter.SingleToInt32Bits(b[i].Y)
                || BitConverter.SingleToInt32Bits(a[i].Z) != BitConverter.SingleToInt32Bits(b[i].Z))
            {
                return false;
            }
        }

        return true;
    }

    // --- 1. bit-identical results ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false, LiquidMode.Bottom)]
    [InlineData(true, LiquidMode.Bottom)]
    [InlineData(false, LiquidMode.Surface)]
    public void TheSharedAndGatedDrape_AreBitIdentical_OnTheFixtureTiles(bool preferTerrainOverProps, LiquidMode liquidMode)
    {
        using Scene scene = Build(liquid: liquidMode == LiquidMode.Surface);
        GroundQuery query = Models(liquidMode) with { PreferTerrainOverProps = preferTerrainOverProps };

        foreach ((Vector3 start, Vector3[] leg) in new[] { (StartA, LegA), (StartB, LegB) })
        {
            List<Vector3> gated = Gated(scene.Collision, start, leg, query);
            List<Vector3>? shared = Shared(scene.Collision, start, leg, query, out SharedDrapeAbort abort);

            Assert.Equal(SharedDrapeAbort.None, abort);
            Assert.NotNull(shared);
            Assert.True(Same(shared, gated), "the shared drape differs from the gated one");
            if (!preferTerrainOverProps && liquidMode == LiquidMode.Bottom)
            {
                Assert.Contains(gated, p => p.Z > Ground + 1f); // the platforms are the floor: the leg is not just terrain
            }
        }
    }

    [Fact]
    public void ThePlainModelHeight_IsTheSame_SharedAndGated()
    {
        using Scene scene = Build();
        for (float x = XA - 15f; x <= XA + 15f; x += 0.75f)
        {
            float? gated = scene.Vmaps.GetModelHeight(0, x, YA, Ground + 4f, 8f);
            float? shared;
            using (VMapSharedReader.Enter())
            {
                shared = scene.Vmaps.GetModelHeight(0, x, YA, Ground + 4f, 8f);
                Assert.False(VMapSharedReader.Aborted);
            }

            Assert.Equal(gated, shared);
        }
    }

    [RealTerrainFact]
    public void TheSharedAndGatedDrape_AreBitIdentical_OnTheRealExtraction()
    {
        string root = Environment.GetEnvironmentVariable(RealTerrainFactAttribute.Variable)!;
        var vmaps = new VMapManager(Path.Combine(root, "vmaps"));
        var nav = new NavMeshPathfinder(Path.Combine(root, "mmaps"));
        using WorldRuntime world = TestWorld.CreateRuntime();
        world.Options.Maps.DataDirectory = root;
        WorldCollision.Of(world).Install(lineOfSight: vmaps, pathfinder: nav);
        Map map = world.GetMap(0);
        foreach ((float cx, float cy) in new[] { (2260f, 290f), (1630f, 240f), (-9700f, -400f) })
        {
            (int tx, int ty) = TerrainTile.TileOf(cx, cy)!.Value;
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    vmaps.LoadTile(0, tx + dx, ty + dy);
                    nav.LoadTile(0, tx + dx, ty + dy);
                    map.Terrain.GetTile(tx + dx, ty + dy);
                }
            }
        }

        int compared = 0;
        int shared = 0;
        foreach ((float cx, float cy) in new[] { (2260f, 290f), (1630f, 240f), (-9700f, -400f) })
        {
            for (int k = 0; k < 24; k++)
            {
                float angle = k * MathF.PI / 12f;
                float r0 = 6f + (k % 5);
                float r1 = 40f + (3f * k);
                float z0 = map.Terrain.GetHeight(cx + (r0 * MathF.Cos(angle)), cy + (r0 * MathF.Sin(angle)), 0f);
                float z1 = map.Terrain.GetHeight(cx + (r1 * MathF.Cos(angle)), cy + (r1 * MathF.Sin(angle)), 0f);
                if (z0 <= TerrainTile.InvalidHeight || z1 <= TerrainTile.InvalidHeight)
                {
                    continue;
                }

                var start = new Vector3(cx + (r0 * MathF.Cos(angle)), cy + (r0 * MathF.Sin(angle)), z0);
                Vector3[] leg = [new(cx + (r1 * MathF.Cos(angle)), cy + (r1 * MathF.Sin(angle)), z1)];
                GroundQuery query = GroundHeight.Walker;
                List<Vector3> gated = GroundHeight.DrapePath(map.Collision, start, leg, step: 1f, tolerance: 0.11f, query: query, modelTolerance: 0.4f);
                List<Vector3>? answer = GroundHeight.TryDrapePathShared(map.Collision, start, leg, out _, step: 1f, tolerance: 0.11f, query: query, modelTolerance: 0.4f);
                compared++;
                if (answer is not null)
                {
                    shared++;
                    Assert.True(Same(answer, gated), $"leg {k} at ({cx}, {cy}) differs");
                }
            }
        }

        output.WriteLine($"real extraction: {shared} of {compared} legs answered shared, all identical");
        Assert.True(compared > 0 && shared > 0, "no leg was answered shared");
    }

    // --- 2. a missing tile falls back to the gated path ---------------------------------------------------------------------------

    private static readonly FieldInfo UngatedMotion = typeof(CreatureMapSystem).GetField("_ungatedMotion", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private (CreatureMapSystem System, Creature Creature) CreatureOn(Scene scene)
    {
        Map map = scene.Map;
        CreatureTemplate template = Template() with { InhabitType = 1 };
        CreatureContent content = Content([template], [Spawn(1, WolfEntry, XA, YA, Ground + GroundHeight.StandOffset)]);
        var system = new CreatureMapSystem(map, content, null, random: new Random(1));
        map.AddUpdater(system);
        Player player = TestWorld.CreatePlayer(1, XA, YA, new FakeSession(1));
        map.AddPlayer(player);
        scene.World.RunTick(0);
        map.Terrain.GetTile(31, 32);
        map.Terrain.GetTile(31, 33);
        UngatedMotion.SetValue(system, true);
        return (system, Assert.Single(system.Creatures));
    }

    [Fact]
    public void AMissingModelTile_FallsBackToTheGatedDrape_AndALoadedOneIsThenSharedWithoutTheGate()
    {
        using Scene scene = Build(loadModelTiles: false);
        (CreatureMapSystem system, Creature creature) = CreatureOn(scene);
        scene.Vmaps.UnloadTile(0, 31, 32); // the player's grid loaded it; a tile no grid holds
        Assert.False(scene.Vmaps.HoldsTile(0, XA, YA));

        // The model tile under the leg is not loaded: the shared drape gives up and the gated one answers (as before this change, terrain only:
        // a drape never reads a tile itself, the grids and the prefetch bring them in).
        long hits = SharedDrapeStats.Hits;
        long fallbacks = SharedDrapeStats.FallbacksFor(SharedDrapeAbort.ModelTileNotLoaded);
        List<Vector3> fallback = system.DrapeOnGround(creature, StartA, LegA);
        Assert.Equal(0, system.SharedDrapes);
        Assert.Equal(1, system.GatedDrapes);
        Assert.True(SharedDrapeStats.FallbacksFor(SharedDrapeAbort.ModelTileNotLoaded) > fallbacks);
        Assert.All(fallback, p => Assert.Equal(Ground + GroundHeight.StandOffset, p.Z, 0.001f));
        Assert.Null(Shared(scene.Collision, StartA, LegA, Models(), out SharedDrapeAbort abort));
        Assert.Equal(SharedDrapeAbort.ModelTileNotLoaded, abort);

        // The owner loads the tile (a grid does): the next drape is answered outside the gate and equals the gated one.
        Assert.True(scene.Vmaps.LoadTile(0, 31, 32));
        List<Vector3> loaded = system.DrapeOnGround(creature, StartA, LegA);
        Assert.Equal(1, system.SharedDrapes);
        Assert.Equal(1, system.GatedDrapes);
        Assert.True(SharedDrapeStats.Hits > hits);
        Assert.True(Same(loaded, Gated(scene.Collision, StartA, LegA, GroundHeight.Walker with { Liquid = LiquidMode.Bottom })));
    }

    // --- 3. every abort reason --------------------------------------------------------------------------------------------------

    [Fact]
    public void ATerrainTileThatIsNotInMemory_GivesUp_WithoutLoadingIt()
    {
        using Scene scene = Build(loadTerrain: false);

        Assert.Null(Shared(scene.Collision, StartA, LegA, Models(), out SharedDrapeAbort abort));
        Assert.Equal(SharedDrapeAbort.TerrainTileNotLoaded, abort);
        Assert.False(scene.Map.Terrain.IsTileLoaded(31, 32), "the shared drape loaded a terrain tile");
        _ = Gated(scene.Collision, StartA, LegA, Models()); // the gated drape keeps the heights and does not throw
        Assert.False(scene.Map.Terrain.IsTileLoaded(31, 32));
    }

    [Fact]
    public void ANavmeshBranch_GivesUp_WhenTheNavmeshIsNotOneASharedQueryMayUse()
    {
        using Scene scene = Build();
        Vector3[] farAbove = [new(XA + 18f, YA, Ground + 10f)]; // another storey: no floor in the window, so the mesh fallback is asked
        var start = new Vector3(XA - 18f, YA, Ground + 10f);

        Assert.Null(Shared(scene.Collision, start, farAbove, Models(), out SharedDrapeAbort abort));
        Assert.Equal(SharedDrapeAbort.MeshNotShared, abort);
        Assert.Equal(farAbove[0], Gated(scene.Collision, start, farAbove, Models())[^1]); // gated: no floor, the height is kept
    }

    [Fact]
    public void ALiquidReadOnATileThatIsNotInMemory_IsRefused_AndOtherwiseEqualsTheGatedRead()
    {
        using Scene scene = Build(liquid: true, loadTerrain: false);
        TerrainInfo terrain = scene.Map.Terrain;

        Assert.False(terrain.TryGetLoadedLiquidStatus(XA, YA, Ground, LiquidTypeFlags.AllLiquids, out _, out _));
        Assert.False(terrain.IsTileLoaded(31, 32));

        terrain.GetTile(31, 32);
        Assert.True(terrain.TryGetLoadedLiquidStatus(XA, YA, Ground, LiquidTypeFlags.AllLiquids, out LiquidStatus status, out LiquidData data));
        LiquidStatus expected = terrain.GetLiquidStatus(XA, YA, Ground, LiquidTypeFlags.AllLiquids, out LiquidData expectedData);
        Assert.Equal(expected, status);
        Assert.Equal(expectedData.Level, data.Level);
        Assert.NotEqual(LiquidStatus.NoWater, status);
    }

    [Fact]
    public void AQueryThatLoadsTiles_OrServicesNotKnownToBeSafe_AreNotDrapedShared()
    {
        using Scene scene = Build();
        Assert.Null(Shared(scene.Collision, StartA, LegA, Models() with { LoadTiles = true }, out SharedDrapeAbort abort));
        Assert.Equal(SharedDrapeAbort.Unsupported, abort);

        using WorldRuntime other = TestWorld.CreateRuntime();
        WorldCollision.Of(other).Install(lineOfSight: new UnknownModels());
        Assert.Null(Shared(other.GetMap(0).Collision, StartA, LegA, Models(), out abort));
        Assert.Equal(SharedDrapeAbort.Unsupported, abort);
    }

    private sealed class UnknownModels : ILineOfSight
    {
        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        {
            hit = to;
            return false;
        }

        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => null;

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        {
            info = default;
            return false;
        }
    }

    // --- 4. shared reads do not take the serial gate -----------------------------------------------------------------------------

    [Fact]
    public void SharedDrapes_DoNotEnterTheSerialGate()
    {
        using Scene scene = Build();
        (CreatureMapSystem system, Creature creature) = CreatureOn(scene);
        long hits = SharedDrapeStats.Hits;
        const int Drapes = 40;

        for (int i = 0; i < Drapes; i++)
        {
            system.DrapeOnGround(creature, StartA, LegA);
        }

        Assert.Equal(Drapes, system.SharedDrapes);
        Assert.Equal(0, system.GatedDrapes); // GatedDrapes counts the drapes that took Serial()
        Assert.True(SharedDrapeStats.Hits >= hits + Drapes);
        Assert.Equal((long)Drapes, system.DrapedPaths);
    }

    // --- 4b. every change of the loaded tiles takes the write side of the lock a shared read holds ---------------------------------

    [Theory]
    [InlineData("unload")]
    [InlineData("install")]
    [InlineData("emptytile")]
    [InlineData("tree")]
    public async Task EveryChangeOfTheLoadedTiles_WaitsForAReaderInsideTheLock(string mutator)
    {
        using Scene scene = Build();
        _models.Write(1); // a second map whose vmtree is not read yet
        VMapManager vmaps = scene.Vmaps;
        if (mutator == "install")
        {
            vmaps.UnloadTile(0, 31, 33); // not loaded, its file exists
        }

        Action mutate = mutator switch
        {
            "unload" => () => vmaps.UnloadTile(0, 31, 33),
            "install" => () => Assert.True(vmaps.LoadTile(0, 31, 33)),
            "emptytile" => () => Assert.False(vmaps.LoadTile(0, 31, 34)), // no vmtile file: known empty
            _ => () => Assert.NotNull(vmaps.GetTree(1)),
        };

        ReaderWriterLockSlim gate = vmaps.SharedLock;
        Task writer;
        gate.EnterReadLock(); // a shared reader inside the lock
        try
        {
            writer = Task.Run(mutate);
            SpinWait.SpinUntil(() => gate.WaitingWriteCount == 1 || writer.IsCompleted, TimeSpan.FromSeconds(10));
            Assert.False(writer.IsCompleted, $"{mutator} changed the loaded tiles while a shared reader was inside the lock");
            Assert.Equal(1, gate.WaitingWriteCount);
        }
        finally
        {
            gate.ExitReadLock();
        }

        await writer.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, gate.WaitingWriteCount);
    }

    // --- 5. concurrent drapes while tiles load and unload -----------------------------------------------------------------------

    [Fact]
    public async Task ConcurrentDrapes_WhileATileLoadsAndUnloads_NeverThrowAndNeverDiffer()
    {
        using Scene scene = Build();
        GroundQuery query = Models();
        List<Vector3> expectedA = Gated(scene.Collision, StartA, LegA, query);
        List<Vector3> expectedB = Gated(scene.Collision, StartB, LegB, query);
        Assert.Contains(expectedB, p => p.Z > Ground + 1f);

        const int Readers = 4;
        const int Rounds = 3000;
        const int Cycles = 2000;
        long aHits = 0;
        long bHits = 0;
        long bAborts = 0;
        long differing = 0;
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        using var start = new Barrier(Readers + 1);

        Task[] readers = new Task[Readers];
        for (int t = 0; t < Readers; t++)
        {
            readers[t] = Task.Factory.StartNew(() =>
            {
                try
                {
                    start.SignalAndWait();
                    for (int i = 0; i < Rounds; i++)
                    {
                        // Tile A stays loaded: every drape of it is answered, and identical.
                        List<Vector3>? a = Shared(scene.Collision, StartA, LegA, query, out _);
                        if (a is null)
                        {
                            Interlocked.Increment(ref differing);
                        }
                        else if (!Same(a, expectedA))
                        {
                            Interlocked.Increment(ref differing);
                        }
                        else
                        {
                            Interlocked.Increment(ref aHits);
                        }

                        // Tile B is loaded and unloaded under the reader: an answer is identical, or the drape gave up whole.
                        List<Vector3>? b = Shared(scene.Collision, StartB, LegB, query, out SharedDrapeAbort abort);
                        if (b is null)
                        {
                            if (abort != SharedDrapeAbort.ModelTileNotLoaded)
                            {
                                Interlocked.Increment(ref differing);
                            }
                            else
                            {
                                Interlocked.Increment(ref bAborts);
                            }
                        }
                        else if (!Same(b, expectedB))
                        {
                            Interlocked.Increment(ref differing);
                        }
                        else
                        {
                            Interlocked.Increment(ref bHits);
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                }
            }, TaskCreationOptions.LongRunning);
        }

        Task loader = Task.Factory.StartNew(() =>
        {
            try
            {
                start.SignalAndWait();
                for (int i = 0; i < Cycles; i++)
                {
                    scene.Vmaps.UnloadTile(0, 31, 33);
                    scene.Vmaps.LoadTile(0, 31, 33);
                    if (i % 25 == 0)
                    {
                        scene.Vmaps.TrimUnusedModels(); // drops nothing a loaded tile names, and re-parses on the next load
                    }

                    if (i % 40 == 0)
                    {
                        scene.Vmaps.LoadTileOnDemand(0, XB, YB);
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Enqueue(ex);
            }
        }, TaskCreationOptions.LongRunning);

        // A bounded wait only to turn a deadlock into a failure; it is not a timing assertion.
        await Task.WhenAll([.. readers, loader]).WaitAsync(TimeSpan.FromMinutes(5));
        output.WriteLine($"stress: A answered {aHits}/{Readers * Rounds}, B answered {bHits}, B gave up {bAborts}, differing {differing}, loader cycles {Cycles}");

        Assert.Empty(errors);
        Assert.Equal(0, differing);
        Assert.Equal(Readers * Rounds, aHits);
        Assert.Equal(Readers * Rounds, bHits + bAborts);
    }
}
