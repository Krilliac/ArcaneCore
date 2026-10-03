using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Casters;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;
using static ArcaneCore.Game.Tests.SpellMods.ModTestSupport;

namespace ArcaneCore.Game.Tests.SpellMods;

/// <summary>
/// The amount-side spell mods: DAMAGE on the done amount of direct damage and healing, DOT on the done amount of an
/// over-time snapshot, SPELL_BONUS_DAMAGE on the coefficient (x100), and the weapon-damage and melee-class paths that never
/// reached the spell amount modifier. vmangos SpellCaster.cpp:1443-1452 (SpellDamageBonusDone), :1520-1525 (healing),
/// :1696-1700 (melee), :1760-1766 (coefficient). The done amount is modified BEFORE the target side (damage taken).
/// </summary>
public sealed class AmountModTests
{
    private const uint FrostBolt = 943001;
    private const uint Greater = 943002;
    private const uint FireDot = 943003;
    private const uint PowerFrost = 943004;
    private const uint PowerHeal = 943005;
    private const uint TakenUp = 943006;
    private const uint DamageFlat = 943010;
    private const uint DamagePct = 943011;
    private const uint DotPct = 943012;
    private const uint BonusDamageFlat = 943013;
    private const uint WeaponStrike = 943020;
    private const uint MeleeSchoolHit = 943021;
    private const uint IgnoringBolt = 943022;
    private const uint Leech = 943023;
    private const uint MultiplyMod = 943024;
    private const int FrostMask = 0x10;
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

    private static SpellInfo Aura(uint id, AuraType type, int amount, int mask) => Spell(
        id, Effect(SpellEffectName.ApplyAura, amount, aura: type, misc: mask)) with
    {
        Duration = new SpellDuration(60000, 0, 60000),
        SpellVisual = 1,
    };

    private static SpellInfo Damage(uint id, int amount, SpellSchool school) => InFamily(Spell(
        id, Effect(SpellEffectName.SchoolDamage, amount, SpellImplicitTarget.UnitEnemy)) with
    {
        CastTime = new SpellCastTime(3000, 0, 0),
        School = school,
        DamageClass = SpellDamageClass.Magic,
        RangeIndex = 4,
        Range = new SpellRange(0, 30),
    });

    private static SpellTestKit Kit()
    {
        var kit = new SpellTestKit(
            Damage(FrostBolt, 100, SpellSchool.Frost),
            InFamily(Spell(Greater, Effect(SpellEffectName.Heal, 100, SpellImplicitTarget.Unit)) with
            {
                CastTime = new SpellCastTime(3000, 0, 0),
                School = SpellSchool.Holy,
                DamageClass = SpellDamageClass.Magic,
                RangeIndex = 4,
                Range = new SpellRange(0, 30),
            }),
            InFamily(Spell(FireDot, Effect(SpellEffectName.ApplyAura, 20, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicDamage, amplitude: 3000)) with
            {
                Duration = new SpellDuration(12000, 0, 12000),
                School = SpellSchool.Fire,
                DamageClass = SpellDamageClass.Magic,
                SpellVisual = 1,
                RangeIndex = 4,
                Range = new SpellRange(0, 30),
            }),
            Aura(PowerFrost, AuraType.ModDamageDone, 100, FrostMask),
            Aura(PowerHeal, AuraType.ModHealingDone, 110, AllSchools),
            Aura(TakenUp, AuraType.ModDamagePercentTaken, 20, AllSchools),
            Flat(DamageFlat, SpellModOp.Damage, 20),
            Pct(DamagePct, SpellModOp.Damage, 10),
            Pct(DotPct, SpellModOp.Dot, 50),
            Flat(BonusDamageFlat, SpellModOp.SpellBonusDamage, 50),
            InFamily(Spell(WeaponStrike, Effect(SpellEffectName.WeaponDamage, 5, SpellImplicitTarget.UnitEnemy)) with
            {
                DamageClass = SpellDamageClass.Melee,
                RangeIndex = 4,
                Range = new SpellRange(0, 30),
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            }),
            InFamily(Spell(MeleeSchoolHit, Effect(SpellEffectName.SchoolDamage, 40, SpellImplicitTarget.UnitEnemy)) with
            {
                DamageClass = SpellDamageClass.Melee,
                RangeIndex = 4,
                Range = new SpellRange(0, 30),
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            }),
            Damage(IgnoringBolt, 100, SpellSchool.Frost) with { AttributesEx3 = 0x20000000 },
            InFamily(Spell(Leech, Effect(SpellEffectName.HealthLeech, 20, SpellImplicitTarget.UnitEnemy) with { MultipleValue = 0.5f }) with
            {
                School = SpellSchool.Shadow,
                DamageClass = SpellDamageClass.Magic,
                RangeIndex = 4,
                Range = new SpellRange(0, 30),
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            }),
            Pct(MultiplyMod, SpellModOp.MultipleValue, 100));
        CasterSpellModules.Register(kit.System, null);
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
    public void DirectDamage_FlatDamageMod_IsAddedBeforeTheTargetSide()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player target, RecordingSink sink) = Setup(kit);
        Cast(kit, caster, PowerFrost, caster);
        Cast(kit, target, TakenUp, target);

