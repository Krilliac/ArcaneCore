using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Tests.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.SpellRules;

/// <summary>Spell crit chance and crit bonus (vmangos Unit::GetSpellCritChance Unit.cpp:5212-5316, SpellCaster::SpellCriticalDamageBonus :958-993, SpellCriticalHealingBonus :995-1024).</summary>
public sealed class SpellCritRulesTests
{
    private const uint Bolt = 900_101;
    private const uint DummyOnly = 900_102;
    private const uint Potion = 900_103;
    private const uint Healthstone = 900_104;
    private const uint Heal = 900_105;
    private const uint Strike = 900_106;
    private const uint SchoolCrit = 900_110;
    private const uint AttackerCrit = 900_111;
    private const uint VersusBeast = 900_112;
    private const uint GenericCrit = 900_113;
    private const uint MeleeAttackerCrit = 900_114;
    private const uint RangedAttackerCrit = 900_115;
    private const uint CritPercent = 900_116;

    private static SpellTestKit Kit() => new(
        RuleTestSupport.Magic(Bolt, SpellSchool.Fire),
        SpellTestKit.Spell(DummyOnly, SpellTestKit.Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.UnitEnemy)) with { DamageClass = SpellDamageClass.Magic, School = SpellSchool.Fire },
        RuleTestSupport.Magic(Potion, SpellSchool.Fire) with { SpellFamilyName = 13 },
        RuleTestSupport.Magic(Healthstone, SpellSchool.Fire) with { SpellFamilyName = 5, SpellFamilyFlags = 0x10000 },
        SpellTestKit.Spell(Heal, SpellTestKit.Effect(SpellEffectName.Heal, 10, SpellImplicitTarget.UnitFriend)) with { DamageClass = SpellDamageClass.Magic, School = SpellSchool.Holy },
        SpellTestKit.Spell(Strike, SpellTestKit.Effect(SpellEffectName.WeaponDamage, 5, SpellImplicitTarget.UnitEnemy)) with { DamageClass = SpellDamageClass.Melee },
        RuleTestSupport.Grant(SchoolCrit, AuraType.ModSpellCritChanceSchool, 7, 1 << (int)SpellSchool.Fire),
        RuleTestSupport.Grant(AttackerCrit, AuraType.ModAttackerSpellCritChance, 12, 1 << (int)SpellSchool.Fire),
        RuleTestSupport.Grant(VersusBeast, AuraType.ModCritPercentVersus, 10, 1),
        RuleTestSupport.Grant(GenericCrit, AuraType.ModSpellCritChance, 3),
        RuleTestSupport.Grant(MeleeAttackerCrit, AuraType.ModAttackerMeleeCritChance, 4),
        RuleTestSupport.Grant(RangedAttackerCrit, AuraType.ModAttackerRangedCritChance, 6),
        RuleTestSupport.Grant(CritPercent, AuraType.ModCritPercent, 3));

    [Fact]
    public void Creatures_NeverCritWithSpells_UnlessTheOptionRestoresIt()
    {
        using SpellTestKit kit = Kit();
        (Player target, _) = kit.AddPlayer(1);
        Creature caster = FoundationTests.MakeCreature(0, 20);
        SpellInfo bolt = kit.Store.Get(Bolt)!;

        Assert.Equal(0f, new VanillaSpellCombatRules().CritChance(kit.System, caster, bolt));
        var allowed = new VanillaSpellCombatRules { Options = new SpellRuleOptions { CreatureSpellCrit = true } };
        Assert.Equal(5f, allowed.CritChance(kit.System, caster, bolt));
        kit.System.Random = new ScriptedRandom(0);
        Assert.False(new VanillaSpellCombatRules().RollCrit(kit.System, caster, target, bolt));
    }

    [Fact]
    public void SpellsWithoutACrittingEffect_NeverCrit()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        var rules = new VanillaSpellCombatRules();
        kit.System.Random = new ScriptedRandom(0, 0);

        Assert.False(rules.RollCrit(kit.System, caster, target, kit.Store.Get(DummyOnly)!)); // dummy effect only (SpellEntry.h:1110-1121)
        Assert.True(rules.RollCrit(kit.System, caster, target, kit.Store.Get(Bolt)!));        // roll 0 < 5%
    }

    [Fact]
    public void PotionAndHealthstoneFamilies_CritTenPercent()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        var rules = new VanillaSpellCombatRules();

        Assert.Equal(10f, rules.CritChance(kit.System, caster, kit.Store.Get(Potion)!));
        Assert.Equal(10f, rules.CritChance(kit.System, caster, kit.Store.Get(Healthstone)!));
        Assert.Equal(5f, rules.CritChance(kit.System, caster, kit.Store.Get(Bolt)!));
    }

    [Fact]
    public void HostileMagic_AddsTheVictimsAttackerCrit_ButPositiveSpellsDoNot()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        RuleTestSupport.Apply(kit, target, AttackerCrit);
        var rules = new VanillaSpellCombatRules();

        Assert.Equal(17f, rules.CritChance(kit.System, caster, target, kit.Store.Get(Bolt)!));
        Assert.Equal(5f, rules.CritChance(kit.System, caster, target, kit.Store.Get(Heal)!));
    }

    [Fact]
    public void CasterAuras_AddFlatAndSchoolCrit_AndTheModifierSeamAppliesLast()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        RuleTestSupport.Apply(kit, caster, SchoolCrit);
        RuleTestSupport.Apply(kit, caster, GenericCrit);
        var rules = new VanillaSpellCombatRules();

        Assert.Equal(15f, rules.CritChance(kit.System, caster, target, kit.Store.Get(Bolt)!)); // 5 + 7 + 3
        var modded = new VanillaSpellCombatRules { Modifiers = new MagicHitChanceTests.AddModifier(SpellModOp.CriticalChance, 10f) };
        Assert.Equal(25f, modded.CritChance(kit.System, caster, target, kit.Store.Get(Bolt)!));
    }

    [Fact]
    public void TheStatsSeam_SuppliesTheBaseChance()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        var rules = new VanillaSpellCombatRules { CritSource = ISpellCritSource.Flat(8f) };

        Assert.Equal(8f, rules.CritChance(kit.System, caster, kit.Store.Get(Bolt)!));
    }

    [Fact]
    public void MeleeSpellsAgainstASittingPlayer_AlwaysCrit()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        var rules = new VanillaSpellCombatRules();
        SpellInfo strike = kit.Store.Get(Strike)!;

        Assert.True(rules.CritChance(kit.System, caster, target, strike) < 100f);
        target.StandState = StandState.Sit;
        Assert.Equal(100f, rules.CritChance(kit.System, caster, target, strike));
    }

    [Fact]
    public void CriticalDamage_AddsHalfForMagicAndAllForMelee_ThenTheVersusMultiplier()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        Creature beast = FoundationTests.MakeCreature(0, 5);
        var rules = new VanillaSpellCombatRules();
        SpellInfo bolt = kit.Store.Get(Bolt)!;
        SpellInfo strike = kit.Store.Get(Strike)!;

        Assert.Equal(151u, rules.CriticalDamage(kit.System, caster, target, bolt, 101));  // 101 + 101/2
        Assert.Equal(202u, rules.CriticalDamage(kit.System, caster, target, strike, 101)); // 101 + 101

        RuleTestSupport.Apply(kit, caster, VersusBeast);
        Assert.Equal(151u, rules.CriticalDamage(kit.System, caster, target, bolt, 101)); // a player target is humanoid
        Assert.Equal(166u, rules.CriticalDamage(kit.System, caster, beast, bolt, 101));  // (101 + 50) * 1.1 truncated
    }

    [Fact]
    public void CriticalDamage_ThroughTheTalentBonusSeam()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        var rules = new VanillaSpellCombatRules { Modifiers = new MagicHitChanceTests.AddModifier(SpellModOp.CritDamageBonus, 25f) };

        Assert.Equal(176u, rules.CriticalDamage(kit.System, caster, target, kit.Store.Get(Bolt)!, 101)); // 101 + (50 + 25)
    }

    [Fact]
    public void CriticalHeal_AddsHalf()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        var rules = new VanillaSpellCombatRules();

        Assert.Equal(151u, rules.CriticalHeal(kit.System, caster, target, kit.Store.Get(Heal)!, 101));
    }

    [Fact]
    public void MeleeSpells_AddTheVictimsMeleeAttackerCrit_RangedSpellsTheRangedOne()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        (Player plain, _) = kit.AddPlayer(3, 3);
        caster.Level = 60;
        target.Level = 60;
        plain.Level = 60;
        caster.SetFloat(UpdateFields.PlayerCritPercentage, 10f);
        caster.SetFloat(UpdateFields.PlayerRangedCritPercentage, 30f); // high enough that the skill-0 penalty (-12) does not floor at 0
        var rules = new VanillaSpellCombatRules();
        SpellInfo strike = kit.Store.Get(Strike)!;
        SpellInfo shot = strike with { DamageClass = SpellDamageClass.Ranged };
        RuleTestSupport.Apply(kit, target, MeleeAttackerCrit);

        Assert.Equal(14f, rules.CritChance(kit.System, caster, target, strike), 3); // 10 + 4, equal skills add nothing (Unit.cpp:2580-2590)
        float rangedBase = rules.CritChance(kit.System, caster, plain, shot);       // a player with no ranged weapon has weapon skill 0 (SpellCaster.cpp GetWeaponSkillValue)
        Assert.Equal(rangedBase, rules.CritChance(kit.System, caster, target, shot), 3); // the melee aura does not count for a ranged spell
        RuleTestSupport.Apply(kit, target, RangedAttackerCrit);
        Assert.Equal(rangedBase + 6f, rules.CritChance(kit.System, caster, target, shot), 3);
    }

    [Fact]
    public void AMeleeSpellAgainstAHigherLevelCreature_LosesCritFromTheSkillGap()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        caster.Level = 60;
        caster.SetFloat(UpdateFields.PlayerCritPercentage, 10f);
        Creature boss = FoundationTests.MakeCreature(0, 63);
        var rules = new VanillaSpellCombatRules();

        // weapon 300, defense 315: not ahead and the victim is no player, so the capped difference counts 0.2 per point (Unit.cpp:2590).
        Assert.Equal(7f, rules.CritChance(kit.System, caster, boss, kit.Store.Get(Strike)!), 3);
    }

    [Fact]
    public void AMeleeSpellAgainstALowerLevelPlayer_GainsCritAtFourHundredthsPerPoint()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        caster.Level = 60;
        target.Level = 50;
        caster.SetFloat(UpdateFields.PlayerCritPercentage, 10f);

        Assert.Equal(12f, new VanillaSpellCombatRules().CritChance(kit.System, caster, target, kit.Store.Get(Strike)!), 3); // 50 points * 0.04
    }

    [Fact]
    public void APlayersCritField_IsNotDoubleCountedWithTheCritPercentAura()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        caster.Level = 60;
        target.Level = 60;
        caster.SetFloat(UpdateFields.PlayerCritPercentage, 10f);
        RuleTestSupport.Apply(kit, caster, CritPercent);

        Assert.Equal(10f, new VanillaSpellCombatRules().CritChance(kit.System, caster, target, kit.Store.Get(Strike)!), 3); // the field already includes auras (Unit.cpp:2556-2577)
    }

    [Fact]
    public void ACreatureCaster_UsesFivePlusTheCritPercentAura()
    {
        using SpellTestKit kit = Kit();
        (Player target, _) = kit.AddPlayer(1);
        target.Level = 20;
        Creature caster = FoundationTests.MakeCreature(0, 20);
        kit.System.CastSpell(caster, CritPercent, SpellCastTargets.ForSelf(), triggered: true);
        var rules = new VanillaSpellCombatRules { Options = new SpellRuleOptions { CreatureSpellCrit = true } };

        Assert.Equal(8f, rules.CritChance(kit.System, caster, target, kit.Store.Get(Strike)!), 3); // 5 + 3
    }

    [Fact]
    public void TheCritChanceIsNeverNegative()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        caster.Level = 60;
        caster.SetFloat(UpdateFields.PlayerCritPercentage, 1f);
        Creature boss = FoundationTests.MakeCreature(0, 63);

        Assert.Equal(0f, new VanillaSpellCombatRules().CritChance(kit.System, caster, boss, kit.Store.Get(Strike)!)); // 1 - 3 floors at 0 (Unit.cpp:2592)
    }}
