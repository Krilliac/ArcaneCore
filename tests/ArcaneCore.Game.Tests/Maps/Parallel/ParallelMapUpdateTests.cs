using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Game.Tests.GridTerrain;

/// <summary>
/// The parallel maps phase (World:Parallel:MapUpdateThreads; docs/research/multithreading-plan-20261017.md, W2/W3): every map is
/// updated once per tick by one thread, ungated (map-local) work really runs concurrently, gated work never does, maps made during the
/// phase join the next tick, cross-map changes are caught by the ownership guard, and the same seeded ticks give identical state and
/// identical packet streams at 1 and N threads.
/// </summary>
public sealed class ParallelMapUpdateTests
{
    private static WorldRuntime CreateRuntime(int threads, OwnerGuardMode guard = OwnerGuardMode.Throw, bool gateAll = false, string ungated = "")
        => new(
            new WorldRuntimeOptions
            {
                UpdateCompressionThreshold = 0,
                AutosaveIntervalMs = 0,
                CommandTimeBudgetMs = 0,
                Parallel = new ParallelMapOptions { MapUpdateThreads = threads, OwnerGuard = guard, GateAllStages = gateAll, Ungated = ungated },
            },
            new RecordingSaveQueue(),
            NullLogger<WorldRuntime>.Instance);

    [Fact]
    public void OneThread_NeverEntersTheParallelPhase()
    {
        using WorldRuntime world = CreateRuntime(1);
        var probe = new ProbeUpdater(world);
        world.GetMap(0).AddUpdater(probe);
        world.GetMap(1).AddUpdater(new ProbeUpdater(world));
        for (int i = 0; i < 5; i++) world.RunTick(50);
        Assert.Equal(5, probe.Updates);
        Assert.False(probe.SawParallelPhase);
        Assert.Equal(1, world.MapUpdateThreads);
        Assert.Equal(0, world.ParallelCounters.ParallelTicks);
    }

    [Fact]
    public void EveryMapIsUpdatedOncePerTick_ByTheThreadThatOwnsIt()
    {
        using WorldRuntime world = CreateRuntime(4);
        var probes = new List<ProbeUpdater>();
        foreach ((uint map, uint instance) in new[] { (0u, 0u), (1u, 0u), (33u, 1u), (36u, 2u), (489u, 3u) })
        {
            var probe = new ProbeUpdater(world);
            world.GetMap(map, instance).AddUpdater(probe);
            probes.Add(probe);
        }

        for (int i = 0; i < 20; i++) world.RunTick(50);
        Assert.All(probes, p =>
        {
            Assert.Equal(20, p.Updates);
            Assert.True(p.SawParallelPhase);
            Assert.True(p.OwnedItsMap, "TickOwner.CurrentMap was the map being updated and IsOwner held");
        });
        Assert.Equal(4, world.MapUpdateThreads);
        Assert.Equal(20, world.ParallelCounters.ParallelTicks);
        Assert.False(world.IsParallelPhase);
    }

    [Fact]
    public void MapLocalUpdaters_RunConcurrently()
    {
        using WorldRuntime world = CreateRuntime(2);
        using var meet = new Barrier(2);
        var a = new MeetingUpdater(meet);
        var b = new MeetingUpdater(meet);
        world.GetMap(0).AddUpdater(a);
        world.GetMap(1).AddUpdater(b);
        world.RunTick(50);
        Assert.True(a.Met && b.Met, "both map-local updaters were inside their update at the same time");
    }

    [Fact]
    public void GatedUpdaters_NeverOverlap_AndGateAllStagesGatesMapLocalOnesToo()
    {
        foreach (bool gateAll in new[] { false, true })
        {
            using WorldRuntime world = CreateRuntime(4, gateAll: gateAll);
            var shared = new SharedCounter();
            for (uint map = 0; map < 6; map++)
            {
                world.GetMap(map, map).AddUpdater(gateAll ? new LocalOverlapUpdater(shared) : new GlobalOverlapUpdater(shared));
            }

            for (int i = 0; i < 30; i++) world.RunTick(50);
            Assert.Equal(1, shared.MaxConcurrent);
            Assert.Equal(6 * 30, shared.Total);
            Assert.True(world.ParallelCounters.GateEntries > 0);
        }
    }

