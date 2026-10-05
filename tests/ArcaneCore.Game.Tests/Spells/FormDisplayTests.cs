using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using FormIds = ArcaneCore.Game.Spells.Druid.DruidForms;
using ArcaneCore.Kernel.WorldData;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

public sealed class FormDisplayTests
{
    [Theory]
    [InlineData(1, Race.NightElf, 892u, 0.8f)]
    [InlineData(1, Race.Tauren, 8571u, 0.8f)]
    [InlineData(3, Race.NightElf, 632u, 0.8f)]
    [InlineData(4, Race.NightElf, 2428u, 0.8f)]
    [InlineData(5, Race.NightElf, 2281u, 1f)]
    [InlineData(5, Race.Tauren, 2289u, 1f)]
    [InlineData(8, Race.Tauren, 2289u, 1f)]
    [InlineData(31, Race.NightElf, 15374u, 1f)]
    [InlineData(31, Race.Tauren, 15375u, 1f)]
    [InlineData(2, Race.NightElf, 864u, 1f)]
    public void RealFormAura_UsesPinnedRaceDisplayAndRestoresScale(int form, Race race, uint display, float scale)
    {
        using var kit = new SpellTestKit(Spell(998130,
            Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ModShapeshift, misc: form)));
        (Player player, _) = kit.AddPlayer(1);
        player.SetByte(UpdateFields.UnitFieldBytes0, 0, (byte)race);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Druid);
        player.NativeDisplayId = 100;
        player.DisplayId = 100;
        player.SetFloat(UpdateFields.ObjectFieldScaleX, 1.25f);
        new ShapeshiftService(kit.System, new ShapeshiftFormCatalog([new((uint)form, 0, 0)]),
            new CombatOptions(), _ => []).Install();
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 998130, SpellCastTargets.ForSelf(), true));
        Assert.Equal((byte)form, player.GetByte(UpdateFields.UnitFieldBytes1, 2));
        Assert.Equal(display, player.DisplayId);
        Assert.Equal(scale, player.GetFloat(UpdateFields.ObjectFieldScaleX), 3);
        kit.System.RemoveAuras(player, 998130);
        Assert.Equal(100u, player.DisplayId);
        Assert.Equal(1.25f, player.GetFloat(UpdateFields.ObjectFieldScaleX), 3);
        Assert.Equal((byte)0, player.GetByte(UpdateFields.UnitFieldBytes1, 2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovingFormUnderTransform_PreservesTransformAndRestoresNative(bool transformFirst)
    {
        using var kit = new SpellTestKit(
            Spell(998131, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ModShapeshift, misc: 1)),
            Spell(998132, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Transform, misc: 1)),
            Spell(998133, Effect(SpellEffectName.ApplyAura, 50, aura: AuraType.ModScale)));
        (Player player, _) = kit.AddPlayer(1);
        player.NativeDisplayId = 100;
        player.DisplayId = 100;
        new ShapeshiftService(kit.System, new ShapeshiftFormCatalog([new(1, 0, 0)]),
            new CombatOptions(), _ => []).Install();
        kit.System.TransformDisplayResolver = (_, _) => new TransformDisplay(900, 2f);
        kit.System.CastSpell(player, 998133, SpellCastTargets.ForSelf(), true);
        kit.System.CastSpell(player, transformFirst ? 998132u : 998131u, SpellCastTargets.ForSelf(), true);
        kit.System.CastSpell(player, transformFirst ? 998131u : 998132u, SpellCastTargets.ForSelf(), true);
        Assert.Equal(900u, player.DisplayId);
        Assert.Equal(3f, player.GetFloat(UpdateFields.ObjectFieldScaleX), 3);
        kit.System.RemoveAuras(player, 998131);
        Assert.Equal(900u, player.DisplayId);
        Assert.Equal(3f, player.GetFloat(UpdateFields.ObjectFieldScaleX), 3);
        kit.System.RemoveAuras(player, 998132);
        Assert.Equal(100u, player.DisplayId);
        Assert.Equal(1.5f, player.GetFloat(UpdateFields.ObjectFieldScaleX), 3);
    }

    [Fact]
    public void CatForm_UsesPinnedDisplayAndScale_AndStaleRemovalCannotRestore()
    {
        SpellInfo cat = Spell(998100, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster,
            AuraType.ModShapeshift, misc: FormIds.Cat)) with { Duration = new SpellDuration(60_000, 0, 60_000) };
        SpellInfo cat2 = Spell(998101, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster,
            AuraType.ModShapeshift, misc: FormIds.Cat)) with { Duration = new SpellDuration(60_000, 0, 60_000) };
        using var kit = new SpellTestKit(cat, cat2);
        (Player player, _) = kit.AddPlayer(1);
        var service = new ShapeshiftService(kit.System,
            new ShapeshiftFormCatalog([new ShapeshiftFormInfo(FormIds.Cat, 0, 0)]),
            new CombatOptions(), _ => []);
        service.Install();
        player.SetByte(UpdateFields.UnitFieldBytes0, 0, (byte)Race.Human);
        player.NativeDisplayId = 100;
        player.DisplayId = 100;
        player.SetFloat(UpdateFields.ObjectFieldScaleX, 1.0f);

        kit.System.CastSpell(player, 998100, SpellCastTargets.ForSelf(), true);
        Assert.Equal(892u, player.DisplayId);
        Assert.Equal(0.8f, player.GetFloat(UpdateFields.ObjectFieldScaleX), 3);
        kit.System.CastSpell(player, 998101, SpellCastTargets.ForSelf(), true);
        kit.System.RemoveAuras(player, 998100);
        Assert.Equal(892u, player.DisplayId);
        kit.System.RemoveAuras(player, 998101);
        Assert.Equal(100u, player.DisplayId);
    }

    [Fact]
    public void FormAndTransformScaleOverlay_ModScaleIsAppliedOnceAcrossBothRemovalOrders()
    {
        SpellInfo cat = Spell(998102, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster,
            AuraType.ModShapeshift, misc: FormIds.Cat));
        SpellInfo mod = Spell(998103, Effect(SpellEffectName.ApplyAura, 50, SpellImplicitTarget.UnitCaster,
            AuraType.ModScale));
        SpellInfo transform = Spell(998104, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster,
            AuraType.Transform, misc: 1));
        using var kit = new SpellTestKit(cat, mod, transform);
        (Player player, _) = kit.AddPlayer(1);
        var service = new ShapeshiftService(kit.System,
            new ShapeshiftFormCatalog([new ShapeshiftFormInfo(FormIds.Cat, 0, 0)]),
            new CombatOptions(), _ => []);
        service.Install();
        player.NativeDisplayId = 100;
        player.DisplayId = 100;
        kit.System.TransformDisplayResolver = (_, _) => new TransformDisplay(900, 2.0f);

        kit.System.CastSpell(player, 998103, SpellCastTargets.ForSelf(), true);
        kit.System.CastSpell(player, 998102, SpellCastTargets.ForSelf(), true);
        kit.System.CastSpell(player, 998104, SpellCastTargets.ForSelf(), true);
        Assert.Equal(3.0f, player.GetFloat(UpdateFields.ObjectFieldScaleX), 3);
        kit.System.RemoveAuras(player, 998104);
        Assert.Equal(1.2f, player.GetFloat(UpdateFields.ObjectFieldScaleX), 3);
        kit.System.RemoveAuras(player, 998102);
        Assert.Equal(1.5f, player.GetFloat(UpdateFields.ObjectFieldScaleX), 3);
    }
}
