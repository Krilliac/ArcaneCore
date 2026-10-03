using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Druid;

/// <summary>
/// Unit-level shapeshift interlocks, read from the form byte (UNIT_FIELD_BYTES_1 byte 2,
/// D:\refs\vmangos\src\game\Objects\UnitDefines.h:85-88).
/// <para>
/// Not covered (documented limits): the item-use rule (SpellHandler.cpp:112-127) and the weapon-skill-gain rule
/// (Player.cpp:5351) depend on SpellShapeshiftForm.dbc flags1 (IsShapeShifted), which the repository does not read
/// yet; the display half of IsInDisallowedMountForm (a non-native display that cannot mount) needs the
/// CreatureDisplayInfo DBCs.
/// </para>
/// </summary>
public static class FormInterlocks
{
    public static byte GetForm(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return unit.GetByte(UpdateFields.UnitFieldBytes1, 2);
    }

    /// <summary>The form-id half of Unit::IsInDisallowedMountForm (see <see cref="DruidForms.IsDisallowedMountForm"/>).</summary>
    public static bool IsInDisallowedMountForm(Unit unit) => DruidForms.IsDisallowedMountForm(GetForm(unit));

    /// <summary>A flight-master taxi request is answered ERR_TAXIPLAYERSHAPESHIFTED (Player.cpp:17872-17880).</summary>
    public static bool BlocksTaxi(Player player) => IsInDisallowedMountForm(player);
}
