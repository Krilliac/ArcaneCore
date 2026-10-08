using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rogue;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Rogue;

/// <summary>
/// Rupture and Garrote at the 1.12 build (vmangos Aura::CalculateDotDamage, SpellAuras.cpp:4366-4384). Spell data follow the build 5875
/// spell_template rows: Rupture rank 1 1943 (family Rogue 0x100000, PeriodicDamage every 2 s, base 8, 2 per combo point, finishing move),
/// Garrote rank 1 703 (family Rogue 0x100, PeriodicDamage every 3 s, base 24).
/// </summary>
public sealed class RogueBleedScriptTests : IDisposable
{
    private const uint Rupture = 1943;
    private const uint Garrote = 703;
    private const uint OtherDot = 900_960;
    private const uint FinishingDamage = 0x00100000;

    private readonly SpellTestKit _kit;
    private readonly Player _rogue;
    private readonly Player _enemy;
    private readonly ComboPointService _combos;

    public RogueBleedScriptTests()
    {
        _kit = new SpellTestKit(
            Spell(Rupture, Effect(SpellEffectName.ApplyAura, 8, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicDamage, amplitude: 2000) with { PointsPerComboPoint = 2f }) with
            {
                AttributesEx = (SpellAttributesEx)FinishingDamage,
                SpellFamilyName = RogueBleedScripts.RogueFamily,
                SpellFamilyFlags = 0x100000,
                Duration = new SpellDuration(8000, 0, 8000),
                RangeIndex = 4,
                Range = new SpellRange(0, 30),
                SpellVisual = 1,
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            },
            Spell(Garrote, Effect(SpellEffectName.ApplyAura, 24, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicDamage, amplitude: 3000)) with
            {
                SpellFamilyName = RogueBleedScripts.RogueFamily,
                SpellFamilyFlags = 0x100,
                Duration = new SpellDuration(18000, 0, 18000),
                RangeIndex = 4,
                Range = new SpellRange(0, 30),
                SpellVisual = 1,
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            },
            Spell(OtherDot, Effect(SpellEffectName.ApplyAura, 24, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicDamage, amplitude: 3000)) with
            {
                SpellFamilyName = RogueBleedScripts.RogueFamily,
                SpellFamilyFlags = 0x2,
                Duration = new SpellDuration(18000, 0, 18000),
                RangeIndex = 4,
                Range = new SpellRange(0, 30),
                SpellVisual = 1,
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            });
        (_rogue, _) = _kit.AddPlayer(1);
        (_enemy, _) = _kit.AddPlayer(2, 3, 0);
        _combos = new ComboPointService(_kit.System, (_, guid) => _kit.World.FindOnlinePlayer(guid));
        _combos.Install();
        RogueBleedScripts.Install(_kit.System, _combos);
        _kit.World.RunTick(0);
        _rogue.SetInt32(UpdateFields.UnitFieldAttackPower, 1000);
    }

    public void Dispose() => _kit.Dispose();

    private int TickAmount(uint spell) => _kit.System.GetAuras(_enemy).Single(h => h.Spell.Id == spell).Auras.OfType<SpellAura>().Single().Amount;

    [Theory]
    [InlineData(1, 10)]   // 1000 * 1 / 100
    [InlineData(3, 30)]
    [InlineData(5, 30)]   // only the first three points add attack power
    public void Rupture_TickAmount_IncludesTheAttackPowerTermCappedAtThreeComboPoints(int points, int apTerm)
    {
        _combos.AddComboPoints(_rogue, _enemy, points);

        _kit.System.CastSpell(_rogue, Rupture, SpellCastTargets.ForUnit(_enemy.Guid), triggered: true);

        int comboScaled = 8 + (2 * points);
        Assert.Equal(comboScaled + apTerm, TickAmount(Rupture));
    }

    [Fact]
    public void Garrote_TickAmount_AddsThreePercentOfTheAttackPower()
    {
        _kit.System.CastSpell(_rogue, Garrote, SpellCastTargets.ForUnit(_enemy.Guid), triggered: true);

        Assert.Equal(24 + 30, TickAmount(Garrote));
    }

    [Fact]
    public void TheTerm_ReadsTheTotalAttackPower_WithItsModsAndMultiplier()
    {
        _rogue.SetUInt16(UpdateFields.UnitFieldAttackPowerMods, 0, 200);   // +200
        _rogue.SetFloat(UpdateFields.UnitFieldAttackPowerMultiplier, 0.5f); // (1000 + 200) * 1.5 = 1800

        _kit.System.CastSpell(_rogue, Garrote, SpellCastTargets.ForUnit(_enemy.Guid), triggered: true);

        Assert.Equal(24 + 54, TickAmount(Garrote));
    }

    [Fact]
    public void OtherRogueDots_AreNotTouched()
    {
        _combos.AddComboPoints(_rogue, _enemy, 5);

        _kit.System.CastSpell(_rogue, OtherDot, SpellCastTargets.ForUnit(_enemy.Guid), triggered: true);

        Assert.Equal(24, TickAmount(OtherDot));
    }

    [Fact]
    public void TheTicks_DealTheSnapshot()
    {
        _kit.System.CastSpell(_rogue, Garrote, SpellCastTargets.ForUnit(_enemy.Guid), triggered: true);
        uint before = _enemy.Health;
        _rogue.SetInt32(UpdateFields.UnitFieldAttackPower, 0); // a later change does not move the stored amount

        _kit.Advance(3000);

        Assert.Equal(before - 54u, _enemy.Health);
    }
}
