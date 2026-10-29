using System.Diagnostics;
using System.Globalization;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Maps.Collision.VMaps;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using Xunit.Abstractions;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// The measurement behind the wander-job change: the same 400-wanderer Elwynn field (real vmaps and navmesh from
/// <c>ARCANECORE_TEST_TERRAIN_DIR</c>) run three ways, each over 600 ticks of 100 ms after 50 warm-up ticks, with no sleeps:
/// A = path jobs on, wander jobs off (the old route: the point is drawn on the map's thread, the path goes to the pool);
/// B = path jobs and wander jobs on (point and path in one pool job);
/// C = path jobs off (everything on the map's thread).
/// Runs go A, B, C, A, B, C and print one greppable <c>WANDER-MEASURE</c> line each. Only functional properties are asserted: the
/// numbers are the output, and timing relations between the modes are deliberately not asserted.
/// </summary>
public sealed class WanderJobRealTerrainMeasurementTests(ITestOutputHelper output)
{
    private const int Warmup = 50;
    private const int MeasuredTicks = 600;
    private const int WantedWanderers = 400;
    private const int PoolThreads = 4;

    // Open ground east of Goldshire (Elwynn Forest, map 0): a 24 x 24 candidate grid, 8 yd apart. A candidate with no navmesh floor
    // (a tree, a building, water) is skipped; the actual count is printed.
    private const float OriginX = -9380f;
    private const float OriginY = -40f;
    private const float Spacing = 8f;
    private const int Columns = 24;
    private const int Rows = 24;

    private static string Root => Environment.GetEnvironmentVariable(RealTerrainFactAttribute.Variable)!;

