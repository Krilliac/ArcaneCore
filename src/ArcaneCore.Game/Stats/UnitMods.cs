namespace ArcaneCore.Game.Stats;

/// <summary>
/// The four modifier slots of every <see cref="UnitMods"/> group: a value is computed as
/// <c>((BaseValue * BasePct) + TotalValue) * TotalPct</c> (vmangos UnitDefines.h:237-244
/// UnitModifierType; Unit.cpp:119-125 initialises BaseValue/TotalValue to 0 and the two
/// percentages to 1).
/// </summary>
public enum UnitModifierType
{
    BaseValue = 0,
    BasePct = 1,
    TotalValue = 2,
    TotalPct = 3,
}

/// <summary>
/// Modifier groups of a unit, in vmangos' order (UnitDefines.h:274-298 UnitMods). The five stat
/// groups, the power groups and the resistance groups are indexed by the Stats, Powers and
/// SpellSchools enums (UnitDefines.h:276, 282, 287), so their relative order must not change.
/// </summary>
public enum UnitMods
{
    StatStrength = 0,
    StatAgility = 1,
    StatStamina = 2,
    StatIntellect = 3,
    StatSpirit = 4,
    Health = 5,
    Mana = 6,
    Rage = 7,
    Focus = 8,
    Energy = 9,
    Happiness = 10,
    Armor = 11,
    ResistanceHoly = 12,
    ResistanceFire = 13,
    ResistanceNature = 14,
    ResistanceFrost = 15,
    ResistanceShadow = 16,
    ResistanceArcane = 17,
    DamageMainHand = 18,
    DamageOffHand = 19,
    DamageRanged = 20,
    DamagePhysical = 21,
    End = 22,
}

/// <summary>Shapeshift forms the player stat formulas distinguish (vmangos SharedDefines.h:1423-1431).</summary>
public enum ShapeshiftForm : byte
{
    None = 0,
    Cat = 1,
    Bear = 5,
    DireBear = 8,
}

/// <summary>Constants shared by the stat formulas.</summary>
public static class UnitModConstants
{
    /// <summary>Weapon damage of an unarmed or unusable weapon, minimum (vmangos UnitDefines.h:71 BASE_MINDAMAGE).</summary>
    public const float BaseMinDamage = 1.0f;

    /// <summary>Weapon damage of an unarmed or unusable weapon, maximum (vmangos UnitDefines.h:72 BASE_MAXDAMAGE).</summary>
    public const float BaseMaxDamage = 2.0f;

    /// <summary>
    /// The off-hand TOTAL_PCT starting value: off-hand attacks deal half damage until a talent
    /// raises it (vmangos Unit.cpp:126-127).
    /// </summary>
    public const float OffHandDamageTotalPct = 0.5f;

    /// <summary>The default value of a modifier slot (vmangos Unit.cpp:119-125).</summary>
    public static float Default(UnitModifierType type, UnitMods mod)
    {
        if (type == UnitModifierType.TotalPct && mod == UnitMods.DamageOffHand)
        {
            return OffHandDamageTotalPct;
        }

        return type is UnitModifierType.BasePct or UnitModifierType.TotalPct ? 1.0f : 0.0f;
    }
}
