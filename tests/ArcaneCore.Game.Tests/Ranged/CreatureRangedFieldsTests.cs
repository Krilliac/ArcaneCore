using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.Ranged;

/// <summary>
/// Ranged lane S08: creatures carry ranged min/max damage and ranged attack power, so the 47 creatures that cast
/// WEAPON_DAMAGE ranged spells (Multi-Shot 14443-shaped, Piercing Shot, Exploding Shot ...) roll their template damage and
/// not the 5-damage fallback (vmangos Creature.cpp:1840-1843; classic-db creature_ai_scripts action 11).
/// </summary>
public sealed class CreatureRangedFieldsTests
{
    private static Creature Spawn(CreatureTemplate template)
    {
        CreatureSpawn spawn = CreatureTestKit.Spawn(1, template.Entry, 10, 10);
        CreatureContent content = Content([template], [spawn]);
        return new Creature(spawn.Guid, template, spawn, content, new Random(1));
    }

    private static CreatureTemplate Archer() => Template(WolfEntry) with
    {
        MinRangedDamage = 12f,
        MaxRangedDamage = 20f,
        RangedAttackPower = 40,
        RangedBaseAttackTime = 2200,
    };

    [Fact]
    public void TemplateRangedDamageAndAttackPower_ReachTheUpdateFields()
    {
        Creature creature = Spawn(Archer());

        Assert.Equal(12f, creature.GetFloat(UpdateFields.UnitFieldMinrangeddamage));
        Assert.Equal(20f, creature.GetFloat(UpdateFields.UnitFieldMaxrangeddamage));
        Assert.Equal(40, creature.GetInt32(UpdateFields.UnitFieldRangedAttackPower));
        Assert.Equal(2200u, creature.GetUInt32(UpdateFields.UnitFieldRangedattacktime)); // regression: already written before this lane
    }

    [Fact]
    public void ARangedWeaponRoll_StaysInsideTheTemplateRange_NotTheFallback()
    {
        using var kit = new SpellTestKit();
        Creature creature = Spawn(Archer());

        for (int i = 0; i < 200; i++)
        {
            float roll = kit.System.WeaponDamageRoll(creature, WeaponAttackType.RangedAttack, normalized: false);
            Assert.InRange(roll, 12f, 20f);
        }
    }

    [Fact]
    public void ATemplateWithoutRangedDamage_KeepsTheFallbackForCreaturesThatNeverShoot()
    {
        using var kit = new SpellTestKit();
        Creature creature = Spawn(Template(WolfEntry));

        Assert.Equal(0f, creature.GetFloat(UpdateFields.UnitFieldMaxrangeddamage));
        float roll = kit.System.WeaponDamageRoll(creature, WeaponAttackType.RangedAttack, normalized: false);
        Assert.InRange(roll, 0f, CombatConstants.FallbackMaxDamage);
    }

    [Fact]
    public void ANormalizedMeleeRoll_OfACreature_IgnoresItsAttackPower()
    {
        // vmangos Unit::CalculateDamage normalizes only for a player (Unit.cpp:2295): a creature's min/max damage already include its attack power.
        using var kit = new SpellTestKit();
        Creature creature = Spawn(Template(WolfEntry) with { MinMeleeDamage = 10f, MaxMeleeDamage = 10f, MeleeAttackPower = 1400, MeleeBaseAttackTime = 1000 });
        Assert.Equal(1400, creature.GetInt32(UpdateFields.UnitFieldAttackPower));

        Assert.Equal(10f, kit.System.WeaponDamageRoll(creature, WeaponAttackType.BaseAttack, normalized: true));
    }
}
