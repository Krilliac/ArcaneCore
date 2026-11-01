using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Generic;
using ArcaneCore.Game.Spells.Scripts;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// Aura lifetimes of the contract-sweep baseline lines (b.never-expires, b.state-not-restored) on the manual clock of <see cref="SpellTestKit"/>.
/// The rows are the build 5875 Spell.dbc ones of Curse of Idiocy (1010), the creature Whirlwind (29851) and Hex of Jammal'an (12479). Spells whose
/// periodic trigger names their own id (1010, 29851) refreshed themselves for ever, so they never ran out.
/// </summary>
public sealed class AuraLifetimeTests
{
    private static SpellInfo Instant(SpellInfo spell) => spell with { StartRecoveryCategory = 0, StartRecoveryTime = 0, SpellVisual = 1 };

    private static SpellTestKit Kit(params SpellInfo[] spells)
    {
        var kit = new SpellTestKit(spells);
        SpellScriptDispatcher.Install(kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        return kit;
    }

    private static SpellInfo CurseOfIdiocy() => Instant(Spell(GenericDummyAuraModule.CurseOfIdiocy,
        Effect(SpellEffectName.ApplyAura, -7, SpellImplicitTarget.UnitEnemy, AuraType.ModStat, misc: 3),
        Effect(SpellEffectName.ApplyAura, -7, SpellImplicitTarget.UnitEnemy, AuraType.ModStat, misc: 4),
        Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicTriggerSpell, amplitude: 3000, trigger: GenericDummyAuraModule.CurseOfIdiocy)) with
    {
        StackAmount = 15,
        Duration = new SpellDuration(120_000, 0, 120_000),
        Attributes = SpellAttributes.AuraIsDebuff,
    });

    [Fact]
    public void CurseOfIdiocy_StacksThroughItsOwnTrigger_ButExpiresAfter120Seconds()
    {
        using SpellTestKit kit = Kit(CurseOfIdiocy());
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);

        kit.System.CastSpell(caster, GenericDummyAuraModule.CurseOfIdiocy, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        kit.Advance(10_000);
        SpellAuraHolder holder = kit.System.GetAuras(target).Single(h => h.Spell.Id == GenericDummyAuraModule.CurseOfIdiocy);
        Assert.True(holder.StackAmount > 1, "the periodic trigger stacks the curse");

        kit.Advance(108_000); // 118 s
        Assert.True(kit.System.HasAura(target, GenericDummyAuraModule.CurseOfIdiocy));

        kit.Advance(3_000); // 121 s
        Assert.False(kit.System.HasAura(target, GenericDummyAuraModule.CurseOfIdiocy));
    }

    [Fact]
    public void CreatureWhirlwind_ExpiresAfterTwoSeconds()
    {
        using SpellTestKit kit = Kit(Instant(Spell(GenericDummyAuraModule.CreatureWhirlwind,
            Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.PeriodicTriggerSpell, amplitude: 1000, trigger: GenericDummyAuraModule.CreatureWhirlwind)) with
        {
            Duration = new SpellDuration(2000, 0, 2000),
        }));
        (Player caster, _) = kit.AddPlayer(1);

        kit.System.CastSpell(caster, GenericDummyAuraModule.CreatureWhirlwind, SpellCastTargets.ForSelf(), triggered: true);
        kit.Advance(1_500);
        Assert.True(kit.System.HasAura(caster, GenericDummyAuraModule.CreatureWhirlwind));

        kit.Advance(1_000);
        Assert.False(kit.System.HasAura(caster, GenericDummyAuraModule.CreatureWhirlwind));
    }

    [Fact]
    public void HexOfJammalan_RestoresTheDisplayOnceItsRewardTransformEnds()
    {
        const uint hex = 12479, reward = 12480;
        using SpellTestKit kit = Kit(
            Instant(Spell(hex, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.Dummy)) with
            {
                Duration = new SpellDuration(10_000, 0, 10_000),
                Attributes = SpellAttributes.AuraIsDebuff,
            }),
            Instant(Spell(reward, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.Transform, misc: 8505)) with
            {
                Duration = new SpellDuration(15_000, 0, 15_000),
                Attributes = SpellAttributes.AuraIsDebuff,
            }));
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        target.NativeDisplayId = 100;
        target.DisplayId = 100;
        FixedTransformDisplays.Use(kit, 900);

        kit.System.CastSpell(caster, hex, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        kit.Advance(10_500);
        Assert.False(kit.System.HasAura(target, hex));
        Assert.True(kit.System.HasAura(target, reward), "the hex casts its reward transform when it ends");
        Assert.Equal(900u, target.DisplayId);

        kit.Advance(15_000);
        Assert.False(kit.System.HasAura(target, reward));
        Assert.Equal(100u, target.DisplayId);
    }
}
