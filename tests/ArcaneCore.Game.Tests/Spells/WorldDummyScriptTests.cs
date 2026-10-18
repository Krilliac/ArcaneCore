using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Generic;
using ArcaneCore.Game.Spells.Scripts;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>vmangos Spell::EffectDummy world and quest cases ported to <c>Spells/Generic/WorldDummyScripts.cs</c>.</summary>
public sealed class WorldDummyScriptTests
{
    private static SpellInfo Instant(SpellInfo spell) => spell with { StartRecoveryCategory = 0, StartRecoveryTime = 0, SpellVisual = 1 };

    private static SpellTestKit Kit(params SpellInfo[] spells)
    {
        var kit = new SpellTestKit(spells);
        SpellScriptDispatcher.Install(kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        return kit;
    }

    private static SpellInfo Marker(uint id, SpellImplicitTarget target) =>
        Instant(Spell(id, Effect(SpellEffectName.ApplyAura, 0, target, AuraType.Dummy)) with
        {
            Duration = new SpellDuration(30_000, 0, 30_000),
            RangeIndex = 4,
            Range = new SpellRange(0, 40),
        });

    [Fact]
    public void ZeroManaFullHealth_FillsHealthAndEmptiesMana()
    {
        using SpellTestKit kit = Kit(Instant(Spell(23777, Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.UnitCaster))));
        (Player unit, _) = kit.AddPlayer(1);
        unit.MaxHealth = 5000;
        unit.Health = 100;
        SpellSystem.SetPower(unit, PowerType.Mana, 900);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(unit, 23777, SpellCastTargets.ForSelf(), triggered: true));

        Assert.Equal(5000u, unit.Health);
        Assert.Equal(0u, SpellSystem.GetPower(unit, PowerType.Mana));
    }

    [Fact]
    public void RanshallaTorchTrap_EndsLightTorch()
    {
        using SpellTestKit kit = Kit(Instant(Spell(18955, Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.UnitCaster))), Marker(18953, SpellImplicitTarget.UnitCaster));
        (Player ranshalla, _) = kit.AddPlayer(1);
        Assert.True(kit.System.AddAura(ranshalla, 18953));

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(ranshalla, 18955, SpellCastTargets.ForSelf(), triggered: true));

        Assert.False(kit.System.HasAura(ranshalla, 18953));
    }

    [Fact]
    public void NaturesBounty_RestoresTheTargetsOwnPowerType()
    {
        using SpellTestKit kit = Kit(
            Instant(Spell(27798, Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.UnitCaster))),
            Marker(27782, SpellImplicitTarget.UnitCaster), Marker(27783, SpellImplicitTarget.UnitCaster), Marker(27784, SpellImplicitTarget.UnitCaster));
        (Player mage, _) = kit.AddPlayer(1);
        ArcaneCore.Game.Spells.Druid.PowerTypeSwitch.SetPowerType(mage, PowerType.Mana);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(mage, 27798, SpellCastTargets.ForSelf(), triggered: true));

        Assert.True(kit.System.HasAura(mage, 27782));
        Assert.False(kit.System.HasAura(mage, 27783));
    }

    [Fact]
    public void TameBeastTrigger_WithoutAChannelledBeast_DoesNothing()
    {
        using SpellTestKit kit = Kit(
            Instant(Spell(13535, Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.UnitCaster))),
            Marker(TameBeastTriggerScript.TameBeastEffect, SpellImplicitTarget.UnitCaster));
        (Player hunter, _) = kit.AddPlayer(1);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(hunter, 13535, SpellCastTargets.ForSelf(), triggered: true));

        Assert.False(kit.System.HasAura(hunter, TameBeastTriggerScript.TameBeastEffect));
    }

    [Fact]
    public void Discovery_RegistersTheWorldDummies()
    {
        var ids = SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly).SpellIds.ToHashSet();
        foreach (uint id in new uint[] { 13535, 9204, 18955, 18993, 18954, 23777, 11886, 11887, 11888, 11889, 12699, 15998, 19614, 6655, 25716, 27798, 9976, 17271 })
        {
            Assert.Contains(id, ids);
        }
    }
}