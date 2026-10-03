using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Casters;
using ArcaneCore.Game.Spells.Casters.Bonus;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells.Casters;

/// <summary>
/// +damage / +healing end to end through the spell system: caster side when a direct effect lands or an over-time
/// aura is created, target side on every tick. vmangos SpellCaster.cpp:1457-1700, Unit.cpp:5175-5385,
/// SpellAuras.cpp:4306-4466 (snapshots, patches 1.10 / 1.11) and :5878-5890, :6106-6140 (tick order).
/// </summary>
public sealed class SpellBonusWiringTests
{
    private const uint FrostBolt = 4001;
    private const uint SmallBolt = 4201;
    private const uint GreaterHeal = 4002;
    private const uint FlashHeal = 4003;
    private const uint RenewLike = 4004;
    private const uint FireDot = 4005;
    private const uint BonusDamageFrost = 4101;
    private const uint BonusDamageFire = 4102;
    private const uint BonusHealing = 4103;
    private const uint MoreHealing = 4104;
    private const uint AmplifyLike = 4105;
    private const uint DampenLike = 4106;
    private const uint HealingTakenUp = 4107;
    private const uint HealingTakenDown = 4108;
    private const uint SpiritHealing = 4109;
    private const uint MeleeDot = 4110;
    private const uint RangedBolt = 4202;
    private const int FrostMask = 0x10;
    private const int FireMask = 0x04;
    private const int AllSchools = 0x7F;

    private sealed class RecordingSink : IDamageSink
    {
        public List<uint> Damage { get; } = [];

        public List<uint> Healing { get; } = [];

        public uint DealSpellDamage(Unit caster, Unit victim, SpellInfo spell, uint damage, bool periodic)
        {
            Damage.Add(damage);
            return damage;
        }

        public uint Heal(Unit caster, Unit target, SpellInfo spell, uint amount)
        {
            Healing.Add(amount);
            return amount;
        }
    }

    private static SpellInfo Aura(uint id, AuraType type, int amount, int mask) => SpellTestKit.Spell(
        id, SpellTestKit.Effect(SpellEffectName.ApplyAura, amount, aura: type, misc: mask)) with
    {
        Duration = new SpellDuration(60000, 0, 60000),
        SpellVisual = 1,
    };

    private static SpellInfo Damage(uint id, int amount, int castMs, SpellSchool school, uint level = 0) => SpellTestKit.Spell(
        id, SpellTestKit.Effect(SpellEffectName.SchoolDamage, amount, SpellImplicitTarget.UnitEnemy)) with
    {
        CastTime = new SpellCastTime(castMs, 0, 0),
        School = school,
        SpellLevel = level,
        DamageClass = SpellDamageClass.Magic,
        RangeIndex = 4,
        Range = new SpellRange(0, 30),
    };

    private static SpellInfo Heal(uint id, int amount, int castMs, uint level = 0) => SpellTestKit.Spell(
        id, SpellTestKit.Effect(SpellEffectName.Heal, amount, SpellImplicitTarget.Unit)) with
    {
        CastTime = new SpellCastTime(castMs, 0, 0),
        School = SpellSchool.Holy,
        SpellLevel = level,
        DamageClass = SpellDamageClass.Magic,
        RangeIndex = 4,
        Range = new SpellRange(0, 30),
    };

    private static SpellTestKit NewKit(bool install = true, CasterOptions? options = null)
    {
        var kit = new SpellTestKit(
            Damage(FrostBolt, 100, 3000, SpellSchool.Frost),
            Damage(SmallBolt, 8, 3000, SpellSchool.Frost),
            Damage(RangedBolt, 100, 3000, SpellSchool.Frost) with { DamageClass = SpellDamageClass.Ranged },
            Heal(GreaterHeal, 100, 3000),
            Heal(FlashHeal, 100, 1500),
            SpellTestKit.Spell(RenewLike, SpellTestKit.Effect(SpellEffectName.ApplyAura, 40, SpellImplicitTarget.Unit, AuraType.PeriodicHeal, amplitude: 3000)) with
            {
                Duration = new SpellDuration(15000, 0, 15000),
                SpellLevel = 8,
                School = SpellSchool.Holy,
                DamageClass = SpellDamageClass.Magic,
                SpellVisual = 1,
            },
            SpellTestKit.Spell(FireDot, SpellTestKit.Effect(SpellEffectName.ApplyAura, 20, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicDamage, amplitude: 3000)) with
            {
                Duration = new SpellDuration(12000, 0, 12000),
                School = SpellSchool.Fire,
                DamageClass = SpellDamageClass.Magic,
                SpellVisual = 1,
                RangeIndex = 4,
                Range = new SpellRange(0, 30),
            },
            SpellTestKit.Spell(MeleeDot, SpellTestKit.Effect(SpellEffectName.ApplyAura, 20, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicDamage, amplitude: 3000)) with
            {
                Duration = new SpellDuration(12000, 0, 12000),
                School = SpellSchool.Normal,
                DamageClass = SpellDamageClass.Melee,
                SpellVisual = 1,
                RangeIndex = 4,
                Range = new SpellRange(0, 30),
            },
            Aura(BonusDamageFrost, AuraType.ModDamageDone, 100, FrostMask),
            Aura(BonusDamageFire, AuraType.ModDamageDone, 100, FireMask),
            Aura(BonusHealing, AuraType.ModHealingDone, 110, AllSchools),
            Aura(MoreHealing, AuraType.ModHealingDone, 50, AllSchools),
            Aura(AmplifyLike, AuraType.ModDamageTaken, 60, AllSchools),
            Aura(DampenLike, AuraType.ModDamageTaken, -20, AllSchools),
            Aura(HealingTakenUp, AuraType.ModHealing, 120, AllSchools),
            Aura(HealingTakenDown, AuraType.ModHealingPct, -50, 0),
            Aura(SpiritHealing, AuraType.ModSpellHealingOfStatPercent, 25, 0));
        if (install)
        {
            CasterSpellModules.Register(kit.System, options);
        }

        return kit;
    }

