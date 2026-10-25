using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.GridTerrain;

/// <summary>
/// World:Parallel:CreatureSplinePass (docs/research/multithreading-plan-20261017.md, W3): the creature walk in a spline pass and a
/// gated rest. With CreatureMapSystem ungated the spline pass of every map runs on the map threads at once; the same seeded creature
/// ticks still give identical positions and packet streams at 1 and N threads.
/// </summary>
public sealed class CreatureSplinePassTests
{
    private static readonly (uint Map, uint Instance)[] Maps = [(0, 0), (1, 0), (36, 2), (36, 3)];

    private static WorldRuntime CreateRuntime(int threads, string ungated, bool splinePass)
        => new(
            new WorldRuntimeOptions
            {
                UpdateCompressionThreshold = 0,
                AutosaveIntervalMs = 0,
                CommandTimeBudgetMs = 0,
                Parallel = new ParallelMapOptions
                {
                    MapUpdateThreads = threads, OwnerGuard = OwnerGuardMode.Throw, Ungated = ungated, CreatureSplinePass = splinePass,
                },
            },
            new RecordingSaveQueue(),
            NullLogger<WorldRuntime>.Instance);

    private static CreatureContent SeededContent()
    {
        var spawns = new List<CreatureSpawn>();
        uint guid = 1;
        foreach (uint mapId in Maps.Select(m => m.Map).Distinct())
        {
            var random = new Random((int)mapId * 31 + 7);
            for (int i = 0; i < 80; i++)
            {
                // Over about three cells each way (66.7 yd cells) so wanderers cross cell borders; half wander, a few stand.
                float x = -100 + random.NextSingle() * 200;
                float y = -100 + random.NextSingle() * 200;
                byte movement = (byte)(i % 5 == 0 ? 0 : 1);
                spawns.Add(Spawn(guid++, WolfEntry, x, y, mapId: mapId, movementType: movement, wander: 8 + random.NextSingle() * 12));
            }
        }

        return Content([Template()], spawns);
    }

    [Fact]
    public void SameSeededCreatureTicks_GiveIdenticalResults_At1AndNThreads_WithTheSplinePassUngated()
    {
        (string serial, int moved, long packets) = RunScripted(1, ungated: "", splinePass: true);
        Assert.True(moved > 20, $"only {moved} creatures moved: the run does not exercise the spline pass");
        Assert.True(packets > 100, $"only {packets} packets: the run does not exercise the observers");
        Assert.Equal(serial, RunScripted(1, ungated: "", splinePass: true).Digest);
        Assert.Equal(serial, RunScripted(4, ungated: "", splinePass: true).Digest);
        Assert.Equal(serial, RunScripted(4, ungated: "CreatureMapSystem", splinePass: true).Digest);
        Assert.Equal(serial, RunScripted(3, ungated: "CreatureMapSystem,Output,Heartbeat,Grid", splinePass: true).Digest);
    }

    [Fact]
    public void WithoutTheSplinePass_UngatingCreatureMapSystem_StillGivesTheSerialResults()
    {
        string serial = RunScripted(1, ungated: "", splinePass: false).Digest;
        Assert.Equal(serial, RunScripted(4, ungated: "CreatureMapSystem", splinePass: false).Digest);
    }

    [Fact]
    public void ThePathTimeBudget_FeedsTheDigest_WhichIsWhyTheDeterminismFactsUseACountOnlyBudget()
    {
        // A zero wall budget defers every wanderer after the first drape; a count-only one never does. The digests differ, so a budget with
        // a real wall limit makes the digest depend on how loaded the host is.
        string timed = RunScripted(1, ungated: "", splinePass: true, new CreaturePathBudget(64, 16384, MaxMilliseconds: 0)).Digest;
        string countOnly = RunScripted(1, ungated: "", splinePass: true).Digest;
        Assert.NotEqual(countOnly, timed);
    }

    [Fact]
    public void TheSlowLineBreakdown_NamesTheSplinePass_WhenItRuns()
    {
        using WorldRuntime world = CreateRuntime(1, "", splinePass: true);
        Map map = world.GetMap(0);
        var system = new CreatureMapSystem(map, SeededContent(), random: new Random(1));
        map.AddUpdater(system);
        map.AddPlayer(TestWorld.CreatePlayer(5000, 0, 0, new FakeSession(5000)));
        for (int i = 0; i < 40; i++) world.RunTick(50);

        Assert.Contains(", spline pass ", ((IMapUpdateBreakdown)system).DescribeLastUpdate());
    }

