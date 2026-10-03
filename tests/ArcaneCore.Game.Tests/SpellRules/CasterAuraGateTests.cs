using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Spells.Rules.Gating;
using ArcaneCore.Game.Tests.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.SpellRules;

/// <summary>What the caster's own states prevent (vmangos Spell::CheckCasterAuras, Spell.cpp:6565-6672).</summary>
public sealed class CasterAuraGateTests
{
    private const uint Plain = 960_001;
    private const uint Silenceable = 960_002;
    private const uint Pacifiable = 960_003;
    private const uint StunFlagged = 960_004;
    private const uint TrinketShape = 960_005;
    private const uint DivineShieldShape = 960_006;
    private const uint StunAuraSource = 960_010;
    private const uint SilenceAuraSource = 960_011;
    private const uint PacifyAuraSource = 960_012;

    private static SpellInfo Cast(uint id, uint prevention = 0, SpellInterruptFlags interrupt = SpellInterruptFlags.None) =>
        SpellTestKit.Spell(id, SpellTestKit.Effect(SpellEffectName.SchoolDamage, 5, SpellImplicitTarget.UnitEnemy))
        with { PreventionType = prevention, InterruptFlags = interrupt, School = SpellSchool.Fire, CastTime = new SpellCastTime(2000, 0, 0) };

    private static SpellInfo ImmunityGrant(uint id, uint prevention, params SpellEffectInfo[] effects) =>
        SpellTestKit.Spell(id, effects) with { AttributesEx = (SpellAttributesEx)SpellRuleFlags.ExImmunityPurgesEffect, PreventionType = prevention, CastTime = default };