    private static (Player Caster, Player Target, RecordingSink Sink) Setup(SpellTestKit kit)
    {
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 10, 0);
        var sink = new RecordingSink();
        kit.System.Damage = sink;
        return (caster, target, sink);
    }

    private static void Cast(SpellTestKit kit, Unit caster, uint spell, Unit target)
        => kit.System.CastSpell(caster, spell, SpellCastTargets.ForUnit(target.Guid), triggered: true);

    [Fact]
    public void DirectDamage_AddsTheMatchingSchoolBenefitTimesTheCastTimeCoefficient()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, Player target, RecordingSink sink) = Setup(kit);
        Cast(kit, caster, BonusDamageFrost, caster);

        Cast(kit, caster, FrostBolt, target);

        // 3000 ms -> 3000/3500 = 0.857143: 100 + 100 * 0.857143 = 185.7 -> 185.
        Assert.Equal([185u], sink.Damage);
    }

    [Fact]
    public void DirectDamage_IgnoresBenefitOfAnotherSchool()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, Player target, RecordingSink sink) = Setup(kit);
        Cast(kit, caster, BonusDamageFire, caster);

        Cast(kit, caster, FrostBolt, target);

        Assert.Equal([100u], sink.Damage);
    }

    [Fact]
    public void DirectHeal_AddsHealingBenefit()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, Player target, RecordingSink sink) = Setup(kit);
        Cast(kit, caster, BonusHealing, caster);

        Cast(kit, caster, GreaterHeal, target);
        Cast(kit, caster, FlashHeal, target);

        // +110: 100 + 110 * 0.857143 = 194.28; Flash Heal 1500 ms: 100 + 110 * 0.428571 = 147.14.
        Assert.Equal([194u, 147u], sink.Healing);
    }

    [Fact]
    public void SpiritBasedHealing_UsesTheCastersSpirit()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, Player target, RecordingSink sink) = Setup(kit);
        caster.SetUInt32(UpdateFields.UnitFieldStat0 + 4, 100);
        Cast(kit, caster, SpiritHealing, caster);

        Cast(kit, caster, GreaterHeal, target);

        // 25 percent of 100 spirit = 25 benefit: 100 + 25 * 0.857143 = 121.4.
        Assert.Equal([121u], sink.Healing);
    }

    [Fact]
    public void HealOverTime_IsSnapshottedWhenCreated_AndLaterGearChangesDoNotChangeTheTicks()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, Player target, RecordingSink sink) = Setup(kit);
        Cast(kit, caster, BonusHealing, caster);
        Cast(kit, caster, RenewLike, target);

        // 15000 ms / 3000 ms = 5 ticks: coefficient 0.2, level 8 penalty 0.55 -> 40 + 110 * 0.11 = 52.1.
        SpellAura aura = kit.System.GetAuras(target).Single(h => h.Spell.Id == RenewLike).Auras.OfType<SpellAura>().Single();
        Assert.Equal(52, aura.Amount);

        Cast(kit, caster, MoreHealing, caster);
        kit.Advance(9000);

        Assert.Equal([52u, 52u, 52u], sink.Healing);
    }

    [Fact]
    public void DamageOverTime_SnapshotsTheCasterSide_AndTheTargetSideAppliesPerTick()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, Player target, RecordingSink sink) = Setup(kit);
        Cast(kit, caster, BonusDamageFire, caster);
        Cast(kit, caster, FireDot, target);

        // 12000/3000 = 4 ticks: coefficient (12000/15000)/4 = 0.2, no level penalty: 20 + 100 * 0.2 = 40.
        SpellAura aura = kit.System.GetAuras(target).Single(h => h.Spell.Id == FireDot).Auras.OfType<SpellAura>().Single();
        Assert.Equal(40, aura.Amount);

        Cast(kit, target, AmplifyLike, target);
        kit.Advance(3000);

        // Amplify Magic +60 on the target: 40 + 60 * 0.2 = 52 per tick.
        Assert.Equal([52u], sink.Damage);
    }

    [Fact]
    public void TargetDamageTaken_AddsToDirectDamage()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, Player target, RecordingSink sink) = Setup(kit);
        Cast(kit, target, AmplifyLike, target);

        Cast(kit, caster, FrostBolt, target);

        // 100 + 60 * 0.857143 = 151.4.
        Assert.Equal([151u], sink.Damage);
    }

    [Fact]
    public void NegativeTakenBenefit_CannotRemoveMoreThanHalfTheDamage()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, Player target, RecordingSink sink) = Setup(kit);
        Cast(kit, target, DampenLike, target);

        Cast(kit, caster, SmallBolt, target);

        // -20 * 0.857 = -17.1 would remove more than half of 8: clamped to -4.
        Assert.Equal([4u], sink.Damage);
    }

    [Fact]
    public void HealingTaken_AddsFlatAndPercent()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, Player target, RecordingSink sink) = Setup(kit);
        Cast(kit, target, HealingTakenUp, target);
        Cast(kit, caster, FlashHeal, target);
        kit.System.RemoveAuras(target, HealingTakenUp);
        Cast(kit, target, HealingTakenDown, target);
        Cast(kit, caster, FlashHeal, target);

        // +120 healing taken on a 1500 ms heal: 100 + 120 * 0.428571 = 151.4; then -50 percent: 50.
        Assert.Equal([151u, 50u], sink.Healing);
    }

    [Fact]
    public void MeleeAndRangedClassDirectDamage_IsLeftToTheMeleeFormulas()
    {
        // vmangos SpellCaster.cpp:1243-1276: only DmgClass NONE and MAGIC use SpellDamageBonusDone / Taken.
        using SpellTestKit kit = NewKit();
        (Player caster, Player target, RecordingSink sink) = Setup(kit);
        Cast(kit, caster, BonusDamageFrost, caster);

        Cast(kit, caster, RangedBolt, target);

        Assert.Equal([100u], sink.Damage);
    }

    [Fact]
    public void MeleeClassPeriodicDamage_IsLeftToTheMeleeFormulas()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, Player target, _) = Setup(kit);
        Cast(kit, caster, BonusDamageFire, caster);

        Cast(kit, caster, MeleeDot, target);

        Assert.Equal(20, kit.System.GetAuras(target).Single(h => h.Spell.Id == MeleeDot).Auras.OfType<SpellAura>().Single().Amount);
    }

    [Fact]
    public void ExplicitCoefficient_WinsAndSkipsTheLevelPenalty_AndZeroDisablesTheBonus()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, Player target, RecordingSink sink) = Setup(kit);
        var module = (SpellBonusModule)kit.System.AmountModifier!;
        var table = new TestTable();
        module.Coefficients = table;
        Cast(kit, caster, BonusDamageFrost, caster);

        table.Values[(FrostBolt, 0)] = 0.5f;
        Cast(kit, caster, FrostBolt, target);
        table.Values[(FrostBolt, 0)] = 0f;
        Cast(kit, caster, FrostBolt, target);

        Assert.Equal([150u, 100u], sink.Damage);
    }

    [Fact]
    public void Disabled_KeepsTheRawSpellAmounts()
    {
        var options = new CasterOptions();
        options.Bonus.Enabled = false;
        using SpellTestKit kit = NewKit(options: options);
        (Player caster, Player target, RecordingSink sink) = Setup(kit);
        Cast(kit, caster, BonusDamageFrost, caster);

        Cast(kit, caster, FrostBolt, target);

        Assert.Null(kit.System.AmountModifier);
        Assert.Equal([100u], sink.Damage);
    }

    [Fact]
    public void WithoutAModifier_AmountsAreUntouched()
    {
        using SpellTestKit kit = NewKit(install: false);
        (Player caster, Player target, RecordingSink sink) = Setup(kit);
        kit.System.CastSpell(caster, BonusDamageFrost, SpellCastTargets.ForSelf(), triggered: true);

        Cast(kit, caster, FrostBolt, target);
        Cast(kit, caster, RenewLike, target);

        Assert.Equal([100u], sink.Damage);
        Assert.Equal(40, kit.System.GetAuras(target).Single(h => h.Spell.Id == RenewLike).Auras.OfType<SpellAura>().Single().Amount);
    }

    [Fact]
    public void InstallingOverAnotherModifier_Fails()
    {
        using SpellTestKit kit = NewKit(install: false);
        kit.System.AmountModifier = new OtherModifier();

        Assert.Throws<InvalidOperationException>(() => CasterSpellModules.Register(kit.System));
    }

    private sealed class TestTable : ISpellBonusCoefficients
    {
        public Dictionary<(uint, int), float> Values { get; } = [];

        public float? Get(uint spellId, int effectIndex) => Values.TryGetValue((spellId, effectIndex), out float value) ? value : null;
    }

    private sealed class OtherModifier : ISpellAmountModifier
    {
        public float Modify(SpellAmountStage stage, Unit caster, Unit target, SpellInfo spell, int effectIndex, float amount, uint stack) => amount;
    }
}