    /// <summary>Installs the real vmaps and navmesh on <paramref name="world"/> and loads the 3 x 3 tiles around the point (plus the terrain tiles).</summary>
    private static void InstallRealCollision(WorldRuntime world, uint mapId, float x, float y)
    {
        var vmaps = new VMapManager(Path.Combine(Root, "vmaps"));
        var nav = new NavMeshPathfinder(Path.Combine(Root, "mmaps"));
        world.Options.Maps.DataDirectory = Root;
        WorldCollision.Of(world).Install(lineOfSight: vmaps, pathfinder: nav);
        (int tx, int ty) = TerrainTile.TileOf(x, y)!.Value;
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                vmaps.LoadTile(mapId, tx + dx, ty + dy);
                nav.LoadTile(mapId, tx + dx, ty + dy);
                world.GetMap(mapId).Terrain.GetTile(tx + dx, ty + dy);
            }
        }
    }

    private sealed record Result(int Wanderers, long MotionTicks, long Calls, long Legs, long WanderSubmitted, long WanderApplied, long PathSubmitted, long PathApplied);

    private Result Run(char mode)
    {
        float centerX = OriginX + (Columns * Spacing / 2f);
        float centerY = OriginY + (Rows * Spacing / 2f);
        using WorldRuntime world = TestWorld.CreateRuntime();
        InstallRealCollision(world, 0u, centerX, centerY);
        Map map = world.GetMap(0);

        var spawns = new List<CreatureSpawn>();
        for (int row = 0; row < Rows && spawns.Count < WantedWanderers; row++)
        {
            for (int column = 0; column < Columns && spawns.Count < WantedWanderers; column++)
            {
                float x = OriginX + (column * Spacing);
                float y = OriginY + (row * Spacing);
                if (map.Collision.TryGetMeshFloor(x, y, 70f, 60f, 60f, out float z))
                {
                    spawns.Add(Spawn((uint)(spawns.Count + 1), WolfEntry, x, y, z, 0, movementType: 1, wander: 9));
                }
            }
        }

        Assert.True(spawns.Count >= 50, $"only {spawns.Count} of {Columns * Rows} candidate points have a navmesh floor; the field is not open ground");

        var options = new CreatureOptions();
        options.Movement.PathJobs = mode != 'C';
        options.Movement.PathJobsWander = mode == 'B';
        var system = new CreatureMapSystem(map, Content([Template() with { InhabitType = 1 }], spawns), options, random: new Random(1), aiServices: new CreatureAiServices());
        map.AddUpdater(system);
        using NavJobSystem? pool = mode == 'C' ? null : new NavJobSystem(PoolThreads);
        system.PathJobs = pool;

        // Nine players over the field keep every cell active (the map updates only the cells around players and active objects).
        uint guid = 1;
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                AddPlayer(world, guid++, OriginX + (i * Columns * Spacing / 2f), OriginY + (j * Rows * Spacing / 2f));
            }
        }

        List<Creature> wolves = [.. system.Creatures];
        Assert.Equal(spawns.Count, wolves.Count);
        system.TimeRandomMotion = true;
        for (int i = 0; i < Warmup; i++)
        {
            world.RunTick(100);
        }

        Assert.Equal(0, wolves.Count(w => w.IsFrozenOutsideActiveCells));

        long ticks0 = system.RandomMotionTicksTotal;
        long calls0 = system.RandomMotionCalls;
        long wanderSubmitted0 = system.WanderJobsSubmitted;
        long wanderApplied0 = system.WanderJobsApplied;
        long pathSubmitted0 = system.PathJobsSubmitted;
        long pathApplied0 = system.PathJobsApplied;
        var last = new CreatureSpline?[wolves.Count];
        for (int i = 0; i < wolves.Count; i++)
        {
            last[i] = wolves[i].Spline;
        }

        long legs = 0;
        for (int tick = 0; tick < MeasuredTicks; tick++)
        {
            world.RunTick(100);
            for (int i = 0; i < wolves.Count; i++)
            {
                CreatureSpline? spline = wolves[i].Spline;
                if (spline is not null && !ReferenceEquals(spline, last[i]))
                {
                    legs++;
                }

                last[i] = spline;
            }
        }

        return new Result(
            wolves.Count, system.RandomMotionTicksTotal - ticks0, system.RandomMotionCalls - calls0, legs,
            system.WanderJobsSubmitted - wanderSubmitted0, system.WanderJobsApplied - wanderApplied0,
            system.PathJobsSubmitted - pathSubmitted0, system.PathJobsApplied - pathApplied0);
    }

    [RealTerrainFact]
    public void FourHundredWanderers_AreMeasuredBeforeAfterAndOnTheMapThread()
    {
        char[] order = ['A', 'B', 'C', 'A', 'B', 'C'];
        for (int i = 0; i < order.Length; i++)
        {
            char mode = order[i];
            int run = (i / 3) + 1;
            GC.Collect();
            Result r = Run(mode);

            double motionMs = r.MotionTicks * 1000.0 / Stopwatch.Frequency;
            double perTickUs = motionMs * 1000.0 / MeasuredTicks;
            double perCallUs = r.Calls == 0 ? 0 : motionMs * 1000.0 / r.Calls;
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"WANDER-MEASURE mode={mode} run={run} n={r.Wanderers} ticks={MeasuredTicks} motion_ms={motionMs:F2} per_tick_us={perTickUs:F1} per_call_us={perCallUs:F2} calls={r.Calls} legs={r.Legs} wander_jobs={r.WanderSubmitted}/{r.WanderApplied} path_jobs={r.PathSubmitted}/{r.PathApplied}"));

            Assert.True(r.Legs > 0, $"mode {mode} run {run}: no wander leg was started");
            switch (mode)
            {
                case 'A':
                    Assert.Equal(0, r.WanderSubmitted);
                    break;
                case 'B':
                    Assert.True(r.WanderApplied > 0, $"mode B run {run}: no wander job was applied");
                    break;
                case 'C':
                    Assert.Equal(0, r.WanderSubmitted);
                    Assert.Equal(0, r.PathSubmitted);
                    break;
            }
        }
    }
}
