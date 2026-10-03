using ArcaneCore.Game.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.CombatMechanics;

/// <summary>
/// S02a spell data model: the additive <see cref="SpellInfo"/> fields, the vmangos enum values and the
/// pure helpers (vmangos SpellEntry.h / SpellEntry.cpp). Attribute words used below are the 1.12.1
/// <c>spell_template</c> values of the cited classic-db spells (read only; the numbers are the facts
/// asserted, no rows are copied).
/// </summary>
public sealed class SpellModelTests
{
    /// <summary>Heroic Strike (78) / Cleave (845): classic-db Attributes 327700 = 0x50014 (bit 0x4, not 0x400).</summary>
    private const uint HeroicStrikeAttributes = 0x00050014;

    /// <summary>Overpower (7384): classic-db Attributes 2424848 = 0x250010, AttributesEx 1209008640 = 0x48100000.</summary>
    private const uint OverpowerAttributes = 0x00250010;

    private const uint OverpowerAttributesEx = 0x48100000;

    private static SpellInfo Spell(uint attributes = 0, uint ex = 0, uint ex2 = 0, uint stances = 0, uint stancesNot = 0, uint id = 1) => new()
    {
        Id = id,
        Attributes = (SpellAttributes)attributes,
        AttributesEx = (SpellAttributesEx)ex,
        AttributesEx2 = (SpellAttributesEx2)ex2,
        Stances = stances,
        StancesNot = stancesNot,
        Effects = [],
    };

    // --- enum values (vmangos SharedDefines.h:1420-1442, SpellDefines.h:602-631/642-656/1043-1117) ---------

    [Fact]
    public void ShapeshiftForm_Values_MatchVmangos()
    {
        Assert.Equal(0x01, (int)ShapeshiftForm.Cat);
        Assert.Equal(0x05, (int)ShapeshiftForm.Bear);
        Assert.Equal(0x08, (int)ShapeshiftForm.DireBear);
        Assert.Equal(0x10, (int)ShapeshiftForm.GhostWolf);
        Assert.Equal(0x11, (int)ShapeshiftForm.BattleStance);
        Assert.Equal(0x12, (int)ShapeshiftForm.DefensiveStance);
        Assert.Equal(0x13, (int)ShapeshiftForm.BerserkerStance);
        Assert.Equal(0x1C, (int)ShapeshiftForm.Shadow);
        Assert.Equal(0x1E, (int)ShapeshiftForm.Stealth);
        Assert.Equal(0x1F, (int)ShapeshiftForm.Moonkin);
        Assert.Equal(0x20, (int)ShapeshiftForm.SpiritOfRedemption);
    }

    [Fact]
    public void AuraState_Values_MatchVmangos_IncludingTheCustomHealthlessStates()
    {
        Assert.Equal(1u, (uint)AuraState.Defense);
        Assert.Equal(2u, (uint)AuraState.Healthless20Percent);
        Assert.Equal(3u, (uint)AuraState.Berserking);
        Assert.Equal(5u, (uint)AuraState.Judgement);
        Assert.Equal(7u, (uint)AuraState.HunterParry);
        Assert.Equal(8u, (uint)AuraState.RogueAttackFromStealth);
        Assert.Equal(9u, (uint)AuraState.Healthless15Percent);
        Assert.Equal(10u, (uint)AuraState.Healthless10Percent);
        Assert.Equal(11u, (uint)AuraState.Healthless5Percent);
    }

    [Fact]
    public void ProcFlags_Values_MatchVmangos()
    {
        Assert.Equal(0x00000004u, (uint)ProcFlags.DealMeleeSwing);
        Assert.Equal(0x00000008u, (uint)ProcFlags.TakeMeleeSwing);
        Assert.Equal(0x00000010u, (uint)ProcFlags.DealMeleeAbility);
        Assert.Equal(0x00000020u, (uint)ProcFlags.TakeMeleeAbility);
        Assert.Equal(0x00001000u, (uint)ProcFlags.DealHarmfulAbility);
        Assert.Equal(0x00100000u, (uint)ProcFlags.TakenAnyDamage);
        Assert.Equal(0x00800000u, (uint)ProcFlags.OffHandWeaponSwing);

        // Shield Block (2565) carries 680 = TAKE_MELEE_SWING | TAKE_MELEE_ABILITY | TAKE_RANGED_ATTACK | TAKE_RANGED_ABILITY.
        Assert.Equal(680u, (uint)(ProcFlags.TakeMeleeSwing | ProcFlags.TakeMeleeAbility | ProcFlags.TakeRangedAttack | ProcFlags.TakeRangedAbility));
    }

