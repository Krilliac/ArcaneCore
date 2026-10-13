using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Spells.Rules.CrowdControl;
using ArcaneCore.Game.Tests.Collision;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Creatures;

/// <summary>
/// Wander and flee choose their points on the navmesh (vmangos <c>Creature::GetRandomPoint</c> with <c>MOVE_PATHFINDING</c>, and the
/// raycast in <c>FleeingMovementGenerator</c>): over a mesh tile with a hole in it (a building) no creature ever picks a destination
/// inside the hole, walks through it, or flees off the mesh. Before this, wander took the terrain height at a random point and walked a
/// straight line, and flee did the same with its far point.
/// </summary>
public sealed class MeshPointMovementTests
{
    private const uint MapId = 0;

    /// <summary>20×20 cells of 2 yd over world [0, 40)² with a hole of 4×4 cells at world [16, 24)² (a house).</summary>
    private static CellTile House() => new(0, 0, 0, 0, 20, 20, Size: 2) { Walkable = (i, j) => !(i is >= 8 and < 12 && j is >= 8 and < 12) };

    private static bool InHouse(float x, float y) => x > 16.05f && x < 23.95f && y > 16.05f && y < 23.95f;

    private static bool OnTile(float x, float y) => x >= -0.01f && x <= 40.01f && y >= -0.01f && y <= 40.01f;

    private sealed class Scene : IDisposable
    {
        private readonly NavMeshFixture _fixture = new();

        public Scene(byte movementType, float wander, float x, float y, float playerX, float playerY)
        {
            _fixture.WriteParams(MapId);
            _fixture.WriteTile(MapId, 31, 31, House());
            Nav = new NavMeshPathfinder(_fixture.Directory);
            Assert.True(Nav.LoadTile(MapId, 31, 31));
            CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, x, y, z: 0, movementType: movementType, wander: wander)]);
            (World, _, Creatures) = CreateAiSystem(content);
            WorldCollision.Of(World).Install(pathfinder: Nav);
            (Player, _) = AddPlayer(World, 1, playerX, playerY);
            Wolf = Assert.Single(Creatures.Creatures);
        }

        public NavMeshPathfinder Nav { get; }

        public WorldRuntime World { get; }

        public CreatureMapSystem Creatures { get; }

        public Player Player { get; }

        public Creature Wolf { get; }

        public void Dispose()
        {
            World.Dispose();
            _fixture.Dispose();
        }
    }

    /// <summary>Run <paramref name="ticks"/> ticks of 50 ms; every position the creature stands on and every spline destination it is given is passed to the checks.</summary>
    private static int Watch(Scene scene, int ticks, Action<float, float> position, Action<Vector3> destination)
    {
        object? last = null;
        int legs = 0;
        for (int i = 0; i < ticks; i++)
        {
            scene.World.RunTick(50);
            position(scene.Wolf.X, scene.Wolf.Y);
            if (scene.Wolf.Spline is { } spline && !ReferenceEquals(spline, last))
            {
                last = spline;
                legs++;
                foreach (Vector3 point in spline.Points)
                {
                    destination(point);
                }
            }
        }

        return legs;
    }

    [Fact]
    public void Wander_NeverPicksADestinationInTheHouse_NorWalksThroughIt()
    {
        // The spawn is 4 yd east of the house; the 12 yd wander circle covers it.
        using var scene = new Scene(movementType: 1, wander: 12f, x: 28, y: 20, playerX: 0, playerY: 0);
        int checks = 0;

        int legs = Watch(scene, 12_000, // 10 minutes
            (x, y) => Assert.False(InHouse(x, y), $"the wanderer walked into the house at ({x}, {y})"),
            p =>
            {
                checks++;
                Assert.False(InHouse(p.X, p.Y), $"a wander leg goes through ({p.X}, {p.Y}) inside the house");
                Assert.True(OnTile(p.X, p.Y), $"a wander leg goes through ({p.X}, {p.Y}), off the mesh");
            });

        Assert.True(legs >= 20, $"only {legs} legs were walked");
        Assert.True(checks >= legs);
    }

    [Fact]
    public void Wander_WithoutMeshData_KeepsTheTerrainPoint()
    {
        // No navmesh installed for the map: the old draw (two NextDouble) and a straight MoveTo, a single-point spline.
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 28, 20, z: 0, movementType: 1, wander: 12f)]);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime runtime = world;
        AddPlayer(runtime, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);
        for (int i = 0; i < 100 && !wolf.IsMoving; i++)
        {
            runtime.RunTick(50);
        }

        Assert.True(wolf.IsMoving);
        Assert.Single(wolf.Spline!.Points);
        Assert.InRange(Vector3.Distance(new Vector3(28, 20, 0), wolf.Spline.Points[0]), 0f, 12.01f);
    }

    [Fact]
    public void Flee_NeverTargetsTheHouse_OrOffTheMesh_AndNeverWalksThroughIt()
    {
        // The wolf stands 6 yd east of the house with the caster behind it: away is west, straight through the house and off the tile.
        using var scene = new Scene(movementType: 0, wander: 0, x: 30, y: 20, playerX: 36, playerY: 20);
        CcState.RememberFearSource(scene.Wolf, scene.Player);
        scene.Wolf.UnitFlags |= UnitFlags.Fleeing;
        int checks = 0;

        int legs = Watch(scene, 2_400,
            (x, y) => Assert.False(InHouse(x, y), $"the fleeing wolf walked into the house at ({x}, {y})"),
            p =>
            {
                checks++;
                Assert.False(InHouse(p.X, p.Y), $"a flee leg goes through ({p.X}, {p.Y}) inside the house");
                Assert.True(OnTile(p.X, p.Y), $"a flee leg goes through ({p.X}, {p.Y}), off the mesh");
            });

        Assert.True(legs >= 10, $"only {legs} flee legs were walked");
        Assert.True(checks >= legs);
    }

    [Fact]
    public void Flee_WithAWallRightInFront_NeverIssuesANearZeroLeg()
    {
        // The wolf stands 0.6 yd from the tile's west edge with the caster east of it: "away" is a wall. Every leg it walks is a real
        // one (another direction was drawn), never a stub at the wall.
        using var scene = new Scene(movementType: 0, wander: 0, x: 0.6f, y: 20, playerX: 6, playerY: 20);
        CcState.RememberFearSource(scene.Wolf, scene.Player);
        scene.Wolf.UnitFlags |= UnitFlags.Fleeing;
        var shortest = new List<float>();
        object? last = null;
        for (int i = 0; i < 1_200; i++)
        {
            scene.World.RunTick(50);
            if (scene.Wolf.Spline is { } spline && !ReferenceEquals(spline, last))
            {
                last = spline;
                shortest.Add(MathF.Sqrt(((spline.EndX - spline.StartX) * (spline.EndX - spline.StartX)) + ((spline.EndY - spline.StartY) * (spline.EndY - spline.StartY))));
            }
        }

        Assert.True(shortest.Count >= 5, $"only {shortest.Count} flee legs were walked");
        Assert.True(shortest.Min() >= 0.99f, $"a flee leg of {shortest.Min()} yd was issued");
    }
}
