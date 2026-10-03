using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.CombatMechanics;

/// <summary>
/// S09 combo points against vmangos: Player::AddComboPoints / ClearComboPoints / SetComboPoints (Player.cpp:19032-19093),
/// Spell::CheckPower (Spell.cpp:7035-7038), CalculateSpellEffectValue (SpellCaster.cpp:1190-1192), CalculateDuration
/// (SpellEntry.cpp:731-735), Spell::finish (Spell.cpp:4374-4395) and EffectAddComboPoints (SpellEffects.cpp:4618-4630).
/// </summary>
public sealed class ComboPointTests
{
    private const uint Builder = 940001;       // damage + one combo point
    private const uint Finisher = 940002;      // FINISHING_MOVE_DAMAGE, +5 damage per point
    private const uint SliceLike = 940003;     // FINISHING_MOVE_DURATION self buff, 6 s .. 18 s
    private const uint PlainStretch = 940004;  // an ordinary buff whose duration index has base != max
    private const uint RetainAura = 940005;    // SPELL_AURA_RETAIN_COMBO_POINTS

    private const uint FinishingDamage = 0x00100000;
    private const uint FinishingDuration = 0x00400000;

    private static SpellInfo NoGcd(SpellInfo spell) => spell with { StartRecoveryCategory = 0, StartRecoveryTime = 0 };

