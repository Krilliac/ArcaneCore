using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Creatures;

/// <summary>
/// The unreachable-target rule: the chase generator reports a victim unreachable when the pathfinder returns no path or a
/// partial one, re-paths while stuck, and the host gives the victim up after <c>Creatures:UnreachableTargetEvadeMs</c> (evade
/// when alone on the threat list, drop the victim otherwise; mangos Object/UnitThreat.cpp:342-361). EventAI's
/// EVENT_T_TARGET_NOT_REACHABLE (36) fires from the same flag. The synthetic map has no navmesh: the pathfinders below
/// stand in for one.
/// </summary>
public sealed class UnreachableTargetTests
{
    private const float WolfX = 15f;
    private const float WallX = 10f;

    /// <summary>A wall at <see cref="WallX"/>: a route from east of it to anywhere west ends at the wall (<see cref="PathType.Incomplete"/>).</summary>
    private sealed class WallPathfinder : IPathfinder
    {
        public int Calls { get; private set; }

        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            Calls++;
            if (start.X >= WallX && end.X < WallX)
            {
                return new PathResult(PathType.Incomplete, [start, new Vector3(WallX, end.Y, end.Z)]);
            }

            return new PathResult(PathType.Normal, [start, end]);
        }
    }

    private sealed record Fight(WorldRuntime World, Map Map, CreatureMapSystem System, Player Player, Creature Wolf, WallPathfinder Paths) : IDisposable
    {
        public void Dispose() => World.Dispose();
    }

    private static Fight Start(CreatureOptions? options = null, IPathfinder? pathfinder = null, CreatureTemplate? template = null, CreatureAiContent? ai = null,
        CreatureAiServices? services = null)
    {
        CreatureContent content = new([template ?? Template()], [Spawn(1, WolfEntry, WolfX, 0)], [], [], [], ai ?? CreatureAiContent.Empty);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content, services ?? new CreatureAiServices { Hostility = new AlwaysHostile() }, options);
        var wall = new WallPathfinder();
        WorldCollision.Of(runtime).Install(pathfinder: pathfinder ?? wall);
        (Player player, _) = AddPlayer(runtime, 1, 0, 0); // 15 yd west of the wolf, behind the wall
        Creature wolf = Assert.Single(system.Creatures);
        return new Fight(runtime, map, system, player, wolf, wall);
    }

    private static void Pull(Fight f, Unit attacker) => f.Map.Combat.DealDamage(attacker, f.Wolf, 1, direct: false);

    // --- the flag ----------------------------------------------------------------------------------------

    [Fact]
    public void APartialPath_MarksTheChaseUnreachable_AndACompleteOneDoesNot()
    {
        using Fight stuck = Start();
        Pull(stuck, stuck.Player);
        Assert.Equal(MovementGeneratorType.Chase, stuck.Wolf.Motion.CurrentType);
        Assert.False(stuck.Wolf.Motion.IsReachable);

        using Fight open = Start(pathfinder: new DetourPathfinder());
        Pull(open, open.Player);
        Assert.Equal(MovementGeneratorType.Chase, open.Wolf.Motion.CurrentType);
        Assert.True(open.Wolf.Motion.IsReachable);
    }

    [Fact]
    public void NoPath_IsUnreachable_ButTheCreatureStillGoesStraight()
    {
        // vmangos chase with PATHFIND_NOPATH outside instances: the straight line is walked, the generator still says unreachable.
        using Fight f = Start(pathfinder: new NoPathPathfinder());
        Pull(f, f.Player);

        Assert.False(f.Wolf.Motion.IsReachable);
        Assert.True(f.Wolf.IsMoving);
        Assert.True(f.Wolf.Spline!.Points[^1].X < WolfX); // towards the player
    }

    [Fact]
    public void ReachingThePlayer_ClearsTheFlag()
    {
        using Fight f = Start(pathfinder: new NoPathPathfinder());
        Pull(f, f.Player);
        Assert.False(f.Wolf.Motion.IsReachable);

        Run(f.World, 3000); // the straight line brings it into melee reach

        Assert.True(MapCombat.CanReachWithMeleeAutoAttack(f.Wolf, f.Player));
        Assert.True(f.Wolf.Motion.IsReachable);
        Assert.False(f.Wolf.IsInEvadeMode);
    }

    [Fact]
    public void AStuckChaser_RePathsEveryRecheckInterval_NotEveryTick()
    {
        using Fight f = Start();
        Pull(f, f.Player);
        Run(f.World, 2000); // at the wall, standing still
        Assert.False(f.Wolf.IsMoving);
        Assert.Equal(WallX, f.Wolf.X, 1);

        int before = f.Paths.Calls;
        Run(f.World, 1000, step: 50);

        Assert.InRange(f.Paths.Calls - before, 8, 12); // TargetedMovementGenerator.RecheckMs = 100 while unreachable, 50 ms ticks
    }

    // --- the timer -------------------------------------------------------------------------------------------

    [Fact]
    public void AloneOnTheThreatList_TheCreatureEvades_AfterTheTimer_NotBefore()
    {
        using Fight f = Start();
        Pull(f, f.Player);

        Run(f.World, 4500);
        Assert.False(f.Wolf.IsInEvadeMode);
        Assert.Same(f.Player, f.Wolf.Combat.Victim);

        Run(f.World, 1000);
        Assert.True(f.Wolf.IsInEvadeMode);
        Assert.Null(f.Wolf.Combat.Victim);
        Assert.Equal(MovementGeneratorType.Home, f.Wolf.Motion.CurrentType);
    }

    [Fact]
    public void WithAnotherTargetOnTheList_TheUnreachableOneIsDropped_AndTheCreatureSwitches()
    {
        using Fight f = Start();
        (Player reachable, _) = AddPlayer(f.World, 2, WolfX + 8, 0); // east of the wall: reachable
        Pull(f, f.Player);                                           // threat 1 from the unreachable player
        f.Wolf.Combat.Threat.AddThreat(reachable, 0.5f);
        Assert.Same(f.Player, f.Wolf.Combat.Victim);

        Run(f.World, 4500);
        Assert.Same(f.Player, f.Wolf.Combat.Victim);
        Assert.Equal(2, f.Wolf.Combat.Threat.Entries.Count);

        Run(f.World, 1000);
        Assert.False(f.Wolf.IsInEvadeMode);
        Assert.Same(reachable, f.Wolf.Combat.Victim);
        Assert.DoesNotContain(f.Wolf.Combat.Threat.Entries, e => ReferenceEquals(e.Target, f.Player));
        Assert.True(f.Wolf.Combat.IsInCombat);
        Assert.True(f.Wolf.Motion.IsReachable); // the new chase has a path
    }

    [Fact]
    public void TheTimerPauses_WhileTheCreatureCannotMove()
    {
        using Fight f = Start();
        Pull(f, f.Player);
        Run(f.World, 1000);

        f.Wolf.UnitFlags |= UnitFlags.Stunned;
        Run(f.World, 6000);
        Assert.False(f.Wolf.IsInEvadeMode); // 1 s counted, the stunned 6 s did not

        f.Wolf.UnitFlags &= ~UnitFlags.Stunned;
        Run(f.World, 3500);
        Assert.False(f.Wolf.IsInEvadeMode); // 4.5 s
        Run(f.World, 1000);
        Assert.True(f.Wolf.IsInEvadeMode);  // 5.5 s
    }

    [Fact]
    public void ZeroDisablesTheEvade_TheFlagStays()
    {
        using Fight f = Start(new CreatureOptions { UnreachableTargetEvadeMs = 0 });
        Pull(f, f.Player);

        Run(f.World, 9000);

        Assert.False(f.Wolf.IsInEvadeMode);
        Assert.Same(f.Player, f.Wolf.Combat.Victim);
        Assert.False(f.Wolf.Motion.IsReachable);
        Assert.False(f.System.IsTargetUnreachableForTooLong(f.Wolf));
    }

    [Fact]
    public void ACreatureThatDoesNotChase_HasNothingToBeUnreachable()
    {
        using Fight f = Start();
        f.Wolf.AI!.CombatMovement = false;
        Pull(f, f.Player);

        Run(f.World, 9000);

        Assert.NotEqual(MovementGeneratorType.Chase, f.Wolf.Motion.CurrentType);
        Assert.True(f.Wolf.Motion.IsReachable);
        Assert.False(f.Wolf.IsInEvadeMode);
    }

    // --- EventAI -----------------------------------------------------------------------------------------------

    private static CreatureAiEvent NotReachableRow(uint marker) => new()
    {
        Id = 1,
        CreatureId = WolfEntry,
        EventType = 36,
        Flags = 1, // repeatable
        Action1 = new CreatureAiAction((byte)EventAiActionType.Cast, (int)marker, (int)EventAiTarget.Self, 0),
    };

    [Fact]
    public void EventAi_TargetNotReachable_FiresWhileTheChaseIsStuck_AndNotWithAPath()
    {
        CreatureTemplate eventAi = Template() with { AIName = CreatureAiFactory.EventAIName };
        var stuckCaster = new FakeCaster();
        using Fight stuck = Start(template: eventAi, ai: new CreatureAiContent([NotReachableRow(7)], []),
            services: new CreatureAiServices { Hostility = new AlwaysHostile(), Spells = stuckCaster });
        Pull(stuck, stuck.Player);
        Run(stuck.World, 1300); // batches at 600 and 1200 ms
        Assert.Equal(2, stuckCaster.Casts.Count(c => c.Spell == 7));

        var openCaster = new FakeCaster();
        using Fight open = Start(template: eventAi, ai: new CreatureAiContent([NotReachableRow(7)], []), pathfinder: new DetourPathfinder(),
            services: new CreatureAiServices { Hostility = new AlwaysHostile(), Spells = openCaster });
        Pull(open, open.Player);
        Run(open.World, 1300);
        Assert.DoesNotContain(openCaster.Casts, c => c.Spell == 7);
    }
}
