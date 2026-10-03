using ArcaneCore.Game.Spells.Druid;
using Xunit;

namespace ArcaneCore.Game.Tests.Druid;

/// <summary>vmangos SpellEffects.cpp:4442-4480 (Shapeshift Form Effect 9033), SpellDefines.h:710-714, Unit.cpp:543-556.</summary>
public class ShapeshiftFormEffectRulesTests
{
    private static uint M(int mechanic) => ShapeshiftFormEffectRules.Bit(mechanic);

    [Fact]
    public void Root_WithAnyMechanicIsRemoved_WithMaskZeroSurvives()
    {
        Assert.True(ShapeshiftFormEffectRules.RemovesRoot(M(ShapeshiftFormEffectRules.MechanicRoot)));
        Assert.True(ShapeshiftFormEffectRules.RemovesRoot(M(ShapeshiftFormEffectRules.MechanicStun)));
        Assert.False(ShapeshiftFormEffectRules.RemovesRoot(0));
    }

    [Fact]
    public void Snare_IsRemoved()
    {
        Assert.True(ShapeshiftFormEffectRules.RemovesSnare(M(ShapeshiftFormEffectRules.MechanicSnare), 0, 0));
    }

    [Theory]
    [InlineData(ShapeshiftFormEffectRules.MechanicStun)]
    [InlineData(ShapeshiftFormEffectRules.MechanicFear)]
    [InlineData(ShapeshiftFormEffectRules.MechanicDaze)]
    [InlineData(ShapeshiftFormEffectRules.MechanicCharm)]
    [InlineData(ShapeshiftFormEffectRules.MechanicSapped)]
    [InlineData(ShapeshiftFormEffectRules.MechanicFreeze)]
    [InlineData(ShapeshiftFormEffectRules.MechanicHorror)]
    public void CrowdControlAndDazeSnaresSurvive(int mechanic)
    {
        // A snare that also carries a crowd-control mechanic is kept.
        uint mask = M(ShapeshiftFormEffectRules.MechanicSnare) | M(mechanic);

        Assert.False(ShapeshiftFormEffectRules.RemovesSnare(mask, 0, 0));
    }

    [Fact]
    public void Polymorph_IsNotInTheKeepMask()
    {
        Assert.Equal(0u, ShapeshiftFormEffectRules.NotRemovedMechanics & M(ShapeshiftFormEffectRules.MechanicPolymorph));
        Assert.True(ShapeshiftFormEffectRules.RemovesSnare(M(ShapeshiftFormEffectRules.MechanicPolymorph), 0, 0));
    }

    [Fact]
    public void DazeLikeIcon15WithoutDispelSurvivesUnlessItAlsoCarriesTheSnareMechanic()
    {
        uint other = M(9); // silence
        Assert.False(ShapeshiftFormEffectRules.RemovesSnare(other, 15, 0));
        Assert.True(ShapeshiftFormEffectRules.RemovesSnare(other | M(ShapeshiftFormEffectRules.MechanicSnare), 15, 0));
        Assert.True(ShapeshiftFormEffectRules.RemovesSnare(other, 15, 1));
    }

    [Fact]
    public void SnareWithNoMechanicMask_IsKept()
    {
        // vmangos comment: spells 33572 and 38132 are confirmed not removed on polymorph.
        Assert.False(ShapeshiftFormEffectRules.RemovesSnare(0, 0, 0));
    }

    [Fact]
    public void KeepMaskMatchesTheDefine()
    {
        // (1<<(1-1))|(1<<(2-1))|(1<<(5-1))|(1<<(8-1))|(1<<(12-1))|(1<<(13-1))|(1<<(18-1))|(1<<(20-1))|(1<<(24-1))|(1<<(23-1))|(1<<(27-1))|(1<<(30-1))
        const uint expected = 0x1u | 0x2u | 0x10u | 0x80u | 0x800u | 0x1000u | 0x20000u | 0x80000u | 0x800000u | 0x400000u | 0x4000000u | 0x20000000u;
        Assert.Equal(expected, ShapeshiftFormEffectRules.NotRemovedMechanics);
    }
}
