using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureMovement;

/// <summary>
/// vmangos / mangos-classic waypoint movement: which path a creature walks (its own spawn rows, else the template path of its entry;
/// WaypointManager.h GetDefaultPath), where evade sends it (the last node it reached, WaypointMovementGenerator.cpp:292-303), the AI
/// being told of each arrival (:158-160), walk legs (:240) and the casting / cannot-move gates (:249-272). All on the map clock.
/// </summary>
public sealed class WaypointGeneratorTests
{
    private static CreatureWaypoint Node(uint point, float x, float y, uint waitMs = 0, float orientation = 100f)
        => new(point, x, y, 83.5f, orientation, waitMs);

    private static (WorldRuntime World, Map Map, CreatureMapSystem System, Creature Wolf, Player Player) Start(
        CreatureContent content, CreatureAiServices? services = null, CreatureOptions? options = null, float playerX = -5, float playerY = -5)
    {
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content, services, options);
        (Player player, _) = AddPlayer(world, 1, playerX, playerY);
        return (world, map, system, Assert.Single(system.Creatures), player);
    }

    [Fact]
    public void ASpawnWithoutItsOwnRows_WalksItsEntryTemplatePath_InPointIdOrder()
    {
        // Point ids 1, 2, 5 (classic-db has ten paths with gaps): the order is by id, the ids are not renumbered.
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 0, 0, movementType: 2)],
            entryWaypoints: [(WolfEntry, 0u, Node(5, 0, 10)), (WolfEntry, 0u, Node(1, 10, 0)), (WolfEntry, 0u, Node(2, 10, 10))]);
        (WorldRuntime w, _, _, Creature wolf, _) = Start(content);
        using WorldRuntime world = w;

        Assert.IsType<WaypointMovementGenerator>(wolf.Motion.Default);
        Assert.True(wolf.IsMoving);
        Assert.Equal((10f, 0f), (wolf.Spline!.EndX, wolf.Spline.EndY)); // node 1 first

        var visited = new List<(float, float)>();
        for (int i = 0; i < 400 && visited.Count < 3; i++)
        {
            world.RunTick(100);
            if (wolf.IsMoving && (visited.Count == 0 || visited[^1] != (wolf.Spline!.EndX, wolf.Spline.EndY)))
            {
                visited.Add((wolf.Spline!.EndX, wolf.Spline.EndY));
            }
        }

        Assert.Equal([(10f, 0f), (10f, 10f), (0f, 10f)], visited);
    }

    [Fact]
    public void TheSpawnOwnRowsWin_OverTheEntryTemplatePath()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 0, 0, movementType: 2)],
            waypoints: [(1u, Node(1, 0, 20))],
            entryWaypoints: [(WolfEntry, 0u, Node(1, 30, 0))]);
        (WorldRuntime w, _, _, Creature wolf, _) = Start(content);
        using WorldRuntime world = w;

        Assert.Equal((0f, 20f), (wolf.Spline!.EndX, wolf.Spline.EndY));
    }

    [Fact]
    public void ASummonWhoseTemplateMovesByWaypoint_WalksTheEntryPath()
    {
        // mangos-classic WaypointManager.h case 2a: a summoned creature with creature_template.MovementType=2 uses the entry path.
        CreatureContent content = Content([Template() with { MovementType = 2 }], [],
            entryWaypoints: [(WolfEntry, 0u, Node(1, 12, 0))]);
        (WorldRuntime w, _, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = w;
        AddPlayer(world, 1, 0, 0);

        Creature summoned = system.SpawnTemporary(content.FindTemplate(WolfEntry)!, 0, 0, 83.5f, 0);

        Assert.IsType<WaypointMovementGenerator>(summoned.Motion.Default);
        Assert.True(summoned.IsMoving);
        Assert.Equal(12f, summoned.Spline!.EndX);
    }

    [Fact]
    public void ASpawnWithNoPathAnywhere_StillIdles()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 0, 0, movementType: 2)]);
        (WorldRuntime w, _, _, Creature wolf, _) = Start(content);
        using WorldRuntime world = w;

        Assert.Equal(MovementGeneratorType.Idle, wolf.Motion.DefaultType);
        Assert.False(wolf.IsMoving);
    }

    [Fact]
    public void Evade_RunsToTheLastReachedNode_NotToWhereTheFightBegan()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 0, 0, movementType: 2)],
            waypoints: [(1u, Node(1, 10, 0, waitMs: 20_000)), (1u, Node(2, 10, 40))]);
        (WorldRuntime w, Map map, CreatureMapSystem system, Creature wolf, Player player) = Start(content, playerX: 10, playerY: -5);
        using WorldRuntime world = w;
        Run(world, 6000); // 10 yd at walk speed = 4 s: it stands at node 1 (point id 1) and waits
        Assert.False(wolf.IsMoving);
        Assert.Equal(10f, wolf.X, 1);

        // Pulled away from the node, then evading: vmangos Home -> GetResetPosition = the last reached node.
        wolf.Relocate(10f, 25f, 83.5f, 0f, 0);
        map.Combat.DealDamage(player, wolf, 1, direct: false);
        system.EnterEvadeMode(wolf);
        Assert.Equal(MovementGeneratorType.Home, wolf.Motion.CurrentType);
        Run(world, 6000);

        Assert.False(wolf.IsInEvadeMode);
        Assert.Equal((10f, 0f), (MathF.Round(wolf.X, 1), MathF.Round(wolf.Y, 1)));
    }

    [Fact]
    public void Evade_BeforeAnyNodeWasReached_GoesToTheSpawnPoint_AndTheLegResumes()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 0, 0, movementType: 2)],
            waypoints: [(1u, Node(1, 40, 0)), (1u, Node(2, 0, 0))]);
        (WorldRuntime w, Map map, CreatureMapSystem system, Creature wolf, Player player) = Start(content, playerX: 20, playerY: 10);
        using WorldRuntime world = w;
        Run(world, 2000); // 5 yd along the way to node 1
        Assert.InRange(wolf.X, 3f, 8f);

        map.Combat.DealDamage(player, wolf, 1, direct: false);
        system.EnterEvadeMode(wolf);
        Run(world, 2500); // run home (7 yd/s) takes under a second, then the leg restarts

        Assert.False(wolf.IsInEvadeMode);
        Assert.True(wolf.IsMoving);
        Assert.Equal(40f, wolf.Spline!.EndX); // heading for node 1 again
        Assert.True(wolf.X < 8f, $"at {wolf.X}: it went back to the spawn point and set off again");
    }

    [Fact]
    public void TheAiIsToldOfEachArrival_BeforeTheNodeDelay()
    {
        CreatureContent content = Content([Template(configure: t => t.AIName = RecorderName)], [Spawn(1, WolfEntry, 0, 0, movementType: 2)],
            waypoints: [(1u, Node(3, 10, 0, waitMs: 5000)), (1u, Node(7, 10, 10))]);
        (WorldRuntime w, _, _, Creature wolf, _) = Start(content, new CreatureAiServices { Factory = RecorderFactory() });
        using WorldRuntime world = w;
        var ai = (RecorderAI)wolf.AI!;

        Run(world, 4500); // arrives at node 3 after 4 s and waits 5 s

        Assert.Equal((MovementGeneratorType.Waypoint, 3u), ai.LastInform); // the DB point id, once
        Assert.Single(ai.Calls, c => c == "inform:Waypoint:3");
        Assert.False(wolf.IsMoving);
        Run(world, 10_000);
        Assert.Contains("inform:Waypoint:7", ai.Calls);
    }

    [Fact]
    public void Legs_Walk_UnlessTheTemplateRunsAlways_AndTheRunColumnIsOnlyHonouredOnRequest()
    {
        CreatureWaypoint runNode = Node(1, 10, 0) with { Run = true };
        CreatureContent plain = Content([Template()], [Spawn(1, WolfEntry, 0, 0, movementType: 2)], waypoints: [(1u, runNode)]);
        (WorldRuntime w1, _, _, Creature walker, _) = Start(plain);
        using WorldRuntime world1 = w1;
        Assert.False(walker.Spline!.Run); // the Run column is an ArcaneCore addition, not in vmangos or classic-db

        var honour = new CreatureOptions();
        honour.Movement.HonorWaypointRunColumn = true;
        (WorldRuntime w2, _, _, Creature runner, _) = Start(plain, options: honour);
        using WorldRuntime world2 = w2;
        Assert.True(runner.Spline!.Run);

        CreatureContent always = Content(
            [Template() with { ExtraFlags = 0x40, ExtraFlagsDialect = CreatureExtraFlagsDialect.VMangos }],
            [Spawn(1, WolfEntry, 0, 0, movementType: 2)], waypoints: [(1u, Node(1, 10, 0))]);
        (WorldRuntime w3, _, _, Creature alwaysRuns, _) = Start(always);
        using WorldRuntime world3 = w3;
        Assert.True(alwaysRuns.Spline!.Run);
    }

    [Fact]
    public void ACastingWalker_StopsAndRestartsTheSameLegWhenTheCastEnds()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 0, 0, movementType: 2)],
            waypoints: [(1u, Node(1, 40, 0)), (1u, Node(2, 0, 0))]);
        var spells = new FakeCaster();
        (WorldRuntime w, _, _, Creature wolf, _) = Start(content, new CreatureAiServices { Spells = spells });
        using WorldRuntime world = w;
        Run(world, 2000);
        Assert.True(wolf.IsMoving);

        spells.Casting = true;
        world.RunTick(100);
        Assert.False(wolf.IsMoving); // vmangos Update :261-272: StopMoving, arrival not done, restart at 1 ms
        float stoppedAt = wolf.X;
        Run(world, 5000);
        Assert.Equal(stoppedAt, wolf.X, 2);
        Assert.False(wolf.IsMoving);

        spells.Casting = false;
        Run(world, 400);
        Assert.True(wolf.IsMoving);
        Assert.Equal(40f, wolf.Spline!.EndX); // the same node again
    }

    [Fact]
    public void AStunnedWalker_StartsNoNewLeg()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 0, 0, movementType: 2)],
            waypoints: [(1u, Node(1, 10, 0, waitMs: 1000)), (1u, Node(2, 10, 10))]);
        (WorldRuntime w, _, _, Creature wolf, _) = Start(content);
        using WorldRuntime world = w;
        Run(world, 4300); // at node 1, waiting
        wolf.UnitFlags |= UnitFlags.Stunned;

        Run(world, 10_000);
        Assert.False(wolf.IsMoving);
        Assert.Equal(0f, wolf.Y, 1);

        wolf.UnitFlags &= ~UnitFlags.Stunned;
        Run(world, 1500);
        Assert.True(wolf.IsMoving);
        Assert.Equal(10f, wolf.Spline!.EndY);
    }

    [Fact]
    public void TheOptionDefaultsToRetail()
        => Assert.False(new CreatureOptions().Movement.HonorWaypointRunColumn);
}
