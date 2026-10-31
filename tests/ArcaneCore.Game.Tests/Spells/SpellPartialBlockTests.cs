using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.CombatCore;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// The partial block of a melee or ranged class spell (vmangos Unit::CalculateAbsorbResistBlock, Unit.cpp:2205-2224, IsSpellPartiallyBlocked
/// :2351-2374, RollSpellBlockChanceOutcome :2376-2395): a victim that faces the caster and rolls under its block chance takes its shield
/// block value (a creature: level / 2 + strength / 20) off the direct damage before absorb and resist.
/// </summary>
public sealed class SpellPartialBlockTests
{
    private const uint MeleeStrike = 905100;
    private const uint RangedShot = 905101;
    private const uint MagicBolt = 905102;
    private const uint Ambush = 905103;
    private const uint Unblockable = 905104;

    /// <summary>A random source whose double is a fixed fraction.</summary>
    private sealed class FixedRandom(double value) : Random
    {
        public override double NextDouble() => value;

        public override int Next(int minValue, int maxValue) => minValue;
    }

    private sealed class MapUnitResolver : ISpellUnitResolver
    {
        public Unit? Find(Unit reference, ObjectGuid guid)
            => reference.Guid == guid ? reference : reference.Map?.FindObject(guid) as Unit;
    }

    private static SpellTestKit Kit() => new(
        Spell(MeleeStrike, Effect(SpellEffectName.SchoolDamage, 100, SpellImplicitTarget.UnitEnemy)) with { DamageClass = SpellDamageClass.Melee },
        Spell(RangedShot, Effect(SpellEffectName.SchoolDamage, 100, SpellImplicitTarget.UnitEnemy)) with { DamageClass = SpellDamageClass.Ranged },
        Spell(MagicBolt, Effect(SpellEffectName.SchoolDamage, 100, SpellImplicitTarget.UnitEnemy)) with { DamageClass = SpellDamageClass.Magic },
        Spell(Ambush, Effect(SpellEffectName.SchoolDamage, 100, SpellImplicitTarget.UnitEnemy)) with { DamageClass = SpellDamageClass.Melee, Attributes = (SpellAttributes)SpellAttributesCombat.NoActiveDefense },
        Spell(Unblockable, Effect(SpellEffectName.SchoolDamage, 100, SpellImplicitTarget.UnitEnemy)) with { DamageClass = SpellDamageClass.Melee, AttributesEx3 = (uint)SpellAttributesEx3Combat.CompletelyBlocked });

    /// <summary>The caster at the origin and a level 60 creature two yards away facing <paramref name="facing"/> (pi looks at the caster).</summary>
    private static (Player Caster, CombatTestUnit Victim) Scene(SpellTestKit kit, float facing, double roll)
    {
        (Player caster, _) = kit.AddPlayer(1);
        var victim = new CombatTestUnit { MaxHealth = 10_000, Health = 10_000 };
        victim.Relocate(2, 0, caster.Z, facing, 0);
        caster.Map!.AddObject(victim);
        victim.Map!.Combat.Track(victim);
        kit.World.RunTick(0);
        kit.System.Units = new MapUnitResolver();
        kit.System.Random = new FixedRandom(roll);
        return (caster, victim);
    }

    [Theory]
    [InlineData(MeleeStrike, 70u)]  // 100 - (60 / 2) block value
    [InlineData(RangedShot, 70u)]
    [InlineData(MagicBolt, 100u)]   // a magic class spell is never partially blocked
    [InlineData(Ambush, 100u)]      // SPELL_ATTR_NO_ACTIVE_DEFENSE
    [InlineData(Unblockable, 100u)] // EX3_COMPLETELY_BLOCKED is decided by the hit roll, not here
    public void AFacingVictimThatRollsUnderItsBlockChance_BlocksTheShieldValueOfMeleeAndRangedSpells(uint spellId, uint expectedDealt)
    {
        using SpellTestKit kit = Kit();
        (Player caster, CombatTestUnit victim) = Scene(kit, MathF.PI, roll: 0.0);

        SpellDamageResult result = kit.System.DealDirectDamage(caster, victim, kit.Store.Get(spellId)!, 100, allowCrit: false);

        Assert.Equal(expectedDealt, result.Dealt);
        Assert.Equal(10_000u - expectedDealt, victim.Health);
    }

    [Fact]
    public void AVictimLookingAway_DoesNotBlock()
    {
        using SpellTestKit kit = Kit();
        (Player caster, CombatTestUnit victim) = Scene(kit, 0f, roll: 0.0);

        Assert.Equal(100u, kit.System.DealDirectDamage(caster, victim, kit.Store.Get(MeleeStrike)!, 100, allowCrit: false).Dealt);
    }

    [Fact]
    public void ARollAboveTheFivePercentCreatureCap_DoesNotBlock()
    {
        using SpellTestKit kit = Kit();
        (Player caster, CombatTestUnit victim) = Scene(kit, MathF.PI, roll: 0.06);

        Assert.Equal(100u, kit.System.DealDirectDamage(caster, victim, kit.Store.Get(MeleeStrike)!, 100, allowCrit: false).Dealt);
    }

    [Fact]
    public void ACreatureWithTheNoBlockBehaviour_DoesNotBlock()
    {
        using SpellTestKit kit = Kit();
        (Player caster, CombatTestUnit victim) = Scene(kit, MathF.PI, roll: 0.0);
        victim.CanBlock = false;

        Assert.Equal(100u, kit.System.DealDirectDamage(caster, victim, kit.Store.Get(MeleeStrike)!, 100, allowCrit: false).Dealt);
    }

    [Fact]
    public void TheBlockedValue_IsCappedAtTheDamage()
    {
        using SpellTestKit kit = Kit();
        (Player caster, CombatTestUnit victim) = Scene(kit, MathF.PI, roll: 0.0);

        Assert.Equal(0u, kit.System.DealDirectDamage(caster, victim, kit.Store.Get(MeleeStrike)!, 20, allowCrit: false).Dealt); // 20 < 30
    }
}
