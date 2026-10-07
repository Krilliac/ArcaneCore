using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

public sealed class CombatHealthRegenTests
{
    [Fact]
    public void ModRegenDuringCombat_AllowsSpiritHealthRegenOnlyWhileAuraIsActive()
    {
        using var kit = new SpellTestKit(
            Spell(995001, Effect(SpellEffectName.ApplyAura, 50, aura: AuraType.ModRegenDuringCombat)) with { Duration = new SpellDuration(30_000, 0, 30_000) },
            Spell(995002, Effect(SpellEffectName.ApplyAura, 50, aura: AuraType.ModRegenDuringCombat)) with { Duration = new SpellDuration(30_000, 0, 30_000) });
        (Player player, _) = kit.AddPlayer(1);
        player.SetUInt32(UpdateFields.UnitFieldMaxhealth, 1000);
        player.Health = 100;
        player.SetUInt32(UpdateFields.UnitFieldStat0 + 4, 30);
        SpellSystem.SetPower(player, PowerType.Rage, 100);
        player.Map!.Combat.SetInCombatState(player, 10_000);
        CombatEnvironment.Register(kit.World, new CombatEnvironment(new CombatOptions(), new SpellSystemPowerAuras(kit.System)));
        kit.World.RunTick(2000);
        Assert.Equal(100u, player.Health);
        kit.System.CastSpell(player, 995001, SpellCastTargets.ForSelf(), true);
        kit.System.CastSpell(player, 995002, SpellCastTargets.ForSelf(), true);
        kit.World.RunTick(2000);
        uint bothGain = player.Health - 100;
        Assert.True(bothGain > 0);
        Assert.Equal(100u, SpellSystem.GetPower(player, PowerType.Rage));
        kit.System.RemoveAuras(player, 995001);
        player.Health = 100;
        kit.World.RunTick(2000);
        uint oneGain = player.Health - 100;
        Assert.InRange(oneGain, 1u, bothGain - 1);
        kit.System.RemoveAuras(player, 995002);
        player.Health = 100;
        kit.World.RunTick(2000);
        Assert.Equal(100u, player.Health);
    }
}
