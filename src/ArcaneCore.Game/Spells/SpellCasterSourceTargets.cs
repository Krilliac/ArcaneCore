namespace ArcaneCore.Game.Spells;

/// <summary>
/// vmangos IsCasterSourceTarget (SpellEntry.h:304-327) and SpellMgr IsSpellWithCasterSourceTargetsOnly (SpellMgr.cpp:3244-3260):
/// a spell whose every effect picks its targets around the caster. A trigger-spell effect of such a spell is cast by the
/// effect's target (the spell then centres on that unit) instead of by the caster (SpellEffects.cpp:1571).
/// </summary>
public static class SpellCasterSourceTargets
{
    // Values with no named member in SpellImplicitTarget: TARGET_UNIT_CASTER_MASTER (27) and the four caster-corner locations
    // TARGET_LOCATION_CASTER_FRONT_RIGHT/BACK_RIGHT/BACK_LEFT/FRONT_LEFT (41..44), vmangos SpellDefines.h:82,96-99.
    private const uint UnitCasterMaster = 27;
    private const uint CasterFrontRight = 41;
    private const uint CasterBackRight = 42;
    private const uint CasterBackLeft = 43;
    private const uint CasterFrontLeft = 44;

    /// <summary>vmangos IsCasterSourceTarget.</summary>
    public static bool IsCasterSourceTarget(SpellImplicitTarget target) => target switch
    {
        SpellImplicitTarget.UnitCaster
            or SpellImplicitTarget.UnitCasterPet
            or SpellImplicitTarget.EnumUnitsPartyWithinCasterRange
            or SpellImplicitTarget.EnumUnitsEnemyInCone24
            or SpellImplicitTarget.LocationUnitMinionPosition
            or SpellImplicitTarget.EnumUnitsPartyAoeAtSrcLoc
            or SpellImplicitTarget.EnumUnitsPartyAoeAtDestLoc
            or SpellImplicitTarget.LocationCasterFishingSpot
            or SpellImplicitTarget.EnumGameObjectsScriptAoeAtDestLoc => true,
        _ => (uint)target is UnitCasterMaster or CasterFrontRight or CasterBackRight or CasterBackLeft or CasterFrontLeft,
    };

    /// <summary>vmangos IsSpellWithCasterSourceTargetsOnly: every effect slot names a target, and every named target is caster-sourced.</summary>
    public static bool IsCasterSourceTargetsOnly(SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        foreach (SpellEffectInfo effect in spell.Effects)
        {
            if (effect.TargetA != SpellImplicitTarget.None && !IsCasterSourceTarget(effect.TargetA))
            {
                return false;
            }

            if (effect.TargetB != SpellImplicitTarget.None && !IsCasterSourceTarget(effect.TargetB))
            {
                return false;
            }

            if (effect.TargetA == SpellImplicitTarget.None && effect.TargetB == SpellImplicitTarget.None)
            {
                return false;
            }
        }

        return true;
    }
}