    [Fact]
    public void ConfigUngated_LetsAnUnclassifiedUpdaterRunConcurrently()
    {
        using WorldRuntime world = CreateRuntime(2, ungated: nameof(UnclassifiedMeetingUpdater));
        using var meet = new Barrier(2);
        var a = new UnclassifiedMeetingUpdater(meet);
        var b = new UnclassifiedMeetingUpdater(meet);
        world.GetMap(0).AddUpdater(a);
        world.GetMap(1).AddUpdater(b);
        world.RunTick(50);
        Assert.True(a.Met && b.Met);
    }

    [Fact]
    public void AMapMadeDuringThePhase_IsRegisteredAndJoinsTheNextTick()
    {
        using WorldRuntime world = CreateRuntime(3);
        Map? made = null;
        var maker = new ActionUpdater((map, tick) =>
        {
            if (tick == 1)
            {
                made = map.World.GetMap(229, 7); // an instance asked for from inside a map update (a portal, a summon)
                Assert.Same(made, map.World.GetMap(229, 7));
                Assert.Same(made, map.World.FindMap(229, 7));
            }
        });
        world.GetMap(0).AddUpdater(maker);
        world.GetMap(1).AddUpdater(new ProbeUpdater(world));
        world.RunTick(50);
        Assert.NotNull(made);
        var probe = new ProbeUpdater(world);
        made!.AddUpdater(probe);
        world.RunTick(50);
        world.RunTick(50);
        Assert.Equal(2, probe.Updates);
        Assert.Contains(made, world.Maps);
    }

    [Fact]
    public void ChangingAnotherMapDuringThePhase_IsAnOwnershipViolation()
    {
        using WorldRuntime world = CreateRuntime(2, guard: OwnerGuardMode.Throw);
        Map other = world.GetMap(1);
        Exception? caught = null;
        world.GetMap(0).AddUpdater(new ActionUpdater((map, tick) =>
        {
            try
            {
                other.AddObject(new TestUnit(77, 10, 10));
            }
            catch (InvalidOperationException ex)
            {
                caught = ex;
            }
        }));
        world.GetMap(1).AddUpdater(new ProbeUpdater(world));
        world.RunTick(50);
        Assert.NotNull(caught);
        Assert.True(world.ParallelCounters.OwnerViolations >= 1);
    }

    [Fact]
    public void ReportMode_CountsTheViolation_AndCarriesOn()
    {
        using WorldRuntime world = CreateRuntime(2, guard: OwnerGuardMode.Report);
        Map other = world.GetMap(1);
        bool returned = false;
        world.GetMap(0).AddUpdater(new ActionUpdater((map, tick) =>
        {
            world.CheckOwner(other, "test change"); // what a map mutator calls; Report must not throw
            returned = true;
        }));
        world.GetMap(1).AddUpdater(new ProbeUpdater(world));
        world.RunTick(50);
        Assert.True(returned);
        Assert.True(world.ParallelCounters.OwnerViolations >= 1);
    }

