using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Generic;
using ArcaneCore.Game.Spells.Scripts;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>Goblin Jumper Cables 8342 and XL 22999 (vmangos spell_item.cpp GoblinJumperCablesScript): a failed roll shocks the user and cancels the resurrect.</summary>
public sealed class EngineeringGadgetScriptTests
{
    private sealed class FixedRandom(int next) : Random(1)
    {
        public override int Next(int maxValue) => Math.Min(next, maxValue - 1);
    }

    private static SpellInfo Instant(SpellInfo spell) => spell with { StartRecoveryCategory = 0, StartRecoveryTime = 0, SpellVisual = 1 };

    private static SpellTestKit Kit(int roll, int[] resurrects, uint cable)
    {
        uint shock = cable == 8342 ? 8338u : 23055u;
        var kit = new SpellTestKit(
            Instant(Spell(cable, Effect(SpellEffectName.Resurrect, 15, SpellImplicitTarget.None)) with { Targets = 0x8000, RangeIndex = 4, Range = new SpellRange(0, 30) }),
            Instant(Spell(shock, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Dummy)) with
            {
                Duration = new SpellDuration(20_000, 0, 20_000),
            }));
        kit.System.RegisterEffect(SpellEffectName.Resurrect, _ => resurrects[0]++);
        kit.System.Random = new FixedRandom(roll);
        SpellScriptDispatcher.Install(kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        return kit;
    }

    [Theory]
    [InlineData(8342u, 8338u, 66, true)]
    [InlineData(8342u, 8338u, 67, false)]
    [InlineData(22999u, 23055u, 49, true)]
    [InlineData(22999u, 23055u, 50, false)]
    public void JumperCables_FailedRollShocksTheUserAndSkipsTheResurrect(uint cable, uint shock, int roll, bool fails)
    {
        int[] resurrects = [0];
        using SpellTestKit kit = Kit(roll, resurrects, cable);
        (Player engineer, _) = kit.AddPlayer(1);
        (Player corpse, _) = kit.AddPlayer(2, 2);
        corpse.Map!.Combat.KillPlayer(corpse);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(engineer, cable, SpellCastTargets.ForUnit(corpse.Guid), triggered: true));

        Assert.Equal(fails, kit.System.HasAura(engineer, shock));
        Assert.Equal(fails ? 0 : 1, resurrects[0]);
    }

    [Fact]
    public void Discombobulate_RemovesTheTargetsMountAuraWhenItApplies()
    {
        const uint Mount = 940010;
        using var kit = new SpellTestKit(
            Instant(Spell(Mount, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Mounted)) with { Duration = new SpellDuration(-1, 0, -1) }),
            Instant(Spell(TransformAuras.Discombobulate, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.Transform)) with
            {
                Duration = new SpellDuration(20_000, 0, 20_000),
                RangeIndex = 4,
                Range = new SpellRange(0, 30),
            }));
        (Player caster, _) = kit.AddPlayer(1, 0, 0);
        (Player victim, _) = kit.AddPlayer(2, 5, 0);
        kit.System.CastSpell(victim, Mount, SpellCastTargets.ForSelf(), triggered: true);
        Assert.True(kit.System.HasAura(victim, Mount));

        kit.System.CastSpell(caster, TransformAuras.Discombobulate, SpellCastTargets.ForUnit(victim.Guid), triggered: true);

        Assert.True(kit.System.HasAura(victim, TransformAuras.Discombobulate));
        Assert.False(kit.System.HasAura(victim, Mount));
    }
}
