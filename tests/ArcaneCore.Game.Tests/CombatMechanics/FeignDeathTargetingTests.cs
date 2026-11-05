using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Ranged;
using ArcaneCore.Game.Tests.CombatCore;
using Xunit;

namespace ArcaneCore.Game.Tests.CombatMechanics;

/// <summary>
/// vmangos Unit::IsValidAttackTarget (Unit.cpp:6158): a unit that is neither a player nor under a player's control cannot pick a victim
/// feigning death for a single-target attack (<c>!forAoE</c>, not helpful). Players and area effects are unaffected, so
/// <see cref="CombatHooks.CanAttack"/> itself stays true for the feigning victim.
/// </summary>
public sealed class FeignDeathTargetingTests
{
    private static (WorldRuntime World, Map Map, Player Feigner, Player Other, CombatTestUnit Mob) Scene()
    {
        (WorldRuntime world, Map map, ScriptedRandom _, TestCombatHooks _) = CombatTestKit.CreateWorld();
        Player feigner = CombatTestKit.AddPlayer(world, 1, 0, 0, new FakeSession(1));
        Player other = CombatTestKit.AddPlayer(world, 2, 0, 0, new FakeSession(2));
        var mob = new CombatTestUnit();
        mob.Spawn(map, 3, 0);
        return (world, map, feigner, other, mob);
    }

    [Fact]
    public void ACreature_CannotTargetAFeigningPlayer_ButCanTargetAnotherPlayer()
    {
        (WorldRuntime world, Map map, Player feigner, Player other, CombatTestUnit mob) = Scene();
        using WorldRuntime _ = world;
        Assert.True(map.Combat.Hooks.CanAttackSingleTarget(mob, feigner));

        feigner.SetFlag(UpdateFields.UnitDynamicFlags, UnitDynFlags.Dead);

        Assert.False(map.Combat.Hooks.CanAttackSingleTarget(mob, feigner));
        Assert.True(map.Combat.Hooks.CanAttackSingleTarget(mob, other));
    }

    [Fact]
    public void AreaEffects_ThroughCanAttack_StillSeeTheFeigningPlayer()
    {
        (WorldRuntime world, Map map, Player feigner, Player _, CombatTestUnit mob) = Scene();
        using WorldRuntime _ = world;
        feigner.SetFlag(UpdateFields.UnitDynamicFlags, UnitDynFlags.Dead);

        Assert.True(map.Combat.Hooks.CanAttack(mob, feigner));
    }

    [Fact]
    public void TheFeignEnds_WhenTheDeadFlagGoes()
    {
        (WorldRuntime world, Map map, Player feigner, Player _, CombatTestUnit mob) = Scene();
        using WorldRuntime _ = world;
        feigner.SetFlag(UpdateFields.UnitDynamicFlags, UnitDynFlags.Dead);
        feigner.RemoveFlag(UpdateFields.UnitDynamicFlags, UnitDynFlags.Dead);

        Assert.True(map.Combat.Hooks.CanAttackSingleTarget(mob, feigner));
    }
}
