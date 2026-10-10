using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Maps.Collision.VMaps;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Tests.Transports;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Game.Tests.GridTerrain;

[CollectionDefinition("World tick load", DisableParallelization = true)]
public sealed class WorldTickLoadCollection;

/// <summary>Deterministic simulation, wall time measured only as output; no timing-based correctness assertions.</summary>
[Collection("World tick load")]
public sealed class WorldTickLoadTests(ITestOutputHelper output)
{
    [Fact]
    public void SixMovingPlayersAndShips_With66000Spawns_RecordTickPhases()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        world.UseManualClock();
        Map map = world.GetMap(0);
        var random = new Random(5875);
        var spawns = new WorldObject[66_000];
        var hubs = new Vector2[60];
        hubs[0] = new(-8949.95f, -132.493f); // ClassicDB playercreateinfo, human start
        for (int i = 1; i < hubs.Length; i++) hubs[i] = new(-9500 + random.NextSingle() * 12000, -3500 + random.NextSingle() * 4500);
        for (int i = 0; i < spawns.Length; i++)
        {
            Vector2 hub = hubs[i % hubs.Length];
            float x = i % 10 < 7 ? hub.X + (random.NextSingle() - .5f) * 240 : -9500 + random.NextSingle() * 12000;
            float y = i % 10 < 7 ? hub.Y + (random.NextSingle() - .5f) * 240 : -3500 + random.NextSingle() * 4500;
            spawns[i] = new TestUnit((uint)i + 1, x, y);
            map.AddObject(spawns[i]);
        }
        var players = new Player[6];
        for (int i = 0; i < players.Length; i++)
        {
            players[i] = TestWorld.CreatePlayer((uint)i + 1, hubs[0].X + i * 3, hubs[0].Y, new Sink(i + 1));
            world.AddPlayer(players[i]);
        }
        WorldObject[] movers = spawns.OrderBy(o => MathF.Abs(o.X - hubs[0].X) + MathF.Abs(o.Y - hubs[0].Y)).Take(670).ToArray();
        var movement = new LoadMovement(players, movers, hubs[0]);
        map.AddUpdater(movement);
        var ships = TransportTestKit.Install(world, TransportTestKit.Ferry, TransportTestKit.Crossing);
        string? terrainRoot = Environment.GetEnvironmentVariable("ARCANECORE_TEST_TERRAIN_DIR");
        var queries = new Queries(terrainRoot);
        map.AddUpdater(queries);
        const int warmup = 100, samples = 600;
        var rows = new List<Row>(warmup + samples);
        long simulation = 0, visibility = 0, values = 0, flush = 0, cleanup = 0;
        Action<Map, MapUpdateDiagnostics> observe = (_, d) =>
        {
            Assert.True(d.Completed);
            simulation += d.SimulationMicros; visibility += d.VisibilityMicros;
            values += d.ValuesMicros; flush += d.FlushMicros; cleanup += d.CleanupMicros;
        };
        // ARCANECORE_VISIBILITY_AB=1: alternate the merged and the sorted visibility candidates in blocks of 10 ticks on the
        // same world (their results are identical, VisibilityJoinOrderEquivalenceTests), so load on a shared box hits both
        // alike; medians of visibility wall time and of the world thread's CPU time are reported per mode.
        bool ab = Environment.GetEnvironmentVariable("ARCANECORE_VISIBILITY_AB") == "1";
        var abRows = new List<(bool Sorted, long VisibilityUs, long CpuNs)>();
        for (int tick = 0; tick < warmup + samples; tick++)
        {
            bool sorted = ab && (tick / 10) % 2 == 1;
            map.UseReferenceVisibilityOrder = sorted;
            long cpu0 = ab ? ThreadCpuNs() : 0;
            simulation = visibility = values = flush = cleanup = 0;
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            long bytes = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
            world.RunTick(50, observe);
            long elapsed = Micros(start), allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
            rows.Add(new(tick, elapsed, allocated, GC.CollectionCount(0) - g0, GC.CollectionCount(1) - g1,
                GC.CollectionCount(2) - g2, world.LastTickPhases.CommandsMicros, world.LastTickPhases.FeaturesMicros,
                simulation, visibility, values, flush, cleanup, queries.LoadMicros, queries.TerrainMicros, queries.VmapMicros, queries.PathMicros));
            if (ab && tick >= warmup)
            {
                abRows.Add((sorted, visibility, ThreadCpuNs() - cpu0));
            }
        }

