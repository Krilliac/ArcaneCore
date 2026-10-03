using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Druid;
using Xunit;

namespace ArcaneCore.Game.Tests.Druid;

/// <summary>
/// Shapeshift interlocks read from UNIT_FIELD_BYTES_1 byte 2 (D:\refs\vmangos\src\game\Objects\UnitDefines.h:85-88):
/// Unit::IsInDisallowedMountForm (Unit.cpp:5870-5878) and the taxi reply of Player::ActivateTaxiPathTo
/// (Player.cpp:17872-17880, ERR_TAXIPLAYERSHAPESHIFTED).
/// </summary>
public class FormInterlocksTests
{
    private static Player PlayerInForm(byte form)
    {
        Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
        player.SetByte(UpdateFields.UnitFieldBytes1, 2, form);
        return player;
    }

    [Theory]
    [InlineData(DruidForms.None, false)]
    [InlineData(DruidForms.BattleStance, false)]
    [InlineData(DruidForms.BerserkerStance, false)]
    [InlineData(DruidForms.DefensiveStance, false)]
    [InlineData(DruidForms.Shadow, false)]
    [InlineData(DruidForms.Stealth, false)]
    [InlineData(DruidForms.Cat, true)]
    [InlineData(DruidForms.Tree, true)]
    [InlineData(DruidForms.Travel, true)]
    [InlineData(DruidForms.Aquatic, true)]
    [InlineData(DruidForms.Bear, true)]
    [InlineData(DruidForms.DireBear, true)]
    [InlineData(DruidForms.Moonkin, true)]
    public void IsInDisallowedMountForm_ReadsTheFormByteOfTheUnit(byte form, bool expected)
    {
        Player player = PlayerInForm(form);

        Assert.Equal(form, FormInterlocks.GetForm(player));
        Assert.Equal(expected, FormInterlocks.IsInDisallowedMountForm(player));
        Assert.Equal(expected, FormInterlocks.BlocksTaxi(player));
    }
}
