using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Druid;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.ClassSpells;

public sealed class DruidShapeshiftInterruptionTests
{
    private const uint ShapeCancelBuff = 990901;
    private const uint ShapeSafeBuff = 990902;
    private const uint CatForm = 990903;
    private const uint BearForm = 990904;
    private const uint Stance = 990905;
    private const uint IncomingFlaggedForm = 990906;
    private const uint ShapeshiftingCancels = ShapeshiftService.ShapeshiftingCancelsFlag;

    private static SpellInfo DruidForm(uint id, byte form, SpellAuraInterruptFlags interrupt = 0) => Spell(id,
        Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ModShapeshift, misc: form)) with
    {
        AuraInterruptFlags = interrupt,
        Duration = new SpellDuration(-1, 0, -1),
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static SpellInfo SelfBuff(uint id) => Spell(id,
        Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
    {
        Duration = new SpellDuration(-1, 0, -1),
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static Player AddDruid(SpellTestKit kit, params ShapeshiftFormInfo[] forms)
    {
        (Player player, _) = kit.AddPlayer(1);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Druid);
        player.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
        new ShapeshiftService(kit.System, new ShapeshiftFormCatalog(forms), new CombatOptions(), _ => []).Install();
        return player;
    }

    [Theory]
    [InlineData(DruidForms.Cat, CatForm)]
    [InlineData(DruidForms.Bear, BearForm)]
    public void DruidForm_RemovesFlaggedAuraAndKeepsUnflaggedAura(byte form, uint formSpell)
    {
        using var kit = new SpellTestKit(
            DruidForm(formSpell, form),
            SelfBuff(ShapeCancelBuff) with { AuraInterruptFlags = (SpellAuraInterruptFlags)ShapeshiftingCancels },
            SelfBuff(ShapeSafeBuff));
        Player player = AddDruid(kit,
            new ShapeshiftFormInfo(DruidForms.Cat, 0, 0),
            new ShapeshiftFormInfo(DruidForms.Bear, 0, 0));

        kit.System.CastSpell(player, ShapeCancelBuff, SpellCastTargets.ForSelf(), true);
        kit.System.CastSpell(player, ShapeSafeBuff, SpellCastTargets.ForSelf(), true);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, formSpell, SpellCastTargets.ForSelf(), true));

        Assert.False(kit.System.HasAura(player, ShapeCancelBuff));
        Assert.True(kit.System.HasAura(player, ShapeSafeBuff));
        Assert.Equal((ShapeshiftForm)form, ShapeshiftService.GetForm(player));
    }

    [Fact]
    public void DruidForm_ExcludesIncomingFlaggedHolderFromCancellation()
    {
        using var kit = new SpellTestKit(DruidForm(IncomingFlaggedForm, DruidForms.Cat,
            (SpellAuraInterruptFlags)ShapeshiftingCancels));
        Player player = AddDruid(kit, new ShapeshiftFormInfo(DruidForms.Cat, 0, 0));

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, IncomingFlaggedForm, SpellCastTargets.ForSelf(), true));

        Assert.True(kit.System.HasAura(player, IncomingFlaggedForm));
        Assert.Equal(ShapeshiftForm.Cat, ShapeshiftService.GetForm(player));
    }

    [Fact]
    public void WarriorStance_KeepsFlaggedAuraBecauseCatalogMarksItAsStance()
    {
        using var kit = new SpellTestKit(
            DruidForm(Stance, (byte)ShapeshiftForm.BattleStance),
            SelfBuff(ShapeCancelBuff) with { AuraInterruptFlags = (SpellAuraInterruptFlags)ShapeshiftingCancels });
        (Player player, _) = kit.AddPlayer(1);
        new ShapeshiftService(kit.System,
            new ShapeshiftFormCatalog([new((uint)ShapeshiftForm.BattleStance, (uint)ShapeshiftFlags.Stance, 0)]),
            new CombatOptions(), _ => []).Install();

        kit.System.CastSpell(player, ShapeCancelBuff, SpellCastTargets.ForSelf(), true);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, Stance, SpellCastTargets.ForSelf(), true));

        Assert.True(kit.System.HasAura(player, ShapeCancelBuff));
        Assert.Equal(ShapeshiftForm.BattleStance, ShapeshiftService.GetForm(player));
    }
}
