using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>MotionMaster generators on a deterministic clock, and the SMSG_MONSTER_MOVE path layouts.</summary>
public sealed class CreatureMotionTests
{
    [Fact]
    public void PathPacket_PacksDestinationOffsets_AndRoundTripsEveryPointToAQuarterYard()
    {
        var guid = ObjectGuid.WithEntry(HighGuid.Unit, WolfEntry, 7);
        var start = new Vector3(100, 200, 50);
        Vector3[] points = [new(110, 205, 51), new(120, 190, 49.5f), new(130, 200, 52)];

        byte[] payload = CreatureMovePackets.BuildPath(guid, start, 9, SplineFacing.ToTarget(new ObjectGuid(0x1234)), run: true, 4000, points);
        PathMove move = ParsePathMove(payload);

        Assert.Equal(guid.Value, move.Guid);
        Assert.Equal(start, move.Start);
        Assert.Equal(9u, move.SplineId);
        Assert.Equal(MonsterMoveType.FacingTarget, move.Type);
        Assert.Equal(0x1234ul, move.Target);
        Assert.Equal((uint)SplineFlags.Runmode, move.Flags); // final-facing bits are not sent in MONSTER_MOVE
        Assert.Equal(4000u, move.Duration);
        Assert.Equal(3, move.Points.Count);
        Assert.Equal(points[^1], move.Points[^1]);
        for (int i = 0; i < points.Length - 1; i++)
        {
            Assert.True(Vector3.Distance(points[i], move.Points[i]) <= 0.25f * MathF.Sqrt(3), $"point {i}: {move.Points[i]}");
        }
    }

