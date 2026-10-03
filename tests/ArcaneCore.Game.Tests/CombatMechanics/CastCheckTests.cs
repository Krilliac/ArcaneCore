using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.CombatMechanics;

/// <summary>S16: the generic CheckCast rules the warrior and rogue abilities hit (vmangos Spell.cpp:5302-5350, 5640-5649).</summary>
public sealed class CastCheckTests
{
    private const uint Strike = 960001;          // an ordinary melee ability
    private const uint SittingSpell = 960002;    // ALLOW_WHILE_SITTING
    private const uint ChargeLike = 960003;      // NOT_IN_COMBAT_ONLY_PEACEFUL (classic-db Charge: Attributes 805634064 = 0x30050010)
    private const uint StealthOpener = 960004;   // ONLY_STEALTHED
    private const uint StealthAura = 960005;     // SPELL_AURA_MOD_STEALTH
    private const uint BackstabLike = 960006;    // AttributesEx2 == 0x100000, AttributesEx & 0x200
    private const uint FacingSpell = 960007;     // Attributes == 0x150010
    private const uint CastTimeCharge = 960008;  // a combat-forbidden spell with a cast time
    private const uint CooldownStrike = 960009;

    private static SpellInfo Melee(uint id) => Spell(id, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy)) with
    {
        RangeIndex = 4,
        Range = new SpellRange(0, 30),
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static IEnumerable<SpellInfo> Spells()
    {
        yield return Melee(Strike);
        yield return Melee(SittingSpell) with { Attributes = SpellAttributes.AllowWhileSitting };
        yield return Melee(ChargeLike) with { Attributes = (SpellAttributes)0x30050010u };
        yield return Melee(StealthOpener) with { Attributes = (SpellAttributes)0x00020000u };
        yield return Spell(StealthAura, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.ModStealth)) with
        {
            Duration = new SpellDuration(-1, 0, -1),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };
        yield return Melee(BackstabLike) with { AttributesEx = (SpellAttributesEx)0x00000200u, AttributesEx2 = (SpellAttributesEx2)0x00100000u };
        yield return Melee(FacingSpell) with { Attributes = (SpellAttributes)0x00150010u };
        yield return Melee(CastTimeCharge) with { CastTime = new SpellCastTime(1000, 0, 0), Attributes = (SpellAttributes)0x10000000u };
        yield return Melee(CooldownStrike) with { RecoveryTime = 10_000 };
    }

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            Kit = new SpellTestKit([.. Spells()]);
            (Caster, _) = Kit.AddPlayer(1);
            (Target, _) = Kit.AddPlayer(2, 3, 0);
            GeneralCastChecks.Install(Kit.System);
            Kit.Spellbook.Teach(Caster, CastTimeCharge);
            Kit.World.RunTick(0);
        }

        public SpellTestKit Kit { get; }

        public Player Caster { get; }

        public Player Target { get; }

        public SpellSystem System => Kit.System;

        public SpellCastResult Cast(uint spell, bool triggered = false)
            => System.CastSpell(Caster, spell, SpellCastTargets.ForUnit(Target.Guid), triggered);

        public void Dispose() => Kit.Dispose();
    }

    [Fact]
    public void Cast_WhileSitting_FailsNotStanding_UnlessTheSpellAllowsIt()
    {
        using var rig = new Rig();
        rig.Caster.StandState = StandState.Sit;

        Assert.Equal(SpellCastResult.NotStanding, rig.Cast(Strike));
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(SittingSpell));
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(Strike, triggered: true));

        rig.Caster.StandState = StandState.Stand;
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(Strike));
    }

    [Fact]
    public void TheDeadStandState_CountsAsStanding()
    {
        // vmangos Unit::IsStandingUp is true for the dead stand state too (a feigning caster is not told to stand).
        using var rig = new Rig();
        rig.Caster.StandState = StandState.Dead;

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(Strike));
    }

    [Fact]
    public void TheStandingCheck_RunsBeforeTheCooldownCheck()
    {
        using var rig = new Rig();
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(CooldownStrike));
        rig.Caster.StandState = StandState.Sit;

        Assert.Equal(SpellCastResult.NotStanding, rig.Cast(CooldownStrike));   // NOT_READY if the cooldown were asked first
    }

    [Fact]
    public void Charge_InCombat_FailsAffectingCombat_ButTriggeredCastsPass()
    {
        using var rig = new Rig();
        rig.Caster.UnitFlags |= UnitFlags.InCombat;

        Assert.Equal(SpellCastResult.AffectingCombat, rig.Cast(ChargeLike));
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(ChargeLike, triggered: true));

        rig.Caster.UnitFlags &= ~UnitFlags.InCombat;
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(ChargeLike));
    }

    [Fact]
    public void ACombatForbiddenSpellStartedOutOfCombat_StillLands_WhenCombatStartsDuringTheCast()
    {
        using var rig = new Rig();
        Assert.Equal(SpellCastResult.CastOk, rig.System.HandleCastRequest(rig.Caster, CastTimeCharge, SpellCastTargets.ForUnit(rig.Target.Guid)));

        rig.Caster.UnitFlags |= UnitFlags.InCombat;   // the landing re-check is not strict
        rig.Kit.Advance(1000);

        Assert.True(rig.Target.Health < 60u);
    }

    [Fact]
    public void AnOpener_NeedsAStealthAura()
    {
        using var rig = new Rig();
        Assert.Equal(SpellCastResult.OnlyStealthed, rig.Cast(StealthOpener));

        rig.Cast(StealthAura, triggered: true);
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(StealthOpener));

        rig.System.RemoveAuras(rig.Caster, StealthAura);
        Assert.Equal(SpellCastResult.OnlyStealthed, rig.Cast(StealthOpener));
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(StealthOpener, triggered: true));
    }

    [Fact]
    public void ABehindOnlySpell_FromTheFront_FailsNotBehind()
    {
        using var rig = new Rig();
        rig.Target.Relocate(rig.Target.X, rig.Target.Y, rig.Target.Z, MathF.PI, 1);   // faces the caster

        Assert.Equal(SpellCastResult.NotBehind, rig.Cast(BackstabLike));

        rig.Target.Relocate(rig.Target.X, rig.Target.Y, rig.Target.Z, 0, 2);   // faces away
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(BackstabLike));
    }

    [Fact]
    public void ANotInFrontSpell_NeedsTheTargetToFaceTheCaster()
    {
        using var rig = new Rig();
        rig.Target.Relocate(rig.Target.X, rig.Target.Y, rig.Target.Z, 0, 1);   // faces away

        Assert.Equal(SpellCastResult.NotInfront, rig.Cast(FacingSpell));

        rig.Target.Relocate(rig.Target.X, rig.Target.Y, rig.Target.Z, MathF.PI, 2);
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(FacingSpell));
    }

    [Fact]
    public void Install_RegistersTheFourChecks_InTheirPhases_AndTheOrdersFollowTheSource()
    {
        using var rig = new Rig();

        Assert.Equal(SpellCheckPhase.Start, rig.System.CastChecks.OfType<StandingCastCheck>().Single().Phase);
        Assert.Equal(SpellCheckPhase.Caster, rig.System.CastChecks.OfType<CombatRestrictionCastCheck>().Single().Phase);
        Assert.Equal(SpellCheckPhase.Caster, rig.System.CastChecks.OfType<StealthCastCheck>().Single().Phase);
        Assert.Equal(SpellCheckPhase.Target, rig.System.CastChecks.OfType<FacingCastCheck>().Single().Phase);
        Assert.True(SpellCastCheckOrder.AffectingCombat < SpellCastCheckOrder.Shapeshift);   // vmangos: combat check 5343, shapeshift 5349, stealth 5353
        Assert.True(SpellCastCheckOrder.Shapeshift < SpellCastCheckOrder.Stealth);
        Assert.True(SpellCastCheckOrder.Stealth < SpellCastCheckOrder.CasterAuraState);
    }

    [Fact]
    public void IsFromBehindOnly_FollowsTheVmangosAttributeRule()
    {
        Assert.True(FacingCastCheck.IsFromBehindOnly(new SpellInfo { Id = 1, AttributesEx = (SpellAttributesEx)0x200u, AttributesEx2 = (SpellAttributesEx2)0x100000u }));
        Assert.False(FacingCastCheck.IsFromBehindOnly(new SpellInfo { Id = 1, AttributesEx = (SpellAttributesEx)0x200u, AttributesEx2 = (SpellAttributesEx2)0x100001u }));   // Ex2 must be exactly 0x100000
        Assert.False(FacingCastCheck.IsFromBehindOnly(new SpellInfo { Id = 1, AttributesEx2 = (SpellAttributesEx2)0x100000u }));
    }
}
