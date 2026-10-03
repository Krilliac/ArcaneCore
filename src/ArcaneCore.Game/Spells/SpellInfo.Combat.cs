namespace ArcaneCore.Game.Spells;

/// <summary>
/// Combat helpers on <see cref="SpellInfo"/> (S02a spell data model). Each is a pure function of the
/// spell row, taken from vmangos at the 1.12.1 branch (SUPPORTED_CLIENT_BUILD, src/shared/Progression.h:36).
/// </summary>
public sealed partial record SpellInfo
{
    /// <summary>Tests a combat-relevant attribute bit (see SpellEnums.Combat.cs); the legacy overloads stay in SpellInfo.cs.</summary>
    public bool HasAttribute(SpellAttributesCombat flag) => ((uint)Attributes & (uint)flag) != 0;

    public bool HasAttribute(SpellAttributesExCombat flag) => ((uint)AttributesEx & (uint)flag) != 0;

    public bool HasAttribute(SpellAttributesEx2Combat flag) => ((uint)AttributesEx2 & (uint)flag) != 0;

    public bool HasAttribute(SpellAttributesEx3Combat flag) => (AttributesEx3 & (uint)flag) != 0;

    /// <summary>
    /// vmangos SpellEntry::IsNextMeleeSwingSpell (SpellEntry.h:887-890): Attributes has
    /// ON_NEXT_SWING_NO_DAMAGE (0x4, Heroic Strike / Cleave) or ON_NEXT_SWING (0x400). The legacy
    /// <see cref="SpellAttributes.OnNextSwing"/> bit alone misses the 0x4 spells.
    /// </summary>
    public bool IsNextMeleeSwing => ((uint)Attributes & (uint)(SpellAttributesCombat.OnNextSwingNoDamage | SpellAttributesCombat.OnNextSwing)) != 0;

    /// <summary>vmangos SpellEntry::NeedsComboPoints (SpellEntry.h:1082-1085): AttributesEx FINISHING_MOVE_DAMAGE or FINISHING_MOVE_DURATION.</summary>
    public bool NeedsComboPoints => ((uint)AttributesEx & (uint)(SpellAttributesExCombat.FinishingMoveDamage | SpellAttributesExCombat.FinishingMoveDuration)) != 0;

    /// <summary>
    /// vmangos SpellEntry::IsRemovedOnShapeLostSpell (SpellEntry.h:1180-1186): the aura is stance-bound
    /// (Stances set, or the hard-coded spell 24864) and neither ALLOW_WHILE_NOT_SHAPESHIFTED nor NOT_SHAPESHIFT.
    /// </summary>
    public bool IsRemovedOnShapeLost =>
        (Stances != 0 || Id == 24864)
        && !HasAttribute(SpellAttributesEx2Combat.AllowWhileNotShapeshifted)
        && !HasAttribute(SpellAttributesCombat.NotShapeshift);

    /// <summary>
    /// vmangos SpellEntry::GetErrorAtShapeshiftedCast (SpellEntry.cpp:1032-1074). <paramref name="form"/> is the
    /// caster's current <see cref="ShapeshiftForm"/> (0 = none). <paramref name="formFlags"/> is that form's
    /// SpellShapeshiftForm.dbc flags1 (<see cref="ShapeshiftFlags"/>), or null when the form has no row: vmangos
    /// then logs an error and returns SPELL_CAST_OK (SpellEntry.cpp:1051-1055). <paramref name="isTalentLearnSpell"/>
    /// is vmangos' <c>HasEffect(LEARN_SPELL) &amp;&amp; GetTalentSpellCost(Id) &gt; 0</c> exemption (lines 1034-1037);
    /// the talent tree is not loaded by this core yet, so the caller supplies it.
    /// </summary>
    public SpellCastResult GetErrorAtShapeshiftedCast(uint form, uint? formFlags, bool isTalentLearnSpell = false)
    {
        if (isTalentLearnSpell)
        {
            return SpellCastResult.CastOk;
        }

        uint stanceMask = form != 0 ? 1u << (int)(form - 1) : 0u;

        if ((stanceMask & StancesNot) != 0)
        {
            return SpellCastResult.NotShapeshift;
        }

        if ((stanceMask & Stances) != 0)
        {
            return SpellCastResult.CastOk;
        }

        bool actAsShifted = false;
        if (form > 0)
        {
            if (formFlags is null)
            {
                return SpellCastResult.CastOk;
            }

            actAsShifted = (formFlags.Value & (uint)ShapeshiftFlags.Stance) == 0;
        }

        if (actAsShifted)
        {
            if (HasAttribute(SpellAttributesCombat.NotShapeshift))
            {
                return SpellCastResult.NotShapeshift;
            }

            if (Stances != 0)
            {
                return SpellCastResult.OnlyShapeshift;
            }
        }
        else if (!HasAttribute(SpellAttributesEx2Combat.AllowWhileNotShapeshifted) && Stances != 0)
        {
            return SpellCastResult.OnlyShapeshift;
        }

        return SpellCastResult.CastOk;
    }
}
