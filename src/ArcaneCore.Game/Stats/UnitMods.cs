using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;

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

    /// <summary>
    /// Melee attack power (mangos UNIT_MOD_ATTACK_POWER, Unit.h:416). Appended after the vmangos order so the existing values
    /// keep their numbers; only the percent slots are used (the multiplier field is TotalPct - 1, StatSystem.cpp:786-790).
    /// </summary>
    AttackPower = 22,

    /// <summary>Ranged attack power (mangos UNIT_MOD_ATTACK_POWER_RANGED).</summary>
    AttackPowerRanged = 23,
    End = 24,
}

/// <summary>
/// One multiplicative percent slot (vmangos Unit::HandleStatModifier BASE_PCT / TOTAL_PCT, Unit.cpp:119-125 and
/// UnitStatModifier.cpp:73-100): the product of <c>(100 + amount) / 100</c> of every active modifier, where an amount of
/// -100 or less is stored as -200 (the reference's "small hack-fix for -100% modifiers"). The count of active modifiers
/// is kept so the product is reset to exactly 1 when the last one is removed instead of keeping float drift. The default
/// struct is neutral (1).
/// </summary>
public struct PercentFactor
{
    private float _value;
    private int _count;

    /// <summary>The product of the active modifiers; 1 when none is active.</summary>
    public readonly float Value => _count == 0 ? 1.0f : _value;

    /// <summary>The number of modifiers currently applied.</summary>
    public readonly int Count => _count;

    /// <summary>The multiplier one modifier of <paramref name="amount"/> percent contributes.</summary>
    public static float Multiplier(float amount) => (100.0f + (amount <= -100.0f ? -200.0f : amount)) / 100.0f;

    /// <summary>Multiply in (<paramref name="apply"/>) or divide out an amount; a removal with nothing applied is ignored.</summary>
    public void Apply(float amount, bool apply)
    {
        float multiplier = Multiplier(amount);
        if (apply)
        {
            _value = _count == 0 ? multiplier : _value * multiplier;
            _count++;
            return;
        }

        if (_count == 0)
        {
            return;
        }

        _count--;
        _value = _count == 0 ? 1.0f : _value * (1.0f / multiplier);
    }
}

/// <summary>
/// The percent slots of the <see cref="UnitMods"/> groups of one unit (<see cref="UnitModifierType.BasePct"/> and
/// <see cref="UnitModifierType.TotalPct"/>) and, per group, the amount those percentages currently add to the field that holds the
/// group's value (<see cref="Applied"/>).
/// <para>
/// The repo keeps no BASE_VALUE / TOTAL_VALUE slots: items, level-ups and flat auras write the update fields as deltas (see
/// <c>StatAuras</c>), so a field holds the flat sum. The percent auras compose on top of that sum: the field is
/// <c>pre-percent value + Applied</c>, and every refresh recomputes <c>Applied</c> from the current pre-percent value
/// (<c>field - Applied</c>) and the two factors, which gives vmangos' order, flat first and then percent
/// (<c>((base * BasePct) + total) * TotalPct</c>, UnitStatModifier.cpp:117-125). Owned by the world thread; a player's ledger
/// lives on <see cref="PlayerStatState.Mods"/>, any other unit's is created on its first percent aura.
/// </para>
/// </summary>
public sealed class UnitModLedger
{
    private static readonly ConditionalWeakTable<Unit, UnitModLedger> s_others = new();

    private readonly PercentFactor[] _basePct = new PercentFactor[(int)UnitMods.End];
    private readonly PercentFactor[] _totalPct = new PercentFactor[(int)UnitMods.End];
    private readonly int[] _applied = new int[(int)UnitMods.End];

    /// <summary>The ledger of <paramref name="unit"/>, created when it has none (aura apply only, never a per-tick path).</summary>
    public static UnitModLedger For(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return unit is Player player ? player.StatState.Mods : s_others.GetValue(unit, static _ => new UnitModLedger());
    }

    /// <summary>The ledger of <paramref name="unit"/>, or null when no percent aura ever touched it.</summary>
    public static UnitModLedger? Find(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return unit is Player player ? player.StatState.Mods : s_others.TryGetValue(unit, out UnitModLedger? ledger) ? ledger : null;
    }

    /// <summary>BASE_PCT of a group (1 when none is active).</summary>
    public float BasePct(UnitMods group) => _basePct[(int)group].Value;

    /// <summary>TOTAL_PCT of a group: the product of the active modifiers times the group's default (<see cref="UnitModConstants.Default"/>).</summary>
    public float TotalPct(UnitMods group) => UnitModConstants.Default(UnitModifierType.TotalPct, group) * _totalPct[(int)group].Value;

    /// <summary>True when no percent modifier is active on the group and none of its field is left over from one.</summary>
    public bool IsNeutral(UnitMods group) => _basePct[(int)group].Count == 0 && _totalPct[(int)group].Count == 0 && _applied[(int)group] == 0;

    /// <summary>The amount the percentages of a group currently add to its field.</summary>
    public int Applied(UnitMods group) => _applied[(int)group];

    internal void SetApplied(UnitMods group, int amount) => _applied[(int)group] = amount;

    /// <summary>
    /// vmangos Unit::HandleStatModifier for the two percent slots: multiply in or divide out a percent. The flat slots do not exist
    /// here (see the type comment), so any other <paramref name="type"/> is refused.
    /// </summary>
    public void Apply(UnitMods group, UnitModifierType type, float amount, bool apply)
    {
        if (group >= UnitMods.End)
        {
            throw new ArgumentOutOfRangeException(nameof(group));
        }

        switch (type)
        {
            case UnitModifierType.BasePct:
                _basePct[(int)group].Apply(amount, apply);
                break;
            case UnitModifierType.TotalPct:
                _totalPct[(int)group].Apply(amount, apply);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, "only the percent slots are kept; flat contributions live in the update fields");
        }
    }
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
