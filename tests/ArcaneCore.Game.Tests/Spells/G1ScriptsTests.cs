using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>Lane g1-scripts: DUMMY, SCRIPT_EFFECT and HEAL_MECHANICAL behaviour that vmangos implements in C++.</summary>
public sealed class G1ScriptsTests
{
    private static SpellInfo Instant(SpellInfo spell) => spell with { StartRecoveryCategory = 0, StartRecoveryTime = 0, SpellVisual = 1 };

    private static SpellTestKit Kit(params SpellInfo[] spells)
    {
        var kit = new SpellTestKit(spells);
        SpellScriptDispatcher.Install(kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        return kit;
    }

    [Fact]
    public void HealMechanical_HasAHandler_AndHealsALivingTarget()
    {
        using SpellTestKit kit = Kit(Instant(Spell(15057, Effect(SpellEffectName.HealMechanical, 700, SpellImplicitTarget.UnitFriend))));
        (Player caster, FakeSession session) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        target.MaxHealth = 2000;
        target.Health = 100;
        session.Clear();

        Assert.True(kit.System.HasEffectHandler(SpellEffectName.HealMechanical));
        kit.System.CastSpell(caster, 15057, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Assert.Equal(800u, target.Health); // vmangos EffectHealMechanical (SpellEffects.cpp:1835): the effect value, no crit
        Assert.Single(Packets(session, WorldOpcode.SmsgSpellheallog));
    }

    [Fact]
    public void HealMechanical_OnADeadTarget_DoesNothing()
    {
        using SpellTestKit kit = Kit(Instant(Spell(15057, Effect(SpellEffectName.HealMechanical, 700, SpellImplicitTarget.UnitFriend))));
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        target.Health = 0;

        kit.System.CastSpell(caster, 15057, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Assert.Equal(0u, target.Health);
    }
}
