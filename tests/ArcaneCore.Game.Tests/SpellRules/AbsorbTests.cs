using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.SpellRules;

/// <summary>School absorb, mana shield and damage splitting (vmangos Unit::CalculateDamageAbsorbAndResist, Unit.cpp:1920-2200; 1.12 order absorb, mana shield, split).</summary>
public sealed class AbsorbTests
{
    private const uint FireBolt = 980_001;
    private const uint FrostBolt = 980_002;
    private const uint ShieldAll = 980_010;
    private const uint ShieldFire = 980_011;
    private const uint ShieldSmall = 980_012;
    private const uint ShieldCharged = 980_013;
    private const uint ManaShield = 980_014;
    private const uint SplitFlat = 980_015;
    private const uint SplitPct = 980_016;
    private const uint SchoolImmune = 980_017;
    private const uint FireDot = 980_018;

    private static SpellInfo Bolt(uint id, SpellSchool school) =>
        SpellTestKit.Spell(id, SpellTestKit.Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy))
        with { School = school, DamageClass = SpellDamageClass.Magic };

    private static SpellInfo Shield(uint id, AuraType type, int amount, int misc, float multiple = 0f, uint charges = 0, bool onTarget = false)
    {
        SpellInfo spell = RuleTestSupport.Grant(id, type, amount, misc);
        SpellEffectInfo effect = spell.Effects[0] with { MultipleValue = multiple, TargetA = onTarget ? SpellImplicitTarget.Unit : SpellImplicitTarget.UnitCaster };
        return spell with { Effects = [effect], ProcCharges = charges, RangeIndex = onTarget ? 4u : spell.RangeIndex, Range = onTarget ? new SpellRange(0, 30) : spell.Range };
    }

    private static SpellTestKit Kit() => new(
        Bolt(FireBolt, SpellSchool.Fire),
        Bolt(FrostBolt, SpellSchool.Frost),
        Shield(ShieldAll, AuraType.SchoolAbsorb, 100, (int)SpellSchoolMasks.All),
        Shield(ShieldFire, AuraType.SchoolAbsorb, 100, (int)SpellSchoolMasks.Of(SpellSchool.Fire)),
        Shield(ShieldSmall, AuraType.SchoolAbsorb, 30, (int)SpellSchoolMasks.All),
        Shield(ShieldCharged, AuraType.SchoolAbsorb, 1000, (int)SpellSchoolMasks.All, charges: 1),
        Shield(ManaShield, AuraType.ManaShield, 50, (int)SpellSchoolMasks.All, multiple: 2f),
        Shield(SplitFlat, AuraType.SplitDamageFlat, 40, (int)SpellSchoolMasks.All, onTarget: true),
        Shield(SplitPct, AuraType.SplitDamagePct, 50, (int)SpellSchoolMasks.All, onTarget: true),
        Shield(SchoolImmune, AuraType.SchoolImmunity, (int)SpellSchoolMasks.All, (int)SpellSchoolMasks.All),
        RuleTestSupport.Grant(FireDot, AuraType.PeriodicDamage, 20) with
        {
            School = SpellSchool.Fire,
            Attributes = SpellAttributes.AuraIsDebuff,
            Effects = [SpellTestKit.Effect(SpellEffectName.ApplyAura, 20, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicDamage, amplitude: 3000)],
            Duration = new SpellDuration(30_000, 0, 30_000),
        });

    private static (Player Caster, Player Victim) Pair(SpellTestKit kit)
    {
        (Player caster, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2);
        victim.Health = victim.MaxHealth = 1000;
        return (caster, victim);
    }

    private static SpellDamageResult Hit(SpellTestKit kit, Unit caster, Unit victim, uint spell, uint damage) =>
        kit.System.DealDirectDamage(caster, victim, kit.Store.Get(spell)!, damage, allowCrit: false);

    private static int AbsorbLeft(SpellTestKit kit, Unit unit, uint spell) =>
        kit.System.GetAuras(unit).Single(h => h.Spell.Id == spell).Auras.OfType<SpellAura>().Single().Amount;

    [Fact]
    public void AShield_AbsorbsWhatItCanAndKeepsTheRest()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player victim) = Pair(kit);
        RuleTestSupport.Apply(kit, victim, ShieldAll);

        SpellDamageResult result = Hit(kit, caster, victim, FireBolt, 60);

        Assert.Equal(0u, result.Dealt);
        Assert.Equal(60u, result.Absorbed);
        Assert.Equal(40, AbsorbLeft(kit, victim, ShieldAll));
        Assert.Equal(1000u, victim.Health);
    }

    [Fact]
    public void AShieldThatRunsOut_IsRemoved_AndTheRestHurts()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player victim) = Pair(kit);
        RuleTestSupport.Apply(kit, victim, ShieldAll);

        SpellDamageResult result = Hit(kit, caster, victim, FireBolt, 150);

        Assert.Equal(50u, result.Dealt);
        Assert.Equal(100u, result.Absorbed);
        Assert.False(kit.System.HasAura(victim, ShieldAll));
        Assert.Equal(950u, victim.Health);
    }

    [Fact]
    public void ASchoolShield_OnlyAbsorbsItsSchool()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player victim) = Pair(kit);
        RuleTestSupport.Apply(kit, victim, ShieldFire);

        Assert.Equal(40u, Hit(kit, caster, victim, FrostBolt, 40).Dealt);
        Assert.Equal(0u, Hit(kit, caster, victim, FireBolt, 40).Dealt);
        Assert.Equal(60, AbsorbLeft(kit, victim, ShieldFire));
    }

    [Fact]
    public void TwoShields_AreConsumedInAuraOrder()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player victim) = Pair(kit);
        RuleTestSupport.Apply(kit, victim, ShieldSmall);
        RuleTestSupport.Apply(kit, victim, ShieldAll);

        SpellDamageResult result = Hit(kit, caster, victim, FireBolt, 50);

        Assert.Equal(50u, result.Absorbed);
        Assert.False(kit.System.HasAura(victim, ShieldSmall)); // the first 30
        Assert.Equal(80, AbsorbLeft(kit, victim, ShieldAll));  // then 20 of the second
    }

    [Fact]
    public void AShieldWithOneCharge_BreaksOnTheFirstAbsorbedHit()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player victim) = Pair(kit);
        RuleTestSupport.Apply(kit, victim, ShieldCharged);

        SpellDamageResult result = Hit(kit, caster, victim, FireBolt, 10);

        Assert.Equal(10u, result.Absorbed);
        Assert.False(kit.System.HasAura(victim, ShieldCharged)); // vmangos DropAuraCharge: the last charge breaks the shield
    }

    [Fact]
    public void ASchoolImmuneTarget_AbsorbsAllOfTheDamage()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player victim) = Pair(kit);
        RuleTestSupport.Apply(kit, victim, SchoolImmune);

        SpellDamageResult result = Hit(kit, caster, victim, FireBolt, 77);

        Assert.Equal((0u, 77u), (result.Dealt, result.Absorbed));
    }

    [Fact]
    public void TheOrder_IsShieldThenManaShield_SoAShieldLargeEnoughSparesTheMana()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player victim) = Pair(kit);
        victim.SetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana, 100);
        victim.SetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Mana, 100);
        RuleTestSupport.Apply(kit, victim, ManaShield);
        RuleTestSupport.Apply(kit, victim, ShieldAll);

        SpellDamageResult result = Hit(kit, caster, victim, FireBolt, 120);

        // 100 by the shield first (although the mana shield was applied before it), then 20 by the mana shield at 2 mana each.
        Assert.Equal((0u, 120u), (result.Dealt, result.Absorbed));
        Assert.Equal(60u, SpellSystem.GetPower(victim, PowerType.Mana));
        Assert.Equal(30, AbsorbLeft(kit, victim, ManaShield));
        Assert.False(kit.System.HasAura(victim, ShieldAll));
    }

    [Fact]
    public void TheOrder_ManaShieldThenTheRestIsDealt()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player victim) = Pair(kit);
        victim.SetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana, 100);
        victim.SetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Mana, 100);
        RuleTestSupport.Apply(kit, victim, ShieldSmall);
        RuleTestSupport.Apply(kit, victim, ManaShield);

        SpellDamageResult result = Hit(kit, caster, victim, FireBolt, 100);

        // 30 by the shield; 70 remain; the mana shield holds 50 and costs 2 mana per point = 100 mana; 20 are dealt.
        Assert.Equal((20u, 80u), (result.Dealt, result.Absorbed));
        Assert.Equal(0u, SpellSystem.GetPower(victim, PowerType.Mana));
        Assert.False(kit.System.HasAura(victim, ShieldSmall));
        Assert.False(kit.System.HasAura(victim, ManaShield));
    }


    [Fact]
    public void ManaShield_IsLimitedByTheManaItCanPayFor()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player victim) = Pair(kit);
        victim.SetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana, 100);
        victim.SetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Mana, 30);
        RuleTestSupport.Apply(kit, victim, ManaShield);

        SpellDamageResult result = Hit(kit, caster, victim, FireBolt, 40);

        Assert.Equal((25u, 15u), (result.Dealt, result.Absorbed)); // 30 mana / 2 = 15 damage absorbed
        Assert.Equal(0u, SpellSystem.GetPower(victim, PowerType.Mana));
        Assert.Equal(35, AbsorbLeft(kit, victim, ManaShield));
    }

    [Fact]
    public void SplitDamageFlat_SendsDamageToTheLivingCaster_AndNeverToItselfOrTheDead()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player victim) = Pair(kit);
        (Player paladin, _) = kit.AddPlayer(3, 3);
        paladin.Health = paladin.MaxHealth = 1000;
        kit.System.CastSpell(paladin, SplitFlat, SpellCastTargets.ForUnit(victim.Guid), triggered: true);
        Assert.True(kit.System.HasAura(victim, SplitFlat));

        SpellDamageResult result = Hit(kit, caster, victim, FireBolt, 100);

        Assert.Equal(60u, result.Dealt);  // 40 went to the paladin
        Assert.Equal(960u, paladin.Health);
        Assert.Equal(40u, result.Absorbed);

        // A dead paladin shares nothing.
        paladin.Health = 0;
        paladin.Combat.DeathState = Game.Combat.DeathState.Dead;
        SpellDamageResult second = Hit(kit, caster, victim, FireBolt, 100);
        Assert.Equal(100u, second.Dealt);
    }

    [Fact]
    public void SplitDamagePct_SendsThatShareOfWhatRemains()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player victim) = Pair(kit);
        (Player owner, _) = kit.AddPlayer(3, 3);
        owner.Health = owner.MaxHealth = 1000;
        kit.System.CastSpell(owner, SplitPct, SpellCastTargets.ForUnit(victim.Guid), triggered: true);

        SpellDamageResult result = Hit(kit, caster, victim, FireBolt, 100);

        Assert.Equal(50u, result.Dealt);
        Assert.Equal(950u, owner.Health);
    }

    [Fact]
    public void SplitDamage_FromTheTargetsOwnAura_IsIgnored()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player victim) = Pair(kit);
        RuleTestSupport.Apply(kit, victim, SplitPct); // caster == target: nothing to split to

        Assert.Equal(100u, Hit(kit, caster, victim, FireBolt, 100).Dealt);
    }

    [Fact]
    public void ADotTick_IsAbsorbedToo_AndTheLogShowsIt()
    {
        using SpellTestKit kit = Kit();
        (Player caster, FakeSession session) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2);
        victim.Health = victim.MaxHealth = 1000;
        kit.System.Relations = new FakeRelations { Hostile = { victim.Guid } };
        RuleTestSupport.Apply(kit, victim, ShieldAll);
        kit.System.CastSpell(caster, FireDot, SpellCastTargets.ForUnit(victim.Guid), triggered: true);
        session.Clear();

        kit.Advance(3100);

        Assert.Equal(1000u, victim.Health);
        Assert.Equal(80, AbsorbLeft(kit, victim, ShieldAll));
        Assert.Contains(session.Sent, p => p.Opcode == WorldOpcode.SmsgPeriodicauralog);
    }

    [Fact]
    public void ThePublicApi_ReturnsTheAbsorbedAmount_ForTheMeleeLane()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player victim) = Pair(kit);
        RuleTestSupport.Apply(kit, victim, ShieldSmall);

        uint absorbed = kit.System.AbsorbDamage(caster, victim, SpellSchoolMasks.Of(SpellSchool.Normal), 50, spell: null);

        Assert.Equal(30u, absorbed);
        Assert.False(kit.System.HasAura(victim, ShieldSmall));
    }
}