    private static IEnumerable<SpellInfo> Spells()
    {
        yield return NoGcd(Spell(Builder,
            Effect(SpellEffectName.SchoolDamage, 4, SpellImplicitTarget.UnitEnemy),
            Effect(SpellEffectName.AddComboPoints, 1, SpellImplicitTarget.UnitEnemy)) with { RangeIndex = 4, Range = new SpellRange(0, 30) });
        yield return NoGcd(Spell(Finisher, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy) with { PointsPerComboPoint = 5.0f }) with
        {
            AttributesEx = (SpellAttributesEx)FinishingDamage,
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
        });
        yield return NoGcd(Spell(SliceLike, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Dummy)) with
        {
            AttributesEx = (SpellAttributesEx)FinishingDuration,
            Duration = new SpellDuration(6000, 0, 18000),
        });
        yield return NoGcd(Spell(PlainStretch, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Dummy)) with
        {
            Duration = new SpellDuration(10000, 0, 20000),
        });
        yield return NoGcd(Spell(RetainAura, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.RetainComboPoints)) with
        {
            Duration = new SpellDuration(30000, 0, 30000),
        });
    }

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            Kit = new SpellTestKit([.. Spells()]);
            (Rogue, _) = Kit.AddPlayer(1);
            Rogue.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Rogue);
            (Enemy, _) = Kit.AddPlayer(2, 3, 0);
            (OtherEnemy, _) = Kit.AddPlayer(3, 0, 3);
            (Warrior, _) = Kit.AddPlayer(4, 0, -3);
            Warrior.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Warrior);
            Combos = new ComboPointService(Kit.System, (_, guid) => Kit.World.FindOnlinePlayer(guid));
            Combos.Install();
            Kit.World.RunTick(0);
        }

        public SpellTestKit Kit { get; }

        public Player Rogue { get; }

        public Player Warrior { get; }

        public Player Enemy { get; }

        public Player OtherEnemy { get; }

        public ComboPointService Combos { get; }

        public SpellSystem System => Kit.System;

        public SpellCastResult Cast(Player caster, uint spell, Unit? target = null, bool triggered = false)
            => System.CastSpell(caster, spell, target is null ? SpellCastTargets.ForSelf() : SpellCastTargets.ForUnit(target.Guid), triggered);

        public void Dispose() => Kit.Dispose();
    }

    // --- state and client fields ------------------------------------------------------------------------------

    [Fact]
    public void AddComboPoints_AccumulatesOnOneTarget_CapsAtFive_AndWritesTheClientFields()
    {
        using var rig = new Rig();

        rig.Combos.AddComboPoints(rig.Rogue, rig.Enemy, 2);
        rig.Combos.AddComboPoints(rig.Rogue, rig.Enemy, 1);
        Assert.Equal(3, rig.Combos.GetComboPoints(rig.Rogue));
        Assert.Equal(rig.Enemy.Guid, rig.Combos.GetComboTarget(rig.Rogue));
        Assert.Equal(rig.Enemy.Guid.Value, rig.Rogue.GetUInt64(UpdateFields.PlayerFieldComboTarget));
        Assert.Equal(3, rig.Rogue.GetByte(UpdateFields.PlayerFieldBytes, 1));

        rig.Combos.AddComboPoints(rig.Rogue, rig.Enemy, 9);
        Assert.Equal(5, rig.Combos.GetComboPoints(rig.Rogue));
        Assert.Equal(5, rig.Rogue.GetByte(UpdateFields.PlayerFieldBytes, 1));
    }

    [Fact]
    public void AddComboPoints_OnANewTarget_RestartsTheCount()
    {
        using var rig = new Rig();
        rig.Combos.AddComboPoints(rig.Rogue, rig.Enemy, 4);

        rig.Combos.AddComboPoints(rig.Rogue, rig.OtherEnemy, 1);

        Assert.Equal(1, rig.Combos.GetComboPoints(rig.Rogue));
        Assert.Equal(rig.OtherEnemy.Guid, rig.Combos.GetComboTarget(rig.Rogue));
        Assert.Equal(rig.OtherEnemy.Guid.Value, rig.Rogue.GetUInt64(UpdateFields.PlayerFieldComboTarget));
    }

    [Fact]
    public void AddComboPoints_NegativeCount_FloorsAtZero_AndZeroDoesNothing()
    {
        using var rig = new Rig();
        rig.Combos.AddComboPoints(rig.Rogue, rig.Enemy, 0);
        Assert.Equal(0, rig.Combos.GetComboPoints(rig.Rogue));
        Assert.True(rig.Combos.GetComboTarget(rig.Rogue).IsEmpty);

        rig.Combos.AddComboPoints(rig.Rogue, rig.Enemy, 2);
        rig.Combos.AddComboPoints(rig.Rogue, rig.Enemy, -9);

        Assert.Equal(0, rig.Combos.GetComboPoints(rig.Rogue));
    }

    [Fact]
    public void ClearComboPoints_ResetsTheFields_AndDoesNothingWithoutPoints()
    {
        using var rig = new Rig();
        rig.Combos.ClearComboPoints(rig.Rogue);   // nothing held: no throw, no change

        rig.Combos.AddComboPoints(rig.Rogue, rig.Enemy, 3);
        rig.Combos.ClearComboPoints(rig.Rogue);

        Assert.Equal(0, rig.Combos.GetComboPoints(rig.Rogue));
        Assert.True(rig.Combos.GetComboTarget(rig.Rogue).IsEmpty);
        Assert.Equal(0, rig.Rogue.GetByte(UpdateFields.PlayerFieldBytes, 1));
    }

    [Fact]
    public void TheClientFields_AreOnlyWritten_WhileTheTargetCanBeFound()
    {
        // vmangos Player::SetComboPoints writes only when ObjectAccessor::GetUnit finds the target.
        using var kit = new SpellTestKit();
        (Player rogue, _) = kit.AddPlayer(1);
        (Player enemy, _) = kit.AddPlayer(2, 3, 0);
        var combos = new ComboPointService(kit.System, (_, _) => null);

        combos.AddComboPoints(rogue, enemy, 3);

        Assert.Equal(3, combos.GetComboPoints(rogue));
        Assert.Equal(0ul, rogue.GetUInt64(UpdateFields.PlayerFieldComboTarget));
        Assert.Equal(0, rogue.GetByte(UpdateFields.PlayerFieldBytes, 1));
    }

    [Fact]
    public void ComboPoints_AreLost_WhenTheTargetIsGone_OrThePlayerLeaves()
    {
        using var rig = new Rig();
        rig.Combos.AddComboPoints(rig.Rogue, rig.Enemy, 3);
        rig.Combos.AddComboPoints(rig.Warrior, rig.Enemy, 1);

        rig.Combos.OnTargetGone(rig.Enemy);

        Assert.Equal(0, rig.Combos.GetComboPoints(rig.Rogue));
        Assert.Equal(0, rig.Combos.GetComboPoints(rig.Warrior));

        rig.Combos.AddComboPoints(rig.Rogue, rig.OtherEnemy, 2);
        rig.Combos.OnTargetGone(rig.Enemy);   // a different unit leaving changes nothing
        Assert.Equal(2, rig.Combos.GetComboPoints(rig.Rogue));

        rig.Combos.OnPlayerGone(rig.Rogue);
        Assert.Equal(0, rig.Combos.GetComboPoints(rig.Rogue));
    }

    [Fact]
    public void Observe_DropsThePoints_WhenTheTargetOrTheOwnerDies()
    {
        using var rig = new Rig();
        MapCombat combat = rig.Rogue.Map!.Combat;
        rig.Combos.Observe(combat);
        rig.Combos.Observe(combat);   // twice is harmless
        rig.Combos.AddComboPoints(rig.Rogue, rig.Enemy, 3);

        combat.Kill(rig.Warrior, rig.Enemy);
        Assert.Equal(0, rig.Combos.GetComboPoints(rig.Rogue));

        rig.Combos.AddComboPoints(rig.Rogue, rig.OtherEnemy, 2);
        combat.Kill(rig.Warrior, rig.Rogue);
        Assert.Equal(0, rig.Combos.GetComboPoints(rig.Rogue));
    }

    [Fact]
    public void ChangingTheComboPoints_EndsAuraRetainComboPointsAuras()
    {
        using var rig = new Rig();
        rig.Cast(rig.Rogue, RetainAura, triggered: true);
        Assert.True(rig.System.HasAura(rig.Rogue, RetainAura));

        rig.Combos.AddComboPoints(rig.Rogue, rig.Enemy, 1);

        Assert.False(rig.System.HasAura(rig.Rogue, RetainAura));

        rig.Cast(rig.Rogue, RetainAura, triggered: true);
        rig.Combos.ClearComboPoints(rig.Rogue);
        Assert.False(rig.System.HasAura(rig.Rogue, RetainAura));
    }

    // --- the finishing move check -----------------------------------------------------------------------------

    [Fact]
    public void FinishingMove_WithoutPoints_FailsNoComboPoints_ForARogue_AndBadTargets_ForAWarrior()
    {
        using var rig = new Rig();

        Assert.Equal(SpellCastResult.NoComboPoints, rig.Cast(rig.Rogue, Finisher, rig.Enemy));
        Assert.Equal(SpellCastResult.BadTargets, rig.Cast(rig.Warrior, Finisher, rig.Enemy));
        Assert.Equal(60u, rig.Enemy.Health);
    }

    [Fact]
    public void FinishingMove_NeedsThePointsOnItsOwnTarget()
    {
        using var rig = new Rig();
        rig.Combos.AddComboPoints(rig.Rogue, rig.Enemy, 2);

        Assert.Equal(SpellCastResult.NoComboPoints, rig.Cast(rig.Rogue, Finisher, rig.OtherEnemy));
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(rig.Rogue, Finisher, rig.Enemy));
    }

    [Fact]
    public void FinishingMove_TriggeredCasts_AndSelfBuffs_SkipTheCheck()
    {
        using var rig = new Rig();

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(rig.Rogue, Finisher, rig.Enemy, triggered: true));
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(rig.Rogue, SliceLike));   // no explicit unit target: vmangos asks only for explicit targets
    }

    [Fact]
    public void ANonFinishingSpell_IsNotGated()
    {
        using var rig = new Rig();
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(rig.Rogue, Builder, rig.Enemy));
    }

    // --- builders, scaling and spending -----------------------------------------------------------------------

    [Fact]
    public void AddComboPointsEffect_GivesPointsOnTheTarget_AndRestartsOnANewOne()
    {
        using var rig = new Rig();

        rig.Cast(rig.Rogue, Builder, rig.Enemy);
        rig.Cast(rig.Rogue, Builder, rig.Enemy);
        Assert.Equal(2, rig.Combos.GetComboPoints(rig.Rogue));
        Assert.Equal(rig.Enemy.Guid, rig.Combos.GetComboTarget(rig.Rogue));

        rig.Cast(rig.Rogue, Builder, rig.OtherEnemy);
        Assert.Equal(1, rig.Combos.GetComboPoints(rig.Rogue));
        Assert.Equal(rig.OtherEnemy.Guid, rig.Combos.GetComboTarget(rig.Rogue));
    }

    [Fact]
    public void FinishingMove_DamageGrowsByThePerPointValue_OnTheComboTarget()
    {
        using var rig = new Rig();
        rig.Combos.AddComboPoints(rig.Rogue, rig.Enemy, 3);

        rig.Cast(rig.Rogue, Finisher, rig.Enemy);

        Assert.Equal(60u - 25u, rig.Enemy.Health);   // 10 + 5 x 3
    }

    [Fact]
    public void FinishingMove_SpendsThePoints_WhenItLands()
    {
        using var rig = new Rig();
        rig.Combos.AddComboPoints(rig.Rogue, rig.Enemy, 3);

        rig.Cast(rig.Rogue, Finisher, rig.Enemy);

        Assert.Equal(0, rig.Combos.GetComboPoints(rig.Rogue));
        Assert.Equal(0, rig.Rogue.GetByte(UpdateFields.PlayerFieldBytes, 1));
    }

    [Fact]
    public void FinishingMove_KeepsThePoints_WhenAHarmfulMoveIsAvoided()
    {
        using var rig = new Rig();
        var rules = new FixedRules { Miss = SpellMissInfo.Dodge };
        rig.System.CombatRules = rules;
        rig.Combos.AddComboPoints(rig.Rogue, rig.Enemy, 3);

        rig.Cast(rig.Rogue, Finisher, rig.Enemy);
        Assert.Equal(3, rig.Combos.GetComboPoints(rig.Rogue));

        rules.Miss = SpellMissInfo.None;
        rig.Cast(rig.Rogue, Finisher, rig.Enemy);
        Assert.Equal(0, rig.Combos.GetComboPoints(rig.Rogue));
    }

    [Fact]
    public void AFinishingBuff_AlwaysSpendsThePoints()
    {
        using var rig = new Rig();
        rig.Combos.AddComboPoints(rig.Rogue, rig.Enemy, 4);

        rig.Cast(rig.Rogue, SliceLike);

        Assert.Equal(0, rig.Combos.GetComboPoints(rig.Rogue));
    }

    [Theory]
    [InlineData(0, 6000)]
    [InlineData(1, 8400)]
    [InlineData(2, 10800)]
    [InlineData(3, 13200)]
    [InlineData(5, 18000)]
    public void Duration_StretchesTowardTheMaximum_ByPointsOverFive(int points, int expectedMs)
    {
        using var rig = new Rig();
        if (points > 0)
        {
            rig.Combos.AddComboPoints(rig.Rogue, rig.Enemy, points);
        }

        rig.Cast(rig.Rogue, SliceLike, triggered: true);

        Assert.Equal(expectedMs, rig.System.GetState(rig.Rogue.Guid)!.AuraHolders.Single(h => h.Spell.Id == SliceLike).MaxDuration);
    }

    [Fact]
    public void Duration_StretchesForAnyBuffWithDifferentBaseAndMax_NotOnlyFinishers()
    {
        // vmangos CalculateDuration does not look at NeedsComboPoints (SpellEntry.cpp:731-735).
        using var rig = new Rig();
        rig.Combos.AddComboPoints(rig.Rogue, rig.Enemy, 5);

        rig.Cast(rig.Rogue, PlainStretch, triggered: true);

        Assert.Equal(20000, rig.System.GetState(rig.Rogue.Guid)!.AuraHolders.Single(h => h.Spell.Id == PlainStretch).MaxDuration);
    }

    [Fact]
    public void Duration_IsNotStretchedForNonPlayers_OrWithoutPoints()
    {
        using var rig = new Rig();
        rig.Cast(rig.Warrior, PlainStretch, triggered: true);

        Assert.Equal(10000, rig.System.GetState(rig.Warrior.Guid)!.AuraHolders.Single(h => h.Spell.Id == PlainStretch).MaxDuration);
    }

    [Fact]
    public void Install_RegistersTheCheck_TheModifier_TheObserver_AndTheEffect()
    {
        using var rig = new Rig();

        Assert.Single(rig.System.CastChecks.OfType<ComboPointCastCheck>());
        Assert.Single(rig.System.ValueModifiers.OfType<ComboValueModifier>());
        Assert.Single(rig.System.Observers.OfType<ComboFinishObserver>());
        Assert.True(rig.System.HasEffectHandler(SpellEffectName.AddComboPoints));
    }
}
