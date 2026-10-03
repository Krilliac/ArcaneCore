using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>
/// Creature::SelectHostileTarget as vmangos Unit::SelectHostileTarget (Objects/Unit.cpp:7544-7612): respawn pacify, taunt target,
/// NO_THREAT_LIST creatures, the stun / fear / confuse rule and the "attacker without threat" case that keeps a creature from
/// evading. Positions are set directly and the map clock is advanced by the world tick.
/// </summary>
public sealed class VictimSelectionTests
{
    private sealed record Fight(WorldRuntime World, Map Map, CreatureMapSystem System, Player First, Player Second, Creature Wolf) : IDisposable
    {
        public void Dispose() => World.Dispose();
    }

    private static Fight Start(CreatureTemplate? template = null)
    {
        CreatureContent content = Content([template ?? Template()], [Spawn(1, WolfEntry, 5, 0)]);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Hostility = new AlwaysHostile() });
        (Player first, _) = AddPlayer(runtime, 1, 0, 0);
        (Player second, _) = AddPlayer(runtime, 2, 0, 1);
        Creature wolf = Assert.Single(system.Creatures);
        wolf.AI!.CombatMovement = false; // stay where it is
        map.Combat.DealDamage(first, wolf, 1, direct: false);
        Assert.Same(first, wolf.Combat.Victim);
        return new Fight(runtime, map, system, first, second, wolf);
    }

    [Fact]
    public void ACreatureInItsRespawnPacify_ChoosesNothing_AndDoesNotEvadeOrSwitch()
    {
        using Fight f = Start();
        f.Wolf.Combat.Threat.AddThreat(f.Second, 5000); // would take aggro by far
        f.Wolf.PacifiedMs = 3000;

        Assert.False(f.System.SelectHostileTarget(f.Wolf));

        Assert.Same(f.First, f.Wolf.Combat.Victim);
        Assert.False(f.Wolf.IsInEvadeMode);
    }

    [Fact]
    public void ANoThreatListCreature_KeepsItsVictim_WithAnEmptyList_AndDoesNotEvade()
    {
        using Fight f = Start(Template() with { ExtraFlags = 0x800, ExtraFlagsDialect = CreatureExtraFlagsDialect.VMangos });
        Assert.True(f.Wolf.Combat.Threat.IsEmpty);

        Assert.True(f.System.SelectHostileTarget(f.Wolf));
        Run(f.World, 5000);

        Assert.True(f.Wolf.Combat.Threat.IsEmpty);
        Assert.Same(f.First, f.Wolf.Combat.Victim);
        Assert.False(f.Wolf.IsInEvadeMode);
    }

    [Fact]
    public void ATauntTarget_BeatsAHigherThreatEntry_AndTheListTakesOverWhenTheTaunterIsGone()
    {
        using Fight f = Start();
        f.Wolf.Combat.Threat.AddThreat(f.First, 1000);
        f.Wolf.Combat.Threat.AddThreat(f.Second, 10);
        f.Wolf.Combat.Threat.AddTauntCaster(f.Second);

        Assert.True(f.System.SelectHostileTarget(f.Wolf));
        Assert.Same(f.Second, f.Wolf.Combat.Victim);

        f.Second.Health = 0; // the taunter dies
        Assert.True(f.System.SelectHostileTarget(f.Wolf));
        Assert.Same(f.First, f.Wolf.Combat.Victim);
    }

    [Fact]
    public void AStunnedCreature_KeepsItsVictim_AndSwitchesOnceItRecovers()
    {
        using Fight f = Start();
        f.Wolf.Combat.Threat.AddThreat(f.Second, 5000);
        f.Wolf.UnitFlags |= UnitFlags.Stunned;

        Assert.True(f.System.SelectHostileTarget(f.Wolf));
        Assert.Same(f.First, f.Wolf.Combat.Victim);

        f.Wolf.UnitFlags &= ~UnitFlags.Stunned;
        Assert.True(f.System.SelectHostileTarget(f.Wolf));
        Assert.Same(f.Second, f.Wolf.Combat.Victim);
    }

    [Fact]
    public void AnAttackerWithoutThreat_KeepsANonChasingCreatureFromEvading_ButAChasingOneEvades()
    {
        using Fight f = Start();
        f.Wolf.Combat.Threat.Clear(); // the attacker never made damage: no threat entry
        Assert.True(f.Map.Combat.Attack(f.Second, f.Wolf));
        Assert.True(f.Wolf.Combat.IsInCombat);
        Assert.NotEqual(MovementGeneratorType.Chase, f.Wolf.Motion.CurrentType);

        Assert.False(f.System.SelectHostileTarget(f.Wolf));
        Assert.False(f.Wolf.IsInEvadeMode);

        f.Wolf.Motion.MoveChase(f.Second);
        Assert.Equal(MovementGeneratorType.Chase, f.Wolf.Motion.CurrentType);
        Assert.False(f.System.SelectHostileTarget(f.Wolf));
        Assert.True(f.Wolf.IsInEvadeMode);
    }

    [Fact]
    public void WhileATauntAuraIsUp_AnEmptyListDoesNotEvade()
    {
        using Fight f = Start();
        f.Wolf.Combat.Threat.Clear();
        f.Wolf.Combat.Threat.AddTauntCaster(f.Second);
        f.Second.Health = 0; // the taunter is no valid target: no taunt target, no threat list
        f.Wolf.Motion.MoveChase(f.First);

        Assert.False(f.System.SelectHostileTarget(f.Wolf));
        Assert.False(f.Wolf.IsInEvadeMode);
    }
}
