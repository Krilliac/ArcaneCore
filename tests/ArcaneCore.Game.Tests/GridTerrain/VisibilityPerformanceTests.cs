using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Game.Tests.GridTerrain;

/// <summary>
/// Benchmark-style reproduction of the live "Slow map update ... visibility 42-58 ms; players 4, moved ~670" warning:
/// ~66k spawns on map 0 (clustered around hubs, like real towns and camps), four moving players and ~670 creatures
/// wandering near them every tick. Each tick the incremental visibility result is checked against a brute-force
/// oracle (who sees whom must not change), the visibility work is counted, and the phase timing is reported.
/// </summary>
public sealed class VisibilityPerformanceTests(ITestOutputHelper output)
{
    private const int SpawnCount = 66_000;
    private const int HubCount = 60;
    private const int MoverCount = 670;
    private const int WarmupTicks = 40;
    private const int MeasuredTicks = 60;
    private const uint TickMs = 100;
    private const int MovingPlayers = 2;

    /// <summary>A session that drops everything (the benchmark sends hundreds of thousands of blocks).</summary>
    private sealed class SilentSession(int accountId) : IPlayerSession
    {
        public int AccountId { get; } = accountId;

        public AccountSecurity Security => AccountSecurity.Player;

        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload)
        {
        }

        public void ProcessWorldPackets(Player player)
        {
        }

        public void Kick()
        {
        }