    [Fact]
    public void ProcFlagsEx_Values_MatchVmangos()
    {
        Assert.Equal(0x1u, (uint)ProcFlagsEx.NormalHit);
        Assert.Equal(0x2u, (uint)ProcFlagsEx.CriticalHit);
        Assert.Equal(0x10u, (uint)ProcFlagsEx.Dodge);
        Assert.Equal(0x20u, (uint)ProcFlagsEx.Parry);
        Assert.Equal(0x40u, (uint)ProcFlagsEx.Block);
        Assert.Equal(0x10000u, (uint)ProcFlagsEx.TriggerAlways);
        Assert.Equal(0x80000u, (uint)ProcFlagsEx.CastEnd);
    }

    [Fact]
    public void SpellModOp_Values_MatchVmangos_AndSkipTheUnusedValue13()
    {
        Assert.Equal(0, (int)SpellModOp.Damage);
        Assert.Equal(1, (int)SpellModOp.Duration);
        Assert.Equal(2, (int)SpellModOp.Threat);
        Assert.Equal(7, (int)SpellModOp.CriticalChance);
        Assert.Equal(10, (int)SpellModOp.CastingTime);
        Assert.Equal(11, (int)SpellModOp.Cooldown);
        Assert.Equal(14, (int)SpellModOp.Cost);
        Assert.Equal(21, (int)SpellModOp.GlobalCooldown);
        Assert.Equal(27, (int)SpellModOp.MultipleValue);
        Assert.Equal(28, (int)SpellModOp.ResistDispelChance);
        Assert.Equal(29, (int)SpellModOp.Max);
        Assert.False(Enum.IsDefined((SpellModOp)13));
    }

    // --- attribute bits ---------------------------------------------------------------------------------

    [Fact]
    public void AttributeBits_MatchVmangos()
    {
        Assert.Equal(0x00000004u, (uint)SpellAttributesCombat.OnNextSwingNoDamage);
        Assert.Equal(0x00010000u, (uint)SpellAttributesCombat.NotShapeshift);
        Assert.Equal(0x00200000u, (uint)SpellAttributesCombat.NoActiveDefense);
        Assert.Equal(0x10000000u, (uint)SpellAttributesCombat.NotInCombatOnlyPeaceful);
        Assert.Equal(0x00100000u, (uint)SpellAttributesExCombat.FinishingMoveDamage);
        Assert.Equal(0x00400000u, (uint)SpellAttributesExCombat.FinishingMoveDuration);
        Assert.Equal(0x08000000u, (uint)SpellAttributesExCombat.DiscountPowerOnMiss);
        Assert.Equal(0x40000000u, (uint)SpellAttributesExCombat.ComboOnBlock);
        Assert.Equal(0x00020000u, (uint)SpellAttributesEx2Combat.DoNotResetCombatTimers);
        Assert.Equal(0x00080000u, (uint)SpellAttributesEx2Combat.AllowWhileNotShapeshifted);
        Assert.Equal(0x00000008u, (uint)SpellAttributesEx3Combat.CompletelyBlocked);
        Assert.Equal(0x00000400u, (uint)SpellAttributesEx3Combat.RequiresMainHandWeapon);
        Assert.Equal(0x00040000u, (uint)SpellAttributesEx3Combat.AlwaysHit);
        Assert.Equal(0x01000000u, (uint)SpellAttributesEx3Combat.RequiresOffhandWeapon);
    }

    [Fact]
    public void Overpower_CarriesNoActiveDefense_ComboOnBlock_AndDiscountPowerOnMiss()
    {
        SpellInfo overpower = Spell(OverpowerAttributes, OverpowerAttributesEx);
        Assert.True(overpower.HasAttribute(SpellAttributesCombat.NoActiveDefense));
        Assert.True(overpower.HasAttribute(SpellAttributesExCombat.ComboOnBlock));
        Assert.True(overpower.HasAttribute(SpellAttributesExCombat.DiscountPowerOnMiss));
        Assert.True(overpower.HasAttribute(SpellAttributesExCombat.FinishingMoveDamage));
        Assert.False(overpower.HasAttribute(SpellAttributesCombat.OnNextSwingNoDamage));
    }

