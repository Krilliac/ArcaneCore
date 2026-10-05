using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

public sealed class TransformPolymorphTests
{
    [Fact]
    public void MagePolymorph_UsesMaxHealthPerTenInCombat_AndRestoresDisplayOnRemoval()
    {
        SpellInfo poly = Spell(998001,
            Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.ModConfuse),
            Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Transform, misc: 999999))
            with { SpellFamilyName = 3, PreventionType = SpellConstants.PreventionTypeSilence,
                Duration = new SpellDuration(60_000, 0, 60_000) };
        SpellInfo flat = Spell(998012, Effect(SpellEffectName.ApplyAura, 5, SpellImplicitTarget.UnitCaster,
            AuraType.ModHealthRegenInCombat)) with { Duration = new SpellDuration(60_000, 0, 60_000) };
        SpellInfo percent = Spell(998013, Effect(SpellEffectName.ApplyAura, 50, SpellImplicitTarget.UnitCaster,
            AuraType.ModHealthRegenPercent)) with { Duration = new SpellDuration(60_000, 0, 60_000) };
        SpellInfo food = Spell(998014, Effect(SpellEffectName.ApplyAura, 100, SpellImplicitTarget.UnitCaster,
            AuraType.ModRegen, amplitude: 5000)) with { Duration = new SpellDuration(60_000, 0, 60_000) };
        using var kit = new SpellTestKit(poly, flat, percent, food);
        (Player player, _) = kit.AddPlayer(1);
        player.MaxHealth = 1000;
        player.Health = 100;
        player.NativeDisplayId = 123;
        player.DisplayId = 123;
        player.SetUInt32(UpdateFields.UnitFieldStat0 + 4, 100);
        player.SetStandState(StandState.Sit);
        kit.System.TransformDisplayResolver = (_, _) => new TransformDisplay(777, 1.0f);
        CombatEnvironment.Register(kit.World, new CombatEnvironment(new CombatOptions(), new SpellSystemPowerAuras(kit.System)));
        player.Map!.Combat.SetInCombatState(player, 10_000);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 998001, SpellCastTargets.ForSelf(), true));
        kit.System.CastSpell(player, 998013, SpellCastTargets.ForSelf(), true);
        kit.System.CastSpell(player, 998014, SpellCastTargets.ForSelf(), true);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 998012, SpellCastTargets.ForSelf(), true));
        Assert.Equal(777u, player.DisplayId);
        Assert.Equal(998001u, player.TransformSpellId);
        kit.World.RunTick(2000);
        Assert.Equal(202u, player.Health);

        kit.System.RemoveAuras(player, 998001);
        Assert.Equal(123u, player.DisplayId);
        Assert.Equal(0u, player.TransformSpellId);
    }

    [Fact]
    public void GenericTransformAndConfuseDoNotCountAsPolymorph()
    {
        SpellInfo generic = Spell(998002, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster,
            AuraType.Transform, misc: 999999));
        using var kit = new SpellTestKit(generic);
        (Player player, _) = kit.AddPlayer(1);
        player.MaxHealth = 1000;
        player.Health = 100;
        player.SetUInt32(UpdateFields.UnitFieldStat0 + 4, 100);
        kit.System.TransformDisplayResolver = (_, _) => new TransformDisplay(778, 1.0f);
        CombatEnvironment.Register(kit.World, new CombatEnvironment(new CombatOptions(), new SpellSystemPowerAuras(kit.System)));
        player.Map!.Combat.SetInCombatState(player, 10_000);

        kit.System.CastSpell(player, 998002, SpellCastTargets.ForSelf(), true);
        kit.World.RunTick(2000);
        Assert.Equal(100u, player.Health);
    }

    [Fact]
    public void NewerTransformOwnsDisplay_AndRemovingStaleHolderDoesNotRestoreIt()
    {
        SpellInfo first = Spell(998003, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster,
            AuraType.Transform, misc: 1));
        SpellInfo second = Spell(998004, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster,
            AuraType.Transform, misc: 2));
        SpellInfo third = Spell(998011, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster,
            AuraType.Transform, misc: 3));
        using var kit = new SpellTestKit(first, second, third);
        (Player player, _) = kit.AddPlayer(1);
        player.NativeDisplayId = 100;
        player.DisplayId = 100;
        kit.System.TransformDisplayResolver = (_, entry) => new TransformDisplay(entry switch { 1 => 701u, 2 => 702u, _ => 703u }, 1.0f);

        kit.System.CastSpell(player, 998003, SpellCastTargets.ForSelf(), true);
        kit.System.CastSpell(player, 998004, SpellCastTargets.ForSelf(), true);
        kit.System.CastSpell(player, 998011, SpellCastTargets.ForSelf(), true);
        Assert.Equal(703u, player.DisplayId);
        kit.System.RemoveAuras(player, 998003);
        Assert.Equal(703u, player.DisplayId);
        kit.System.RemoveAuras(player, 998011);
        Assert.Equal(702u, player.DisplayId);
        kit.System.RemoveAuras(player, 998004);
        Assert.Equal(100u, player.DisplayId);
    }

    [Fact]
    public void TransformScale_PreservesModScaleBeforeAndAfterTransform_AndClampsMinus100()
    {
        SpellInfo scale = Spell(998005, Effect(SpellEffectName.ApplyAura, 50, SpellImplicitTarget.UnitCaster, AuraType.ModScale));
        SpellInfo transform = Spell(998006, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Transform, misc: 1));
        SpellInfo minus100 = Spell(998007, Effect(SpellEffectName.ApplyAura, -100, SpellImplicitTarget.UnitCaster, AuraType.ModScale));
        using var kit = new SpellTestKit(scale, transform, minus100);
        (Player player, _) = kit.AddPlayer(1);
        player.NativeDisplayId = 100;
        player.DisplayId = 100;
        kit.System.TransformDisplayResolver = (_, _) => new TransformDisplay(703, 2.0f);

        kit.System.CastSpell(player, 998005, SpellCastTargets.ForSelf(), true);
        Assert.Equal(1.5f, player.GetFloat(UpdateFields.ObjectFieldScaleX), 3);
        kit.System.CastSpell(player, 998006, SpellCastTargets.ForSelf(), true);
        Assert.Equal(3.0f, player.GetFloat(UpdateFields.ObjectFieldScaleX), 3);
        kit.System.RemoveAuras(player, 998006);
        Assert.Equal(1.5f, player.GetFloat(UpdateFields.ObjectFieldScaleX), 3);

        kit.System.CastSpell(player, 998007, SpellCastTargets.ForSelf(), true);
        Assert.InRange(player.GetFloat(UpdateFields.ObjectFieldScaleX), 0.001f, 0.002f);
    }
}
