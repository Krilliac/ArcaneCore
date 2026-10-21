using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Tests.GridTerrain;
using ArcaneCore.Protocol;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureMovement;

/// <summary>
/// The gates of <see cref="CreatureMapSystem.DrapeOnGround"/> on synthetic ground: flat terrain at 20 with shallow water at 21.5 over it (one
/// tile). Which creatures are put on the ground (walkers and swimmers; fliers and water-only creatures keep the mesh's heights), where a
/// swimmer stands (the surface) and a walker (the bed), and the narrow window for a spawn's own height at home. The same gates on the real
/// extraction are in <c>GroundSnapRealTerrainTests</c>.
/// </summary>
public sealed class GroundSnapGatesTests : IDisposable
{
    private const float X = 30f;
    private const float Y = -30f;
    private const float Ground = 20f;
    private const float Surface = 21.5f;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-snapgates-" + Guid.NewGuid().ToString("N"));

    public GroundSnapGatesTests()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "maps"));
        var builder = new MapFileBuilder
        {
            GridHeight = Ground,
            HasLiquid = true,
            LiquidGlobalFlags = (byte)LiquidTypeFlags.Water,
            LiquidGlobalEntry = 1,
            LiquidLevel = Surface,
        };
        File.WriteAllBytes(Path.Combine(_directory, "maps", TerrainTile.FileName(0, 31, 32)), builder.Build());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // a temp directory a test left behind is not worth a failure
        }
    }

    /// <summary>A pathfinder whose mesh surface is one height everywhere; no paths.</summary>
    private sealed class FlatMesh(float height) : IPathfinder, IMeshFloorQuery
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => new(PathType.Normal, [start, end]);

        public bool TryGetMeshFloor(uint mapId, float x, float y, float z, float below, float above, out float h)
        {
            h = height;
            return h >= z - below && h <= z + above;
        }
    }

    private (WorldRuntime World, CreatureMapSystem System, Creature Creature) Scene(uint inhabit, float? meshHeight = null)
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        if (meshHeight is { } mesh)
        {
            WorldCollision.Of(world).Install(pathfinder: new FlatMesh(mesh));
        }

        world.Options.Maps.DataDirectory = _directory;
        Map map = world.GetMap(0);
        CreatureTemplate template = Template() with { InhabitType = (byte)inhabit };
        CreatureContent content = Content([template], [Spawn(1, WolfEntry, X, Y, Ground + GroundHeight.StandOffset)]);
        var system = new CreatureMapSystem(map, content, null, random: new Random(1));
        map.AddUpdater(system);
        Player player = TestWorld.CreatePlayer(1, X, Y, new FakeSession(1));
        map.AddPlayer(player);
        world.RunTick(0);
        return (world, system, Assert.Single(system.Creatures));
    }

    private float DrapedZ(uint inhabit, float cornerZ, float? window = null, float? meshHeight = null, Action<Creature>? prepare = null)
    {
        (WorldRuntime world, CreatureMapSystem system, Creature creature) = Scene(inhabit, meshHeight);
        using (world)
        {
            prepare?.Invoke(creature);
            var start = new Vector3(X, Y, cornerZ);
            List<Vector3> draped = system.DrapeOnGround(creature, start, [new Vector3(X + 8f, Y, cornerZ)], window: window);
            return draped[^1].Z;
        }
    }

    [Fact]
    public void AWalker_IsPutOnTheBed_UnderTheWater()
    {
        Assert.Equal(Ground + GroundHeight.StandOffset, DrapedZ(inhabit: 1, cornerZ: 21.2f), 0.001f);
    }

    [Fact]
    public void ACreatureThatAlsoSwims_IsPutOnTheWaterSurface()
    {
        Assert.Equal(Surface, DrapedZ(inhabit: 3, cornerZ: 21.2f), 0.001f);
    }

    [Theory]
    [InlineData(2u)]  // swims only: stays in its water, at the mesh's height
    [InlineData(4u)]  // flies
    [InlineData(5u)]  // walks and flies
    [InlineData(7u)]  // all three
    public void WaterOnlyCreaturesAndFliers_KeepTheHeightTheyCameWith(uint inhabit)
    {
        Assert.Equal(21.2f, DrapedZ(inhabit, cornerZ: 21.2f));
    }

    [Fact]
    public void ASpawnsOwnHeightAtHome_IsSnappedOnlyWithinHalfAYard()
    {
        // The database z of a spawn that is already standing at home: a floor half a yard up or down is its floor; another one is not.
        Assert.Equal(Ground + GroundHeight.StandOffset, DrapedZ(inhabit: 1, cornerZ: Ground + 0.4f, window: CreatureMapSystem.AtHomeSnapYards), 0.001f);
        Assert.Equal(Ground + 1.0f, DrapedZ(inhabit: 1, cornerZ: Ground + 1.0f, window: CreatureMapSystem.AtHomeSnapYards), 0.001f);
        Assert.Equal(Ground + GroundHeight.StandOffset, DrapedZ(inhabit: 1, cornerZ: Ground + 1.0f), 0.001f); // the plain window of 2 yd takes it
    }

    [Fact]
    public void ASpawnHalfAYardOffTheGround_IsNotLiftedOntoADeckByTheMeshFallback()
    {
        // The terrain is 0.7 yd under the spawn (outside the 0.5 yd window) and a mesh polygon is 3 yd over it: the fallback searches up to
        // 4 yd above and would put the spawn on that deck. Inside the window there is no floor, so the height is kept.
        Assert.Equal(Ground + 0.7f, DrapedZ(inhabit: 1, cornerZ: Ground + 0.7f, window: CreatureMapSystem.AtHomeSnapYards, meshHeight: Ground + 3.7f), 0.001f);
        // With nothing above either: kept, not moved 0.65 yd to the mesh surface.
        Assert.Equal(Ground + 0.7f, DrapedZ(inhabit: 1, cornerZ: Ground + 0.7f, window: CreatureMapSystem.AtHomeSnapYards, meshHeight: Ground + 0.3f), 0.001f);
    }

    [Fact]
    public void ACreatureOnATransport_KeepsItsHeights()
    {
        float z = DrapedZ(inhabit: 1, cornerZ: 21.2f, prepare: c => c.SetTransportData(new ObjectGuid(0x1000UL), 1f, 0f, 1f, 0f));
        Assert.Equal(21.2f, z);
    }

    [Theory]
    [InlineData(MovementFlags.Flying)]
    [InlineData(MovementFlags.Hover)]
    public void AWalkerAScriptSetFlyingOrHovering_KeepsItsHeights(MovementFlags flag)
    {
        Assert.Equal(21.2f, DrapedZ(inhabit: 1, cornerZ: 21.2f, prepare: c => c.AddMovementFlags(flag)));
    }

    [Fact]
    public void WithTheSnapSwitchedOff_EveryCreatureKeepsItsHeights()
    {
        (WorldRuntime world, CreatureMapSystem system, Creature creature) = Scene(inhabit: 1);
        using (world)
        {
            system.GroundSnapEnabled = false;
            Assert.Equal(21.2f, system.DrapeOnGround(creature, new Vector3(X, Y, 21.2f), [new Vector3(X + 8f, Y, 21.2f)])[^1].Z);
            Assert.Equal(0L, system.DrapedPaths);
        }
    }
}
