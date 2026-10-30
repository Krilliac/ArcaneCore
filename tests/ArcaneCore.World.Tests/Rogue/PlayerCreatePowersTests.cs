using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Packets;
using Xunit;

namespace ArcaneCore.World.Tests.Rogue;

/// <summary>
/// Every player is created with a maximum of 1000 rage and 100 energy whatever the class: vmangos Player::InitStatsForLevel runs
/// SetMaxPower(i, GetCreatePowers(i)) for every power (Player.cpp:3367-3368) and Unit::GetCreatePowers returns 1000 and 100
/// (Unit.cpp:8253-8260); mangos-classic agrees for a player (Unit.cpp:9380-9382). UNIT_FIELD_MAXPOWER1..5 are public fields, so the
/// create block of every player carries both values. Found by the spell contract sweep (b.state-not-restored max-power).
/// </summary>
public sealed class PlayerCreatePowersTests
{
    private static Player Create(Class cls, PowerType power)
    {
        var character = new CharacterRecord
        {
            Id = 11, AccountId = 1, Name = "Powers", Race = (byte)Race.Human, Class = (byte)cls, Gender = (byte)Gender.Male, Level = 60, MapId = 0,
        };
        PlayerAppearance appearance = CharacterPackets.BuildAppearance(new RaceInfo(49, 1), new ClassInfo(60, 100, (byte)power));
        return new Player(character, appearance, new NullPlayerSession());
    }

    [Fact]
    public void Warrior_HasMaxEnergy100_AndRogue_HasMaxRage1000()
    {
        Player warrior = Create(Class.Warrior, PowerType.Rage);
        Player rogue = Create(Class.Rogue, PowerType.Energy);

        Assert.Equal(100u, MapCombat.GetMaxPower(warrior, PowerType.Energy));
        Assert.Equal(1000u, MapCombat.GetMaxPower(rogue, PowerType.Rage));
        Assert.Equal(1000u, MapCombat.GetMaxPower(warrior, PowerType.Rage));
        Assert.Equal(100u, MapCombat.GetMaxPower(rogue, PowerType.Energy));
    }

    [Fact]
    public void Mage_HasRageAndEnergyMaxima_AndKeepsItsManaPool_AndNoFocusOrHappiness()
    {
        Player mage = Create(Class.Mage, PowerType.Mana);

        Assert.Equal(1000u, mage.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Rage));
        Assert.Equal(100u, mage.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Energy));
        Assert.Equal(100u, MapCombat.GetMaxPower(mage, PowerType.Mana));
        Assert.Equal(0u, mage.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Focus));
        Assert.Equal(0u, mage.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Happiness));
        Assert.Equal(PowerType.Mana, mage.PowerType);
    }

    [Fact]
    public void TheMaximaDoNotAddCurrentValues_RageStartsEmptyAndTheOtherClassPowersStayZero()
    {
        Player warrior = Create(Class.Warrior, PowerType.Rage);

        Assert.Equal(0u, MapCombat.GetPower(warrior, PowerType.Rage));
        Assert.Equal(0u, MapCombat.GetPower(warrior, PowerType.Energy));
        Assert.Equal(0u, MapCombat.GetPower(warrior, PowerType.Mana));
    }
}