        map.UseReferenceVisibilityOrder = false;
        Assert.Equal(2, ships.Ships.Count);
        Assert.All(ships.Ships, ship => Assert.True(ship.PathProgress > 0));
        Assert.All(players, p => Assert.True(p.VisibleObjects.Count > 100));
        Assert.Equal(warmup + samples, movement.Ticks);
        Assert.Equal(warmup + samples, queries.Ticks); // an updater exception must not silently pass
        if (terrainRoot is not null)
        {
            Assert.True(queries.Loaded);
            Assert.True(queries.MeshPaths > 0);
            Assert.True(queries.BlockedSight > 0);
        }
        Row[] measured = rows.Skip(warmup).ToArray();
        output.WriteLine($"terrain={terrainRoot ?? "disabled"}; 66000 units, 670 movers, 6 simulated players, 2 ships; 50ms manual steps; warmup={warmup}, samples={samples}");
        Report("tick", measured.Select(r => r.TickUs));
        Report("simulation", measured.Select(r => r.SimulationUs));
        Report("visibility", measured.Select(r => r.VisibilityUs));
        Report("path", measured.Select(r => r.PathUs));
        Report("vmap", measured.Select(r => r.VmapUs));
        Report("cleanup", measured.Select(r => r.CleanupUs));
        output.WriteLine($"allocated mean={measured.Average(r => r.Bytes):F0} bytes/tick; GC={measured.Sum(r => r.Gen0)}/{measured.Sum(r => r.Gen1)}/{measured.Sum(r => r.Gen2)}; cold first tick={rows[0].TickUs}us load={rows[0].LoadUs}us; mesh paths={queries.MeshPaths}");
        if (ab)
        {
            foreach (bool mode in new[] { false, true })
            {
                long[] vis = [.. abRows.Where(r => r.Sorted == mode).Select(r => r.VisibilityUs).Order()];
                long[] cpu = [.. abRows.Where(r => r.Sorted == mode).Select(r => r.CpuNs).Order()];
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"AB {(mode ? "sorted" : "merged")}: ticks={vis.Length} visibility median={vis[vis.Length / 2] / 1000d:F3} ms; thread cpu median={cpu[cpu.Length / 2] / 1e6:F3} ms"));
            }
        }

        string? csv = Environment.GetEnvironmentVariable("ARCANECORE_TICK_CSV");
        if (!string.IsNullOrWhiteSpace(csv))
        {
            using var writer = new StreamWriter(csv);
            writer.WriteLine("tick,tick_us,bytes,gen0,gen1,gen2,commands_us,features_us,simulation_us,visibility_us,values_us,flush_us,cleanup_us,load_us,terrain_us,vmap_us,path_us");
            foreach (Row r in rows) writer.WriteLine(FormattableString.Invariant($"{r.Index},{r.TickUs},{r.Bytes},{r.Gen0},{r.Gen1},{r.Gen2},{r.CommandsUs},{r.FeaturesUs},{r.SimulationUs},{r.VisibilityUs},{r.ValuesUs},{r.FlushUs},{r.CleanupUs},{r.LoadUs},{r.TerrainUs},{r.VmapUs},{r.PathUs}"));
        }
    }

    private void Report(string phase, IEnumerable<long> values)
    {
        long[] sorted = values.Order().ToArray();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{phase}: mean={sorted.Average()/1000:F3} p95={sorted[(int)Math.Ceiling(sorted.Length*.95)-1]/1000d:F3} p99={sorted[(int)Math.Ceiling(sorted.Length*.99)-1]/1000d:F3} max={sorted[^1]/1000d:F3} ms"));
    }
    /// <summary>CPU time of the calling thread in ns (Linux schedstat; 0 elsewhere): unlike wall time, not inflated by other load.</summary>
    private static long ThreadCpuNs()
    {
        try
        {
            string text = File.ReadAllText("/proc/thread-self/schedstat");
            return long.Parse(text.AsSpan(0, text.IndexOf(' ')), CultureInfo.InvariantCulture);
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static long Micros(long start) => (Stopwatch.GetTimestamp() - start) * 1_000_000 / Stopwatch.Frequency;
    private sealed record Row(int Index, long TickUs, long Bytes, int Gen0, int Gen1, int Gen2, long CommandsUs, long FeaturesUs,
        long SimulationUs, long VisibilityUs, long ValuesUs, long FlushUs, long CleanupUs, long LoadUs, long TerrainUs, long VmapUs, long PathUs);
    private sealed class Sink(int accountId) : IPlayerSession
    {
        public int AccountId => accountId;
        public AccountSecurity Security => AccountSecurity.Player;
        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload) { }
        public void ProcessWorldPackets(Player player) { }
        public void Kick() { }
        public void OnLoggedOut() { }
    }
    private sealed class LoadMovement(Player[] players, WorldObject[] movers, Vector2 hub) : IMapUpdater
    {
        public int Ticks { get; private set; }
        public void OnPlayerRemoved(Map map, Player player) { }
        public void Update(Map map, uint diffMs)
        {
            float angle = Ticks++ * .01f;
            for (int i = 0; i < players.Length; i++) players[i].SetPosition(hub.X + i * 3 + MathF.Cos(angle) * 20, hub.Y + MathF.Sin(angle) * 20, 83.5f, angle);
            float step = Ticks % 100 < 50 ? .25f : -.25f;
            foreach (WorldObject mover in movers) mover.SetPosition(mover.X + step, mover.Y, mover.Z, 0);
        }
    }
    private sealed class Queries : IMapUpdater
    {
        private readonly TerrainManager? _terrain;
        private readonly VMapManager? _vmap;
        private readonly NavMeshPathfinder? _paths;
        private static readonly Vector3 Start = new(-8949.95f, -132.493f, 83.5312f);
        private static readonly Vector3 End = new(-8902.59f, -162.606f, 82.0223f);
        public bool Loaded { get; private set; }
        public int Ticks { get; private set; }
        public int MeshPaths { get; private set; }
        public int BlockedSight { get; private set; }
        public long LoadMicros, TerrainMicros, VmapMicros, PathMicros;
        public Queries(string? root)
        {
            if (root is null) return;
            _terrain = new(root); _vmap = new(Path.Combine(root, "vmaps")); _paths = new(Path.Combine(root, "mmaps"));
        }
        public void OnPlayerRemoved(Map map, Player player) { }
        public void Update(Map map, uint diffMs)
        {
            LoadMicros = TerrainMicros = VmapMicros = PathMicros = 0;
            if (_terrain is not null && _vmap is not null && _paths is not null)
            {
                long start = Stopwatch.GetTimestamp();
                if (!Loaded)
                {
                    (int x, int y) = TerrainTile.TileOf(Start.X, Start.Y)!.Value;
                    Assert.True(_vmap.LoadTile(0, x, y));
                    Assert.True(_paths.LoadTile(0, x, y));
                    Assert.InRange(_terrain.For(0).GetHeight(Start.X, Start.Y, Start.Z), 81, 86);
                    Loaded = true;
                }
                LoadMicros = Micros(start);
                start = Stopwatch.GetTimestamp();
                for (int i = 0; i < 6; i++) _terrain.For(0).GetHeight(Start.X + i, Start.Y, Start.Z);
                TerrainMicros = Micros(start);
                start = Stopwatch.GetTimestamp();
                for (int i = 0; i < 6; i++)
                {
                    _vmap.GetModelHeight(0, Start.X + i, Start.Y, Start.Z + 2, 10);
                    if (!_vmap.IsInLineOfSight(0, new(-8918.36f, -208.411f, 84.3088f), End + new Vector3(0, 0, 2))) BlockedSight++;
                }
                VmapMicros = Micros(start);
                start = Stopwatch.GetTimestamp();
                // Five simulated bots request a path every second, with a deterministic burst rather than sleeps.
                if (Ticks % 20 == 0)
                    for (int i = 0; i < 5; i++)
                    {
                        PathResult result = _paths.FindPath(0, Start + new Vector3(i, 0, 0), End);
                        if ((result.Type & (PathType.NoPath | PathType.NotUsingPath)) == 0) MeshPaths++;
                    }
                PathMicros = Micros(start);
            }
            Ticks++;
        }
    }
}