        public void OnLoggedOut()
        {
        }
    }

    [Fact]
    public void Map0WithSixtySixThousandSpawns_VisibilityMatchesBruteForce_AndOnlyVisitsNearbyPlayersForMovers()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var random = new Random(5875);

        // Hubs over an Eastern-Kingdoms-sized rectangle; 70% of the spawns cluster around them.
        var hubs = new (float X, float Y)[HubCount];
        for (int i = 0; i < HubCount; i++)
        {
            hubs[i] = (Uniform(random, -9500, 2500), Uniform(random, -3500, 1000));
        }

        var spawns = new List<WorldObject>(SpawnCount);
        for (uint i = 0; i < SpawnCount; i++)
        {
            float x, y;
            if (i % 10 < 7)
            {
                (float hx, float hy) = hubs[i % HubCount];
                x = hx + (Gaussian(random) * 120f);
                y = hy + (Gaussian(random) * 120f);
            }
            else
            {
                x = Uniform(random, -9500, 2500);
                y = Uniform(random, -3500, 1000);
            }

            var unit = new TestUnit(i + 1, x, y);
            map.AddObject(unit);
            spawns.Add(unit);
        }

        // Four players: two share a hub (they see each other) and walk in circles; two stand still elsewhere, so the
        // creatures wandering into and out of their sight are found only by the movers' visibility pass.
        (float X, float Y)[] anchors = [hubs[0], (hubs[0].X + 25, hubs[0].Y), hubs[1], hubs[2]];
        var players = new Player[anchors.Length];
        for (int i = 0; i < anchors.Length; i++)
        {
            players[i] = TestWorld.CreatePlayer((uint)(i + 1), anchors[i].X, anchors[i].Y, new SilentSession(i + 1));
            world.AddPlayer(players[i]);
        }

        // The wanderers (live, only the loaded grids around players hold creatures): half are the spawns nearest to the
        // edge of a player's sight, walking straight in or out across it; the rest are the spawns nearest to the
        // players, wandering at random.
        (WorldObject Spawn, int Player, float Distance)[] near = [.. spawns
            .Select(o => Enumerable.Range(0, players.Length)
                .Select(i => (Spawn: o, Player: i, Distance: MathF.Sqrt(DistanceSq(o, players[i]))))
                .MinBy(n => n.Distance))];
        var edgeWalkers = near
            .Where(n => n.Player >= MovingPlayers)
            .OrderBy(n => MathF.Abs(n.Distance - Map.VisibilityRange))
            .Take(MoverCount / 2)
            .ToArray();
        var walkerSet = new HashSet<WorldObject>(edgeWalkers.Select(n => n.Spawn), ReferenceEqualityComparer.Instance);
        WorldObject[] wanderers = [.. near
            .Where(n => !walkerSet.Contains(n.Spawn))
            .OrderBy(n => n.Distance)
            .Take(MoverCount - edgeWalkers.Length)
            .Select(n => n.Spawn)];

        var previous = players.ToDictionary(p => p, p => new HashSet<ObjectGuid>(p.VisibleObjects));
        var visibilityMicros = new List<long>();
        var objectCandidates = new List<long>();
        var playerCandidates = new List<long>();
        var visibleChanges = new int[players.Length];

        for (int tick = 0; tick < WarmupTicks + MeasuredTicks; tick++)
        {
            float angle = tick * 0.1f;
            for (int i = 0; i < MovingPlayers; i++)
            {
                players[i].SetPosition(anchors[i].X + (MathF.Cos(angle) * 30f), anchors[i].Y + (MathF.Sin(angle) * 30f), 0, angle);
            }

            for (int i = 0; i < edgeWalkers.Length; i++)
            {
                // 0.5 yards a tick (5 yd/s), every other walker inwards; 25 yards each way, then back.
                (WorldObject walker, int index, _) = edgeWalkers[i];
                Player stationary = players[index];
                float dx = walker.X - stationary.X;
                float dy = walker.Y - stationary.Y;
                float length = MathF.Max(MathF.Sqrt((dx * dx) + (dy * dy)), 0.01f);
                float step = ((i % 2 == 0) ^ ((tick / 50) % 2 == 1) ? 0.5f : -0.5f) / length;
                walker.SetPosition(walker.X + (dx * step), walker.Y + (dy * step), 0, 0);
            }

            foreach (WorldObject wanderer in wanderers)
            {
                wanderer.SetPosition(wanderer.X + Uniform(random, -0.6f, 0.6f), wanderer.Y + Uniform(random, -0.6f, 0.6f), 0, 0);
            }

            long objectBefore = map.ObjectVisibilityCandidates;
            long playerBefore = map.PlayerVisibilityCandidates;
            var diagnostics = new MapUpdateDiagnostics();
            map.Update(TickMs, diagnostics);

            AssertMatchesBruteForce(map, players, spawns, previous, visibleChanges);

            if (tick >= WarmupTicks)
            {
                visibilityMicros.Add(diagnostics.VisibilityMicros);
                objectCandidates.Add(map.ObjectVisibilityCandidates - objectBefore);
                playerCandidates.Add(map.PlayerVisibilityCandidates - playerBefore);
            }
        }

        visibilityMicros.Sort();
        output.WriteLine(
            $"visibility phase over {MeasuredTicks} ticks: median {visibilityMicros[MeasuredTicks / 2] / 1000.0:F2} ms, " +
            $"p95 {visibilityMicros[(MeasuredTicks * 95) / 100] / 1000.0:F2} ms, max {visibilityMicros[^1] / 1000.0:F2} ms; candidates per tick: movers {objectCandidates.Average():F0}, " +
            $"players {playerCandidates.Average():F0}; visible-set changes {string.Join("/", visibleChanges)}; " +
            $"visible objects {string.Join("/", players.Select(p => p.VisibleObjects.Count))}");

        // The scenario must exercise visibility: players see the hub and each other, and creatures cross the edge of
        // every player's sight — for the players standing still, only the movers' pass can notice.
        Assert.All(players, p => Assert.True(p.VisibleObjects.Count > 100, $"{p.Name} sees {p.VisibleObjects.Count}"));
        Assert.Contains(players[1].Guid, players[0].VisibleObjects);
        Assert.All(visibleChanges, n => Assert.True(n > 10, $"a visible set changed only {n} times"));

        // A non-player object's visibility is only about players (vmangos Map::UpdateObjectVisibility visits the
        // world-object container: players' cameras). At most every player in the map is a candidate per mover.
        Assert.All(objectCandidates, n => Assert.True(
            n <= (long)MoverCount * players.Length,
            $"movers looked at {n} candidates in one tick; the bound is {MoverCount * players.Length}"));
    }

    /// <summary>
    /// The oracle: for every player, the visible set must equal a full scan of the map with the grey distance applied
    /// to what it saw last tick (<see cref="Map.IsWithinVisibilityDistance"/>), and the observer index must agree.
    /// </summary>
    private static void AssertMatchesBruteForce(
        Map map, Player[] players, List<WorldObject> spawns, Dictionary<Player, HashSet<ObjectGuid>> previous, int[] changes)
    {
        for (int index = 0; index < players.Length; index++)
        {
            Player viewer = players[index];
            HashSet<ObjectGuid> before = previous[viewer];
            var expected = new HashSet<ObjectGuid>();
            foreach (WorldObject target in spawns)
            {
                if (Map.IsWithinVisibilityDistance(viewer, target, before.Contains(target.Guid)))
                {
                    expected.Add(target.Guid);
                }
            }

            foreach (Player other in players)
            {
                if (!ReferenceEquals(other, viewer) && Map.IsWithinVisibilityDistance(viewer, other, before.Contains(other.Guid)))
                {
                    expected.Add(other.Guid);
                }
            }

            if (!expected.SetEquals(viewer.VisibleObjects))
            {
                string missing = string.Join(",", expected.Except(viewer.VisibleObjects).Take(5));
                string extra = string.Join(",", viewer.VisibleObjects.Except(expected).Take(5));
                Assert.Fail($"{viewer.Name}: visible set differs from the full scan (missing {missing}; extra {extra})");
            }

            foreach (ObjectGuid guid in viewer.VisibleObjects)
            {
                WorldObject target = map.FindObject(guid)!;
                Assert.Contains(viewer, map.ObserversOf(target));
            }

            if (!before.SetEquals(expected))
            {
                changes[index]++;
            }

            previous[viewer] = expected;
        }
    }

    private static float DistanceSq(WorldObject a, WorldObject b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return (dx * dx) + (dy * dy);
    }

    private static float Uniform(Random random, float min, float max) => min + ((float)random.NextDouble() * (max - min));

    private static float Gaussian(Random random)
    {
        double u1 = 1.0 - random.NextDouble();
        double u2 = random.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
    }
}
