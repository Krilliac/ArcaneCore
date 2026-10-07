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
/// partial one, re-paths while stuck, and the host counts vmangos' m_targetNotReachableTimer (Objects/Creature.cpp:1013-1046): past
/// <c>Creatures:UnreachableTargetSoftEvadeMs</c> (3 s) the creature is in evade mode on the spot, past
/// <c>Creatures:UnreachableTargetEvadeMs</c> (24 s) it evades home. EventAI's
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

    // --- the timer (vmangos Creature::Update, Objects/Creature.cpp:1013-1046; Creature.h:510) ----------------------------------

    [Fact]
    public void AfterThreeSeconds_TheCreatureEvadesHits_KeepsItsVictim_AndStaysInCombat()
    {
        // vmangos IsEvadeBecauseTargetNotReachable (m_targetNotReachableTimer > 3000) makes IsInEvadeMode true: attacks on the creature
        // evade (Unit.cpp:4498, SpellCaster.cpp:172) and its AI does not update (Creature.cpp:1041), but it stays in combat on its victim.
        using Fight f = Start();
        Pull(f, f.Player);

        Run(f.World, 2900);
        Assert.False(f.Wolf.IsInEvadeMode);
        Assert.True(f.Map.Combat.Hooks.CanAttack(f.Player, f.Wolf));

        Run(f.World, 300);
        Assert.True(f.Wolf.IsInEvadeMode);
        Assert.False(f.Map.Combat.Hooks.CanAttack(f.Player, f.Wolf));
        Assert.Same(f.Player, f.Wolf.Combat.Victim);
        Assert.True(f.Wolf.Combat.IsInCombat);
        Assert.Equal(MovementGeneratorType.Chase, f.Wolf.Motion.CurrentType);
    }

    [Fact]
    public void AloneOnTheThreatList_TheCreatureRunsHome_After24Seconds_NotBefore()
    {
        using Fight f = Start();
        Pull(f, f.Player);

        Run(f.World, 23900);
        Assert.Same(f.Player, f.Wolf.Combat.Victim);
        Assert.NotEqual(MovementGeneratorType.Home, f.Wolf.Motion.CurrentType);

        Run(f.World, 300);
        Assert.True(f.Wolf.IsInEvadeMode);
        Assert.Null(f.Wolf.Combat.Victim);
        Assert.False(f.Wolf.Combat.IsInCombat);
        Assert.Equal(MovementGeneratorType.Home, f.Wolf.Motion.CurrentType);
    }

    [Fact]
    public void AnotherTargetOnTheList_IsNotASwitch_TheUnreachableVictimIsKept()
    {
        // vmangos keeps the unreachable victim on the threat list (only Alterac Valley drops it, Creature.cpp:1026-1027) and the whole
        // creature evades at 24 s; the mangos rule that dropped the victim and switched (UnitThreat.cpp:342-361) is not vmangos.
        using Fight f = Start();
        (Player reachable, _) = AddPlayer(f.World, 2, WolfX + 8, 0); // east of the wall: reachable
        Pull(f, f.Player);
        f.Wolf.Combat.Threat.AddThreat(reachable, 0.5f);

        Run(f.World, 10000);
        Assert.Same(f.Player, f.Wolf.Combat.Victim);
        Assert.Equal(2, f.Wolf.Combat.Threat.Entries.Count);
        Assert.True(f.Wolf.IsInEvadeMode);

        Run(f.World, 14500);
        Assert.Equal(MovementGeneratorType.Home, f.Wolf.Motion.CurrentType);
        Assert.Null(f.Wolf.Combat.Victim);
    }

    [Fact]
    public void BecomingReachable_EndsTheEvadeState_AndRestartsTheCount()
    {
        using Fight f = Start();
        Pull(f, f.Player);
        Run(f.World, 5000);
        Assert.True(f.Wolf.IsInEvadeMode);

        f.Player.Relocate(WallX + 1, 0, f.Player.Z, 0, 0); // steps to the wolf's side of the wall
        Run(f.World, 1000);
        Assert.True(f.Wolf.Motion.IsReachable);
        Assert.False(f.Wolf.IsInEvadeMode);
        Assert.True(f.Map.Combat.Hooks.CanAttack(f.Player, f.Wolf));

        f.Player.Relocate(0, 0, f.Player.Z, 0, 0); // back behind the wall: a fresh count
        Run(f.World, 2500);
        Assert.False(f.Wolf.IsInEvadeMode);
        Run(f.World, 1000);
        Assert.True(f.Wolf.IsInEvadeMode);
    }

    [Fact]
    public void TheTimerKeepsCounting_WhileTheCreatureCannotMove()
    {
        // vmangos counts on the generator's last verdict: a stunned chaser keeps its unreachable flag (Creature.cpp:1013-1025).
        using Fight f = Start();
        Pull(f, f.Player);
        Run(f.World, 1000);

        f.Wolf.UnitFlags |= UnitFlags.Stunned;
        Run(f.World, 3000);

        Assert.True(f.Wolf.IsInEvadeMode);
    }

    [Fact]
    public void NoUnreachableEvade_KeepsTheCreatureFighting()
    {
        // vmangos CREATURE_FLAG_EXTRA_NO_UNREACHABLE_EVADE (0x08, CreatureDefines.h:160).
        using Fight f = Start(template: Template() with { ExtraFlags = 0x08, ExtraFlagsDialect = CreatureExtraFlagsDialect.VMangos });
        Pull(f, f.Player);

        Run(f.World, 30000);

        Assert.False(f.Wolf.IsInEvadeMode);
        Assert.Same(f.Player, f.Wolf.Combat.Victim);
        Assert.False(f.Wolf.Motion.IsReachable);
    }

    [Fact]
    public void ZeroDisablesBothStages_TheFlagStays()
    {
        using Fight f = Start(new CreatureOptions { UnreachableTargetEvadeMs = 0, UnreachableTargetSoftEvadeMs = 0 });
        Pull(f, f.Player);

        Run(f.World, 30000);

        Assert.False(f.Wolf.IsInEvadeMode);
        Assert.Same(f.Player, f.Wolf.Combat.Victim);
        Assert.False(f.Wolf.Motion.IsReachable);
    }

    [Fact]
    public void TheEvadingCreature_RegeneratesAsIfOutOfCombat()
    {
        // vmangos RegenerateAll(update_diff, IsEvadeBecauseTargetNotReachable()) skips the in-combat check (Creature.cpp:1057, :1094).
        using Fight f = Start();
        Pull(f, f.Player);
        f.Wolf.Health = f.Wolf.MaxHealth / 4;

        Run(f.World, 9000);

        Assert.True(f.Wolf.Combat.IsInCombat);
        Assert.True(f.Wolf.Health > f.Wolf.MaxHealth / 4);
    }

    [Fact]
    public void ACreatureThatDoesNotChase_HasNothingToBeUnreachable()
    {
        using Fight f = Start();
        f.Wolf.AI!.CombatMovement = false;
        Pull(f, f.Player);

        Run(f.World, 30000);

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