    // --- helpers (vmangos SpellEntry.h) -----------------------------------------------------------------

    [Fact]
    public void IsNextMeleeSwing_IsTrueForBit0x4_AndForBit0x400()
    {
        // vmangos SpellEntry.h:887-890 IsNextMeleeSwingSpell = ON_NEXT_SWING_NO_DAMAGE | ON_NEXT_SWING.
        Assert.True(Spell(HeroicStrikeAttributes).IsNextMeleeSwing);   // Heroic Strike / Cleave: 0x4 only
        Assert.True(Spell(0x00000400).IsNextMeleeSwing);               // the 0x400 form (Maul, Raptor Strike...)
        Assert.True(Spell(0x00000404).IsNextMeleeSwing);
        Assert.False(Spell(OverpowerAttributes).IsNextMeleeSwing);
        Assert.False(Spell().IsNextMeleeSwing);
    }

    [Fact]
    public void NeedsComboPoints_IsTrueForEitherFinishingMoveBit()
    {
        // vmangos SpellEntry.h:1082-1085.
        Assert.True(Spell(ex: 0x00100000).NeedsComboPoints);
        Assert.True(Spell(ex: 0x00400000).NeedsComboPoints);
        Assert.True(Spell(OverpowerAttributes, OverpowerAttributesEx).NeedsComboPoints);
        Assert.False(Spell(HeroicStrikeAttributes, ex: 0x08000000).NeedsComboPoints);
    }

    [Fact]
    public void IsRemovedOnShapeLost_FollowsVmangos()
    {
        // vmangos SpellEntry.h:1180-1186.
        Assert.True(Spell(stances: 1u << 16).IsRemovedOnShapeLost);
        Assert.False(Spell().IsRemovedOnShapeLost);
        Assert.False(Spell(stances: 1u << 16, ex2: 0x00080000).IsRemovedOnShapeLost);   // ALLOW_WHILE_NOT_SHAPESHIFTED
        Assert.False(Spell(attributes: 0x00010000, stances: 1u << 16).IsRemovedOnShapeLost);   // NOT_SHAPESHIFT
        Assert.True(Spell(id: 24864).IsRemovedOnShapeLost);   // the hard-coded id in the vmangos condition
    }

    // --- GetErrorAtShapeshiftedCast truth table (vmangos SpellEntry.cpp:1032-1074) -----------------------

    private const uint BattleMask = 1u << 16;     // form 17
    private const uint DefensiveMask = 1u << 17;  // form 18
    private const uint StanceFlag = 1u;            // SHAPESHIFT_FLAG_STANCE (SharedDefines.h:1471)

    [Fact]
    public void ShapeshiftCast_StanceListed_IsOk()
    {
        // Charge (100): Stances 65536 = Battle Stance only.
        SpellInfo charge = Spell(stances: BattleMask, id: 100);
        Assert.Equal(SpellCastResult.CastOk, charge.GetErrorAtShapeshiftedCast((uint)ShapeshiftForm.BattleStance, StanceFlag));
    }

    [Fact]
    public void ShapeshiftCast_StanceNotListed_NeedsOtherShapeshift()
    {
        SpellInfo charge = Spell(stances: BattleMask, id: 100);

        // A warrior stance has SHAPESHIFT_FLAG_STANCE: it does not "act as shifted", so the else branch applies.
        Assert.Equal(SpellCastResult.OnlyShapeshift, charge.GetErrorAtShapeshiftedCast((uint)ShapeshiftForm.DefensiveStance, StanceFlag));
        Assert.Equal(SpellCastResult.OnlyShapeshift, charge.GetErrorAtShapeshiftedCast(0, null));
    }

    [Fact]
    public void ShapeshiftCast_StancesNot_FailsNotShapeshift_BeforeAnythingElse()
    {
        SpellInfo spell = Spell(stances: BattleMask, stancesNot: BattleMask);
        Assert.Equal(SpellCastResult.NotShapeshift, spell.GetErrorAtShapeshiftedCast((uint)ShapeshiftForm.BattleStance, StanceFlag));
    }

