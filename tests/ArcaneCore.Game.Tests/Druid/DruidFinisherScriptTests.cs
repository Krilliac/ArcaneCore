using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Druid;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Druid;

/// <summary>
/// Ferocious Bite (spell_druid.cpp:23-72) and Rip (SpellAuras.cpp:4341-4363) at the 1.12 build. Spell data follow classic-db:
/// Ferocious Bite rank 5 31018 (35 energy, effect 0 SchoolDamage with 147/100 combo scaling, damage multiplier 2.7, AttributesEx
/// 0x100200, Stances cat), Rip rank 6 9896 (family Druid flag 0x800000, PeriodicDamage over 2 s ticks, 28 per combo point).
/// </summary>
public sealed class DruidFinisherScriptTests : IDisposable
{
    private const uint Bite = 31018;
    private const uint Rip = 9896;
    private const uint Other = 900950;
    private const uint FinishingDamage = 0x00100000;

    private sealed class Capture : ISpellValueModifier
    {
        public List<(uint Spell, int Effect, int Value)> Values { get; } = [];

        public int Modify(SpellValueKind kind, in SpellValueContext context, int value)
        {
            if (kind == SpellValueKind.EffectValue)
            {
                Values.Add((context.Spell.Id, context.EffectIndex, value));
            }

            return value;
        }
    }

    private sealed class Outcomes : ISpellCastObserver
    {
        public List<SpellTargetOutcome> Seen { get; } = [];

        public void OnTargetOutcome(SpellCast cast, SpellTargetOutcome outcome) => Seen.Add(outcome);
    }

    private readonly SpellTestKit _kit;
    private readonly Player _druid;
    private readonly Player _enemy;
    private readonly ComboPointService _combos;
    private readonly Capture _capture = new();
    private readonly Outcomes _outcomes = new();

    public DruidFinisherScriptTests()
    {
        _kit = new SpellTestKit(
            Spell(Bite, Effect(SpellEffectName.SchoolDamage, 100, SpellImplicitTarget.UnitEnemy) with { PointsPerComboPoint = 36f, DamageMultiplier = 2.7f }) with
            {
                AttributesEx = (SpellAttributesEx)FinishingDamage,
                Stances = 1,
                PowerType = (int)PowerType.Energy,
                ManaCost = 35,
                RangeIndex = 4,
                Range = new SpellRange(0, 30),
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            },
            Spell(Rip, Effect(SpellEffectName.ApplyAura, 10, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicDamage, amplitude: 2000) with { PointsPerComboPoint = 4f }) with
            {
                AttributesEx = (SpellAttributesEx)FinishingDamage,
                SpellFamilyName = 7,
                SpellFamilyFlags = 0x800000,
                Stances = 1,
                PowerType = (int)PowerType.Energy,
                ManaCost = 30,
                Duration = new SpellDuration(12000, 0, 12000),
                RangeIndex = 4,
                Range = new SpellRange(0, 30),
                SpellVisual = 1,
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            },
            Spell(Other, Effect(SpellEffectName.SchoolDamage, 100, SpellImplicitTarget.UnitEnemy)) with { RangeIndex = 4, Range = new SpellRange(0, 30), StartRecoveryCategory = 0, StartRecoveryTime = 0 });
        (_druid, _) = _kit.AddPlayer(1);
        (_enemy, _) = _kit.AddPlayer(2, 3, 0);
        _combos = new ComboPointService(_kit.System, (_, guid) => _kit.World.FindOnlinePlayer(guid));
        _combos.Install();
        DruidFinisherScripts.Install(_kit.System, _combos);
        _kit.System.RegisterValueModifier(_capture);
        _kit.System.RegisterObserver(_outcomes);
        _kit.World.RunTick(0);
        PowerTypeSwitch.EnsureFeralPowerCaps(_druid);
        _druid.SetByte(UpdateFields.UnitFieldBytes1, 2, 1);              // Cat Form: the Stances bit of both spells
        _druid.SetInt32(UpdateFields.UnitFieldAttackPower, 1000);
    }

    public void Dispose() => _kit.Dispose();

    private uint Energy => MapCombat.GetPower(_druid, PowerType.Energy);

    [Fact]
    public void FerociousBite_Value_IsBasePlusComboScalingPlusAttackPowerTermPlusEnergyTimesMultiplier_AndAHitSpendsTheEnergy()
    {
        MapCombat.SetPower(_druid, PowerType.Energy, 85);
        _combos.AddComboPoints(_druid, _enemy, 5);

        Assert.Equal(SpellCastResult.CastOk, _kit.System.CastSpell(_druid, Bite, SpellCastTargets.ForUnit(_enemy.Guid), triggered: false));

        // 100 + (int)(36 * 5) = 280; + (int)(1000 * 5 * 0.03) = 430; the cast cost 35 energy, the 50 left give (int)(50 * 2.7) = 135.
        Assert.Equal((Bite, 0, 565), _capture.Values.Single(v => v.Spell == Bite));
        SpellTargetOutcome outcome = Assert.Single(_outcomes.Seen);
        Assert.Equal(outcome.Miss == SpellMissInfo.None ? 0u : 50u, Energy);       // energy is spent only by a hit
    }

    [Fact]
    public void FerociousBite_WithoutComboPoints_AddsOnlyTheEnergyTerm()
    {
        MapCombat.SetPower(_druid, PowerType.Energy, 75);                     // a triggered cast still pays the 35 energy: 40 are left

        _kit.System.CastSpell(_druid, Bite, SpellCastTargets.ForUnit(_enemy.Guid), triggered: true);

        Assert.Equal((Bite, 0, 100 + (int)(40 * 2.7f)), _capture.Values.Single(v => v.Spell == Bite));
    }

    [Theory]
    [InlineData(5, 40)]    // five points count as four: 1000 * 4 / 100
    [InlineData(4, 40)]
    [InlineData(2, 20)]
    public void Rip_TickAmount_IncludesTheAttackPowerTermCappedAtFourComboPoints(int points, int apTerm)
    {
        MapCombat.SetPower(_druid, PowerType.Energy, 100);
        _combos.AddComboPoints(_druid, _enemy, points);

        _kit.System.CastSpell(_druid, Rip, SpellCastTargets.ForUnit(_enemy.Guid), triggered: true);

        int comboScaled = 10 + (int)(4 * points);
        SpellAura aura = _kit.System.GetAuras(_enemy).Single(h => h.Spell.Id == Rip).Auras.OfType<SpellAura>().Single();
        Assert.Equal(comboScaled + apTerm, aura.Amount);
    }

    [Fact]
    public void OtherSpells_AreNotTouched()
    {
        _combos.AddComboPoints(_druid, _enemy, 5);

        _kit.System.CastSpell(_druid, Other, SpellCastTargets.ForUnit(_enemy.Guid), triggered: true);

        Assert.Equal((Other, 0, 100), _capture.Values.Single(v => v.Spell == Other));
    }

    [Fact]
    public void FerociousBiteAndRipAreRecognisedByIdAndFamilyFlag()
    {
        Assert.True(DruidFinisherScripts.IsFerociousBite(_kit.Store.Get(Bite)!));
        Assert.False(DruidFinisherScripts.IsFerociousBite(_kit.Store.Get(Rip)!));
        Assert.True(DruidFinisherScripts.IsRip(_kit.Store.Get(Rip)!));
        Assert.False(DruidFinisherScripts.IsRip(_kit.Store.Get(Bite)!));
        Assert.False(DruidFinisherScripts.IsRip(_kit.Store.Get(Other)!));
    }
}
