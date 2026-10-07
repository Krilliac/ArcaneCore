using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Game.Tests.Pets;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Creatures;

/// <summary>
/// The distract state (vmangos DistractMovementGenerator, Movement/IdleMovementGenerator.cpp:33-75; MotionMaster::MoveDistract and
/// Mutate, MotionMaster.cpp:678-711): the stealth alert holds a creature for 5 s facing the stealthed player (CreatureAI::TriggerAlertDirect,
/// AI/CreatureAI.cpp:376-385), SPELL_EFFECT_DISTRACT holds it for the effect value in seconds facing the spell's destination
/// (Spell::EffectDistract, Spells/SpellEffects.cpp:2632-2649); when the time is up it turns back to its spawn facing and resumes its
/// default movement; any new movement (a chase) ends it.
/// </summary>
public sealed class CreatureDistractTests
{
    private const float SpawnFacing = 1.25f;

    private static (WorldRuntime World, Map Map, CreatureMapSystem System, Creature Wolf, Player Player, FakeSession Session) Start(byte movementType = 1)
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 0, 0, movementType: movementType, wander: 8) with { Orientation = SpawnFacing }]);
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Hostility = new AlwaysHostile() },
            new CreatureOptions { AiRelocationNotifyDelayMs = 3_600_000 });
        (Player player, FakeSession session) = AddPlayer(world, 1, 30, 0); // outside aggro range
        return (world, map, system, Assert.Single(system.Creatures), player, session);
    }

    [Fact]
    public void TheStealthAlert_HoldsTheCreatureFiveSeconds_FacingThePlayer_ThenItTurnsBackAndWandersAgain()
    {
        (WorldRuntime w, _, CreatureMapSystem system, Creature wolf, Player player, FakeSession session) = Start();
        using WorldRuntime world = w;
        Run(world, 1200); // the wander's first leg starts after 1 s
        session.Clear();

        system.TriggerAlert(wolf, player);

        Assert.Equal(MovementGeneratorType.Distract, wolf.Motion.CurrentType);
        Assert.NotEmpty(Packets(session, WorldOpcode.SmsgMonsterMove)); // the facing spline
        float toPlayer = Creature.NormalizeOrientation(MathF.Atan2(player.Y - wolf.Y, player.X - wolf.X));
        world.RunTick(100);
        Assert.Equal(toPlayer, wolf.Orientation, 0.01f);
        (float x, float y) = (wolf.X, wolf.Y);

        Run(world, 4700); // 4.8 s: still distracted, standing
        Assert.Equal(MovementGeneratorType.Distract, wolf.Motion.CurrentType);
        Assert.Equal((x, y), (wolf.X, wolf.Y));

        Run(world, 300);  // 5.1 s: over, facing the spawn orientation again
        Assert.Equal(MovementGeneratorType.Random, wolf.Motion.CurrentType);
        world.RunTick(100);
        Assert.Equal(SpawnFacing, wolf.Orientation, 0.01f);

        Run(world, 12000);
        Assert.NotEqual((x, y), (wolf.X, wolf.Y)); // wandering again
    }

    [Fact]
    public void AChase_EndsTheDistraction()
    {
        // vmangos MotionMaster::Mutate: a distract on top is expired by any new generator (MotionMaster.cpp:699-702).
        (WorldRuntime w, _, CreatureMapSystem system, Creature wolf, Player player, _) = Start(movementType: 0);
        using WorldRuntime world = w;
        system.TriggerAlert(wolf, player);
        Assert.Equal(MovementGeneratorType.Distract, wolf.Motion.CurrentType);

        Assert.True(system.AttackStart(wolf, player));

        Assert.Equal(MovementGeneratorType.Chase, wolf.Motion.CurrentType);
        Assert.DoesNotContain(MovementGeneratorType.Distract, wolf.Motion.ActiveTypes);
    }

    [Fact]
    public void ASecondDistraction_ReplacesTheFirst()
    {
        (WorldRuntime w, _, CreatureMapSystem system, Creature wolf, _, _) = Start(movementType: 0);
        using WorldRuntime world = w;

        system.Distract(wolf, 0f, 2000);
        Run(world, 1500);
        system.Distract(wolf, MathF.PI, 2000);
        Run(world, 1500);

        Assert.Equal(MovementGeneratorType.Distract, wolf.Motion.CurrentType);
        Assert.Equal([MovementGeneratorType.Distract], wolf.Motion.ActiveTypes);
        Run(world, 600);
        Assert.Equal(MovementGeneratorType.Idle, wolf.Motion.CurrentType);
    }

    [Fact]
    public void ACreatureInCombat_OrOneThatCannotReact_IsNotDistracted()
    {
        // vmangos Spell::EffectDistract: unitTarget->IsInCombat() or UNIT_STATE_CAN_NOT_REACT (stunned, feign death, confused, fleeing).
        (WorldRuntime w, _, CreatureMapSystem system, Creature wolf, Player player, _) = Start(movementType: 0);
        using WorldRuntime world = w;

        wolf.UnitFlags |= UnitFlags.Stunned;
        Assert.False(system.Distract(wolf, 0f, 10000));
        wolf.UnitFlags &= ~UnitFlags.Stunned;

        Assert.True(system.AttackStart(wolf, player));
        Assert.False(system.Distract(wolf, 0f, 10000));
        Assert.Equal(MovementGeneratorType.Chase, wolf.Motion.CurrentType);
    }

    [Fact]
    public void TheDistractSpellEffect_HoldsTheCreature_ForTheEffectValueInSeconds_FacingTheDestination()
    {
        // Shaped like rogue Distract (1725): SPELL_EFFECT_DISTRACT, 10 s, AttributesEx 0x20520 (NO_THREAT among them) and AttributesEx3
        // 0x20000 (NO_INITIAL_AGGRO), so the cast does not pull the creature. The real spell hits every enemy within 10 yd of the
        // destination (implicit target 16); a single enemy target keeps the test about the effect.
        const uint DistractSpell = 941725;
        using var kit = new SpellTestKit(SpellTestKit.Spell(DistractSpell,
            SpellTestKit.Effect(SpellEffectName.Distract, 10, SpellImplicitTarget.UnitEnemy)) with
        {
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            AttributesEx = (SpellAttributesEx)0x20520,
            AttributesEx3 = 0x20000,
        });
        Map map = kit.World.GetMap(0);
        var system = new CreatureMapSystem(map, Content([Template()], [Spawn(1, WolfEntry, 20, 0) with { Orientation = SpawnFacing }]), random: new Random(1));
        map.AddUpdater(system);
        kit.System.Units = new MapObjectResolver();
        (Player rogue, _) = kit.AddPlayer(1, 0, 0);
        kit.World.RunTick(50);
        Creature wolf = Assert.Single(system.Creatures);

        SpellCastTargets targets = SpellCastTargets.ForUnit(wolf.Guid);
        targets.Mask |= SpellCastTargetFlags.DestLocation;
        targets.Dest = (20f, 10f, wolf.Z); // due north of the wolf
        kit.System.CastSpell(rogue, DistractSpell, targets, triggered: true);

        Assert.Equal(MovementGeneratorType.Distract, wolf.Motion.CurrentType);
        Assert.Null(wolf.Combat.Victim);
        kit.World.RunTick(100);
        Assert.Equal(MathF.PI / 2, wolf.Orientation, 0.01f);

        Run(kit.World, 9700);
        Assert.Equal(MovementGeneratorType.Distract, wolf.Motion.CurrentType);
        Run(kit.World, 400);
        Assert.Equal(MovementGeneratorType.Idle, wolf.Motion.CurrentType);
    }
}
