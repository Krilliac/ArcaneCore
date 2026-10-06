using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.ClassSpells;

public sealed class GhostWolfLifecycleTests
{
    private const uint GhostWolf = 2645;
    private const uint WaterWalk = 990701;
    private const uint GhostWolfReplacement = 990702;
    private const uint UnrelatedSpeed = 990705;
    private const uint ShapeCancelBuff = 990706;
    private const uint ShapeSafeBuff = 990707;

    private static SpellInfo GhostWolfSpell(uint id = GhostWolf) => Spell(id,
        Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ModShapeshift, misc: (int)ShapeshiftForm.GhostWolf),
        Effect(SpellEffectName.ApplyAura, 40, aura: AuraType.ModIncreaseSpeed)) with
    {
        Duration = new SpellDuration(-1, 0, -1),
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    [Fact]
    public void ImportedGhostWolf_CastsFormDisplayAndIndependentSpeed_ThenRestoresNativeState()
    {
        using var kit = new SpellTestKit(GhostWolfSpell(), Spell(WaterWalk,
            Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.WaterWalk)),
            Spell(UnrelatedSpeed, Effect(SpellEffectName.ApplyAura, 15, aura: AuraType.ModIncreaseSpeed)));
        (Player player, _) = kit.AddPlayer(1);
        player.NativeDisplayId = 100;
        player.DisplayId = 100;
        player.SetFloat(UpdateFields.ObjectFieldScaleX, 1.25f);
        player.SetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana, 100);
        SpellSystem.SetPower(player, PowerType.Mana, 77);
        var forms = new ShapeshiftFormCatalog([new((uint)ShapeshiftForm.GhostWolf, 0, 0)]);
        new ShapeshiftService(kit.System, forms, new CombatOptions(), _ => []).Install();

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, WaterWalk, SpellCastTargets.ForSelf(), true));
        Assert.Single(kit.System.AurasOfType(player, AuraType.WaterWalk));
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, UnrelatedSpeed, SpellCastTargets.ForSelf(), true));
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, GhostWolf, SpellCastTargets.ForSelf(), true));

        Assert.Equal(ShapeshiftForm.GhostWolf, ShapeshiftService.GetForm(player));
        Assert.Equal(4613u, player.DisplayId);
        Assert.Equal(0.80f, player.GetFloat(UpdateFields.ObjectFieldScaleX), 3);
        Assert.Empty(kit.System.AurasOfType(player, AuraType.WaterWalk));
        Assert.Equal(2, kit.System.AurasOfType(player, AuraType.ModIncreaseSpeed).Count());
        Assert.Equal(77u, SpellSystem.GetPower(player, PowerType.Mana));

        kit.System.RemoveAuras(player, GhostWolf);

        Assert.Equal(ShapeshiftForm.None, ShapeshiftService.GetForm(player));
        Assert.Equal(100u, player.DisplayId);
        Assert.Equal(1.25f, player.GetFloat(UpdateFields.ObjectFieldScaleX), 3);
        Assert.Single(kit.System.AurasOfType(player, AuraType.ModIncreaseSpeed));
        Assert.Equal(77u, SpellSystem.GetPower(player, PowerType.Mana));
    }

    [Fact]
    public void NewerGhostWolfHolderOwnsFormAndStaleRemovalCannotRestoreNativeDisplay()
    {
        using var kit = new SpellTestKit(GhostWolfSpell(), GhostWolfSpell(GhostWolfReplacement));
        (Player player, _) = kit.AddPlayer(1);
        player.NativeDisplayId = 100;
        player.DisplayId = 100;
        player.SetFloat(UpdateFields.ObjectFieldScaleX, 1.0f);
        new ShapeshiftService(kit.System,
            new ShapeshiftFormCatalog([new((uint)ShapeshiftForm.GhostWolf, 0, 0)]),
            new CombatOptions(), _ => []).Install();

        kit.System.CastSpell(player, GhostWolf, SpellCastTargets.ForSelf(), true);
        kit.System.CastSpell(player, GhostWolfReplacement, SpellCastTargets.ForSelf(), true);
        kit.System.RemoveAuras(player, GhostWolf);

        Assert.Equal(ShapeshiftForm.GhostWolf, ShapeshiftService.GetForm(player));
        Assert.Equal(4613u, player.DisplayId);
        Assert.Equal(0.80f, player.GetFloat(UpdateFields.ObjectFieldScaleX), 3);

        kit.System.RemoveAuras(player, GhostWolfReplacement);
        Assert.Equal(ShapeshiftForm.None, ShapeshiftService.GetForm(player));
        Assert.Equal(100u, player.DisplayId);
    }

    [Fact]
    public void GhostWolf_OverlaysTransformAndModScale_ThenRestoresEachOwner()
    {
        const uint modScale = 990703;
        const uint transform = 990704;
        using var kit = new SpellTestKit(GhostWolfSpell(),
            Spell(modScale, Effect(SpellEffectName.ApplyAura, 50, aura: AuraType.ModScale)),
            Spell(transform, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Transform, misc: 1)));
        (Player player, _) = kit.AddPlayer(1);
        player.NativeDisplayId = 100;
        player.DisplayId = 100;
        player.SetFloat(UpdateFields.ObjectFieldScaleX, 1.0f);
        new ShapeshiftService(kit.System,
            new ShapeshiftFormCatalog([new((uint)ShapeshiftForm.GhostWolf, 0, 0)]),
            new CombatOptions(), _ => []).Install();
        kit.System.TransformDisplayResolver = (_, _) => new TransformDisplay(900, 2.0f);

        kit.System.CastSpell(player, modScale, SpellCastTargets.ForSelf(), true);
        kit.System.CastSpell(player, GhostWolf, SpellCastTargets.ForSelf(), true);
        kit.System.CastSpell(player, transform, SpellCastTargets.ForSelf(), true);
        Assert.Equal(900u, player.DisplayId);
        Assert.Equal(3.0f, player.GetFloat(UpdateFields.ObjectFieldScaleX), 3);

        kit.System.RemoveAuras(player, transform);
        Assert.Equal(4613u, player.DisplayId);
        Assert.Equal(1.2f, player.GetFloat(UpdateFields.ObjectFieldScaleX), 3);
        kit.System.RemoveAuras(player, modScale);
        Assert.Equal(4613u, player.DisplayId);
        Assert.Equal(0.8f, player.GetFloat(UpdateFields.ObjectFieldScaleX), 3);
        kit.System.RemoveAuras(player, GhostWolf);
        Assert.Equal(100u, player.DisplayId);
        Assert.Equal(1.0f, player.GetFloat(UpdateFields.ObjectFieldScaleX), 3);
    }

    [Fact]
    public void GhostWolf_RemovesOnlyAurasMarkedShapeshiftingCancels()
    {
        SpellInfo cancel = Spell(ShapeCancelBuff, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
        {
            AuraInterruptFlags = (SpellAuraInterruptFlags)ShapeshiftService.ShapeshiftingCancelsFlag,
            Duration = new SpellDuration(-1, 0, -1),
        };
        SpellInfo safe = Spell(ShapeSafeBuff, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
        {
            Duration = new SpellDuration(-1, 0, -1),
        };
        using var kit = new SpellTestKit(GhostWolfSpell(), cancel, safe);
        (Player player, _) = kit.AddPlayer(1);
        new ShapeshiftService(kit.System,
            new ShapeshiftFormCatalog([new((uint)ShapeshiftForm.GhostWolf, 0, 0)]),
            new CombatOptions(), _ => []).Install();

        kit.System.CastSpell(player, ShapeCancelBuff, SpellCastTargets.ForSelf(), true);
        kit.System.CastSpell(player, ShapeSafeBuff, SpellCastTargets.ForSelf(), true);
        kit.System.CastSpell(player, GhostWolf, SpellCastTargets.ForSelf(), true);

        Assert.False(kit.System.HasAura(player, ShapeCancelBuff));
        Assert.True(kit.System.HasAura(player, ShapeSafeBuff));
        Assert.Equal(ShapeshiftForm.GhostWolf, ShapeshiftService.GetForm(player));
    }
}
