using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Tests.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.SpellRules;

/// <summary>
/// Binary-spell classification (vmangos SpellMgr.cpp:3342-3393) and the resist chance
/// (vmangos SpellCaster::GetSpellResistChance, SpellCaster.cpp:882-925).
/// </summary>
public sealed class SpellBinaryAndResistTests
{
    private const uint Penetration = 900_201;

    private static SpellInfo WithAura(AuraType aura, SpellSchool school = SpellSchool.Frost, bool withDamage = false)
    {
        SpellEffectInfo apply = SpellTestKit.Effect(SpellEffectName.ApplyAura, 1, SpellImplicitTarget.UnitEnemy, aura);
        SpellEffectInfo[] effects = withDamage
            ? [SpellTestKit.Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy), apply]
            : [apply];
        return SpellTestKit.Spell(1, effects) with { School = school, DamageClass = SpellDamageClass.Magic };
    }

    [Theory]
    [InlineData(AuraType.ModDecreaseSpeed, true)]
    [InlineData(AuraType.ModFear, true)]
    [InlineData(AuraType.ModStun, true)]
    [InlineData(AuraType.ModPacify, true)]
    [InlineData(AuraType.ModRoot, true)]
    [InlineData(AuraType.ModSilence, true)]
    [InlineData(AuraType.ModDisarm, true)]
    [InlineData(AuraType.ModResistance, true)]
    [InlineData(AuraType.ModDamageTaken, true)]
    [InlineData(AuraType.PeriodicHeal, false)]   // Renew-shaped: binary in the old effect-list rule, not in vmangos
    [InlineData(AuraType.PeriodicDamage, false)]
    [InlineData(AuraType.Dummy, false)]
    [InlineData(AuraType.Transform, false)]      // Polymorph: not in vmangos' list (recorded open question)
    public void IsBinary_FollowsTheVmangosAuraList(AuraType aura, bool expected)
    {
        Assert.Equal(expected, VanillaSpellCombatRules.IsBinary(WithAura(aura, withDamage: true)));
    }

    [Fact]
    public void IsBinary_InterruptCastAndKnockBack_AreBinary_PhysicalAndNonMagicAreNot()
    {
        SpellInfo counterspell = SpellTestKit.Spell(1, SpellTestKit.Effect(SpellEffectName.InterruptCast, 0, SpellImplicitTarget.UnitEnemy))
            with { School = SpellSchool.Arcane, DamageClass = SpellDamageClass.Magic };
        SpellInfo knockback = SpellTestKit.Spell(1, SpellTestKit.Effect(SpellEffectName.KnockBack, 0, SpellImplicitTarget.UnitEnemy))
            with { School = SpellSchool.Nature, DamageClass = SpellDamageClass.Magic };

        Assert.True(VanillaSpellCombatRules.IsBinary(counterspell));
        Assert.True(VanillaSpellCombatRules.IsBinary(knockback));
        Assert.False(VanillaSpellCombatRules.IsBinary(counterspell with { School = SpellSchool.Normal }));
        Assert.False(VanillaSpellCombatRules.IsBinary(counterspell with { DamageClass = SpellDamageClass.Melee }));
        Assert.False(VanillaSpellCombatRules.IsBinary(counterspell with { DamageClass = SpellDamageClass.None }));
    }

    [Theory]
    [InlineData(26143u)]
    [InlineData(26478u)]
    public void IsBinary_TheTwoHardcodedCthunSpells(uint id)
    {
        SpellInfo plainDamage = RuleTestSupport.Magic(id, SpellSchool.Shadow);

        Assert.True(VanillaSpellCombatRules.IsBinary(plainDamage));
        Assert.False(VanillaSpellCombatRules.IsBinary(plainDamage with { Id = id + 1 }));
    }

    [Fact]
    public void IsBinary_FrostboltShape_DamagePlusSnare_IsBinary_FireballShape_IsNot()
    {
        Assert.True(VanillaSpellCombatRules.IsBinary(WithAura(AuraType.ModDecreaseSpeed, withDamage: true)));
        Assert.False(VanillaSpellCombatRules.IsBinary(WithAura(AuraType.PeriodicDamage, SpellSchool.Fire, withDamage: true)));
        Assert.False(VanillaSpellCombatRules.IsBinary(RuleTestSupport.Magic(1, SpellSchool.Shadow)));
    }

    [Fact]
    public void InnateResistance_IsScaledByCasterLevelOver63_AndOnlyAppliesToCreatures()
    {
        using var kit = new SpellTestKit();
        (Player caster, Player player) = (kit.AddPlayer(1).Player, kit.AddPlayer(2, 2).Player);
        caster.Level = 60;
        player.Level = 63;
        Creature creature = FoundationTests.MakeCreature(0, 63);

        // int(8 * 3 * 60 / 63) = 22 resistance, converted at 0.15 / level.
        Assert.Equal(22 * 0.15f / 60f, VanillaSpellCombatRules.AverageResistFraction(caster, creature, SpellSchool.Fire), 5);
        Assert.Equal(0f, VanillaSpellCombatRules.AverageResistFraction(caster, player, SpellSchool.Fire));
    }

    [Fact]
    public void InnateResistance_OfAWorldBoss_UsesTheBossRelativeLevel()
    {
        using var kit = new SpellTestKit();
        (Player caster, _) = kit.AddPlayer(1);
        caster.Level = 60;
        Creature boss = FoundationTests.MakeCreature(3, 60);

        Assert.Equal(22 * 0.15f / 60f, VanillaSpellCombatRules.AverageResistFraction(caster, boss, SpellSchool.Fire), 5);
    }

    [Theory]
    [InlineData(200, 0.5f)]
    [InlineData(400, 0.75f)] // capped
    [InlineData(-150, -0.5f)] // vulnerability: -150 / (5 * 60)
    [InlineData(-900, -0.75f)]
    public void ResistChance_FromTheVictimsResistance(int resistance, float expected)
    {
        using var kit = new SpellTestKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        caster.Level = 60;
        target.Level = 60;
        target.SetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Fire, resistance);

        Assert.Equal(expected, VanillaSpellCombatRules.AverageResistFraction(caster, target, SpellSchool.Fire), 3);
    }

    [Fact]
    public void Penetration_SubtractsFromTheVictimsResistance_ButNeverGoesNegative()
    {
        using var kit = new SpellTestKit(RuleTestSupport.Grant(Penetration, AuraType.ModTargetResistance, -100, 1 << (int)SpellSchool.Fire));
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        caster.Level = 60;
        target.Level = 60;
        RuleTestSupport.Apply(kit, caster, Penetration);
        var rules = new VanillaSpellCombatRules();

        target.SetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Fire, 200);
        Assert.Equal(0.25f, rules.ResistChance(kit.System, caster, target, SpellSchool.Fire, innateResists: true), 3);
        target.SetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Fire, 50);
        Assert.Equal(0f, rules.ResistChance(kit.System, caster, target, SpellSchool.Fire, innateResists: true)); // 1.12 clamp (SpellCaster.cpp:896)
    }

    [Fact]
    public void HolyDamage_IsPartiallyResisted_UnlessTheCmangosRuleIsSelected()
    {
        using var kit = new SpellTestKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        caster.Level = 60;
        target.Level = 60;
        target.SetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Holy, 200);
        SpellInfo holy = RuleTestSupport.Magic(1, SpellSchool.Holy);

        Assert.Equal(50u, new VanillaSpellCombatRules().RollPartialResist(kit.System, caster, target, holy, 100));
        var cmangos = new VanillaSpellCombatRules { Options = new SpellRuleOptions { IgnoreHolyResistance = true } };
        Assert.Equal(0u, cmangos.RollPartialResist(kit.System, caster, target, holy, 100));
    }

    [Fact]
    public void PartialResist_DoesNotRequireDamageClassMagic_ButNeverAppliesToBinaryOrIgnoreResistances()
    {
        using var kit = new SpellTestKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        caster.Level = 60;
        target.Level = 60;
        target.SetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Fire, 200);
        var rules = new VanillaSpellCombatRules();
        SpellInfo dot = RuleTestSupport.Magic(1, SpellSchool.Fire) with { DamageClass = SpellDamageClass.None };

        Assert.Equal(50u, rules.RollPartialResist(kit.System, caster, target, dot, 100)); // Unit.cpp:1936-1946 tests the school, not the class
        Assert.Equal(0u, rules.RollPartialResist(kit.System, caster, target, dot with { AttributesEx4 = SpellRuleFlags.Ex4IgnoreResistances }, 100));
        Assert.Equal(0u, rules.RollPartialResist(kit.System, caster, target, WithAura(AuraType.ModStun, SpellSchool.Fire), 100));
        Assert.Equal(0u, rules.RollPartialResist(kit.System, caster, target, dot with { School = SpellSchool.Normal }, 100));
    }
}
