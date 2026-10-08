using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Kernel.WorldData;
using Xunit;

namespace ArcaneCore.Game.Tests.Druid;

/// <summary>
/// The build-5875 SpellShapeshiftForm.dbc rows shipped as <see cref="ShapeshiftFormCatalog.Retail"/> (read from the
/// developer's own client, the patch.MPQ copy the client resolves over dbc.MPQ, 32 rows) and the flag-based form predicates of vmangos Unit::IsShapeShifted
/// (Unit.cpp:5843-5852) and Unit::GetCreatureType (Unit.cpp:7722-7735).
/// </summary>
public sealed class FormCatalogTests
{
    /// <summary>
    /// The client rows (form id, flags1, creatureType) of the SpellShapeshiftForm.dbc the 1.12.1 client uses, the patch.MPQ copy.
    /// Forms not listed here have flags1 0 and creatureType 0.
    /// </summary>
    public static TheoryData<uint, uint, int> ClientRows => new()
    {
        { 1, 0x70, 1 },  // Cat: DontUseWeapon | AgilityAttackBonus | CanUseEquippedItems
        { 2, 0x10, -1 }, // Tree of Life: DontUseWeapon
        { 3, 0x50, 1 },  // Travel: DontUseWeapon | CanUseEquippedItems
        { 4, 0x50, 1 },  // Aquatic
        { 5, 0x50, 1 },  // Bear
        { 8, 0x50, 1 },  // Dire Bear
        { 14, 0, 1 },    // Creature - Bear
        { 15, 0, 1 },    // Creature - Cat
        { 16, 0x40, 1 }, // Ghost Wolf: CanUseEquippedItems
        { 17, 7, -1 },   // Battle Stance: Stance | NotToggleable | PersistOnDeath
        { 18, 7, 0 },
        { 19, 7, 0 },
        { 28, 9, -1 },   // Shadowform: Stance | CanInteractNpc
        { 30, 1, 0 },    // Stealth: Stance
        { 31, 0x41, -1 }, // Moonkin: Stance | CanUseEquippedItems
        { 32, 0, -1 },   // Spirit of Redemption: no Stance
    };

    [Theory]
    [MemberData(nameof(ClientRows))]
    public void Retail_HasTheClientFlagsForEveryVanillaForm(uint form, uint flags1, int creatureType)
    {
        Assert.True(ShapeshiftFormCatalog.Retail.TryGet(form, out ShapeshiftFormInfo? info));
        Assert.Equal(new ShapeshiftFormInfo(form, flags1, creatureType), info);
    }

    [Fact]
    public void Retail_HasAll32ClientRows()
        => Assert.Equal(32, ShapeshiftFormCatalog.Retail.Count);

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    [InlineData(8, true)]
    [InlineData(16, true)]
    [InlineData(32, true)]    // Spirit of Redemption: flags1 0, not a Stance
    [InlineData(28, false)]   // Shadowform: flags1 0x9 carries the Stance bit
    [InlineData(17, false)]
    [InlineData(18, false)]
    [InlineData(19, false)]
    [InlineData(30, false)]
    [InlineData(31, false)]   // Moonkin: "moonkin form not counted as shapeshift" (vmangos Unit.h:528)
    [InlineData(99, false)]   // no row: vmangos IsShapeShifted is false
    public void IsShapeShifted_IsTrueForCatTreeTravelAquaBearDireBearGhostWolfSpirit_FalseForStancesShadowformStealthMoonkinNone(byte form, bool expected)
        => Assert.Equal(expected, FormQueries.IsShapeShifted(form, ShapeshiftFormCatalog.Retail));

    [Fact]
    public void IsShapeShifted_ReadsTheUnitsFormByte()
    {
        Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
        Assert.False(FormQueries.IsShapeShifted(player, ShapeshiftFormCatalog.Retail));
        player.SetByte(UpdateFields.UnitFieldBytes1, 2, 1);
        Assert.True(FormQueries.IsShapeShifted(player, ShapeshiftFormCatalog.Retail));
        player.SetByte(UpdateFields.UnitFieldBytes1, 2, 30);
        Assert.False(FormQueries.IsShapeShifted(player, ShapeshiftFormCatalog.Retail));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(8, true)]
    [InlineData(0, false)]
    [InlineData(3, false)]
    [InlineData(16, false)]
    [InlineData(17, false)]
    public void IsAttackSpeedOverridden_IsCatBearDireBear(byte form, bool expected)   // vmangos SharedDefines.h:1456-1466
        => Assert.Equal(expected, FormQueries.IsAttackSpeedOverridden(form));

    [Fact]
    public void CreatureTypeMask_OfADruidInCat_IsBeast_InShadowformHumanoid()
    {
        Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
        Assert.Equal(1u << 6, player.CreatureTypeMask());          // humanoid: every race counts as 7 in ChrRaces.dbc
        player.SetByte(UpdateFields.UnitFieldBytes1, 2, 1);
        Assert.Equal(1u, player.CreatureTypeMask());               // beast (creatureType 1)
        player.SetByte(UpdateFields.UnitFieldBytes1, 2, 28);
        Assert.Equal(1u << 6, player.CreatureTypeMask());          // creatureType -1 is not > 0: the race value
        player.SetByte(UpdateFields.UnitFieldBytes1, 2, 2);
        Assert.Equal(1u << 6, player.CreatureTypeMask());          // Tree of Life: -1, the race value
        player.SetByte(UpdateFields.UnitFieldBytes1, 2, 15);
        Assert.Equal(1u, player.CreatureTypeMask());               // Creature - Cat: beast
    }

    [Fact]
    public void CombatOptions_RequireShapeshiftFormDbc_DefaultsFalse()
        => Assert.False(new CombatOptions().RequireShapeshiftFormDbc);

    [Fact]
    public void CombatEnvironment_ShapeshiftFormsLink_DefaultsToNoneAndIsSettable()
    {
        var environment = new CombatEnvironment(new CombatOptions());
        Assert.Null(environment.ShapeshiftForms);
        environment.ShapeshiftForms = ShapeshiftFormCatalog.Retail;
        Assert.Same(ShapeshiftFormCatalog.Retail, environment.ShapeshiftForms);
        Assert.Throws<InvalidOperationException>(() => CombatEnvironment.Default.ShapeshiftForms = ShapeshiftFormCatalog.Retail);
    }
}
