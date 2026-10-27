using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Pets;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Tests.Playerbots.Combat;
using ArcaneCore.World.Tests.Playerbots.Ship;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>The L3 tests read process-wide counters and tables (dock computations, tame scan clocks): no other test class runs beside them.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PlayerbotHotSpotL3Collection
{
    public const string Name = "Playerbot hot spot L3 (process-wide counters)";
}

/// <summary>
/// Hot-spot lane L3: the tame-target scan of a pet-less hunter reads the cells around the bot (not every creature of the map) and keeps its
/// scan clocks bounded; the dock answer of a port is computed once per navigation mesh and port.
/// </summary>
[Collection(PlayerbotHotSpotL3Collection.Name)]
public sealed class PlayerbotHotSpotL3Tests
{
    private static uint s_entry = 996_000;

    private static CreatureTemplate Template(byte level = 1, uint rank = 0, uint type = SummonService.CreatureTypeBeast, uint flags = SummonService.TypeFlagTameable)
    {
        uint entry = Interlocked.Increment(ref s_entry);
        return new CreatureTemplate
        {
            Entry = entry, Name = "tame scan " + entry, CreatureType = type, TypeFlags = flags, Rank = rank,
            MinLevel = level, MaxLevel = level, MinLevelHealth = 50, MaxLevelHealth = 50,
        };
    }

    private static Creature Spawn(WorldTestHost host, Player near, float dx, float dy, CreatureTemplate template, float dz = 0f)
        => host.WorldServices.GetRequiredService<CreatureWorldFeature>().GetOrCreateSystem(near.Map!)
            .SpawnTemporary(template, near.X + dx, near.Y + dy, near.Z + dz, 0);

    /// <summary>The scan as it was: every creature of the map, nearest wins, equal distances to the lower guid.</summary>
    private static Creature? FullScan(Player player, Map map)
    {
        Creature? best = null;
        float bestSq = 28f * 28f;
        foreach (Creature c in map.FindUpdater<CreatureMapSystem>()!.Creatures)
        {
            if (!c.IsInWorld || !c.IsAlive || c.Level > player.Level || c.Template.Rank != 0 || !c.OwnerGuid.IsEmpty || !c.CharmerGuid.IsEmpty
                || c.Combat.IsInCombat || !SummonService.IsTameable(c))
                continue;
            float dx = c.X - player.X, dy = c.Y - player.Y, dz = c.Z - player.Z;
            float sq = (dx * dx) + (dy * dy) + (dz * dz);
            if (sq < bestSq || (sq == bestSq && best is not null && c.Guid.Value < best.Guid.Value))
            {
                best = c;
                bestSq = sq;
            }
        }

        return best;
    }