    [Fact]
    public void PathPacket_FacingAngleAndSpot_CarryTheirPayload()
    {
        var guid = ObjectGuid.WithEntry(HighGuid.Unit, WolfEntry, 7);
        PathMove angle = ParsePathMove(CreatureMovePackets.BuildPath(guid, Vector3.Zero, 1, SplineFacing.ToAngle(2.5f), false, 100, [new Vector3(1, 0, 0)]));
        PathMove spot = ParsePathMove(CreatureMovePackets.BuildPath(guid, Vector3.Zero, 2, SplineFacing.ToSpot(new Vector3(4, 5, 6)), false, 100, [new Vector3(1, 0, 0)]));

        Assert.Equal(MonsterMoveType.FacingAngle, angle.Type);
        Assert.Equal(2.5f, angle.Angle);
        Assert.Equal(0u, angle.Flags);
        Assert.Equal(MonsterMoveType.FacingSpot, spot.Type);
        Assert.Equal(new Vector3(4, 5, 6), spot.Spot);
        Assert.Single(spot.Points);
        Assert.Throws<ArgumentException>(() => CreatureMovePackets.BuildPath(guid, Vector3.Zero, 3, SplineFacing.None, false, 1, []));
    }

    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(-3.25f, 7.5f, -1.75f)]
    [InlineData(255.75f, -256f, 127.75f)]
    public void PackXYZ_RoundTrips(float x, float y, float z)
        => Assert.Equal(new Vector3(x, y, z), CreatureMovePackets.UnpackXYZ(CreatureMovePackets.PackXYZ(new Vector3(x, y, z))));

    [Fact]
    public void MultiPointSpline_FollowsThePathfinderAndInterpolatesBySegment()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 10, 0)]);
        var pathfinder = new DetourPathfinder();
        (WorldRuntime runtime, _, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        WorldCollision.Of(world).Install(pathfinder: pathfinder);
        (Player player, FakeSession session) = AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);

        wolf.Motion.MovePoint(3, 30, 0, 83.5f, run: false);

        Assert.Equal(1, pathfinder.Calls);
        CreatureSpline spline = wolf.Spline!;
        Assert.Equal(2, spline.Points.Count);
        Assert.Equal(new Vector3(20, 5, 83.5f), spline.Points[0]);
        float length = 2 * MathF.Sqrt(100 + 25);
        Assert.Equal((uint)MathF.Round(length / wolf.CreatureWalkSpeed * 1000f), spline.DurationMs);
        PathMove move = ParsePathMove(Packets(session, WorldOpcode.SmsgMonsterMove).Last());
        Assert.Equal(2, move.Points.Count);

        Run(world, (spline.DurationMs / 2) + 100, 50);
        Assert.Equal(20f, wolf.X, 0);
        Assert.Equal(5f, wolf.Y, 0);
        Assert.Single(spline.RemainingPoints(system.ClockMs));
        Run(world, spline.DurationMs, 100);
        Assert.Equal(30f, wolf.X, 2);
        Assert.Equal(0f, wolf.Y, 2);
    }

    [Fact]
    public void Follow_HoldsDistanceBehindTheTarget()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 15, 15)]);
        (WorldRuntime runtime, _, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);

        wolf.Motion.MoveFollow(player, 3, MathF.PI);
        Assert.True(wolf.Spline!.Run); // the player is not in walk mode
        Run(world, 5000);

        float behind = -(player.BoundingRadius + wolf.BoundingRadius + 3);
        Assert.True(Distance2D(wolf, behind, 0) <= FollowMovementGenerator.Leeway, $"{wolf.X},{wolf.Y}");
        Assert.False(wolf.IsMoving);
        Assert.Equal(MovementGeneratorType.Follow, wolf.Motion.CurrentType);

        player.Relocate(20, 0, 83.5f, 0, 0);
        Run(world, 5000);
        Assert.True(Distance2D(wolf, 20 + behind, 0) <= FollowMovementGenerator.Leeway, $"{wolf.X},{wolf.Y}");
    }

    [Fact]
    public void TimedFlee_SetsTheFlag_RunsAway_AndEndsAfterItsDuration()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 3, 0)]);
        (WorldRuntime runtime, _, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);

        wolf.Motion.MoveFleeing(player, 2000);
        Assert.NotEqual(UnitFlags.None, wolf.UnitFlags & UnitFlags.Fleeing);
        Assert.True(wolf.Spline!.Run);
        Run(world, 1500);
        Assert.True(Distance2D(wolf, player) > 5f);
        Assert.Equal(MovementGeneratorType.Fleeing, wolf.Motion.CurrentType);

        Run(world, 600);
        Assert.Equal(UnitFlags.None, wolf.UnitFlags & UnitFlags.Fleeing);
        Assert.Equal(MovementGeneratorType.Idle, wolf.Motion.CurrentType);
        Assert.False(wolf.IsMoving);
    }

    [Fact]
    public void Chase_StandsStillWhileCasting()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 20, 0)]);
        var spells = new FakeCaster { Casting = true };
        (WorldRuntime runtime, _, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Spells = spells });
        using WorldRuntime world = runtime;
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);

        wolf.Motion.MoveChase(player);
        world.RunTick(100);
        Assert.False(wolf.IsMoving);
        Assert.Equal(20f, wolf.X, 1);

        spells.Casting = false;
        Run(world, 3000);
        Assert.True(Combat.MapCombat.CanReachWithMeleeAutoAttack(wolf, player));
    }

    [Fact]
    public void Point_InformsTheAi_AndResumesTheDefault()
    {
        CreatureContent content = Content([Template(configure: t => t.AIName = RecorderName)], [Spawn(1, WolfEntry, 5, 0)]);
        (WorldRuntime runtime, _, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Factory = RecorderFactory() });
        using WorldRuntime world = runtime;
        AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);

        wolf.Motion.MovePoint(7, 5, 5, 83.5f, run: false);
        Assert.Equal([MovementGeneratorType.Point], wolf.Motion.ActiveTypes);
        Run(world, 2500);

        Assert.Equal((MovementGeneratorType.Point, 7u), ((RecorderAI)wolf.AI!).LastInform);
        Assert.Equal(MovementGeneratorType.Idle, wolf.Motion.CurrentType);
        Assert.Equal(5f, wolf.Y, 2);
    }

    [Fact]
    public void RemoveAndClear_ResumeTheDefault_OnlyOutOfCombat()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 5, 0, movementType: 1, wander: 5)]);
        (WorldRuntime runtime, _, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);
        Assert.Equal(MovementGeneratorType.Random, wolf.Motion.DefaultType);

        wolf.Motion.MoveFollow(player, 2, 0);
        Assert.False(wolf.Motion.Remove(MovementGeneratorType.Chase)); // not on top
        Assert.True(wolf.Motion.Remove(MovementGeneratorType.Follow));
        Assert.Equal(MovementGeneratorType.Random, wolf.Motion.CurrentType);
        wolf.Motion.MovePoint(1, 0, 5, 83.5f, false);
        wolf.Motion.MoveFleeing(null, 0);
        Assert.Equal([MovementGeneratorType.Point, MovementGeneratorType.Fleeing], wolf.Motion.ActiveTypes);
        wolf.Motion.Clear();
        Assert.Empty(wolf.Motion.ActiveTypes);
        Assert.Equal(UnitFlags.None, wolf.UnitFlags & UnitFlags.Fleeing);
    }

    [Fact]
    public void Waypoints_RunFlagWaitTimesAndLoop()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 0, 0, movementType: 2)],
        [
            (1u, new CreatureWaypoint(1, 10, 0, 83.5f, 100, 1000) { Run = true }),
            (1u, new CreatureWaypoint(2, 10, 10, 83.5f, 100, 0)),
            (1u, new CreatureWaypoint(3, 0, 10, 83.5f, 100, 0)),
        ]);
        var options = new CreatureOptions();
        options.Movement.HonorWaypointRunColumn = true; // the Run column is not retail: this test is about the column
        (WorldRuntime runtime, _, CreatureMapSystem system) = CreateAiSystem(content, options: options);
        using WorldRuntime world = runtime;
        AddPlayer(world, 1, -5, -5);
        Creature wolf = Assert.Single(system.Creatures);
        var path = Assert.IsType<WaypointMovementGenerator>(wolf.Motion.Default);
        Assert.True(wolf.Spline!.Run);
        Assert.Equal(0, path.CurrentIndex);

        Run(world, 1300); // 10 yd at run speed ≈ 1.25 s
        Assert.False(wolf.IsMoving);
        Assert.True(path.WaitMs > 0);
        Assert.Equal(10f, wolf.X, 2);

        Run(world, 1000);
        Assert.True(wolf.IsMoving);
        Assert.False(wolf.Spline!.Run); // leg 2 walks
        Assert.Equal(1, path.CurrentIndex);

        Run(world, 4100); // 10 yd at walk speed = 4 s, no wait at node 2
        Assert.Equal(2, path.CurrentIndex);
        Assert.True(wolf.IsMoving);
        Run(world, 4200);
        Assert.Equal(0, path.CurrentIndex); // looped back to the first node
        Assert.True(wolf.Spline!.Run);
    }

    [Fact]
    public void NoPath_GoesStraightToTheDestination()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 10, 0)]);
        (WorldRuntime runtime, _, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        WorldCollision.Of(world).Install(pathfinder: new NoPathPathfinder());
        AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);

        wolf.Motion.MovePoint(3, 30, 0, 83.5f, run: false);

        Assert.Equal([new Vector3(30, 0, 83.5f)], wolf.Spline!.Points);
    }

    [Fact]
    public void VmapPathingHook_LaunchesTowardsTheNextCorner()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 10, 0)]);
        (WorldRuntime runtime, _, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        WorldCollision.Of(world).Install(pathfinder: new DetourPathfinder());
        AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);

        PathResult path = CreaturePathing.MoveTowards(system, wolf, new Vector3(30, 0, 83.5f), run: false);

        Assert.True(path.HasPath);
        Assert.Equal(new Vector3(20, 5, 83.5f), Assert.Single(wolf.Spline!.Points));
    }

    [Fact]
    public void LateObserver_GetsTheRemainingPathPoints()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 10, 0)]);
        (WorldRuntime runtime, _, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        WorldCollision.Of(world).Install(pathfinder: new DetourPathfinder());
        AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);
        wolf.Motion.MovePoint(3, 30, 0, 83.5f, run: false);
        Run(world, 200);

        (Player late, FakeSession lateSession) = AddPlayer(world, 2, 5, 5);
        world.RunTick(50);
        world.RunTick(50);

        PathMove catchUp = Packets(lateSession, WorldOpcode.SmsgMonsterMove).Select(ParsePathMove).Last(m => m.Guid == wolf.Guid.Value);
        Assert.Equal(2, catchUp.Points.Count);
        Assert.Equal(new Vector3(30, 0, 83.5f), catchUp.Points[^1]);
        Assert.True(catchUp.Duration < wolf.Spline!.DurationMs);
        Assert.NotNull(late);
    }
}
