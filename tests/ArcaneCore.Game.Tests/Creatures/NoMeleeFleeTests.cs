using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Creatures;

/// <summary>
/// Static flag NO_MELEE_FLEE (0x00100000; vmangos CREATURE_STATIC_FLAG_NO_MELEE, original comment "Flee"): the creature never swings
/// (both references), and when a player or a player's pet engages it, it runs in panic for 30 s and then evades (cmangos
/// Unit::SetInCombatWithVictim, Entities/Unit.cpp:7993-7998: DoFlee(30000) with ORDER_CRITTER_FLEE; CreatureAI::TimedFleeingEnded,
/// AI/BaseAI/CreatureAI.cpp:254-258). Not when it was created by a spell, is rooted, already flees or is casting, nor against a creature.
/// The panic flight is the cmangos rule behind <c>Creatures:NoMeleeFleeOnAggro</c>, off by default: vmangos, the fidelity reference,
/// only takes the melee away (critters still run through CritterAI).
/// </summary>
public sealed class NoMeleeFleeTests
{
    private const uint NoMeleeFlee = 0x00100000;
    private const uint FleerEntry = 7001;

    private static CreatureTemplate Fleer(uint staticFlags = NoMeleeFlee) => Template(FleerEntry) with { StaticFlags1 = staticFlags };

    private static CreatureOptions Panic() => new() { AiRelocationNotifyDelayMs = 3_600_000, NoMeleeFleeOnAggro = true };

    private static (WorldRuntime World, Map Map, CreatureMapSystem System, Creature Fleer, Player Player) Start(CreatureOptions? options = null)
    {
        CreatureContent content = Content([Fleer()], [Spawn(1, FleerEntry, 3, 0)]);
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Hostility = new AlwaysHostile() },
            options ?? Panic());
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        return (world, map, system, Assert.Single(system.Creatures), player);
    }

    [Fact]
    public void APlayersHit_SendsItRunningInPanic_WithoutASwing_AndItEvadesAfter30Seconds()
    {
        (WorldRuntime w, Map map, _, Creature fleer, Player player) = Start();
        using WorldRuntime world = w;

        map.Combat.DealDamage(player, fleer, 1, direct: false);

        Assert.Equal(MovementGeneratorType.Fleeing, fleer.Motion.CurrentType);
        Assert.True((fleer.UnitFlags & UnitFlags.Fleeing) != 0);
        Assert.False(fleer.Combat.IsMeleeAttacking);

        Run(world, 29_000);
        Assert.Equal(MovementGeneratorType.Fleeing, fleer.Motion.CurrentType);
        Assert.True(fleer.Combat.IsInCombat);

        Run(world, 1_200);
        Assert.True(fleer.IsInEvadeMode);
        Assert.Equal(MovementGeneratorType.Home, fleer.Motion.CurrentType);
        Assert.False(fleer.Combat.IsInCombat);
    }

    [Fact]
    public void ByDefault_AsInVmangos_ItOnlyStopsSwinging()
    {
        (WorldRuntime w, Map map, _, Creature fleer, Player player) = Start(new CreatureOptions { AiRelocationNotifyDelayMs = 3_600_000 });
        using WorldRuntime world = w;

        map.Combat.DealDamage(player, fleer, 1, direct: false);

        Assert.Equal(MovementGeneratorType.Chase, fleer.Motion.CurrentType);
        Assert.False(fleer.Combat.IsMeleeAttacking);
    }

    [Fact]
    public void ARootedFleer_StaysPut()
    {
        (WorldRuntime w, Map map, _, Creature fleer, Player player) = Start();
        using WorldRuntime world = w;
        fleer.AddMovementFlags(MovementFlags.Root);

        map.Combat.DealDamage(player, fleer, 1, direct: false);

        Assert.NotEqual(MovementGeneratorType.Fleeing, fleer.Motion.CurrentType);
    }

    [Fact]
    public void ACreatureAttacker_DoesNotFrightenIt()
    {
        (WorldRuntime w, Map map, CreatureMapSystem system, Creature fleer, _) = Start();
        using WorldRuntime world = w;
        Creature wolf = system.SpawnTemporary(Template(), 6, 0, 83.5f, 0);

        map.Combat.DealDamage(wolf, fleer, 1, direct: false);

        Assert.True(fleer.Combat.IsInCombat);
        Assert.NotEqual(MovementGeneratorType.Fleeing, fleer.Motion.CurrentType);
    }

    [Fact]
    public void AFleerWithoutTheFlag_Fights()
    {
        CreatureContent content = Content([Fleer(staticFlags: 0)], [Spawn(1, FleerEntry, 3, 0)]);
        (WorldRuntime w, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Hostility = new AlwaysHostile() });
        using WorldRuntime world = w;
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        Creature fighter = Assert.Single(system.Creatures);

        map.Combat.DealDamage(player, fighter, 1, direct: false);

        Assert.Equal(MovementGeneratorType.Chase, fighter.Motion.CurrentType);
        Assert.True(fighter.Combat.IsMeleeAttacking);
    }
}