    [Fact]
    public void ShapeshiftCast_NoStanceRestriction_IsOkInEveryStanceAndOutOfForm()
    {
        SpellInfo heroicStrike = Spell(HeroicStrikeAttributes, id: 78);
        Assert.Equal(SpellCastResult.CastOk, heroicStrike.GetErrorAtShapeshiftedCast(0, null));
        Assert.Equal(SpellCastResult.CastOk, heroicStrike.GetErrorAtShapeshiftedCast((uint)ShapeshiftForm.BerserkerStance, StanceFlag));
    }

    [Fact]
    public void ShapeshiftCast_NonStanceForm_ActsAsShifted()
    {
        // Cat form (1) has no SHAPESHIFT_FLAG_STANCE: NOT_SHAPESHIFT spells fail, stance-bound spells need another form.
        SpellInfo notWhileShifted = Spell(attributes: 0x00010000);
        Assert.Equal(SpellCastResult.NotShapeshift, notWhileShifted.GetErrorAtShapeshiftedCast((uint)ShapeshiftForm.Cat, 0));

        SpellInfo battleOnly = Spell(stances: BattleMask);
        Assert.Equal(SpellCastResult.OnlyShapeshift, battleOnly.GetErrorAtShapeshiftedCast((uint)ShapeshiftForm.Cat, 0));

        SpellInfo catOnly = Spell(stances: 1u << 0);
        Assert.Equal(SpellCastResult.CastOk, catOnly.GetErrorAtShapeshiftedCast((uint)ShapeshiftForm.Cat, 0));

        // No restriction at all stays castable while shifted (only the two branches above fail).
        Assert.Equal(SpellCastResult.CastOk, Spell().GetErrorAtShapeshiftedCast((uint)ShapeshiftForm.Cat, 0));
    }

    [Fact]
    public void ShapeshiftCast_NotShapeshiftAttribute_IsIgnoredInAStance()
    {
        // In a stance (flags1 & STANCE) the actAsShifted branch is skipped, so NOT_SHAPESHIFT does not fire.
        SpellInfo notWhileShifted = Spell(attributes: 0x00010000);
        Assert.Equal(SpellCastResult.CastOk, notWhileShifted.GetErrorAtShapeshiftedCast((uint)ShapeshiftForm.BattleStance, StanceFlag));
    }

    [Fact]
    public void ShapeshiftCast_AllowWhileNotShapeshifted_SkipsTheOnlyShapeshiftError()
    {
        SpellInfo spell = Spell(stances: BattleMask, ex2: 0x00080000);
        Assert.Equal(SpellCastResult.CastOk, spell.GetErrorAtShapeshiftedCast(0, null));
        Assert.Equal(SpellCastResult.CastOk, spell.GetErrorAtShapeshiftedCast((uint)ShapeshiftForm.DefensiveStance, StanceFlag));

        // ...but it does not help a non-stance form that needs a different one.
        Assert.Equal(SpellCastResult.OnlyShapeshift, spell.GetErrorAtShapeshiftedCast((uint)ShapeshiftForm.Cat, 0));
    }

    [Fact]
    public void ShapeshiftCast_UnknownForm_IsOk()
    {
        // vmangos logs "unknown shapeshift" and returns SPELL_CAST_OK (SpellEntry.cpp:1051-1055).
        Assert.Equal(SpellCastResult.CastOk, Spell(stances: BattleMask).GetErrorAtShapeshiftedCast(0x15, null));
    }

    [Fact]
    public void ShapeshiftCast_TalentLearnSpell_IgnoresStances()
    {
        // vmangos SpellEntry.cpp:1034-1037: a talent that learns a spell is exempt from stance requirements.
        SpellInfo talent = Spell(stances: BattleMask);
        Assert.Equal(SpellCastResult.CastOk, talent.GetErrorAtShapeshiftedCast(0, null, isTalentLearnSpell: true));
    }

    [Fact]
    public void SpellInfo_DefaultsDescribeNoRestrictions()
    {
        var spell = new SpellInfo { Id = 1 };
        Assert.Equal(0u, spell.Stances);
        Assert.Equal(0u, spell.StancesNot);
        Assert.Equal(AuraState.None, spell.CasterAuraState);
        Assert.Equal(AuraState.None, spell.TargetAuraState);
        Assert.Equal(ProcFlags.None, spell.ProcFlags);
        Assert.Equal(-1, spell.EquippedItemClass);   // Spell.dbc: -1 = no equipped-item requirement
        Assert.Equal(0, spell.EquippedItemSubClassMask);
        Assert.Equal(0, spell.EquippedItemInventoryTypeMask);
    }
}
