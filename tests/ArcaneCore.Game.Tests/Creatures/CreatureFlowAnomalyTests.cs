using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Creatures;

/// <summary>
/// The evade-loop, leash-reset-loop and waypoint-path-missing checks driven through the real flows: <see cref="CreatureMapSystem.EnterEvadeMode"/>,
/// the template's hard leash in the map tick, and a waypoint spawn with and without a path. Each scenario owns an entry (the ledger is process-wide).
/// </summary>
public sealed class CreatureFlowAnomalyTests
{
    private const uint EvadeEntry = 950001;
    private const uint SingleEvadeEntry = 950002;
    private const uint LeashEntry = 950003;
    private const uint SingleLeashEntry = 950004;
    private const uint NoPathEntry = 950005;
    private const uint PathEntry = 950006;

    private sealed record Fight(WorldRuntime World, Map Map, CreatureMapSystem System, Player Player, Creature Wolf) : IDisposable
    {
        public void Dispose() => World.Dispose();
    }

    private static Fight Start(uint entry, float leash = 0f)
    {
        CreatureContent content = Content([Template(entry) with { Leash = leash }], [Spawn(1, entry, 5, 0)]);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Hostility = new AlwaysHostile() });
        (Player player, _) = AddPlayer(runtime, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);
        wolf.AI!.CombatMovement = false;
        return new Fight(runtime, map, system, player, wolf);
    }

    private static AnomalyCapture Capture(string key, uint entry)
        => AnomalyLedger.Shared.Capture(n => n.Key == key && n.Subject.StartsWith($"creature entry {entry} ", StringComparison.Ordinal));

    /// <summary>One engine evade: pull the wolf out of place, evade, and let it walk home so the next evade is a new one.</summary>
    private static void EvadeOnce(Fight f)
    {
        f.Wolf.Relocate(25, 0, 83.5f, 0, 0);
        f.System.EnterEvadeMode(f.Wolf);
        Assert.True(f.Wolf.IsEvading);
        Run(f.World, 6_000);
        Assert.False(f.Wolf.IsEvading);
    }

    /// <summary>One hard-leash reset: start a fight, drag the wolf past the leash, and let the 3 s check evade it and the wolf walk home.</summary>
    private static void LeashOnce(Fight f)
    {
        f.Map.Combat.DealDamage(f.Player, f.Wolf, 1, direct: false);
        f.Wolf.Relocate(5 + 40, 0, 83.5f, 0, 0);
        Run(f.World, 3_100);
        Assert.True(f.Wolf.IsInEvadeMode, "the hard leash did not evade the wolf");
        Run(f.World, 8_000);
        Assert.False(f.Wolf.IsEvading);
    }

    [Fact]
    public void ThreeRealEvadesInTheWindow_ReportEvadeLoop_ExactlyOnce()
    {
        using AnomalyCapture capture = Capture("creature/evade-loop", EvadeEntry);
        using Fight f = Start(EvadeEntry);

        EvadeOnce(f);
        EvadeOnce(f);
        Assert.Empty(capture.Notices);

        EvadeOnce(f);
        Assert.Single(capture.Notices);

        EvadeOnce(f);
        Assert.Single(capture.Notices); // one report per episode
    }

    [Fact]
    public void ASingleRealEvade_ReportsNoEvadeLoop()
    {
        using AnomalyCapture capture = Capture("creature/evade-loop", SingleEvadeEntry);
        using Fight f = Start(SingleEvadeEntry);

        EvadeOnce(f);

        Assert.Empty(capture.Notices);
    }

    [Fact]
    public void ThreeHardLeashResets_ReportLeashResetLoop_ExactlyOnce()
    {
        using AnomalyCapture capture = Capture("creature/leash-reset-loop", LeashEntry);
        using Fight f = Start(LeashEntry, leash: 30f);

        LeashOnce(f);
        LeashOnce(f);
        Assert.Empty(capture.Notices);

        LeashOnce(f);
        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Contains("hard leash", notice.Detail);
    }

    [Fact]
    public void ASingleHardLeashReset_ReportsNoLeashResetLoop()
    {
        using AnomalyCapture capture = Capture("creature/leash-reset-loop", SingleLeashEntry);
        using Fight f = Start(SingleLeashEntry, leash: 30f);

        LeashOnce(f);

        Assert.Empty(capture.Notices);
    }

    [Fact]
    public void AWaypointSpawnWithNoPath_ReportsWaypointPathMissing_OncePerSpawn()
    {
        using AnomalyCapture capture = Capture("creature/waypoint-path-missing", NoPathEntry);
        CreatureContent content = Content([Template(NoPathEntry)], [Spawn(1, NoPathEntry, 5, 0, movementType: 2)]);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content);
        using (world)
        {
            AddPlayer(world, 1, 0, 0);
            Run(world, 500);
            Creature wolf = Assert.Single(system.Creatures);
            Assert.NotEqual(MovementGeneratorType.Waypoint, wolf.Motion.CurrentType);

            AnomalyNotice notice = Assert.Single(capture.Notices);
            Assert.Contains("0 points", notice.Detail);
        }
    }

    [Fact]
    public void AWaypointSpawnWithAPath_ReportsNothing()
    {
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Subject.StartsWith($"creature entry {PathEntry} ", StringComparison.Ordinal));
        var node = new CreatureWaypoint(1, 10, 0, 83.5f, 100f, 0);
        CreatureContent content = Content([Template(PathEntry)], [Spawn(1, PathEntry, 5, 0, movementType: 2)], waypoints: [(1u, node)]);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content);
        using (world)
        {
            AddPlayer(world, 1, 0, 0);
            Run(world, 500);

            Assert.Equal(MovementGeneratorType.Waypoint, Assert.Single(system.Creatures).Motion.CurrentType);
            Assert.Empty(capture.Notices);
        }
    }
}