    [Fact]
    public async Task TameCandidate_EqualsTheFullMapScan_OverASeededWorld_TiesIncluded()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession bot = await CombatTestSessions.EnterAsync(host, "TAMEA", "Tamea");
        var random = new Random(20261027);
        for (int round = 0; round < 9; round++)
        {
            (Creature? full, Creature? grid) = await host.OnWorldAsync(() =>
            {
                Player player = bot.Player!;
                Map map = player.Map!;
                // Rings of equal distance (ties), mixed with ineligible neighbours, plus a scatter around and far away.
                CreatureTemplate beast = Template();
                float ring = 24f - (2 * round); // each round puts a new tied pair nearer than the last
                Spawn(host, player, ring, 0f, beast);
                Spawn(host, player, -ring, 0f, beast);
                Spawn(host, player, 0f, ring, beast);
                Spawn(host, player, 0f, -ring, beast);
                Spawn(host, player, 1f, 1f, Template(rank: 1));          // elite: never
                Spawn(host, player, 2f, 2f, Template(level: 9));         // above the hunter: never
                Spawn(host, player, 2f, -2f, Template(type: 7, flags: 0)); // not a beast: never
                // Same XY distance as the ring but 6 yd up/down: the 3D distance decides, so these lose to (or tie by guid with) the ring.
                Spawn(host, player, ring, 0f, beast, dz: 6f);
                Spawn(host, player, -ring, 0f, beast, dz: -6f);
                Spawn(host, player, 3f, 0f, beast, dz: ring + 1f);  // 2D-near, 3D-far: beyond reach in 3D
                for (int i = 0; i < 25; i++)
                    Spawn(host, player, random.Next(-60, 61), random.Next(-60, 61), Template());
                for (int i = 0; i < 10; i++)
                    Spawn(host, player, random.Next(-500, 501), random.Next(-500, 501), Template());
                return (FullScan(player, map), PlayerbotCombatView.ScanForTameCandidate(player, map));
            });
            Assert.NotNull(full);
            Assert.Same(full, grid);
        }
    }

    /// <summary>
    /// Pins the work the scan hands to its filter and the locality of the grid read it relies on. The counter is taken after the 2D radius filter of
    /// <see cref="CreatureMapSystem.CollectCreaturesNear"/>, so it cannot tell a grid read from a full scan by itself (that method falls back to one
    /// for an oversized query); the cell read is therefore checked directly on the same grid with the same circle.
    /// </summary>
    [Fact]
    public async Task TameScan_FiltersOnlyCreaturesWithinReach_AndTheGridCellsAroundTheBotHoldFew()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession bot = await CombatTestSessions.EnterAsync(host, "TAMEB", "Tameb");
        (long examined, int onMap, int inCells) = await host.OnWorldAsync(() =>
        {
            Player player = bot.Player!;
            for (int i = 0; i < 5; i++)
                Spawn(host, player, 5 + i, 0, Template());
            for (int i = 0; i < 120; i++)
                Spawn(host, player, 150 + (i * 3), 150 + (i % 7), Template());
            long before = PlayerbotCombatView.TameCreaturesExamined;
            PlayerbotCombatView.ScanForTameCandidate(player, player.Map!);
            int total = player.Map!.FindUpdater<CreatureMapSystem>()!.Creatures.Count;
            var cells = new List<WorldObject>();
            player.Map.Grids.CollectObjects(player.X, player.Y, 28f + 10f + player.Map.Grids.MaxBoundingRadius, cells);
            return (PlayerbotCombatView.TameCreaturesExamined - before, total, cells.Count);
        });

        Assert.True(onMap >= 125, $"the map holds {onMap} creatures");
        Assert.True(examined >= 5, $"examined {examined}");
        Assert.True(examined < 40, $"the scan filtered {examined} of {onMap} creatures");
        Assert.True(inCells < 40, $"the grid cells around the bot hold {inCells} objects; a full scan would read {onMap}");
    }

    [Fact]
    public void TameScanClocks_ShrinkAfterLogout_AndAgeOut()
    {
        const ulong a = 0x7E57_0001UL, b = 0x7E57_0002UL, c = 0x7E57_0003UL;
        PlayerbotCombatView.NoteTameScanForTest(a, 1_000);
        PlayerbotCombatView.NoteTameScanForTest(b, 1_000);
        PlayerbotCombatView.NoteTameScanForTest(c, 9_000);
        Assert.True(PlayerbotCombatView.HasTameScan(a) && PlayerbotCombatView.HasTameScan(b) && PlayerbotCombatView.HasTameScan(c));

        PlayerbotCombatView.ForgetTameScan(a);
        Assert.False(PlayerbotCombatView.HasTameScan(a));

        // b logged out without a Forget: its clock is older than the interval, so a sweep drops it; c, still fresh, stays.
        PlayerbotCombatView.SweepTameScans(10_000, force: true);
        Assert.False(PlayerbotCombatView.HasTameScan(b));
        Assert.True(PlayerbotCombatView.HasTameScan(c));
        PlayerbotCombatView.ForgetTameScan(c);
        Assert.False(PlayerbotCombatView.HasTameScan(c));
    }

    // ---- dock cache -------------------------------------------------------------------------------------------------------------

    /// <summary>Dry mesh everywhere farther than <c>wetRadius</c> from the port; can be switched to answer "tile pending".</summary>
    private sealed class DockMesh(Vector3 port, float wetRadius) : IPathfinder, IMeshPointQuery
    {
        public bool Pending { get; set; }
        public int Queries { get; private set; }
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            Queries++;
            if (Pending)
                return new PathResult(PathType.TilesPending, [start]);
            if (Vector2.Distance(new Vector2(end.X, end.Y), new Vector2(port.X, port.Y)) < wetRadius)
                return PathResult.None(start);
            return PathResult.StraightLine(start, end with { Z = port.Z }, PathType.Normal);
        }

        public bool TryGetRandomPointAround(uint mapId, Vector3 center, float radius, Random random, PathOptions? options, out Vector3 point)
        {
            point = center;
            return false;
        }

        public MeshRaycastResult Raycast(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => new(true, false, 1f, end with { Z = port.Z });
    }

    private sealed class Floor(float z) : ILineOfSight
    {
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;
        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit) { hit = to; return false; }
        public float? GetModelHeight(uint mapId, float x, float y, float zSearch, float maxSearchDistance) => z;
        public bool TryGetAreaInfo(uint mapId, float x, float y, float zSearch, out ModelAreaInfo info) { info = default; return false; }
    }

    [Fact]
    public async Task DockCache_ReturnsTheUncachedAnswers_AndComputesOncePerPort()
    {
        var mesh = new DockMesh(ShipTestWorld.PortOnMap0.Position, wetRadius: 10f);
        await using ShipTestWorld world = await ShipTestWorld.StartAsync();
        await world.OnWorldAsync(() => WorldCollision.Of(world.Host.World).Install(lineOfSight: new Floor(ShipTestWorld.PortOnMap0.Position.Z), pathfinder: mesh));
        Vector3 port = ShipTestWorld.PortOnMap0.Position;
        Vector3[] asks = [port + new Vector3(-40f, 0f, 0f), port + new Vector3(40f, 3f, 0f), port + new Vector3(0f, 55f, 0f), port + new Vector3(2f, -2f, 0f), port];

        (Vector3[] uncached, Vector3[] cached, long computes, int queriesAfterWarm) = await world.OnWorldAsync(() =>
        {
            Map map = world.Player.Map!;
            var plain = new List<Vector3>();
            foreach (Vector3 toward in asks)
            {
                PlayerbotShip.InvalidateDockCache();
                plain.Add(PlayerbotShip.ResolveDockTarget(map, port, toward));
            }

            PlayerbotShip.InvalidateDockCache();
            long start = PlayerbotShip.DockComputes;
            var hot = new List<Vector3>();
            PlayerbotShip.ResolveDockTarget(map, port, asks[0]); // the one computation
            int warmQueries = mesh.Queries;
            hot.Clear();
            foreach (Vector3 toward in asks)
                hot.Add(PlayerbotShip.ResolveDockTarget(map, port, toward));
            return (plain.ToArray(), hot.ToArray(), PlayerbotShip.DockComputes - start, mesh.Queries - warmQueries);
        });

        Assert.Equal(uncached, cached);
        Assert.NotEqual(port, cached[0]); // a dry candidate was found, not the wet keyframe
        Assert.Equal(1, computes);
        Assert.Equal(0, queriesAfterWarm); // no mesh query after the first answer: the other asks were cache hits
    }

    [Fact]
    public async Task DockCache_DoesNotStoreAPendingAnswer_AndInvalidatesOnDemandAndOnANewMesh()
    {
        var mesh = new DockMesh(ShipTestWorld.PortOnMap0.Position, wetRadius: 10f) { Pending = true };
        await using ShipTestWorld world = await ShipTestWorld.StartAsync();
        Vector3 port = ShipTestWorld.PortOnMap0.Position;
        Vector3 toward = port + new Vector3(-40f, 0f, 0f);
        await world.OnWorldAsync(() => WorldCollision.Of(world.Host.World).Install(lineOfSight: new Floor(port.Z), pathfinder: mesh));

        long pendingComputes = await world.OnWorldAsync(() =>
        {
            PlayerbotShip.InvalidateDockCache();
            long start = PlayerbotShip.DockComputes;
            Assert.Equal(port, PlayerbotShip.ResolveDockTarget(world.Player.Map!, port, toward)); // nothing known dry yet
            Assert.Equal(port, PlayerbotShip.ResolveDockTarget(world.Player.Map!, port, toward));
            return PlayerbotShip.DockComputes - start;
        });
        Assert.Equal(2, pendingComputes); // a pending answer is asked again, not remembered

        mesh.Pending = false;
        (Vector3 resolved, long settled, bool changedAfterInvalidate) = await world.OnWorldAsync(() =>
        {
            Map map = world.Player.Map!;
            long start = PlayerbotShip.DockComputes;
            Vector3 first = PlayerbotShip.ResolveDockTarget(map, port, toward);
            Vector3 second = PlayerbotShip.ResolveDockTarget(map, port, toward);
            Assert.Equal(first, second);
            long computes = PlayerbotShip.DockComputes - start;
            PlayerbotShip.InvalidateDockCache();
            PlayerbotShip.ResolveDockTarget(map, port, toward);
            return (first, computes, PlayerbotShip.DockComputes - start == 2);
        });
        Assert.NotEqual(port, resolved);
        Assert.Equal(1, settled);
        Assert.True(changedAfterInvalidate, "an invalidated cache computes again");

        // A new mesh (a re-install) starts with an empty cache: it never answers for the old one.
        var drier = new DockMesh(port, wetRadius: 20f);
        await world.OnWorldAsync(() => WorldCollision.Of(world.Host.World).Install(lineOfSight: new Floor(port.Z), pathfinder: drier));
        Vector3 other = await world.OnWorldAsync(() => PlayerbotShip.ResolveDockTarget(world.Player.Map!, port, toward));
        Assert.True(Vector2.Distance(new Vector2(other.X, other.Y), new Vector2(port.X, port.Y)) >= 20f, $"resolved {other} against the wider wet area");
    }

    [Fact]
    public async Task DockCache_DoesNotStoreAnAnswerTheProbeLayerCutShort()
    {
        var mesh = new DockMesh(ShipTestWorld.PortOnMap0.Position, wetRadius: 10f);
        await using ShipTestWorld world = await ShipTestWorld.StartAsync();
        Vector3 port = ShipTestWorld.PortOnMap0.Position;
        Vector3 toward = port + new Vector3(-40f, 0f, 0f);
        await world.OnWorldAsync(() => WorldCollision.Of(world.Host.World).Install(lineOfSight: new Floor(port.Z), pathfinder: mesh));

        (long cutComputes, Vector3 cutAnswer, long afterComputes, Vector3 fullAnswer, long hitComputes) = await world.OnWorldAsync(() =>
        {
            Map map = world.Player.Map!;
            PlayerbotShip.InvalidateDockCache();
            // A fallback query with less than MinQueryNodes left is refused without a search: the dock probes answer "no path", which is not "no dock".
            var probe = new PlayerbotThinkProbe { NodeBudget = 10 };
            long start = PlayerbotShip.DockComputes;
            Vector3 cut;
            probe.Begin();
            try
            {
                using (PlayerbotThinkProbe.Fallback())
                    cut = PlayerbotShip.ResolveDockTarget(map, port, toward);
            }
            finally
            {
                probe.End();
            }

            long cutN = PlayerbotShip.DockComputes - start;
            Assert.True(probe.Refused > 0, "the probe refused the dock queries");
            long mark = PlayerbotShip.DockComputes;
            Vector3 full = PlayerbotShip.ResolveDockTarget(map, port, toward); // outside the budget: computed in full, because the cut answer was not stored
            long afterN = PlayerbotShip.DockComputes - mark;
            long hitMark = PlayerbotShip.DockComputes;
            PlayerbotShip.ResolveDockTarget(map, port, toward);
            return (cutN, cut, afterN, full, PlayerbotShip.DockComputes - hitMark);
        });

        Assert.Equal(1, cutComputes);
        Assert.Equal(port, cutAnswer);          // nothing known dry under the refused budget
        Assert.Equal(1, afterComputes);         // the cut answer was not remembered
        Assert.NotEqual(port, fullAnswer);      // the full computation finds the dry pier
        Assert.Equal(0, hitComputes);           // and that one is
    }
}
