using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Tests.SpellRules;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests;

/// <summary>vmangos Unit.cpp:1463-1479,1510-1565,1920-2083: block precedes school/mana absorb.</summary>
public sealed class CombatMeleeAbsorbTests
{
    private const uint Shield = 989_100;
    private const uint HitAura = 989_101;
    private const uint FragileAura = 989_102;

    private sealed class BlockStats(uint value) : ICombatStatSource
    {
        public bool? HasOffhandWeapon(Unit unit) => null;
        public bool? PlayerCanParry(Player player) => false;
        public bool? PlayerCanBlock(Player player) => true;
        public uint? ShieldBlockValue(Unit unit) => value;
    }

    [Fact]
    public void WhiteSwingAbsorbsAfterBlockAndReportsTheRemainingDamage()
    {
        SpellInfo shield = RuleTestSupport.Grant(Shield, AuraType.SchoolAbsorb, 40, (int)SpellSchoolMasks.Of(SpellSchool.Normal));
        using var kit = new SpellTestKit(shield);
        (Player attacker, FakeSession session) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2);
        MapCombat combat = attacker.Map!.Combat;
        combat.SpellMitigation = kit.System;
        attacker.SetFloat(UpdateFields.UnitFieldMindamage, 50);
        attacker.SetFloat(UpdateFields.UnitFieldMaxdamage, 50);
        victim.Health = victim.MaxHealth = 1000;
        RuleTestSupport.Apply(kit, victim, Shield);
        var random = new ScriptedRandom();
        random.Ints.Enqueue(5000); // ordinary hit, past miss and crit
        combat.Random = random;
        session.Clear();

        MeleeDamageInfo hit = combat.AttackerStateUpdate(attacker, victim, WeaponAttackType.BaseAttack)!;

        Assert.Equal(40u, hit.Absorbed);
        Assert.Equal(10u, hit.TotalDamage);
        Assert.Equal(990u, victim.Health);
        Assert.True(hit.HitInfo.HasFlag(HitInfo.Absorb));
        Assert.False(kit.System.HasAura(victim, Shield));
    }

    [Theory]
    [InlineData(20u, 30u, 10)]
    [InlineData(60u, 0u, 40)]
    public void BlockValueIsRemovedBeforeTheAbsorbShield(uint blockValue, uint expectedAbsorb, int expectedShield)
    {
        SpellInfo shield = RuleTestSupport.Grant(Shield, AuraType.SchoolAbsorb, 40, (int)SpellSchoolMasks.Of(SpellSchool.Normal));
        using var kit = new SpellTestKit(shield);
        (Player attacker, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2);
        MapCombat combat = attacker.Map!.Combat;
        combat.SpellMitigation = kit.System;
        combat.Stats = new BlockStats(blockValue);
        attacker.SetFloat(UpdateFields.UnitFieldMindamage, 50);
        attacker.SetFloat(UpdateFields.UnitFieldMaxdamage, 50);
        victim.SetFloat(UpdateFields.PlayerBlockPercentage, 100f);
        victim.SetByte(UpdateFields.UnitFieldBytes2, 0, 1); // shield equipped / sheathed
        victim.Relocate(2, 0, victim.Z, MathF.PI, 0); // faces the attacker
        RuleTestSupport.Apply(kit, victim, Shield);
        var random = new ScriptedRandom();
        random.Ints.Enqueue(500); // after the 5% miss range, before the block range ends
        combat.Random = random;

        MeleeDamageInfo hit = combat.CalculateMeleeDamage(attacker, victim, WeaponAttackType.BaseAttack);

        Assert.Equal(MeleeHitOutcome.Block, hit.Outcome);
        Assert.Equal(Math.Min(blockValue, 50u), hit.Blocked);
        Assert.Equal(expectedAbsorb, hit.Absorbed);
        Assert.Equal(0u, hit.TotalDamage);
        Assert.Equal(expectedShield, kit.System.GetAuras(victim).Single(h => h.Spell.Id == Shield).Auras.OfType<SpellAura>().Single().Amount);
    }

    [Fact]
    public void WhiteSwingHitTableReadsTheAttackersHitAura()
    {
        using var kit = new SpellTestKit(RuleTestSupport.Grant(HitAura, AuraType.ModHitChance, 3));
        (Player attacker, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2);
        MapCombat combat = attacker.Map!.Combat;
        combat.SpellMitigation = kit.System;
        RuleTestSupport.Apply(kit, attacker, HitAura);

        MeleeRollInput input = combat.BuildRollInput(attacker, victim, WeaponAttackType.BaseAttack);
        Assert.Equal(3f, input.HitBonus);
        Assert.Equal(2f, MeleeHitTable.MissChance(input, input.AttackerWeaponSkill - input.VictimDefenseSkill));
    }

    [Fact]
    public void FullyAbsorbedWhiteSwingStillBreaksDamageInterruptAuras()
    {
        SpellInfo shield = RuleTestSupport.Grant(Shield, AuraType.SchoolAbsorb, 100, (int)SpellSchoolMasks.Of(SpellSchool.Normal));
        SpellInfo fragile = RuleTestSupport.Grant(FragileAura, AuraType.ModDamageDone, 1)
            with { AuraInterruptFlags = SpellAuraInterruptFlags.Damage };
        using var kit = new SpellTestKit(shield, fragile);
        (Player attacker, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2);
        MapCombat combat = attacker.Map!.Combat;
        combat.SpellMitigation = kit.System;
        attacker.SetFloat(UpdateFields.UnitFieldMindamage, 50);
        attacker.SetFloat(UpdateFields.UnitFieldMaxdamage, 50);
        victim.Health = victim.MaxHealth = 1000;
        RuleTestSupport.Apply(kit, victim, Shield);
        RuleTestSupport.Apply(kit, victim, FragileAura);
        var random = new ScriptedRandom();
        random.Ints.Enqueue(5000);
        combat.Random = random;

        MeleeDamageInfo hit = combat.AttackerStateUpdate(attacker, victim, WeaponAttackType.BaseAttack)!;

        Assert.Equal((0u, 50u), (hit.TotalDamage, hit.Absorbed));
        Assert.False(kit.System.HasAura(victim, FragileAura));
        Assert.Equal(1000u, victim.Health);
    }
}