        Cast(kit, caster, FrostBolt, target);
        kit.System.LearnSpell(caster, DamageFlat);
        Cast(kit, caster, FrostBolt, target);

        // Done: 100 + 100 * (3000/3500) = 185.714; the mod adds 20 -> 205.714; the target's +20% taken then gives 246.857.
        // Applied after the target side it would be 185.714 * 1.2 + 20 = 242.857, so 246 vs 242 tells the order.
        Assert.Equal([222u, 246u], sink.Damage);
    }

    [Fact]
    public void DirectDamage_PctDamageMod()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player target, RecordingSink sink) = Setup(kit);
        Cast(kit, caster, PowerFrost, caster);
        kit.System.LearnSpell(caster, DamagePct);

        Cast(kit, caster, FrostBolt, target);

        Assert.Equal([204u], sink.Damage);   // 185.714 * 1.10 = 204.28
    }

    [Fact]
    public void DirectHeal_UsesTheDamageOp_LikeVmangos()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player target, RecordingSink sink) = Setup(kit);
        Cast(kit, caster, PowerHeal, caster);
        Cast(kit, caster, Greater, target);
        kit.System.LearnSpell(caster, DamagePct);
        Cast(kit, caster, Greater, target);

        // 100 + 110 * 0.857143 = 194.28; with +10%: 213.7.
        Assert.Equal([194u, 213u], sink.Healing);
    }

    [Fact]
    public void DamageOverTime_UsesDot_NotDamage()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player target, _) = Setup(kit);
        kit.System.LearnSpell(caster, DamagePct);   // a DAMAGE-only mod leaves the DoT alone
        Cast(kit, caster, FireDot, target);
        SpellAura plain = kit.System.GetAuras(target).Single(h => h.Spell.Id == FireDot).Auras.OfType<SpellAura>().Single();
        Assert.Equal(20, plain.Amount);

        kit.System.RemoveAuras(target, FireDot);
        kit.System.LearnSpell(caster, DotPct);
        Cast(kit, caster, FireDot, target);

        SpellAura modded = kit.System.GetAuras(target).Single(h => h.Spell.Id == FireDot).Auras.OfType<SpellAura>().Single();
        Assert.Equal(30, modded.Amount);   // 20 + 50%
    }

    [Fact]
    public void SpellBonusDamage_ScalesTheCoefficient_InTheTimesHundredDomain()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player target, RecordingSink sink) = Setup(kit);
        Cast(kit, caster, PowerFrost, caster);
        kit.System.LearnSpell(caster, BonusDamageFlat);

        Cast(kit, caster, FrostBolt, target);

        // coefficient 0.857143 * 100 + 50 = 135.714 -> 1.35714: 100 + 100 * 1.35714 = 235.7.
        Assert.Equal([235u], sink.Damage);
    }

    [Fact]
    public void SpellBonusDamage_NeedsSomeBenefit_ToMatter()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player target, RecordingSink sink) = Setup(kit);
        kit.System.LearnSpell(caster, BonusDamageFlat);

        Cast(kit, caster, FrostBolt, target);

        Assert.Equal([100u], sink.Damage);
    }

    [Fact]
    public void ASpellThatIgnoresCasterModifiers_IsUntouched()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player target, RecordingSink sink) = Setup(kit);
        kit.System.LearnSpell(caster, DamageFlat);

        Cast(kit, caster, IgnoringBolt, target);

        Assert.Equal([100u], sink.Damage);
    }

    [Fact]
    public void HealthLeech_MultipleValueMod_ScalesTheHealing()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player target, RecordingSink sink) = Setup(kit);
        Cast(kit, caster, Leech, target);
        kit.System.LearnSpell(caster, MultiplyMod);
        Cast(kit, caster, Leech, target);

        // Damage 20, multiple 0.5 -> heals 10; with a +100% MULTIPLE_VALUE mod the multiple is 1.0 -> heals 20 (vmangos SpellEffects.cpp:1804).
        Assert.Equal([10u, 20u], sink.Healing);
    }

    [Fact]
    public void WeaponDamageSpell_GetsTheDamageMod()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player target, RecordingSink sink) = Setup(kit);
        caster.SetFloat(UpdateFields.UnitFieldMindamage, 10);
        caster.SetFloat(UpdateFields.UnitFieldMaxdamage, 10);
        caster.SetUInt32(UpdateFields.UnitFieldBaseattacktime, 2000);
        kit.System.LearnSpell(caster, DamageFlat);

        Cast(kit, caster, WeaponStrike, target);

        Assert.Equal([10u + 5u + 20u], sink.Damage);   // weapon 10 + 5 bonus, +20 flat done mod
    }

    [Fact]
    public void MeleeClassSchoolDamage_GetsTheDamageMod_ThoughSpellPowerIsSkipped()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player target, RecordingSink sink) = Setup(kit);
        kit.System.LearnSpell(caster, DamagePct);

        Cast(kit, caster, MeleeSchoolHit, target);

        Assert.Equal([44u], sink.Damage);   // 40 * 1.10
    }
}
