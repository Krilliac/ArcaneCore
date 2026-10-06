using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.ClassSpells;

public sealed class ShadowformLifecycleTests
{
    private const uint Shadowform = 15473;
    private const uint ShadowformReplacement = 990801;
    private const uint ShapeCancelBuff = 990802;
    private const uint ShapeSafeBuff = 990803;
    private const uint GhostWolf = 990804;

    private static SpellInfo ShadowformSpell(uint id = Shadowform) => Spell(id,
        Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ModShapeshift, misc: (int)ShapeshiftForm.Shadow)) with
    {
        Duration = new SpellDuration(-1, 0, -1),
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static ShapeshiftService Install(SpellTestKit kit, params ShapeshiftForm[] additionalForms)
        => new(kit.System,
            new ShapeshiftFormCatalog([new((uint)ShapeshiftForm.Shadow, 0, 0),
                .. additionalForms.Select(form => new ShapeshiftFormInfo((uint)form, 0, 0))]),
            new CombatOptions(), _ => []);

    [Fact]
    public void ImportedShadowform_SetsFormOnly_AndPreservesManaNativeDisplayAndSpeed()
    {
        using var kit = new SpellTestKit(ShadowformSpell());
        (Player player, _) = kit.AddPlayer(1);
        player.NativeDisplayId = 100;
        player.DisplayId = 100;
        player.SetFloat(UpdateFields.ObjectFieldScaleX, 1.25f);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Priest);
        player.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
        player.SetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana, 100);
        SpellSystem.SetPower(player, PowerType.Mana, 77);
        uint manaBefore = SpellSystem.GetPower(player, PowerType.Mana);
        Install(kit).Install();

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, Shadowform, SpellCastTargets.ForSelf(), true));

        Assert.Equal(ShapeshiftForm.Shadow, ShapeshiftService.GetForm(player));
        Assert.Equal(PowerType.Mana, player.PowerType);
        Assert.Equal(manaBefore, SpellSystem.GetPower(player, PowerType.Mana));
        Assert.Equal(100u, player.DisplayId);
        Assert.Equal(1.25f, player.GetFloat(UpdateFields.ObjectFieldScaleX), 3);
        Assert.Empty(kit.System.AurasOfType(player, AuraType.ModIncreaseSpeed));

        kit.System.RemoveAuras(player, Shadowform);

        Assert.Equal(ShapeshiftForm.None, ShapeshiftService.GetForm(player));
        Assert.Equal(manaBefore, SpellSystem.GetPower(player, PowerType.Mana));
    }

    [Fact]
    public void Shadowform_RemovesOnlyAurasMarkedShapeshiftingCancels()
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
        using var kit = new SpellTestKit(ShadowformSpell(), cancel, safe);
        (Player player, _) = kit.AddPlayer(1);
        Install(kit).Install();

        kit.System.CastSpell(player, ShapeCancelBuff, SpellCastTargets.ForSelf(), true);
        kit.System.CastSpell(player, ShapeSafeBuff, SpellCastTargets.ForSelf(), true);
        kit.System.CastSpell(player, Shadowform, SpellCastTargets.ForSelf(), true);

        Assert.False(kit.System.HasAura(player, ShapeCancelBuff));
        Assert.True(kit.System.HasAura(player, ShapeSafeBuff));
        Assert.Equal(ShapeshiftForm.Shadow, ShapeshiftService.GetForm(player));
    }

    [Fact]
    public void NewerShadowformHolderOwnsForm_AndStaleRemovalCannotClearIt()
    {
        using var kit = new SpellTestKit(ShadowformSpell(), ShadowformSpell(ShadowformReplacement));
        (Player player, _) = kit.AddPlayer(1);
        Install(kit).Install();

        kit.System.CastSpell(player, Shadowform, SpellCastTargets.ForSelf(), true);
        kit.System.CastSpell(player, ShadowformReplacement, SpellCastTargets.ForSelf(), true);
        kit.System.RemoveAuras(player, Shadowform);

        Assert.Equal(ShapeshiftForm.Shadow, ShapeshiftService.GetForm(player));
        kit.System.RemoveAuras(player, ShadowformReplacement);
        Assert.Equal(ShapeshiftForm.None, ShapeshiftService.GetForm(player));
    }

    [Fact]
    public void Shadowform_ReplacingGhostWolfClearsWolfOverlayWithoutApplyingShadowVisuals()
    {
        SpellInfo ghostWolf = Spell(GhostWolf,
            Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ModShapeshift, misc: (int)ShapeshiftForm.GhostWolf));
        using var kit = new SpellTestKit(ghostWolf, ShadowformSpell());
        (Player player, _) = kit.AddPlayer(1);
        player.NativeDisplayId = 100;
        player.DisplayId = 100;
        player.SetFloat(UpdateFields.ObjectFieldScaleX, 1.0f);
        Install(kit, ShapeshiftForm.GhostWolf).Install();

        kit.System.CastSpell(player, ghostWolf.Id, SpellCastTargets.ForSelf(), true);
        Assert.Equal(4613u, player.DisplayId);
        kit.System.CastSpell(player, Shadowform, SpellCastTargets.ForSelf(), true);

        Assert.Equal(ShapeshiftForm.Shadow, ShapeshiftService.GetForm(player));
        Assert.Equal(100u, player.DisplayId);
        Assert.Equal(1.0f, player.GetFloat(UpdateFields.ObjectFieldScaleX), 3);
    }

    [Fact]
    public void ShadowformWithoutCatalogRow_IsIgnoredAndPreservesFormManaAndVisuals()
    {
        using var kit = new SpellTestKit(ShadowformSpell());
        (Player player, _) = kit.AddPlayer(1);
        player.NativeDisplayId = 100;
        player.DisplayId = 100;
        player.SetFloat(UpdateFields.ObjectFieldScaleX, 1.25f);
        player.SetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana, 100);
        SpellSystem.SetPower(player, PowerType.Mana, 77);
        new ShapeshiftService(kit.System, ShapeshiftFormCatalog.Empty, new CombatOptions(), _ => []).Install();

        kit.System.CastSpell(player, Shadowform, SpellCastTargets.ForSelf(), true);

        Assert.Equal(ShapeshiftForm.None, ShapeshiftService.GetForm(player));
        Assert.Equal(77u, SpellSystem.GetPower(player, PowerType.Mana));
        Assert.Equal(100u, player.DisplayId);
        Assert.Equal(1.25f, player.GetFloat(UpdateFields.ObjectFieldScaleX), 3);
    }

    [Fact]
    public void Shadowform_PreservesUnrelatedTransformAndModScaleOwners()
    {
        const uint modScale = 990805;
        const uint transform = 990806;
        using var kit = new SpellTestKit(ShadowformSpell(),
            Spell(modScale, Effect(SpellEffectName.ApplyAura, 50, aura: AuraType.ModScale)),
            Spell(transform, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Transform, misc: 1)));
        (Player player, _) = kit.AddPlayer(1);
        player.NativeDisplayId = 100;
        player.DisplayId = 100;
        player.SetFloat(UpdateFields.ObjectFieldScaleX, 1.0f);
        Install(kit).Install();
        kit.System.TransformDisplayResolver = (_, _) => new TransformDisplay(900, 2.0f);

        kit.System.CastSpell(player, modScale, SpellCastTargets.ForSelf(), true);
        kit.System.CastSpell(player, transform, SpellCastTargets.ForSelf(), true);
        kit.System.CastSpell(player, Shadowform, SpellCastTargets.ForSelf(), true);
        Assert.Equal(900u, player.DisplayId);
        Assert.Equal(3.0f, player.GetFloat(UpdateFields.ObjectFieldScaleX), 3);

        kit.System.RemoveAuras(player, Shadowform);
        Assert.Equal(ShapeshiftForm.None, ShapeshiftService.GetForm(player));
        Assert.Equal(900u, player.DisplayId);
        Assert.Equal(3.0f, player.GetFloat(UpdateFields.ObjectFieldScaleX), 3);

        kit.System.RemoveAuras(player, transform);
        Assert.Equal(100u, player.DisplayId);
        Assert.Equal(1.5f, player.GetFloat(UpdateFields.ObjectFieldScaleX), 3);
    }
}
