using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// Form predicates of a unit that depend on the SpellShapeshiftForm.dbc row of its form (the form is UNIT_FIELD_BYTES_1
/// byte 2). Every query takes the <see cref="ShapeshiftFormCatalog"/> to read (the daemon's configured one, else
/// <see cref="ShapeshiftFormCatalog.Retail"/>).
/// </summary>
public static class FormQueries
{
    /// <summary>The form of a unit (vmangos Unit::GetShapeshiftForm, UNIT_FIELD_BYTES_1 byte 2).</summary>
    public static byte GetForm(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return unit.GetByte(UpdateFields.UnitFieldBytes1, 2);
    }

    /// <summary>
    /// vmangos Unit::IsShapeShifted (Unit.cpp:5843-5852, "mirroring clientside gameplay logic"): a form whose DBC row
    /// lacks the Stance flag. False for no form, for a form without a row, and for the stances, Shadowform, Stealth and Moonkin.
    /// </summary>
    public static bool IsShapeShifted(byte form, ShapeshiftFormCatalog? catalog)
        => form != 0 && (catalog ?? ShapeshiftFormCatalog.Retail).TryGet(form, out ShapeshiftFormInfo info) && (info.Flags1 & (uint)ShapeshiftFlags.Stance) == 0;

    /// <summary><see cref="IsShapeShifted(byte, ShapeshiftFormCatalog?)"/> for a unit's current form.</summary>
    public static bool IsShapeShifted(Unit unit, ShapeshiftFormCatalog? catalog) => IsShapeShifted(GetForm(unit), catalog);

    /// <summary>
    /// vmangos IsAttackSpeedOverridenForm (SharedDefines.h:1456-1466): Cat, Bear and Dire Bear fight with their own
    /// attack time and no weapon. Hard-coded in vmangos, no DBC row involved.
    /// </summary>
    public static bool IsAttackSpeedOverridden(byte form)
        => form is (byte)ShapeshiftForm.Cat or (byte)ShapeshiftForm.Bear or (byte)ShapeshiftForm.DireBear;

    /// <summary>
    /// The creature type id a form imposes on its player (vmangos Unit::GetCreatureType, Unit.cpp:7722-7735): the DBC
    /// creatureType when above 0, else 0 (the caller falls back to the race).
    /// </summary>
    public static uint FormCreatureType(byte form, ShapeshiftFormCatalog? catalog)
        => form != 0 && (catalog ?? ShapeshiftFormCatalog.Retail).TryGet(form, out ShapeshiftFormInfo info) && info.CreatureType > 0
            ? (uint)info.CreatureType
            : 0;
}
