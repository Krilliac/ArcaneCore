using ArcaneCore.Game.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.Auras;

/// <summary>The support matrix is total over the aura types (vmangos TOTAL_AURAS = 193, SpellAuraDefines.h:332).</summary>
public sealed class AuraSupportTests
{
    [Fact]
    public void TheTable_HasExactlyOneRowPerAuraType_AndNoneIsMissing()
    {
        AuraType[] all = [.. Enum.GetValues<AuraType>().Distinct()];

        Assert.Equal(193, all.Length);
        Assert.Equal(all.Length, AuraSupport.Entries.Count);
        Assert.Equal(all.Order().ToArray(), AuraSupport.Entries.Select(e => e.Type).Order().ToArray());
        Assert.All(all, type => Assert.Equal(type, AuraSupport.Get(type).Type));
    }

    [Fact]
    public void EveryRow_NamesItsVmangosHandler_WithALineInTheDispatchTable()
    {
        Assert.All(AuraSupport.Entries, row =>
        {
            Assert.False(string.IsNullOrEmpty(row.VmangosHandler), row.Type.ToString());
            Assert.InRange(row.VmangosLine, 65, 260); // SpellAuras.cpp AuraHandler[] rows
        });
    }

    [Fact]
    public void EveryTypeTheBuiltInEngineHandles_IsAHandlerRow()
    {
        using var kit = new Spells.SpellTestKit();

        foreach (AuraType type in Enum.GetValues<AuraType>().Distinct().Where(kit.System.HasAuraHandler))
        {
            AuraSupportLevel level = AuraSupport.Get(type).Level;
            Assert.True(level == AuraSupportLevel.Handler, $"{type} has a handler but its baseline row says {level}");
        }
    }

    [Fact]
    public void ThePolarityAndRegenAuraTypes_AreAccountedFor()
    {
        // The aura types this lane reads in formulas (food, drink, regen modifiers) are referenced by the regen tick.
        foreach (AuraType type in new[] { AuraType.ModRegen, AuraType.ModPowerRegen, AuraType.ModManaRegenInterrupt, AuraType.ModHealthRegenInCombat })
        {
            Assert.NotEqual(AuraSupportLevel.Unsupported, AuraSupport.Get(type).Level);
        }
    }
}
