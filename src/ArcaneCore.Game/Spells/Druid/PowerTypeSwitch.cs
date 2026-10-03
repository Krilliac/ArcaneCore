using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells.Druid;

/// <summary>
/// Port of vmangos Unit::SetPowerType (D:\refs\vmangos\src\game\Objects\Unit.cpp:4386-4427) and the
/// create-power maxima of Unit::GetCreatePowers (Unit.cpp:8245-8264). A druid is created with the mana
/// power type, so without this rage and energy have a maximum of 0 and every write clamps to 0
/// (<see cref="MapCombat.SetPower"/>). The group PowerType update flag of the vmangos function is
/// produced by the group member-state diff (GroupManager), so nothing is sent from here.
/// </summary>
public static class PowerTypeSwitch
{
    /// <summary>GetCreatePowers(POWER_RAGE): Unit.cpp:8255. Stored x10, like every rage value.</summary>
    public const uint MaxRage = 1000;

    /// <summary>GetCreatePowers(POWER_ENERGY): Unit.cpp:8260.</summary>
    public const uint MaxEnergy = 100;

    /// <summary>
    /// Unit::SetPowerType: write UNIT_FIELD_BYTES_0 byte 3, then for Rage and Energy set the maximum and
    /// zero the current value; Mana changes nothing else. Focus and Happiness are hunter-pet powers whose
    /// create value is 0 for a player (Unit.cpp:8257-8264), so they get max 0 and power 0.
    /// </summary>
    public static void SetPowerType(Unit unit, PowerType powerType)
    {
        unit.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)powerType);
        switch (powerType)
        {
            case PowerType.Rage:
                Reset(unit, PowerType.Rage, MaxRage);
                break;
            case PowerType.Energy:
                Reset(unit, PowerType.Energy, MaxEnergy);
                break;
            case PowerType.Focus:
            case PowerType.Happiness:
                Reset(unit, powerType, 0);
                break;
            default:
                break; // POWER_MANA: no change (Unit.cpp:4405-4407).
        }
    }

    /// <summary>
    /// Gives the unit the rage and energy maxima without touching the power type or any current value
    /// (what Player::UpdateAllStats gives every class in vmangos). Never lowers an existing maximum.
    /// Idempotent, so every lane that needs the caps may call it.
    /// </summary>
    public static void EnsureFeralPowerCaps(Unit unit)
    {
        RaiseMax(unit, PowerType.Rage, MaxRage);
        RaiseMax(unit, PowerType.Energy, MaxEnergy);
    }

    private static void Reset(Unit unit, PowerType power, uint max)
    {
        unit.SetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)power, max);
        MapCombat.SetPower(unit, power, 0);
    }

    private static void RaiseMax(Unit unit, PowerType power, uint max)
    {
        if (MapCombat.GetMaxPower(unit, power) < max)
        {
            unit.SetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)power, max);
        }
    }
}
