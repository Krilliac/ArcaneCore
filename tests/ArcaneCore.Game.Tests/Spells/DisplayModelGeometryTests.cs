using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

public sealed class DisplayModelGeometryTests
{
    [Fact]
    public void UpdateDisplayModel_NormalizesVerifiedNativeScale_AndUsesPinnedFallbackWhenAbsent()
    {
        using var kit = new SpellTestKit();
        (Player player, _) = kit.AddPlayer(1);
        player.DisplayId = 700;
        player.SetFloat(UpdateFields.ObjectFieldScaleX, 2.0f);
        kit.System.DisplayModelResolver = id => id == 700 ? new DisplayModelGeometry(1.25f, 0.5f, 1.5f, 3.0f, 2.0f) : null;

        kit.System.UpdateDisplayModel(player);
        Assert.Equal(0.8f, player.BoundingRadius, 3);
        Assert.Equal(2.4f, player.GetFloat(UpdateFields.UnitFieldCombatreach), 3);
        Assert.Equal(2.4f, player.Locomotion.CollisionHeight, 3);

        Assert.True(ArcaneCore.Game.Spells.Interrupts.TerrainLiquidProbe.IsHighLiquid(
            ArcaneCore.Game.Maps.Terrain.LiquidStatus.InWater, 2.0f, 0, player.Locomotion.CollisionHeight));

        player.DisplayId = 701;
        kit.System.UpdateDisplayModel(player);
        Assert.Equal(1.5f, player.BoundingRadius, 3);
        Assert.Equal(1.5f, player.GetFloat(UpdateFields.UnitFieldCombatreach), 3);
    }

    [Fact]
    public void RealTransformAura_UpdatesGeometryAndRestoresAfterRemoval()
    {
        SpellInfo transform = SpellTestKit.Spell(998200,
            SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Transform, misc: 1));
        using var kit = new SpellTestKit(transform);
        (Player player, _) = kit.AddPlayer(1);
        player.NativeDisplayId = 100;
        player.DisplayId = 100;
        player.SetFloat(UpdateFields.ObjectFieldScaleX, 1.0f);
        FixedTransformDisplays.Use(kit, 700, 1.0f);
        kit.System.DisplayModelResolver = id => id == 700 ? new DisplayModelGeometry(2.0f, 0.5f, 1.5f) : null;

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 998200, SpellCastTargets.ForSelf(), true));
        Assert.Equal(0.25f, player.BoundingRadius, 3);
        Assert.Equal(0.75f, player.GetFloat(UpdateFields.UnitFieldCombatreach), 3);
        kit.System.RemoveAuras(player, 998200);
        Assert.Equal(1.5f, player.BoundingRadius, 3);
        Assert.Equal(1.5f, player.GetFloat(UpdateFields.UnitFieldCombatreach), 3);
    }

    [Fact]
    public void TransformOverride_ReplacesNonUnitNativeScaleAndRestoresIt()
    {
        using var kit = new SpellTestKit(SpellTestKit.Spell(998205,
            SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Transform, misc: 1)));
        (Player player, _) = kit.AddPlayer(1);
        player.NativeDisplayId = player.DisplayId = 100;
        player.SetFloat(UpdateFields.ObjectFieldScaleX, 1.25f);
        FixedTransformDisplays.Use(kit, 700, 2f);
        kit.System.CastSpell(player, 998205, SpellCastTargets.ForSelf(), true);
        Assert.Equal(2f, player.GetFloat(UpdateFields.ObjectFieldScaleX), 3);
        kit.System.RemoveAuras(player, 998205);
        Assert.Equal(1.25f, player.GetFloat(UpdateFields.ObjectFieldScaleX), 3);
    }
}