    [Fact]
    public async Task OnAStartedWorld_MapThreadsCountAsWorldThreads_AndUpdateMapsOffTheWorldThread()
    {
        using WorldRuntime world = CreateRuntime(3);
        world.UseManualClockFrom(0); // a fixed origin: the wall-clock reading so far would shift every timestamp of the run
        // Each map update takes 2 ms: with empty updates the world thread could take every map of a batch before a worker woke (seen
        // under a loaded test run once map updates held the serial gate across their gated stages).
        var probes = new[] { new ProbeUpdater(world) { WorkMs = 2 }, new ProbeUpdater(world) { WorkMs = 2 }, new ProbeUpdater(world) { WorkMs = 2 } };
        world.GetMap(0).AddUpdater(probes[0]);
        world.GetMap(1).AddUpdater(probes[1]);
        world.GetMap(30, 4).AddUpdater(probes[2]);
        world.Start();
        await world.AdvanceClockAsync(50 * 40);
        world.Stop();
        Assert.All(probes, p => Assert.True(p.AlwaysWorldThread, "IsWorldThread held inside every map update"));
        Assert.Contains(probes.SelectMany(p => p.ThreadNames), name => name is not null && name.StartsWith("map-worker", StringComparison.Ordinal));
    }

    [Fact]
    public void AMapUpdateThatThrows_IsContained_AndEveryOtherMapStillUpdatesEveryTick()
    {
        using WorldRuntime world = CreateRuntime(4);
        var probes = new List<ProbeUpdater>();
        for (uint i = 0; i < 6; i++)
        {
            Map map = world.GetMap(i == 0 ? 0u : 30, i);
            var probe = new ProbeUpdater(world);
            map.AddUpdater(probe);
            probes.Add(probe);
        }

        world.GetMap(30, 3).AddUpdater(new ActionUpdater((_, tick) =>
        {
            if (tick % 2 == 0) throw new InvalidOperationException("boom");
        }));

        for (int i = 0; i < 20; i++) world.RunTick(50);

        Assert.Equal(20, world.ParallelCounters.ParallelTicks);
        Assert.All(probes, p => Assert.Equal(20, p.Updates));
    }

    /// <summary>
    /// Churn: maps made and unloaded from inside map updates, every tick, for many ticks at 4 threads; every map that exists at a tick's
    /// start is updated exactly once, and nothing deadlocks or faults.
    /// </summary>
    [Fact]
    public void MapsMadeAndUnloadedFromMapUpdates_UnderChurn_KeepTheOncePerTickRule()
    {
        using WorldRuntime world = CreateRuntime(4);
        var counts = new System.Collections.Concurrent.ConcurrentDictionary<(uint, uint), int>();
        uint nextInstance = 100;
        void Attach(Map map)
        {
            map.AddUpdater(new ActionUpdater((m, tick) =>
            {
                counts.AddOrUpdate((m.MapId, m.InstanceId), 1, (_, c) => c + 1);
                if (m.InstanceId >= 100 && tick == 3)
                {
                    world.UnloadMap(m);
                }
            }));
        }

        for (uint i = 0; i < 4; i++) Attach(world.GetMap(i, 0));
        world.GetMap(0).AddUpdater(new ActionUpdater((_, _) =>
        {
            using WorldRuntime.SerialScope serial = world.EnterSerial();
            for (int k = 0; k < 2; k++) Attach(world.GetMap(33, nextInstance++));
        }));

        for (int tick = 0; tick < 60; tick++)
        {
            int before = world.Maps.Count();
            counts.Clear();
            world.RunTick(50);
            Assert.True(counts.Count >= 4, $"tick {tick}: only {counts.Count} maps updated");
            Assert.All(counts, entry => Assert.Equal(1, entry.Value));
            Assert.True(counts.Count <= before, $"tick {tick}: {counts.Count} updates for {before} maps");
        }

        Assert.Equal(0, world.ParallelCounters.OwnerViolations);
    }

