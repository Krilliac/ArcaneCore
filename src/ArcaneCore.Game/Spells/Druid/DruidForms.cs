namespace ArcaneCore.Game.Spells.Druid;

/// <summary>
/// Shapeshift form ids and the pure predicates that depend only on the form id.
/// Values: D:\refs\vmangos\src\game\SharedDefines.h:1421-1442 (ShapeshiftForm); the form id lives in
/// UNIT_FIELD_BYTES_1 byte 2 (Objects\UnitDefines.h:85-88). A "stance mask" is 1 &lt;&lt; (form - 1),
/// the bit that Spell.dbc Stances / StancesNot test (SpellEntry.cpp:1032-1074).
/// Predicates that depend on SpellShapeshiftForm.dbc flags1 (IsShapeShifted, IsNoWeaponShapeShift) are NOT
/// here: that DBC is user supplied and not part of the references.
/// </summary>
public static class DruidForms
{
    public const byte None = 0x00;
    public const byte Cat = 0x01;
    public const byte Tree = 0x02;
    public const byte Travel = 0x03;
    public const byte Aquatic = 0x04;
    public const byte Bear = 0x05;
    public const byte DireBear = 0x08;
    public const byte BattleStance = 0x11;
    public const byte DefensiveStance = 0x12;
    public const byte BerserkerStance = 0x13;
    public const byte Shadow = 0x1C;
    public const byte Stealth = 0x1E;

    /// <summary>FORM_MOONKIN. Present in 1.12.1 data (spells 24858/24905/24907) and in both reference cores.</summary>
    public const byte Moonkin = 0x1F;

    /// <summary>The bit Spell.dbc Stances/StancesNot test for <paramref name="form"/> (0 for form 0).</summary>
    public static uint StanceMask(byte form) => form is 0 or > 32 ? 0u : 1u << (form - 1);

    /// <summary>IsTankingForm (SharedDefines.h:1444-1454).</summary>
    public static bool IsTankingForm(byte form) => form is Bear or DireBear or DefensiveStance;

    /// <summary>IsAttackSpeedOverridenForm (SharedDefines.h:1456-1466): cat, bear and dire bear.</summary>
    public static bool IsAttackSpeedOverridden(byte form) => form is Cat or Bear or DireBear;

    /// <summary>
    /// The form-id half of Unit::IsInDisallowedMountForm (Unit.cpp:5870-5878): every form except none, the
    /// three warrior stances, shadow and stealth blocks mounting and taxis. The second half of the vmangos
    /// function (a non-native display whose model cannot mount) needs CreatureDisplayInfo DBCs and is a
    /// documented limit.
    /// </summary>
    public static bool IsDisallowedMountForm(byte form) =>
        form != None && form != BattleStance && form != BerserkerStance && form != DefensiveStance
        && form != Shadow && form != Stealth;
}
