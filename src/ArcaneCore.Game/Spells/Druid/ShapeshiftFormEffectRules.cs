namespace ArcaneCore.Game.Spells.Druid;

/// <summary>
/// Which auras the Shapeshift Form Effect (spell 9033) removes. Source: D:\refs\vmangos\src\game\Spells\
/// SpellEffects.cpp:4442-4480 and Unit.cpp:543-556 (RemoveSpellsCausingAuraWithMechanic), with the keep mask
/// MECHANIC_NOT_REMOVED_BY_SHAPESHIFT from SpellDefines.h:710-714 and the Mechanics ids of SpellDefines.h:660-691.
/// Operates on the aura's spell data only (mechanic mask, icon, dispel); the caller walks the holders.
/// </summary>
public static class ShapeshiftFormEffectRules
{
    public const uint SpellId = 9033;

    public const int MechanicCharm = 1;
    public const int MechanicDisoriented = 2;
    public const int MechanicFear = 5;
    public const int MechanicRoot = 7;
    public const int MechanicPacify = 8;
    public const int MechanicSnare = 11;
    public const int MechanicStun = 12;
    public const int MechanicFreeze = 13;
    public const int MechanicPolymorph = 17;
    public const int MechanicBanish = 18;
    public const int MechanicShackle = 20;
    public const int MechanicTurn = 23;
    public const int MechanicHorror = 24;
    public const int MechanicDaze = 27;
    public const int MechanicSapped = 30;

    /// <summary>Daze and all crowd control except polymorph are not removed (SpellDefines.h:710-714).</summary>
    public const uint NotRemovedMechanics =
        (1u << (MechanicCharm - 1)) | (1u << (MechanicDisoriented - 1)) | (1u << (MechanicFear - 1))
        | (1u << (MechanicPacify - 1)) | (1u << (MechanicStun - 1)) | (1u << (MechanicFreeze - 1))
        | (1u << (MechanicBanish - 1)) | (1u << (MechanicShackle - 1)) | (1u << (MechanicHorror - 1))
        | (1u << (MechanicTurn - 1)) | (1u << (MechanicDaze - 1)) | (1u << (MechanicSapped - 1));

    /// <summary>Mechanic bit for a Mechanics id (1 &lt;&lt; (id - 1)).</summary>
    public static uint Bit(int mechanic) => 1u << (mechanic - 1);

    /// <summary>A ModRoot holder is removed only when the spell carries some mechanic (mask 0 survives).</summary>
    public static bool RemovesRoot(uint allMechanicMask) => allMechanicMask != 0;

    /// <summary>
    /// A ModDecreaseSpeed holder is removed unless its mask is 0, it carries a kept crowd-control/daze mechanic,
    /// or it is a daze-like spell (icon 15, no dispel type) that does not also carry the snare mechanic.
    /// </summary>
    public static bool RemovesSnare(uint allMechanicMask, uint spellIconId, uint dispel)
    {
        if (allMechanicMask == 0)
        {
            return false;
        }

        if ((allMechanicMask & NotRemovedMechanics) != 0)
        {
            return false;
        }

        return !(spellIconId == 15 && dispel == 0 && (allMechanicMask & Bit(MechanicSnare)) == 0);
    }
}