    /// <summary>
    /// The determinism check (plan section 8.1): the same seeded world, ticked the same way, ends in identical positions and sends
    /// every session the identical byte stream at 1 thread and at 4 (gated and ungated), and with everything gated.
    /// </summary>
    [Fact]
    public void SameSeededTicks_GiveIdenticalStateAndPackets_At1AndNThreads()
    {
        string serial = RunScripted(1, gateAll: false, ungated: "");
        Assert.Equal(serial, RunScripted(1, gateAll: false, ungated: ""));
        Assert.Equal(serial, RunScripted(4, gateAll: false, ungated: ""));
        Assert.Equal(serial, RunScripted(4, gateAll: true, ungated: ""));
        Assert.Equal(serial, RunScripted(4, gateAll: false, ungated: "Output,Heartbeat,Grid"));
        Assert.Equal(serial, RunScripted(3, gateAll: false, ungated: "Output,Heartbeat,Grid,Packets,Deferred"));
    }

    private static string RunScripted(int threads, bool gateAll, string ungated)
    {
        using WorldRuntime world = CreateRuntime(threads, OwnerGuardMode.Throw, gateAll, ungated);
        world.UseManualClockFrom(0); // a fixed origin: the wall-clock reading so far would shift every timestamp of the run
        var sessions = new List<FakeSession>();
        var units = new List<WorldObject>();
        (uint Map, uint Instance)[] maps = [(0, 0), (1, 0), (33, 1), (36, 2), (209, 5)];
        uint guid = 1;
        foreach ((uint mapId, uint instanceId) in maps)
        {
            Map map = world.GetMap(mapId, instanceId);
            var random = new Random((int)(mapId * 7919 + instanceId));
            var movers = new List<WorldObject>();
            for (int i = 0; i < 40; i++)
            {
                var unit = new TestUnit(guid++, -50 + random.NextSingle() * 100, -50 + random.NextSingle() * 100);
                map.AddObject(unit);
                movers.Add(unit);
                units.Add(unit);
            }

            var players = new List<Player>();
            for (int i = 0; i < 3; i++)
            {
                var session = new FakeSession((int)guid);
                Player player = TestWorld.CreatePlayer(guid++, -30 + i * 30, 0, session, mapId);
                map.AddPlayer(player);
                players.Add(player);
                sessions.Add(session);
            }

            map.AddUpdater(new SeededMover(new Random((int)(mapId * 104729 + instanceId)), movers, players));
            map.AddUpdater(new SeededLocalMover(new Random((int)(mapId * 1299709 + instanceId)), movers));
        }

        var digest = new System.Text.StringBuilder();
        for (int tick = 0; tick < 120; tick++)
        {
            world.RunTick(50);
            ulong hash = 14695981039346656037UL;
            foreach (FakeSession session in sessions)
            {
                while (session.Sent.TryDequeue(out (WorldOpcode Opcode, byte[] Payload) packet))
                {
                    hash = Fnv(hash, BitConverter.GetBytes((ushort)packet.Opcode));
                    hash = Fnv(hash, packet.Payload);
                }

                hash = Fnv(hash, [0xFF]); // session boundary
            }

            digest.Append(hash.ToString("X16")).Append('\n');
        }

        foreach (WorldObject unit in units)
        {
            digest.Append(FormattableString.Invariant($"{unit.Guid.Value:X}:{unit.X:R},{unit.Y:R},{unit.Z:R};"));
        }

        return digest.ToString();
    }

    private static ulong Fnv(ulong hash, ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
        {
            hash = (hash ^ b) * 1099511628211UL;
        }

        return hash;
    }

    /// <summary>Unclassified (gated): moves units and players of its own map from a per-map seeded random.</summary>
    private sealed class SeededMover(Random random, List<WorldObject> movers, List<Player> players) : IMapUpdater
    {
        public void OnPlayerRemoved(Map map, Player player) { }

        public void Update(Map map, uint diffMs)
        {
            foreach (WorldObject mover in movers)
            {
                if (random.Next(4) == 0) mover.SetPosition(Math.Clamp(mover.X + (random.NextSingle() - .5f) * 30, -400, 400), Math.Clamp(mover.Y + (random.NextSingle() - .5f) * 30, -400, 400), mover.Z, random.NextSingle() * 6);
            }

            foreach (Player player in players)
            {
                player.SetPosition(Math.Clamp(player.X + (random.NextSingle() - .5f) * 40, -300, 300), Math.Clamp(player.Y + (random.NextSingle() - .5f) * 40, -300, 300), player.Z, 0);
            }
        }
    }

