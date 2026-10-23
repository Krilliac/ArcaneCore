using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Ranged;
using ArcaneCore.Game.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// Characterisation of the public <see cref="SpellSystem.GetPower"/> / <see cref="SpellSystem.SetPower"/> contract (null guard, clamp to the
/// maximum, per-type field offset) and of <see cref="RangedSpellFacts.IsWandUser"/>, which must survive delegating to MapCombat and
/// de-duplicating the wand-user class lists.
/// </summary>
public sealed class SpellSystemPowerAccessorTests
{
    [Fact]
    public void GetPower_NullUnit_ThrowsArgumentNull()
        => Assert.Throws<ArgumentNullException>(() => SpellSystem.GetPower(null!, PowerType.Mana));

    [Fact]
    public void SetPower_NullUnit_ThrowsArgumentNull()
        => Assert.Throws<ArgumentNullException>(() => SpellSystem.SetPower(null!, PowerType.Mana, 1));

    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(1u, 1u)]
    [InlineData(99u, 99u)]
    [InlineData(100u, 100u)]
    [InlineData(101u, 100u)]
    [InlineData(uint.MaxValue, 100u)]
    public void SetPower_ClampsToMaximum(uint value, uint expected)
    {
        (WorldRuntime world, _, _, _) = CombatTestKit.CreateWorld();
        using WorldRuntime w = world;
        Player player = CombatTestKit.AddPlayer(world, 1, 0, 0, new FakeSession(1), level: 60);
        player.SetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Rage, 100);

        SpellSystem.SetPower(player, PowerType.Rage, value);

        Assert.Equal(expected, SpellSystem.GetPower(player, PowerType.Rage));
        Assert.Equal(expected, player.GetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Rage));
    }

    [Fact]
    public void SetPower_ZeroMaximum_StoresZero()
    {
        (WorldRuntime world, _, _, _) = CombatTestKit.CreateWorld();
        using WorldRuntime w = world;
        Player player = CombatTestKit.AddPlayer(world, 1, 0, 0, new FakeSession(1), level: 60);
        player.SetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Energy, 0);

        SpellSystem.SetPower(player, PowerType.Energy, 50);

        Assert.Equal(0u, SpellSystem.GetPower(player, PowerType.Energy));
    }

    [Fact]
    public void SetPower_WritesOnlyTheRequestedPowerField()
    {
        (WorldRuntime world, _, _, _) = CombatTestKit.CreateWorld();
        using WorldRuntime w = world;
        Player player = CombatTestKit.AddPlayer(world, 1, 0, 0, new FakeSession(1), level: 60);
        player.SetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana, 500);
        player.SetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Rage, 500);
        SpellSystem.SetPower(player, PowerType.Rage, 7);

        SpellSystem.SetPower(player, PowerType.Mana, 300);

        Assert.Equal(300u, SpellSystem.GetPower(player, PowerType.Mana));
        Assert.Equal(7u, SpellSystem.GetPower(player, PowerType.Rage));
    }

    [Theory]
    [InlineData(Class.Priest, true)]
    [InlineData(Class.Mage, true)]
    [InlineData(Class.Warlock, true)]
    [InlineData(Class.Warrior, false)]
    [InlineData(Class.Paladin, false)]
    [InlineData(Class.Hunter, false)]
    [InlineData(Class.Rogue, false)]
    [InlineData(Class.Shaman, false)]
    [InlineData(Class.Druid, false)]
    [InlineData((Class)0, false)]
    [InlineData((Class)6, false)]
    [InlineData((Class)255, false)]
    public void IsWandUser_IsExactlyPriestMageWarlock(Class playerClass, bool expected)
        => Assert.Equal(expected, RangedSpellFacts.IsWandUser(playerClass));
}