    private static SpellTestKit Kit() => new(
        Cast(Plain),
        Cast(Silenceable, prevention: 1),
        Cast(Pacifiable, prevention: 2),
        Cast(StunFlagged, interrupt: SpellInterruptFlags.Stun),
        ImmunityGrant(TrinketShape, prevention: 0,
            SpellTestKit.Effect(SpellEffectName.ApplyAura, (int)(SpellMechanics.Mask(SpellMechanic.Stun) | SpellMechanics.Mask(SpellMechanic.Fear)), aura: AuraType.MechanicImmunityMask, misc: (int)(SpellMechanics.Mask(SpellMechanic.Stun) | SpellMechanics.Mask(SpellMechanic.Fear)))),
        ImmunityGrant(DivineShieldShape, prevention: 2,
            SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.SchoolImmunity, misc: (int)SpellSchoolMasks.All)),
        RuleTestSupport.Grant(StunAuraSource, AuraType.ModStun, 0) with { Mechanic = (uint)SpellMechanic.Stun },
        RuleTestSupport.Grant(SilenceAuraSource, AuraType.ModSilence, 0),
        RuleTestSupport.Grant(PacifyAuraSource, AuraType.ModPacify, 0));

    private static Player Caster(SpellTestKit kit) => kit.AddPlayer(1).Player;

    private static SpellCastResult Check(SpellTestKit kit, Unit caster, uint spell, int castTime = -1)
    {
        SpellInfo info = kit.Store.Get(spell)!;
        return CasterAuraGate.Check(kit.System, caster, info, castTime < 0 ? info.CastTime.Base : castTime);
    }

    [Fact]
    public void Stunned_BlocksInstantSpells_AndCastsOnlyWithTheStunInterruptFlag()
    {
        using SpellTestKit kit = Kit();
        Player caster = Caster(kit);
        caster.UnitFlags |= UnitFlags.Stunned;

        Assert.Equal(SpellCastResult.Stunned, Check(kit, caster, Plain, castTime: 0));      // instant: blocked even without the flag
        Assert.Equal(SpellCastResult.CastOk, Check(kit, caster, Plain, castTime: 2000));    // a cast bar without the flag is allowed
        Assert.Equal(SpellCastResult.Stunned, Check(kit, caster, StunFlagged, castTime: 2000));
    }

    [Fact]
    public void ConfusedAndFleeing_BlockEverything()
    {
        using SpellTestKit kit = Kit();
        Player caster = Caster(kit);

        caster.UnitFlags |= UnitFlags.Confused;
        Assert.Equal(SpellCastResult.Confused, Check(kit, caster, Plain));
        caster.UnitFlags &= ~UnitFlags.Confused;
        caster.UnitFlags |= UnitFlags.Fleeing;
        Assert.Equal(SpellCastResult.Fleeing, Check(kit, caster, Plain, castTime: 0));
    }

    [Fact]
    public void Silenced_BlocksOnlySilencePreventionSpells_AndIsSkippedWhileStunned()
    {
        using SpellTestKit kit = Kit();
        Player caster = Caster(kit);
        caster.UnitFlags |= UnitFlags.Silenced;

        Assert.Equal(SpellCastResult.Silenced, Check(kit, caster, Silenceable));
        Assert.Equal(SpellCastResult.CastOk, Check(kit, caster, Pacifiable));
        Assert.Equal(SpellCastResult.CastOk, Check(kit, caster, Plain));

        caster.UnitFlags |= UnitFlags.Stunned;
        Assert.Equal(SpellCastResult.CastOk, Check(kit, caster, Silenceable, castTime: 2000)); // the silence test is not made while stunned
        Assert.Equal(SpellCastResult.Stunned, Check(kit, caster, Silenceable, castTime: 0));
    }

    [Fact]
    public void Pacified_BlocksOnlyPacifyPreventionSpells()
    {
        using SpellTestKit kit = Kit();
        Player caster = Caster(kit);
        caster.UnitFlags |= UnitFlags.Pacified;

        Assert.Equal(SpellCastResult.Pacified, Check(kit, caster, Pacifiable));
        Assert.Equal(SpellCastResult.CastOk, Check(kit, caster, Silenceable));
    }

    [Fact]
    public void ASchoolLockout_BlocksSilencePreventionSpellsOfThatSchoolAsSilenced()
    {
        using SpellTestKit kit = Kit();
        Player caster = Caster(kit);
        kit.System.StateOf(caster).SchoolLockouts[SpellSchool.Fire] = kit.Now + 4000;

        Assert.Equal(SpellCastResult.Silenced, Check(kit, caster, Silenceable));
        Assert.Equal(SpellCastResult.CastOk, Check(kit, caster, Plain)); // PreventionType 0 is not locked out
        Assert.Equal(SpellCastResult.CastOk, CasterAuraGate.Check(kit.System, caster, kit.Store.Get(Silenceable)! with { School = SpellSchool.Frost }, 2000));
    }

    [Fact]
    public void AnImmunityGrantingSpell_CanBeCastThroughTheStatesItCovers()
    {
        using SpellTestKit kit = Kit();
        Player caster = Caster(kit);
        RuleTestSupport.Apply(kit, caster, StunAuraSource);
        Assert.True((caster.UnitFlags & UnitFlags.Stunned) != 0);

        // The trinket shape covers stun and fear: castable although stunned (instant).
        Assert.Equal(SpellCastResult.CastOk, Check(kit, caster, TrinketShape, castTime: 0));
        // A plain instant spell is not.
        Assert.Equal(SpellCastResult.Stunned, Check(kit, caster, Plain, castTime: 0));
    }


    [Fact]
    public void ASchoolImmunityShape_PassesWhenOnlyPacified()
    {
        using SpellTestKit kit = Kit();
        Player caster = Caster(kit);
        RuleTestSupport.Apply(kit, caster, PacifyAuraSource);
        Assert.True((caster.UnitFlags & UnitFlags.Pacified) != 0);

        Assert.Equal(SpellCastResult.CastOk, Check(kit, caster, DivineShieldShape));
        Assert.Equal(SpellCastResult.Pacified, Check(kit, caster, Pacifiable));
    }

    [Fact]
    public void ImmunityGrantingSpell_WhilePacifiedAndSilenced_ReportsWhatItDoesNotCover()
    {
        using SpellTestKit kit = Kit();
        Player caster = Caster(kit);
        RuleTestSupport.Apply(kit, caster, PacifyAuraSource);
        RuleTestSupport.Apply(kit, caster, SilenceAuraSource);
        SpellInfo shield = kit.Store.Get(DivineShieldShape)!;

        // Both auras come from spells whose school mask is physical (covered by the all-schools immunity): castable.
        Assert.Equal(SpellCastResult.CastOk, CasterAuraGate.Check(kit.System, caster, shield, 0));
        // A mechanic-only grant does not cover them: the right reason comes back.
        SpellInfo mechanicOnly = kit.Store.Get(TrinketShape)! with { PreventionType = 2 };
        Assert.Equal(SpellCastResult.Pacified, CasterAuraGate.Check(kit.System, caster, mechanicOnly, 0));
    }

    [Fact]
    public void IgnoringRestrictionsSpells_SkipTheGate()
    {
        using SpellTestKit kit = Kit();
        Player caster = Caster(kit);
        caster.UnitFlags |= UnitFlags.Stunned | UnitFlags.Confused | UnitFlags.Silenced;
        SpellInfo ignoring = kit.Store.Get(Silenceable)! with { AttributesEx3 = SpellRuleFlags.Ex3IgnoreCasterAndTargetRestrictions };

        Assert.Equal(SpellCastResult.CastOk, CasterAuraGate.Check(kit.System, caster, ignoring, 0));
    }

    [Fact]
    public void ThroughCheckCast_AStunnedPlayerCannotCastAnInstantSpell()
    {
        using SpellTestKit kit = Kit();
        Player caster = Caster(kit);
        kit.Spellbook.Teach(caster, SpellTestKit.InstantHeal);
        RuleTestSupport.Apply(kit, caster, StunAuraSource);

        Assert.Equal(SpellCastResult.Stunned, kit.System.HandleCastRequest(caster, SpellTestKit.InstantHeal, SpellCastTargets.ForSelf()));
    }
}
