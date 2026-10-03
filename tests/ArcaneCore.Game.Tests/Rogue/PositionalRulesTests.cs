using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.Rogue;

/// <summary>Unit::IsBehindTarget (vmangos Unit.cpp:2806-2821), IsFromBehindOnlySpell (SpellEntry.h:909-912) and the Gouge facing shape (Spell.cpp:5646).</summary>
public sealed class PositionalRulesTests
{
    private static (WorldRuntime World, Map Map) World()
    {
        (WorldRuntime world, Map map, _, _) = CombatTestKit.CreateWorld();
        return (world, map);
    }

    /// <summary>A victim facing east (+X) at the origin and an attacker placed at (x, y).</summary>
    private static (Player Target, Player Attacker) Pair(WorldRuntime world, float attackerX, float attackerY)
    {
        Player target = CombatTestKit.AddPlayer(world, 1, 0, 0, new FakeSession(1));
        Player attacker = CombatTestKit.AddPlayer(world, 2, attackerX, attackerY, new FakeSession(2));
        target.Relocate(0, 0, 83.5f, 0f, 0);
        return (target, attacker);
    }

    [Theory]
    [InlineData(-3f, 0f, true)]   // directly behind
    [InlineData(3f, 0f, false)]   // directly in front
    [InlineData(0.01f, 3f, false)] // just inside 90 degrees: still in front
    [InlineData(-3f, 3f, true)]
    public void IsBehindTarget_ReflectsTheTargetsFrontArc(float x, float y, bool behind)
    {
        (WorldRuntime world, _) = World();
        using WorldRuntime w = world;
        (Player target, Player attacker) = Pair(world, x, y);

        Assert.Equal(behind, PositionalRules.IsBehindTarget(attacker, target, strict: false));
        Assert.Equal(!behind, PositionalRules.HasInArc(target, attacker));
    }

    [Fact]
    public void Strict_AFightingCreatureIsTreatedAsFacingItsVictim_UnlessIncapacitated()
    {
        (WorldRuntime world, Map map) = World();
        using WorldRuntime w = world;
        Player rogue = CombatTestKit.AddPlayer(world, 1, -3, 0, new FakeSession(1));
        var mob = new CombatTestUnit();
        mob.Spawn(map, 0, 0, 83.5f, orientation: 0f); // facing east: the rogue at -3 is geometrically behind it
        Assert.True(PositionalRules.IsBehindTarget(rogue, mob, strict: false));
        Assert.True(PositionalRules.IsBehindTarget(rogue, mob, strict: true)); // not fighting the rogue yet

        Assert.True(map.Combat.Attack(mob, rogue)); // the creature now fights the rogue
        Assert.False(PositionalRules.IsBehindTarget(rogue, mob, strict: true)); // it always faces its victim
        Assert.True(PositionalRules.IsBehindTarget(rogue, mob, strict: false)); // the non-strict check is purely geometric

        foreach (UnitFlags flag in new[] { UnitFlags.Stunned, UnitFlags.Confused, UnitFlags.Fleeing, UnitFlags.Possessed })
        {
            mob.UnitFlags = flag;
            Assert.True(PositionalRules.IsBehindTarget(rogue, mob, strict: true), flag.ToString());
        }

        mob.UnitFlags = UnitFlags.None;
        Assert.False(PositionalRules.IsBehindTarget(rogue, mob, strict: true));
    }

    [Fact]
    public void Strict_DoesNotAffectPlayerTargets()
    {
        (WorldRuntime world, Map map) = World();
        using WorldRuntime w = world;
        (Player target, Player attacker) = Pair(world, -3, 0);
        map.Combat.Hooks = new AllHostile();
        Assert.True(map.Combat.Attack(target, attacker));

        Assert.True(PositionalRules.IsBehindTarget(attacker, target, strict: true)); // the exception is creature-only
    }

    [Theory]
    [InlineData(0x100000u, 0x200u, true)]   // Backstab, Ambush, Garrote shape
    [InlineData(0x100000u, 0x202u, true)]   // other AttributesEx bits do not matter, 0x200 must be set
    [InlineData(0x100000u, 0x0u, false)]    // Cheap Shot shape: no 0x200
    [InlineData(0x100001u, 0x200u, false)]  // AttributesEx2 must equal 0x100000 exactly
    [InlineData(0x0u, 0x200u, false)]
    public void IsFromBehindOnly_NeedsEx2Exactly0x100000AndTheExBit(uint ex2, uint ex, bool expected)
    {
        var spell = new SpellInfo { Id = 53, AttributesEx = (SpellAttributesEx)ex, AttributesEx2 = (SpellAttributesEx2)ex2 };
        Assert.Equal(expected, PositionalRules.IsFromBehindOnly(spell));
    }

    [Theory]
    [InlineData(0x150010u, true)]   // Gouge
    [InlineData(0x150011u, false)]
    [InlineData(0x10u, false)]
    public void IsFacingRequired_NeedsAttributesExactly0x150010(uint attributes, bool expected)
        => Assert.Equal(expected, PositionalRules.IsFacingRequired(new SpellInfo { Id = 1776, Attributes = (SpellAttributes)attributes }));

    private sealed class AllHostile : CombatHooks
    {
        public override bool IsFriendly(Unit a, Unit b) => false;

        public override bool CanAttack(Unit attacker, Unit victim) => !ReferenceEquals(attacker, victim);
    }
}