    [Fact]
    public void WithCreatureMapSystemUngated_PlainWanderersMoveOutsideTheGate_AndTheirPacketsLeaveTheOutbox()
    {
        using WorldRuntime world = CreateRuntime(4, "CreatureMapSystem", splinePass: true);
        world.UseManualClockFrom(0);
        CreatureContent content = SeededContent();
        var systems = new List<(Map Map, CreatureMapSystem System)>();
        foreach ((uint mapId, uint instanceId) in Maps)
        {
            Map map = world.GetMap(mapId, instanceId);
            var system = new CreatureMapSystem(map, content, random: new Random((int)(mapId * 7919 + instanceId)));
            map.AddUpdater(system);
            map.AddPlayer(TestWorld.CreatePlayer(20_000 + mapId * 10 + instanceId, 0, 0, new FakeSession((int)(20_000 + mapId * 10 + instanceId)), mapId));
            systems.Add((map, system));
        }

        bool sawUngated = false;
        for (int tick = 0; tick < 60; tick++)
        {
            world.RunTick(50);
            foreach ((Map map, CreatureMapSystem system) in systems)
            {
                Assert.Equal(0, map.OutboxCount); // every held-back packet went out within the map's own update
                sawUngated |= ((IMapUpdateBreakdown)system).DescribeLastUpdate().Contains(" wanderers outside the gate", StringComparison.Ordinal);
            }
        }

        Assert.True(sawUngated, "no wanderer ran outside the gate");
        Assert.Equal(0, world.ParallelCounters.OwnerViolations);
    }

    private static (string Digest, int Moved, long Packets) RunScripted(int threads, string ungated, bool splinePass, CreaturePathBudget? budget = null)
    {
        using WorldRuntime world = CreateRuntime(threads, ungated, splinePass);
        world.UseManualClockFrom(0); // a fixed origin: the wall-clock reading so far would shift every timestamp of the run
        CreatureContent content = SeededContent();
        var sessions = new List<FakeSession>();
        var systems = new List<CreatureMapSystem>();
        uint playerGuid = 10_000;
        foreach ((uint mapId, uint instanceId) in Maps)
        {
            Map map = world.GetMap(mapId, instanceId);
            var system = new CreatureMapSystem(map, content, random: new Random((int)(mapId * 7919 + instanceId)))
            {
                // Count-only: the default 12 ms wall budget makes the digest load-dependent (a descheduled thread defers a wanderer, which draws URand).
                PathBudget = budget ?? new CreaturePathBudget(MaxSearches: 64, MaxNodes: 16384),
            };
            map.AddUpdater(system);
            systems.Add(system);
            for (int i = 0; i < 3; i++)
            {
                var session = new FakeSession((int)playerGuid);
                Player player = TestWorld.CreatePlayer(playerGuid++, -60 + i * 60, -40 + i * 40, session, mapId);
                map.AddPlayer(player);
                sessions.Add(session);
            }
        }

        var spawnedAt = systems.SelectMany(s => s.Creatures).ToDictionary(c => (c.Map!.InstanceId, c.Guid), c => (c.X, c.Y));
        var digest = new System.Text.StringBuilder();
        long packets = 0;
        for (int tick = 0; tick < 200; tick++)
        {
            world.RunTick(50);
            ulong hash = 14695981039346656037UL;
            foreach (FakeSession session in sessions)
            {
                while (session.Sent.TryDequeue(out (WorldOpcode Opcode, byte[] Payload) packet))
                {
                    packets++;
                    hash = Fnv(hash, BitConverter.GetBytes((ushort)packet.Opcode));
                    hash = Fnv(hash, packet.Payload);
                }

                hash = Fnv(hash, [0xFF]);
            }

            digest.Append(hash.ToString("X16")).Append('\n');
        }

        int moved = 0;
        foreach (CreatureMapSystem system in systems)
        {
            foreach (Creature creature in system.Creatures)
            {
                digest.Append(FormattableString.Invariant($"{system.Map.MapId}/{system.Map.InstanceId}/{creature.Guid.Value:X}:{creature.X:R},{creature.Y:R},{creature.Z:R};"));
                if (spawnedAt.TryGetValue((system.Map.InstanceId, creature.Guid), out (float X, float Y) at) && (at.X != creature.X || at.Y != creature.Y))
                {
                    moved++;
                }
            }
        }

        Assert.Equal(0, world.ParallelCounters.OwnerViolations);
        return (digest.ToString(), moved, packets);
    }

    private static ulong Fnv(ulong hash, ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
        {
            hash = (hash ^ b) * 1099511628211UL;
        }

        return hash;
    }
}
