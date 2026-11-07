using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Game.Tests.GridTerrain;

/// <summary>
/// The group manager is written by packet handlers and bots under the world's serial gate, so the combat hooks' group read (the
/// free-for-all friendliness rule, CombatHooks.InSameGroup) must hold the gate too. A map-local (ungated) stage that asks CanAttack or
/// IsFriendly for two free-for-all players takes the gate for the read and is reported; a read already inside the gate is silent.
/// Unproven until the integration gate runs.
/// </summary>
public sealed class GroupReadGateTests
{
    private static WorldRuntime CreateRuntime()
        => new(
            new WorldRuntimeOptions
            {
                UpdateCompressionThreshold = 0,
                AutosaveIntervalMs = 0,
                CommandTimeBudgetMs = 0,
                Parallel = new ParallelMapOptions { MapUpdateThreads = 2, OwnerGuard = OwnerGuardMode.Report },
            },
            new RecordingSaveQueue(),
            NullLogger<WorldRuntime>.Instance);

    private sealed class Probe
    {
        public int Reads;
        public bool ReadsAllHeldTheGate = true;
        public bool SawParallelPhase;
    }

    private static (Player A, Player B) FreeForAllPair(WorldRuntime world, Probe probe)
    {
        Map map = world.GetMap(0);
        Player a = TestWorld.CreatePlayer(1, 10, 0, new FakeSession(1));
        Player b = TestWorld.CreatePlayer(2, 12, 0, new FakeSession(2));
        map.AddPlayer(a);
        map.AddPlayer(b);
        a.Flags |= PlayerFlags.FfaPvp;
        b.Flags |= PlayerFlags.FfaPvp;
        world.GetMap(1).AddPlayer(TestWorld.CreatePlayer(3, 0, 0, new FakeSession(3), mapId: 1)); // a second map, so the maps phase runs in parallel
        CombatHooks.RegisterSameGroup(world, (x, y) =>
        {
            probe.Reads++;
            probe.ReadsAllHeldTheGate &= world.HoldsSerialGate;
            probe.SawParallelPhase |= world.IsParallelPhase;
            return false;
        });
        return (a, b);
    }

    [MapAccess(MapAccess.MapLocal, Reason = "test: runs outside the serial gate on purpose")]
    private sealed class LocalAsker(Action ask) : IMapUpdater
    {
        public void OnPlayerRemoved(Map map, Player player) { }

        public void Update(Map map, uint diffMs) => ask();
    }

    [MapAccess(MapAccess.Global)]
    private sealed class GatedAsker(Action ask) : IMapUpdater
    {
        public void OnPlayerRemoved(Map map, Player player) { }

        public void Update(Map map, uint diffMs) => ask();
    }

    [Fact]
    public void AGroupReadFromAnUngatedMapStage_TakesTheGate_AndIsReported()
    {
        using WorldRuntime world = CreateRuntime();
        var probe = new Probe();
        (Player a, Player b) = FreeForAllPair(world, probe);
        bool friendly = true;
        world.GetMap(0).AddUpdater(new LocalAsker(() => friendly = CombatHooks.Default.IsFriendly(a, b)));
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == "parallel/ungated-read");

        world.RunTick(50);

        Assert.Equal(1, probe.Reads);
        Assert.True(probe.SawParallelPhase, "the read happened during the parallel maps phase");
        Assert.True(probe.ReadsAllHeldTheGate, "the group read ran without the serial gate");
        Assert.False(friendly); // two free-for-all players who are not grouped
        Assert.Single(capture.Notices);
        Assert.Equal(1, world.ParallelCounters.OwnerViolations);
    }

    [Fact]
    public void AGroupReadFromGatedCode_IsNotReported()
    {
        using WorldRuntime world = CreateRuntime();
        var probe = new Probe();
        (Player a, Player b) = FreeForAllPair(world, probe);
        world.GetMap(0).AddUpdater(new GatedAsker(() => CombatHooks.Default.IsFriendly(a, b)));
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == "parallel/ungated-read");

        world.RunTick(50);

        Assert.Equal(1, probe.Reads);
        Assert.True(probe.SawParallelPhase);
        Assert.True(probe.ReadsAllHeldTheGate);
        Assert.Empty(capture.Notices);
        Assert.Equal(0, world.ParallelCounters.OwnerViolations);
    }
}
