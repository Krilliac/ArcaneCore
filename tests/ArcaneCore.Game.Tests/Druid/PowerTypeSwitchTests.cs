using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Druid;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Druid;

/// <summary>
/// vmangos Unit::SetPowerType (Unit.cpp:4386-4427) and GetCreatePowers (Unit.cpp:8245-8264). A druid is
/// created with the mana power type, so rage and energy have no maximum until a form switches the type.
/// </summary>
public class PowerTypeSwitchTests
{
    private static Player CreateDruid(Class cls = Class.Druid)
    {
        var character = new CharacterRecord
        {
            Id = 7,
            AccountId = 1,
            Name = "Druid",
            Race = (byte)Race.NightElf,
            Class = (byte)cls,
            Gender = (byte)Gender.Female,
            Level = 60,
            MapId = 0,
            ZoneId = 12,
            Z = 83.5f,
        };
        var appearance = new PlayerAppearance(
            DisplayId: 2222, FactionTemplate: 4, PowerType.Mana, BaseHealth: 60, BaseMana: 100,
            MaxHealth: 1000, MaxPower: 500, StartPower: 500, NextLevelXp: 400);
        return new Player(character, appearance, new FakeSession(1));
    }

    [Fact]
    public void SetPowerType_Rage_WritesTypeByteSetsMaxThousandAndZeroesRageLeavingMana()
    {
        Player druid = CreateDruid();

        PowerTypeSwitch.SetPowerType(druid, PowerType.Rage);

        Assert.Equal(PowerType.Rage, druid.PowerType);
        Assert.Equal(PowerTypeSwitch.MaxRage, MapCombat.GetMaxPower(druid, PowerType.Rage));
        Assert.Equal(1000u, MapCombat.GetMaxPower(druid, PowerType.Rage));
        Assert.Equal(0u, MapCombat.GetPower(druid, PowerType.Rage));
        Assert.Equal(500u, MapCombat.GetPower(druid, PowerType.Mana));
        Assert.Equal(500u, MapCombat.GetMaxPower(druid, PowerType.Mana));
    }

    [Fact]
    public void SetPowerType_Energy_SetsMaxHundredAndZeroesEnergy()
    {
        Player druid = CreateDruid();
        MapCombat.SetPower(druid, PowerType.Mana, 321);

        PowerTypeSwitch.SetPowerType(druid, PowerType.Energy);

        Assert.Equal(PowerType.Energy, druid.PowerType);
        Assert.Equal(100u, MapCombat.GetMaxPower(druid, PowerType.Energy));
        Assert.Equal(0u, MapCombat.GetPower(druid, PowerType.Energy));
        Assert.Equal(321u, MapCombat.GetPower(druid, PowerType.Mana));
    }

    [Fact]
    public void SetPowerType_Mana_LeavesRageAndEnergyValuesAlone()
    {
        Player druid = CreateDruid();
        PowerTypeSwitch.EnsureFeralPowerCaps(druid);
        MapCombat.SetPower(druid, PowerType.Rage, 420);
        MapCombat.SetPower(druid, PowerType.Energy, 55);

        PowerTypeSwitch.SetPowerType(druid, PowerType.Mana);

        Assert.Equal(PowerType.Mana, druid.PowerType);
        Assert.Equal(420u, MapCombat.GetPower(druid, PowerType.Rage));
        Assert.Equal(55u, MapCombat.GetPower(druid, PowerType.Energy));
    }

    [Fact]
    public void SetPowerType_IsIdempotentForTheSameType()
    {
        Player druid = CreateDruid();
        PowerTypeSwitch.SetPowerType(druid, PowerType.Energy);

        PowerTypeSwitch.SetPowerType(druid, PowerType.Energy);

        Assert.Equal(PowerType.Energy, druid.PowerType);
        Assert.Equal(100u, MapCombat.GetMaxPower(druid, PowerType.Energy));
        Assert.Equal(0u, MapCombat.GetPower(druid, PowerType.Energy));
    }

    [Fact]
    public void EnsureFeralPowerCaps_SetsMaximaButNeverLowersOrTouchesCurrentValues()
    {
        Player druid = CreateDruid(Class.Mage);
        // A player is created with both maxima since the create-powers fix (vmangos Player.cpp:3367-3368); clear them to test the raise.
        druid.SetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Rage, 0);
        druid.SetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Energy, 0);
        Assert.Equal(0u, MapCombat.GetMaxPower(druid, PowerType.Rage));

        PowerTypeSwitch.EnsureFeralPowerCaps(druid);
        MapCombat.SetPower(druid, PowerType.Rage, 800);
        PowerTypeSwitch.EnsureFeralPowerCaps(druid);

        Assert.Equal(1000u, MapCombat.GetMaxPower(druid, PowerType.Rage));
        Assert.Equal(100u, MapCombat.GetMaxPower(druid, PowerType.Energy));
        Assert.Equal(800u, MapCombat.GetPower(druid, PowerType.Rage));
        Assert.Equal(PowerType.Mana, druid.PowerType);
    }

    [Fact]
    public void RewardRage_OnDruidAfterSwitchToRage_RaisesRageInsteadOfClampingToZero()
    {
        Player druid = CreateDruid();
        PowerTypeSwitch.SetPowerType(druid, PowerType.Rage);

        MapCombat.RewardRage(druid, 200, attacker: true);

        Assert.True(MapCombat.GetPower(druid, PowerType.Rage) > 0);
    }

    [Fact]
    public void RewardRage_OnDruidWithoutCaps_ClampsToZero_Baseline()
    {
        // Baseline for a unit without a rage maximum (no created player is one since the create-powers fix): with no rage maximum every write clamps to 0.
        Player druid = CreateDruid(Class.Mage);
        druid.SetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Rage, 0); // created players have the maximum; this is the no-maximum edge
        MapCombat.SetPower(druid, PowerType.Rage, 500);
        Assert.Equal(0u, MapCombat.GetPower(druid, PowerType.Rage));
    }

    [Fact]
    public void NewPlayer_AlreadyHasRageAndEnergyMaxima_WithoutAnyFormSwitch()
    {
        // Wired at player creation for every class (vmangos Player::InitStatsForLevel, Player.cpp:3367-3368).
        Player druid = CreateDruid();

        Assert.Equal(PowerTypeSwitch.MaxRage, MapCombat.GetMaxPower(druid, PowerType.Rage));
        Assert.Equal(PowerTypeSwitch.MaxEnergy, MapCombat.GetMaxPower(druid, PowerType.Energy));
        Assert.Equal(500u, MapCombat.GetMaxPower(druid, PowerType.Mana));
    }

    [Fact]
    public void NewPlayerOfAnyClass_HasRageAndEnergyMaxima_AndTheyStayAfterTheFormIsLeft()
    {
        // Unit::GetCreatePowers gives every player 1000 rage and 100 energy (Unit.cpp:8253-8260) and SetPowerType(MANA) never lowers them (Unit.cpp:4405-4407).
        Player mage = CreateDruid(Class.Mage);
        Assert.Equal(1000u, MapCombat.GetMaxPower(mage, PowerType.Rage));
        Assert.Equal(100u, MapCombat.GetMaxPower(mage, PowerType.Energy));

        Player druid = CreateDruid();
        PowerTypeSwitch.SetPowerType(druid, PowerType.Rage);
        PowerTypeSwitch.SetPowerType(druid, PowerType.Mana);
        Assert.Equal(1000u, MapCombat.GetMaxPower(druid, PowerType.Rage));
        Assert.Equal(100u, MapCombat.GetMaxPower(druid, PowerType.Energy));
    }
}
