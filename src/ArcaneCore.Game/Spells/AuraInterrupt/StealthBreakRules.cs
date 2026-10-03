namespace ArcaneCore.Game.Spells;

/// <summary>
/// Pure rules for which casts break stealth and invisibility (vmangos Spell.cpp:8301-8331
/// Spell::ShouldRemoveStealthAuras) and the implicit-target positivity the late cast interrupt uses
/// (vmangos SpellEntry.h:218-236 IsPositiveTarget).
/// </summary>
public static class StealthBreakRules
{
    /// <summary>vmangos SPELL_ATTR_EX_ALLOW_WHILE_STEALTHED (SpellDefines.h:875): does not break stealth.</summary>
    public const uint AttributesExAllowWhileStealthed = 0x00000020;

    /// <summary>vmangos SPELL_ATTR_EX2_ALLOW_WHILE_INVISIBLE (SpellDefines.h:920).</summary>
    public const uint AttributesEx2AllowWhileInvisible = 0x00004000;

    /// <summary>vmangos SPELL_ATTR_EX2_NOT_AN_ACTION (SpellDefines.h:934).</summary>
    public const uint AttributesEx2NotAnAction = 0x10000000;

    /// <summary>Spell.dbc Dispel type DISPEL_STEALTH (SpellDefines.h:724).</summary>
    public const uint DispelStealth = 5;

    /// <summary>Spell.dbc Dispel type DISPEL_INVISIBILITY (SpellDefines.h:725).</summary>
    public const uint DispelInvisibility = 6;

    /// <summary>Spell icons that never break stealth: Camouflage, Shadowmeld, Vanish (Spell.cpp:8311-8313).</summary>
    public const uint IconCamouflage = 250;

    public const uint IconShadowmeld = 103;

    public const uint IconVanish = 252;

    /// <summary>Sap's spell icon (Spell.cpp:8320).</summary>
    public const uint IconSap = 249;

    /// <summary>Improved Sap ranks 1-3 and the chance each keeps stealth after Sap (Spell.cpp:8322-8327).</summary>
    public static readonly (uint AuraId, int Chance)[] ImprovedSap = [(14076, 30), (14094, 60), (14095, 90)];

    /// <summary>
    /// vmangos Spell::ShouldRemoveStealthAuras. A triggered cast, a spell with ALLOW_WHILE_STEALTHED and the
    /// Camouflage/Shadowmeld/Vanish icons never remove stealth. A player's Sap rolls the Improved Sap chance
    /// of the highest-listed rank the caster has (rank 1 is checked first, as vmangos does).
    /// </summary>
    /// <param name="rollChance">vmangos roll_chance_u: true with the given percent chance.</param>
    public static bool ShouldRemoveStealthAuras(SpellInfo spell, bool triggered, bool casterIsPlayer, Func<uint, bool> casterHasAura, Func<int, bool> rollChance)
    {
        ArgumentNullException.ThrowIfNull(spell);
        ArgumentNullException.ThrowIfNull(casterHasAura);
        ArgumentNullException.ThrowIfNull(rollChance);
        if (triggered
            || ((uint)spell.AttributesEx & AttributesExAllowWhileStealthed) != 0
            || spell.SpellIconId is IconCamouflage or IconShadowmeld or IconVanish)
        {
            return false;
        }

        if (casterIsPlayer && spell.SpellIconId == IconSap)
        {
            foreach ((uint auraId, int chance) in ImprovedSap)
            {
                if (casterHasAura(auraId))
                {
                    return !rollChance(chance);
                }
            }
        }

        return true;
    }

    /// <summary>
    /// vmangos Spells::IsPositiveTarget (SpellEntry.h:218-236): enemy selectors are negative, a caster-source
    /// location is positive only with a friendly or party area as the second target, otherwise the second target
    /// decides and a spell without enemy selectors is positive.
    /// </summary>
    public static bool IsPositiveTarget(SpellImplicitTarget targetA, SpellImplicitTarget targetB)
    {
        // TARGET_ENUM_UNITS_ENEMY_AOE_AT_DYNOBJ_LOC = 28, TARGET_ENUM_UNITS_SCRIPT_AOE_AT_SRC_LOC = 7 (SpellDefines.h:62,83).
        const SpellImplicitTarget enemyAoeAtDynObj = (SpellImplicitTarget)28;
        const SpellImplicitTarget scriptAoeAtSrc = (SpellImplicitTarget)7;
        switch (targetA)
        {
            case SpellImplicitTarget.UnitEnemy:
            case SpellImplicitTarget.EnumUnitsEnemyAoeAtSrcLoc:
            case SpellImplicitTarget.EnumUnitsEnemyAoeAtDestLoc:
            case SpellImplicitTarget.EnumUnitsEnemyInCone24:
            case enemyAoeAtDynObj:
            case SpellImplicitTarget.LocationCasterTargetPosition:
                return false;
            case SpellImplicitTarget.LocationCasterSrc:
                return targetB is SpellImplicitTarget.EnumUnitsPartyAoeAtSrcLoc or SpellImplicitTarget.EnumUnitsFriendAoeAtSrcLoc or scriptAoeAtSrc;
        }

        return targetB == SpellImplicitTarget.None || IsPositiveTarget(targetB, SpellImplicitTarget.None);
    }
}
