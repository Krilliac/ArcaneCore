namespace ArcaneCore.Game;

/// <summary>
/// UpdateField indices for build 5875. Computed with the same symbolic arithmetic as
/// vmangos UpdateFields_1_12_1.h (OBJECT_END / UNIT_END offsets) so the absolute indices
/// match the client's field layout exactly — the inline hex comments in that header are
/// stale, the arithmetic is authoritative.
/// </summary>
public static class UpdateFields
{
    public const int ObjectEnd = 0x6;
    public const int UnitEnd = ObjectEnd + 0xB6;   // 188
    public const int PlayerEnd = UnitEnd + 0x446;  // 1282 — total player field count

    // Object
    public const int ObjectFieldGuid = 0x0;        // size 2
    public const int ObjectFieldType = 0x2;
    public const int ObjectFieldScaleX = 0x4;

    // Unit (relative to OBJECT_END)
    public const int UnitFieldHealth = ObjectEnd + 0x10;
    public const int UnitFieldPower1 = ObjectEnd + 0x11;
    public const int UnitFieldMaxHealth = ObjectEnd + 0x16;
    public const int UnitFieldMaxPower1 = ObjectEnd + 0x17;
    public const int UnitFieldLevel = ObjectEnd + 0x1C;
    public const int UnitFieldFactionTemplate = ObjectEnd + 0x1D;
    public const int UnitFieldBytes0 = ObjectEnd + 0x1E;
    public const int UnitFieldFlags = ObjectEnd + 0x28;
    public const int UnitFieldBaseAttackTime = ObjectEnd + 0x78; // size 2
    public const int UnitFieldBoundingRadius = ObjectEnd + 0x7B;
    public const int UnitFieldCombatReach = ObjectEnd + 0x7C;
    public const int UnitFieldDisplayId = ObjectEnd + 0x7D;
    public const int UnitFieldNativeDisplayId = ObjectEnd + 0x7E;
    public const int UnitFieldBytes1 = ObjectEnd + 0x84;
    public const int UnitModCastSpeed = ObjectEnd + 0x8B;
    public const int UnitFieldBaseMana = ObjectEnd + 0x9C;
    public const int UnitFieldBaseHealth = ObjectEnd + 0x9D;
    public const int UnitFieldBytes2 = ObjectEnd + 0x9E;

    // Player (relative to UNIT_END)
    public const int PlayerFlags = UnitEnd + 0x2;
    public const int PlayerBytes = UnitEnd + 0x5;
    public const int PlayerBytes2 = UnitEnd + 0x6;
    public const int PlayerBytes3 = UnitEnd + 0x7;
    public const int PlayerXp = UnitEnd + 0x210;
    public const int PlayerNextLevelXp = UnitEnd + 0x211;
    public const int PlayerFieldCoinage = UnitEnd + 0x3DC;
    public const int PlayerFieldBytes = UnitEnd + 0x40A;
}

/// <summary>UNIT_FIELD_FLAGS bits used at M3.</summary>
[Flags]
public enum UnitFlags : uint
{
    None = 0,
    /// <summary>Standard player flag set so the client does not treat the unit as attackable junk.</summary>
    PlayerControlled = 0x00000008, // UNIT_FLAG_PLAYER_CONTROLLED
}