    /// <summary>Map-local (ungated): nudges its own map's units.</summary>
    [MapAccess(MapAccess.MapLocal, Reason = "test: touches only its own map's units")]
    private sealed class SeededLocalMover(Random random, List<WorldObject> movers) : IMapUpdater
    {
        public void OnPlayerRemoved(Map map, Player player) { }

        public void Update(Map map, uint diffMs)
        {
            WorldObject mover = movers[random.Next(movers.Count)];
            mover.SetPosition(mover.X + 1, mover.Y - 1, mover.Z, mover.Orientation);
        }
    }

    private sealed class ProbeUpdater(WorldRuntime world) : IMapUpdater
    {
        public int Updates;
        public bool SawParallelPhase;
        public bool OwnedItsMap = true;
        public bool AlwaysWorldThread = true;
        public readonly List<string?> ThreadNames = [];

        /// <summary>Time each update takes (ms): long enough that the caller thread cannot drain a batch before the workers wake.</summary>
        public int WorkMs;

        public void OnPlayerRemoved(Map map, Player player) { }

        public void Update(Map map, uint diffMs)
        {
            if (WorkMs > 0)
            {
                Thread.Sleep(WorkMs);
            }

            Updates++;
            SawParallelPhase |= world.IsParallelPhase;
            if (world.IsParallelPhase)
            {
                OwnedItsMap &= ReferenceEquals(TickOwner.CurrentMap, map) && world.IsOwner(map);
            }

            AlwaysWorldThread &= world.IsWorldThread;
            ThreadNames.Add(Thread.CurrentThread.Name);
        }
    }

    [MapAccess(MapAccess.MapLocal)]
    private sealed class MeetingUpdater(Barrier meet) : IMapUpdater
    {
        public bool Met;

        public void OnPlayerRemoved(Map map, Player player) { }

        public void Update(Map map, uint diffMs) => Met = meet.SignalAndWait(TimeSpan.FromSeconds(10));
    }

    private sealed class UnclassifiedMeetingUpdater(Barrier meet) : IMapUpdater
    {
        public bool Met;

        public void OnPlayerRemoved(Map map, Player player) { }

        public void Update(Map map, uint diffMs) => Met = meet.SignalAndWait(TimeSpan.FromSeconds(10));
    }

    private sealed class SharedCounter
    {
        private int _inside;
        public int MaxConcurrent;
        public int Total;

        public void Enter()
        {
            int now = Interlocked.Increment(ref _inside);
            int max;
            while (now > (max = Volatile.Read(ref MaxConcurrent)) && Interlocked.CompareExchange(ref MaxConcurrent, now, max) != max)
            {
            }

            Interlocked.Increment(ref Total);
            Thread.SpinWait(20_000);
            Interlocked.Decrement(ref _inside);
        }
    }

    [MapAccess(MapAccess.Global)]
    private sealed class GlobalOverlapUpdater(SharedCounter counter) : IMapUpdater
    {
        public void OnPlayerRemoved(Map map, Player player) { }

        public void Update(Map map, uint diffMs) => counter.Enter();
    }

    [MapAccess(MapAccess.MapLocal)]
    private sealed class LocalOverlapUpdater(SharedCounter counter) : IMapUpdater
    {
        public void OnPlayerRemoved(Map map, Player player) { }

        public void Update(Map map, uint diffMs) => counter.Enter();
    }

    private sealed class ActionUpdater(Action<Map, int> action) : IMapUpdater
    {
        private int _tick;

        public void OnPlayerRemoved(Map map, Player player) { }

        public void Update(Map map, uint diffMs) => action(map, ++_tick);
    }
}
